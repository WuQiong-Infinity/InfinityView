using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LightView.App.Persistence;

namespace LightView.App.Platform;

internal sealed record RegistrationEntry(string Key, string Name, string Value);
internal sealed record RegistrationChange(string Key, string Name, RegistryDatum? Before, RegistryDatum? Applied);
internal sealed class RegistrationReceipt
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Executable { get; set; } = "";
    public string AttemptId { get; set; } = "";
    public bool Registered { get; set; }
    public List<RegistrationChange> Changes { get; set; } = [];
    public List<string> CreatedKeys { get; set; } = [];
}
internal enum AssociationTarget { Unknown, OtherApplication, InstalledLightView, OtherLocation, UnavailablePath }
internal sealed record FileAssociationState(string Extension, string? ProgId, string? Executable = null,
    string? Version = null, AssociationTarget Target = AssociationTarget.Unknown)
{ public bool? IsLightView => Target == AssociationTarget.Unknown ? null : Target == AssociationTarget.InstalledLightView; }

internal sealed class AppRegistration
{
    public const string AppName = "InfinityView", ProgId = "LightView.Desktop.Image", AppUserModelId = "LightView.Desktop";
    internal const string CapabilityPath = @"Software\Classes\Applications\LightView.exe\Capabilities";
    internal const string LegacyCapabilityPath = @"Software\LightView\Capabilities";
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp"];
    private readonly IRegistrationStore registry;
    private readonly string executable, receiptPath, pendingPath;
    private readonly bool requireStableInstallation;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AppRegistration(IRegistrationStore registry, string executable, string dataDirectory, bool requireStableInstallation = true)
    {
        this.registry = registry; this.executable = Path.GetFullPath(executable); this.requireStableInstallation = requireStableInstallation;
        receiptPath = Path.Combine(Path.GetFullPath(dataDirectory), "registration-owner.json"); pendingPath = Path.Combine(Path.GetFullPath(dataDirectory), "registration-pending.json");
    }
    public static bool IsStableInstallation(string path) =>
        Path.GetFullPath(path).Equals(InstallationLocator.FindExecutable(), StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<RegistrationEntry> Plan(string path)
    {
        path = Path.GetFullPath(path);
        if (!FrameworkApi.IsPathFullyQualified(path) || path.Contains('"') || !KnownExecutable(path)) throw new ArgumentException("应用路径无效。");
        string command = "\"" + path + "\" \"%1\"", icon = "\"" + path + "\",0";
        string prog = @"Software\Classes\" + ProgId, app = @"Software\Classes\Applications\LightView.exe";
        var entries = new List<RegistrationEntry>
        {
            new(prog,"", "InfinityView 图片"), new(prog,"AppUserModelID",AppUserModelId),
            new(prog+@"\DefaultIcon","",icon),new(prog+@"\shell\open\command","",command),new(prog+@"\shell\open","MultiSelectModel","Single"),
            new(app,"FriendlyAppName",AppName),new(app+@"\DefaultIcon","",icon),new(app+@"\shell\open\command","",command)
        };
        foreach (string ext in Extensions)
        {
            entries.Add(new(app+@"\SupportedTypes",ext,""));
            entries.Add(new(@"Software\Classes\"+ext+@"\OpenWithProgids",ProgId,""));
            entries.Add(new(CapabilityPath+@"\FileAssociations",ext,ProgId));
        }
        entries.AddRange([new(CapabilityPath,"ApplicationName",AppName),
            new(CapabilityPath,"ApplicationDescription","InfinityView 图片查看与安全整理"),
            new(CapabilityPath,"ApplicationIcon",icon),
            new(@"Software\RegisteredApplications","LightView",CapabilityPath)]); // Stable upgrade registration identity; displayed name is InfinityView.
        return entries;
    }
    public void Register()
    {
        if (requireStableInstallation && !IsStableInstallation(executable)) throw new InvalidOperationException("请先安装到稳定位置，再注册默认应用能力。");
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        using var lease = new FileStream(receiptPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        RecoverPending();
        var prior = Load(receiptPath);
        // A normal uninstall deliberately retains user data and writes an empty,
        // retired receipt. Reinstallation at another directory may start a new
        // ownership chain; an active or malformed foreign receipt stays blocked.
        if (prior is { SchemaVersion: 1, Registered: false, Changes.Count: 0, CreatedKeys.Count: 0 } &&
            !string.Equals(prior.Executable, executable, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(prior.AttemptId, "N", out _))
        {
            try { _ = Plan(prior.Executable); }
            catch (ArgumentException error) { throw new IOException("已退役注册回执的程序路径无效，已停止修改。", error); }
            prior = null;
        }
        Validate(prior);
        var entries = Plan(executable);
        var missing = entries.SelectMany(e => Parents(e.Key)).Distinct(StringComparer.OrdinalIgnoreCase).Where(key => !registry.KeyExists(key)).ToList();
        var transaction = NewReceipt(true); var owner = NewReceipt(true); owner.AttemptId = transaction.AttemptId;
        owner.CreatedKeys = (prior?.Registered == true ? prior.CreatedKeys : []).Concat(missing).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        transaction.CreatedKeys = missing;
        foreach (var entry in entries)
        {
            var current = registry.Read(entry.Key, entry.Name); var desired = RegistryDatum.Text(entry.Value);
            var inherited = prior?.Registered == true ? prior.Changes.FirstOrDefault(c => SameName(c, entry.Key, entry.Name) && Equal(current, c.Applied)) : null;
            owner.Changes.Add(new(entry.Key, entry.Name, inherited is null ? current : inherited.Before, desired));
            if (!Equal(current, desired)) transaction.Changes.Add(new(entry.Key, entry.Name, current, desired));
        }
        // Retire RC1-4's custom capability branch only through its durable owner
        // receipt. Restore any prior typed value; leave later external edits.
        if (prior?.Registered == true)
            foreach (var item in prior.Changes.Where(c => IsLegacyCapability(c.Key)))
            {
                var current = registry.Read(item.Key, item.Name);
                if (Equal(current, item.Applied) && !Equal(current, item.Before))
                    transaction.Changes.Add(new(item.Key, item.Name, current, item.Before));
            }
        Commit(transaction, owner);
        if (prior?.Registered == true)
            try { Cleanup(prior.CreatedKeys.Where(IsLegacyCapability)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { /* Empty-key cleanup can be retried at uninstall; ownership is committed. */ }
        NotifyShell();
    }
    public void Unregister()
    {
        if (!Directory.Exists(Path.GetDirectoryName(receiptPath)!)) return;
        using var lease = new FileStream(receiptPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        RecoverPending(); var owner = Load(receiptPath); Validate(owner);
        if (owner?.Registered != true) return;
        var transaction = NewReceipt(false); transaction.CreatedKeys = owner.CreatedKeys;
        foreach (var item in owner.Changes)
        {
            var current = registry.Read(item.Key, item.Name);
            if (Equal(current, item.Applied)) transaction.Changes.Add(new(item.Key, item.Name, current, item.Before));
        }
        var final = NewReceipt(false); final.AttemptId = transaction.AttemptId;
        Commit(transaction, final); NotifyShell();
    }
    private RegistrationReceipt NewReceipt(bool registered) => new() { Executable = executable, AttemptId = Guid.NewGuid().ToString("N"), Registered = registered };
    private void Commit(RegistrationReceipt transaction, RegistrationReceipt owner)
    {
        Save(pendingPath, transaction); // Durable old values precede the first registry write.
        try
        {
            foreach (var change in transaction.Changes)
            {
                if (!Equal(registry.Read(change.Key, change.Name), change.Before)) throw new IOException("注册项已由其他程序更改，未覆盖。");
                Set(change.Key, change.Name, change.Applied);
            }
            if (!owner.Registered) Cleanup(transaction.CreatedKeys);
            Save(receiptPath, owner);
            File.Delete(pendingPath);
        }
        catch
        {
            if (Load(receiptPath)?.AttemptId == transaction.AttemptId) throw;
            Rollback(transaction); File.Delete(pendingPath); throw;
        }
    }
    private void RecoverPending()
    {
        var pending = Load(pendingPath); if (pending is null) return; Validate(pending);
        var committed = Load(receiptPath); Validate(committed);
        if (committed?.AttemptId != pending.AttemptId) Rollback(pending);
        File.Delete(pendingPath);
    }
    private void Rollback(RegistrationReceipt transaction)
    {
        foreach (var item in transaction.Changes.AsEnumerable().Reverse())
            if (Equal(registry.Read(item.Key, item.Name), item.Applied)) Set(item.Key, item.Name, item.Before);
        if (transaction.Registered) Cleanup(transaction.CreatedKeys);
    }
    private void Set(string key, string name, RegistryDatum? value)
    { if (value is null) registry.DeleteValue(key, name); else registry.Write(key, name, value); }
    private void Cleanup(IEnumerable<string> keys)
    { foreach (string key in keys.Where(CanRemoveKey).OrderByDescending(key => key.Length)) registry.DeleteKeyIfEmpty(key); }
    private static bool CanRemoveKey(string key) =>
        key.Equals(@"Software\Classes\" + ProgId, StringComparison.OrdinalIgnoreCase) || key.StartsWith(@"Software\Classes\" + ProgId + "\\", StringComparison.OrdinalIgnoreCase) ||
        key.Equals(@"Software\Classes\Applications\LightView.exe", StringComparison.OrdinalIgnoreCase) || key.StartsWith(@"Software\Classes\Applications\LightView.exe\", StringComparison.OrdinalIgnoreCase) ||
        key.Equals(@"Software\LightView\Capabilities", StringComparison.OrdinalIgnoreCase) || key.StartsWith(@"Software\LightView\Capabilities\", StringComparison.OrdinalIgnoreCase) ||
        Extensions.Any(ext => key.Equals(@"Software\Classes\" + ext + @"\OpenWithProgids", StringComparison.OrdinalIgnoreCase));
    private void Validate(RegistrationReceipt? receipt)
    {
        if (receipt is null) return;
        // Legacy names are accepted solely for receipt migration/recovery and
        // uninstall, never registered as a second permanent capability branch.
        var plan = Plan(executable).Concat(Plan(executable).Where(e => e.Key.StartsWith(CapabilityPath, StringComparison.OrdinalIgnoreCase))
            .Select(e => e with { Key = LegacyCapabilityPath + e.Key.Substring(CapabilityPath.Length) })).ToArray();
        bool compatibleRename = KnownExecutable(receipt.Executable) && FrameworkApi.IsPathFullyQualified(receipt.Executable) && KnownExecutable(executable) &&
            (Path.GetDirectoryName(Path.GetFullPath(receipt.Executable)).Equals(Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase) ||
             IsDirectoryBrandRename(receipt.Executable, executable));
        if (receipt.SchemaVersion != 1 || !(string.Equals(receipt.Executable, executable, StringComparison.OrdinalIgnoreCase) || compatibleRename) || !Guid.TryParseExact(receipt.AttemptId, "N", out _) ||
            receipt.Changes is null || receipt.CreatedKeys is null ||
            receipt.Changes.Any(item => item is null || item.Key is null || item.Name is null || !plan.Any(e => SameName(item, e.Key, e.Name))) ||
            receipt.CreatedKeys.Any(key => key is null || !plan.Any(e => Parents(e.Key).Contains(key, StringComparer.OrdinalIgnoreCase))))
            throw new IOException("注册回执范围不匹配，已停止自动修改。");
        try { foreach (var item in receipt.Changes) { _ = item.Before?.NativeValue(); _ = item.Applied?.NativeValue(); } }
        catch (Exception error) when (error is InvalidOperationException or JsonException or FormatException) { throw new IOException("注册回执的值无效，已停止修改。", error); }
    }
    private static IEnumerable<string> Parents(string path)
    { string current = ""; foreach (string part in path.Split('\\')) { current = current.Length == 0 ? part : current + "\\" + part; yield return current; } }
    private static bool SameName(RegistrationChange item, string key, string name) => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
    private static bool IsLegacyCapability(string key) => key.Equals(LegacyCapabilityPath, StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith(LegacyCapabilityPath + "\\", StringComparison.OrdinalIgnoreCase);
    private static bool KnownExecutable(string? path) => string.Equals(Path.GetFileName(path), "LightView.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(path), "InfinityView.exe", StringComparison.OrdinalIgnoreCase);
    // The installer permits exactly this sibling-directory rename, never a
    // general relocation. The public Register entry still requires the actual
    // owned installation recorded by Windows before consuming any receipt.
    internal static bool IsDirectoryBrandRename(string previous, string current)
    {
        string oldDirectory = Path.GetDirectoryName(Path.GetFullPath(previous))!;
        string newDirectory = Path.GetDirectoryName(Path.GetFullPath(current))!;
        return Path.GetFileName(oldDirectory).Equals("LightView", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(newDirectory).Equals("InfinityView", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetDirectoryName(oldDirectory), Path.GetDirectoryName(newDirectory), StringComparison.OrdinalIgnoreCase);
    }
    private static bool Equal(RegistryDatum? a, RegistryDatum? b) => a is null ? b is null : a.Same(b);
    private static RegistrationReceipt? Load(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("注册回执过大，已停止修改。");
        return JsonSerializer.Deserialize<RegistrationReceipt>(File.ReadAllBytes(path), Options) ?? throw new IOException("注册回执无法读取。");
    }
    private static void Save(string path, RegistrationReceipt receipt)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Options);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static IReadOnlyList<FileAssociationState> QueryDefaults() => Extensions.Select(ext =>
    {
        string? progId = DefaultAssociationQuery.Read(ext);
        string? executable = progId is null ? null : ResolveDefaultExecutable(progId);
        if (progId != DefaultAssociationQuery.Read(ext)) return new FileAssociationState(ext, null);
        return AssessDefault(ext, progId, executable, InstallationLocator.FindExecutable());
    }).ToArray();
    internal static string? ResolveDefaultExecutable(string progId) => progId.StartsWith(@"Applications\", StringComparison.OrdinalIgnoreCase)
        ? QueryAssociation(progId.Substring(13), 2, 2) // ASSOCF_INIT_BYEXENAME, not the .exe file type itself
        : QueryAssociation(progId, 2, 0x2800); // FIXED_PROGID | INIT_FOR_FILE
    private static string? QueryAssociation(string extension, uint kind, uint flags = 0)
    {
        uint length = 0; AssocQueryStringW(flags, kind, extension, null, null, ref length);
        if (length is 0 or > 32768) return null;
        var text = new StringBuilder((int)length);
        return AssocQueryStringW(flags, kind, extension, null, text, ref length) == 0 ? text.ToString() : null;
    }
    internal static FileAssociationState AssessDefault(string extension, string? progId, string? executable, string? installedExecutable)
    {
        bool lightViewId = progId?.Equals(ProgId, StringComparison.OrdinalIgnoreCase) == true ||
            progId?.Equals(@"Applications\LightView.exe", StringComparison.OrdinalIgnoreCase) == true;
        if (progId is null) return new(extension, progId);
        if (FrameworkApi.IsNullOrWhiteSpace(executable))
            return new(extension, progId, Target: lightViewId ? AssociationTarget.Unknown : AssociationTarget.OtherApplication);
        try
        {
            string actual = executable.Trim();
            if (actual.Length >= 2 && actual[0] == '"' && actual[actual.Length - 1] == '"') actual = actual.Substring(1, actual.Length - 2);
            if (actual.Contains('"') || !FrameworkApi.IsPathFullyQualified(actual)) return new(extension, progId);
            actual = Path.GetFullPath(actual);
            bool sameName = KnownExecutable(actual);
            bool installed = installedExecutable is not null && actual.Equals(Path.GetFullPath(installedExecutable), StringComparison.OrdinalIgnoreCase);
            if (!installed && !lightViewId && !sameName) return new(extension, progId, actual, Target: AssociationTarget.OtherApplication);
            if (!File.Exists(actual)) return new(extension, progId, actual, Target: AssociationTarget.UnavailablePath);
            string? version = null;
            try { version = FileVersionInfo.GetVersionInfo(actual).ProductVersion; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            bool portable = File.Exists(Path.Combine(Path.GetDirectoryName(actual)!, "portable"));
            return new(extension, progId, actual, version, installed && !portable ? AssociationTarget.InstalledLightView : AssociationTarget.OtherLocation);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        { return new(extension, progId); }
    }
    internal static string DescribeAssociation(FileAssociationState state)
    {
        string status = state.Target switch
        {
            AssociationTarget.InstalledLightView => "InfinityView（日常安装版）",
            AssociationTarget.OtherLocation => "其他位置的程序，请检查路径",
            AssociationTarget.UnavailablePath => "路径失效或不可访问",
            AssociationTarget.OtherApplication => "其他应用",
            _ => "无法确认实际打开位置"
        };
        return state.Extension + "：" + status + (FrameworkApi.IsNullOrEmpty(state.Version) ? "" : " · " + state.Version) +
            (state.Executable is null || state.Target == AssociationTarget.OtherApplication ? "" : Environment.NewLine + state.Executable);
    }
    public static string DescribeDefaults(IReadOnlyList<FileAssociationState> states)
    {
        if (states.Any(s => s.IsLightView is null)) return "部分或全部默认关联无法读取";
        if (states.Any(s => s.Target == AssociationTarget.UnavailablePath)) return "部分默认路径失效或不可访问";
        if (states.Any(s => s.Target == AssociationTarget.OtherLocation)) return "部分类型未指向日常安装版";
        int count = states.Count(s => s.IsLightView == true);
        return count == states.Count ? "日常安装版已设为默认" : count == 0 ? "尚未设为默认" : "部分类型已设为默认";
    }
    public static void OpenDefaultSettings()
    {
        string uri = Environment.OSVersion.Version.Build >= 22631 ? "ms-settings:defaultapps?registeredAppUser=LightView" : "ms-settings:defaultapps";
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
    }
    public static void SetProcessIdentity() { _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int AssocQueryStringW(uint flags, uint kind, string association, string? extra, StringBuilder? output, ref uint length);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr first, IntPtr second);
}
