using System.Runtime.InteropServices;
using System.Buffers.Binary;
using LightView.App.Imaging;

namespace LightView.App.Platform;

internal static class LittleCmsTransform
{
    // Little CMS TYPE_BGRA_8: RGB color space, four bytes, BGRA byte order.
    private const uint Bgra8 = (4u << 16) | (1u << 7) | (3u << 3) | 1u | (1u << 10) | (1u << 14);
    private const uint Rgba16 = (4u << 16) | (1u << 7) | (3u << 3) | 2u;

    public static byte[] ConvertBgra(byte[] input, int width, int height,
                                     byte[] sourceProfile, byte[] targetProfile, bool highPrecision = false)
    {
        if (input.Length != checked(width * height * (highPrecision ? 8 : 4)))
            throw new ArgumentException("色彩转换缓冲区尺寸不一致。", nameof(input));
        using var session = new Session(sourceProfile, targetProfile, highPrecision);
        byte[] output = new byte[checked(width * height * 4)];
        session.Convert(input, output, checked(width * height));
        return output;
    }

    internal sealed class Session : IDisposable
    {
        private IntPtr source, target, transform;
        private readonly bool highPrecision;
        internal Session(byte[] sourceProfile, byte[] targetProfile, bool highPrecision)
        {
            this.highPrecision = highPrecision;
            try
            {
                source = cmsOpenProfileFromMem(sourceProfile, checked((uint)sourceProfile.Length));
                target = cmsOpenProfileFromMem(targetProfile, checked((uint)targetProfile.Length));
                if (source == IntPtr.Zero || target == IntPtr.Zero)
                    throw new NotSupportedException("图片或显示器的色彩配置无效。未忽略此错误。");
                // Same relative-colorimetric intent and no precalculated LUT.
                transform = cmsCreateTransform(source, highPrecision ? Rgba16 : Bgra8, target, Bgra8, 1, 0x0100);
                if (transform == IntPtr.Zero) throw new NotSupportedException("暂不支持此色彩配置或像素格式组合。");
            }
            catch { Dispose(); throw; }
        }
        internal void Convert(byte[] input, byte[] output, int pixels)
        {
            if (transform == IntPtr.Zero) throw new ObjectDisposedException(nameof(Session));
            if (pixels < 0 || checked(pixels * (highPrecision ? 8 : 4)) > input.Length || checked(pixels * 4) > output.Length)
                throw new ArgumentException("色彩转换缓冲区尺寸不一致。");
            cmsDoTransform(transform, input, output, checked((uint)pixels));
            for (int pixel = 0; pixel < pixels; pixel++)
                output[pixel * 4 + 3] = highPrecision
                    ? (byte)((BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(pixel * 8 + 6, 2)) + 128) / 257)
                    : input[pixel * 4 + 3];
        }
        public void Dispose()
        {
            if (transform != IntPtr.Zero) cmsDeleteTransform(transform);
            if (target != IntPtr.Zero) cmsCloseProfile(target);
            if (source != IntPtr.Zero) cmsCloseProfile(source);
            transform = target = source = IntPtr.Zero;
        }
    }

    public static byte[] ProfileForPng(PngColorMetadata metadata)
    {
        double[] values = metadata.Chromaticities ?? [0.3127, 0.3290, 0.64, 0.33, 0.30, 0.60, 0.15, 0.06];
        var white = new XyY(values[0], values[1]);
        var primaries = new Primaries(new(values[2], values[3]), new(values[4], values[5]), new(values[6], values[7]));
        IntPtr curve = IntPtr.Zero, profile = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            curve = metadata.FileGamma.HasValue ? cmsBuildGamma(IntPtr.Zero, 1.0 / metadata.FileGamma.Value)
                : cmsBuildParametricToneCurve(IntPtr.Zero, 4, [2.4, 1.0 / 1.055, 0.055 / 1.055, 1.0 / 12.92, 0.04045]);
            if (curve == IntPtr.Zero) throw new NotSupportedException("无法解释 PNG 传递曲线。");
            profile = cmsCreateRGBProfile(ref white, ref primaries, [curve, curve, curve]);
            if (profile == IntPtr.Zero) throw new NotSupportedException("无法解释 PNG 色度配置。");
            uint length = 0;
            if (!cmsSaveProfileToMem(profile, IntPtr.Zero, ref length) || length > 16 * 1024 * 1024)
                throw new NotSupportedException("PNG 色彩配置无效。");
            buffer = Marshal.AllocHGlobal(checked((int)length));
            if (!cmsSaveProfileToMem(profile, buffer, ref length)) throw new NotSupportedException("PNG 色彩配置无效。");
            byte[] bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (profile != IntPtr.Zero) cmsCloseProfile(profile);
            if (curve != IntPtr.Zero) cmsFreeToneCurve(curve);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XyY(double x, double y) { public double X = x, Y = y, Luminance = 1; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Primaries(XyY red, XyY green, XyY blue) { public XyY Red = red, Green = green, Blue = blue; }

    [DllImport("lcms2.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern IntPtr cmsBuildGamma(IntPtr context, double gamma);
    [DllImport("lcms2.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern IntPtr cmsBuildParametricToneCurve(IntPtr context, int type, double[] parameters);
    [DllImport("lcms2.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern IntPtr cmsCreateRGBProfile(ref XyY white, ref Primaries primaries, IntPtr[] curves);
    [DllImport("lcms2.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool cmsSaveProfileToMem(IntPtr profile, IntPtr buffer, ref uint size);
    [DllImport("lcms2.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern void cmsFreeToneCurve(IntPtr curve);

    [DllImport("lcms2.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern IntPtr cmsOpenProfileFromMem([In] byte[] profile, uint length);

    [DllImport("lcms2.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern IntPtr cmsCreateTransform(IntPtr source, uint sourceFormat,
        IntPtr target, uint targetFormat, uint intent, uint flags);

    [DllImport("lcms2.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern void cmsDoTransform(IntPtr transform, [In] byte[] source, [Out] byte[] target, uint pixels);

    [DllImport("lcms2.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    private static extern void cmsDeleteTransform(IntPtr transform);

    [DllImport("lcms2.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool cmsCloseProfile(IntPtr profile);
}
