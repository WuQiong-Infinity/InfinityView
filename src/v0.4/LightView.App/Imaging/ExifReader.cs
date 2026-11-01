using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace LightView.App.Imaging;

internal static class ExifReader
{
    private const int MaxBytes = 1024 * 1024;
    public static ExifReadResult Read(Stream input)
    {
        long position = input.Position;
        try
        {
            input.Position = 0;
            byte[] signature = new byte[8]; if (input.Read(signature) < 8) return new([], null);
            if (signature[0] == 0xff && signature[1] == 0xd8)
            {
                input.Position = 2;
                while (input.Position + 4 <= input.Length)
                {
                    if (input.ReadByte() != 0xff) break;
                    int marker; do { marker = input.ReadByte(); } while (marker == 0xff);
                    if (marker is < 0 or 0xda or 0xd9) break;
                    if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
                    int a = input.ReadByte(), b = input.ReadByte(); if (a < 0 || b < 0) break;
                    int length = (a << 8 | b) - 2;
                    if (length < 0 || input.Position + length > input.Length) return new([], "拍摄信息段长度无效。");
                    if (marker == 0xe1 && length >= 6)
                    {
                        byte[] bytes = new byte[length]; input.ReadExactly(bytes);
                        if (bytes.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8)) return ParseTiff(bytes.AsSpan(6).ToArray());
                    }
                    else input.Position += length;
                }
            }
            else if (signature.AsSpan().SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
            {
                byte[] header = new byte[8];
                int chunks = 0;
                while (input.Position + 12 <= input.Length)
                {
                    if (++chunks > 4096) return new([], "PNG 分段过多，未展开拍摄信息。");
                    input.ReadExactly(header); uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (size > input.Length - input.Position - 4) return new([], "拍摄信息段长度无效。");
                    if (header.AsSpan(4).SequenceEqual("eXIf"u8))
                    {
                        if (size > MaxBytes) return new([], "拍摄信息过大，未展开。");
                        byte[] bytes = new byte[(int)size]; input.ReadExactly(bytes); return ParseTiff(bytes);
                    }
                    input.Position += size + 4;
                    if (header.AsSpan(4).SequenceEqual("IEND"u8)) break;
                }
            }
            return new([], null);
        }
        catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
        { return new([], "部分拍摄信息无法读取。"); }
        finally { input.Position = position; }
    }

    internal static ExifReadResult ParseTiff(byte[] data)
    {
        if (data.Length < 8 || data.Length > MaxBytes || !(data[0] == data[1] && data[0] is 0x49 or 0x4d)) return new([], "EXIF 数据头无效。");
        bool little = data[0] == 0x49;
        ushort U16(int p) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p, 2)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
        uint U32(int p) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
        if (U16(2) != 42) return new([], "EXIF 数据格式未识别。");
        var values = new Dictionary<ushort, object>(); bool invalid = false;
        long makerOffset = -1, makerLength = 0; bool makerSeen = false, makerMalformed = false, exifPointerSeen = false, exifPointerAmbiguous = false;
        uint? Directory(uint offset, bool exifDirectory = false)
        {
            if (offset < 8 || offset > data.Length - 2) { invalid = true; return null; }
            int start = (int)offset, count = U16(start);
            if (count > 512 || start + 2L + count * 12L > data.Length) { invalid = true; return null; }
            uint? exif = null;
            for (int i = 0; i < count; i++)
            {
                int entry = start + 2 + i * 12; ushort tag = U16(entry), type = U16(entry + 2); uint n = U32(entry + 4);
                if (tag == 34665 && !exifDirectory)
                {
                    if (exifPointerSeen) exifPointerAmbiguous = true;
                    exifPointerSeen = true;
                    if (type == 4 && n == 1) exif = U32(entry + 8);
                    else exifPointerAmbiguous = true;
                    continue;
                }
                if (tag == 37500 && exifDirectory)
                {
                    if (makerSeen || type != 7 || n == 0) makerMalformed = true;
                    makerSeen = true; makerLength = n; makerOffset = U32(entry + 8);
                    continue;
                }
                if (!Tags.Contains(tag)) continue;
                int unit = type switch { 2 => 1, 3 => 2, 4 or 9 => 4, 5 or 10 => 8, _ => 0 };
                long size = (long)unit * n;
                if (unit == 0 || n == 0 || size > 4096) continue;
                long p = size <= 4 ? entry + 8 : U32(entry + 8);
                if (p < 0 || p + size > data.Length) { invalid = true; continue; }
                int at = (int)p;
                if (type == 2)
                {
                    string text = Encoding.UTF8.GetString(data, at, (int)size).TrimEnd('\0').Trim();
                    text = new string(text.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).Take(512).ToArray());
                    if (text.Length > 0) values[tag] = text;
                }
                else if (type == 3) values[tag] = (double)U16(at);
                else if (type == 4) values[tag] = (double)U32(at);
                else if (type == 9) values[tag] = (double)unchecked((int)U32(at));
                else
                {
                    double numerator = type == 10 ? unchecked((int)U32(at)) : U32(at);
                    double denominator = type == 10 ? unchecked((int)U32(at + 4)) : U32(at + 4);
                    if (denominator > 0 && FrameworkApi.IsFinite(numerator / denominator)) values[tag] = numerator / denominator;
                    else invalid = true;
                }
            }
            return exif;
        }
        uint root = U32(4); uint? child = Directory(root); if (child.HasValue && child != root) Directory(child.Value, true);
        var fields = new List<ImageDetail>();
        string? Text(ushort tag) => values.GetValueOrDefault(tag) as string;
        double? Number(ushort tag) => values.GetValueOrDefault(tag) is double d ? d : null;
        void Add(string id, string label, string? text) { if (!FrameworkApi.IsNullOrWhiteSpace(text)) fields.Add(new(label, text, id)); }
        void Numeric(string id, string label, ushort tag, Func<double, string?> format) { if (Number(tag) is { } number) Add(id, label, format(number)); }
        string F(double number, string format = "0.##") => number.ToString(format, CultureInfo.InvariantCulture);
        Add("camera.make", "相机厂商", Text(271)); Add("camera.model", "相机型号", Text(272)); Add("lens.make", "镜头厂商", Text(42035)); Add("lens.model", "镜头型号", Text(42036));
        string? time = Text(36867) ?? Text(306);
        if (DateTime.TryParseExact(time, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken)) time = taken.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Add("capture.time", "拍摄时间", time is null ? null : time + (Text(36881) is { } zone ? " " + zone : "（文件未注明时区）"));
        Numeric("exposure.shutter", "快门", 33434, d => d <= 0 ? null : d < 1 ? "1/" + F(1 / d, "0.###") + " s" : F(d) + " s");
        Numeric("exposure.aperture", "光圈", 33437, d => d > 0 ? "f/" + F(d) : null);
        if ((Number(34855) ?? Number(34867)) is { } iso && iso > 0) Add("exposure.iso", "ISO", F(iso, "0"));
        Numeric("exposure.focal", "焦距", 37386, d => d > 0 ? F(d) + " mm" : null);
        Numeric("exposure.equivalent", "等效35mm焦距", 41989, d => d > 0 ? F(d) + " mm" : null);
        Numeric("exposure.compensation", "曝光补偿", 37380, d => F(d, "+0.##;-0.##;0") + " EV");
        Numeric("exposure.program", "曝光程序", 34850, d => d switch {1=>"手动",2=>"程序自动",3=>"光圈优先",4=>"快门优先",5=>"创意程序",6=>"运动程序",7=>"人像",8=>"风景",_=>null});
        Numeric("exposure.metering", "测光模式", 37383, d => d switch {1=>"平均",2=>"中央重点",3=>"点测光",4=>"多点",5=>"多区/评价",6=>"局部",_=>null});
        Numeric("exposure.flash", "闪光灯", 37385, d => ((uint)d & 1) != 0 ? "已闪光" : "未闪光");
        Numeric("exposure.whitebalance", "白平衡", 41987, d => d switch {0=>"自动",1=>"手动",_=>null});
        CameraStyle? style = makerMalformed || exifPointerAmbiguous ? CameraStyleReader.Status(CameraStyleState.Malformed)
            : makerSeen ? CameraStyleReader.Read(data, makerOffset, makerLength, Text(271)) : null;
        string? warning = invalid ? "部分 EXIF 字段无效，已忽略。" : null;
        if (style?.State is CameraStyleState.Malformed or CameraStyleState.OverBudget)
            warning = (warning is null ? "" : warning + " ") + "部分照片风格信息无法读取，已忽略。";
        return new(fields.AsReadOnly(), warning, style);
    }
    private static readonly HashSet<ushort> Tags = [271,272,306,36867,36881,42035,42036,33434,33437,34855,34867,37386,41989,37380,34850,37383,37385,41987];
}
