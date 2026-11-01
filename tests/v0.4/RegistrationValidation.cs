using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using LightView.App.Platform;

namespace LightView.Tests;

internal static class RegistrationValidation
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        var baseline = AppRegistration.QueryDefaults();
        var cases = new (string Name, Action Test)[]
        {
            ("plan scopes only own HKCU values and separately quotes executable and photo", () => Plan(root)),
            ("native isolated registration repair and unregister restore original typed values", () => Native(root)),
            ("failed registration rolls back its values and retains later external changes", () => Failure(root)),
            ("interrupted pending attempt recovers before another register", () => Pending(root)),
            ("out of scope receipt is refused without registry writes", () => Tampered(root)),
            ("temporary executable is never registered as a daily handler", () => Stable(root)),
            ("installer probe finds the old data-root mutex without opening UI", () => Probe(root)),
            ("default status distinguishes none partial all and unknown", Defaults),
            ("default aliases use the resolved path and reject same-name old or missing copies", () => DefaultPaths(root)),
            ("legacy capability receipt migrates safely without leaving a duplicate registration", () => Migration(root)),
            ("failed capability migration rolls back and keeps its old ownership receipt", () => MigrationFailure(root)),
            ("native installed alias resolves its exact EXE rather than generic exefile", NativeDefaultLookup),
            ("custom install locator requires coherent uninstall record and marker", () => InstallLocation(root)),
            ("older location cannot unregister newer receipt", () => OldLocation(root)),
            ("retired receipt permits reinstall at another directory", () => ReinstallLocation(root)),
            ("active owner at another directory refuses replacement", () => ActiveOtherLocation(root)),
            ("malformed retired receipt fails before new registry writes", () => MalformedRetired(root))
        }.Concat(InstanceTests.Cases(root)).ToArray();
        int failures = 0;
        foreach (var item in cases)
        {
            try { item.Test(); Console.WriteLine("PASS " + item.Name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + item.Name + ": " + error); }
        }
        var after = AppRegistration.QueryDefaults();
        if (!baseline.SequenceEqual(after)) { failures++; Console.WriteLine("FAIL real default associations changed during isolated tests"); }
        else Console.WriteLine("PASS real Windows default associations unchanged");
        File.WriteAllText(Path.Combine(root, "default-query-readonly.json"), JsonSerializer.Serialize(new { checkedAt = DateTimeOffset.UtcNow, states = after, unchanged = baseline.SequenceEqual(after) }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"RESULT {cases.Length + 1 - failures}/{cases.Length + 1} registration checks passed"); return failures == 0 ? 0 : 1;
    }
    private static void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
    private static string Dir(string root, string name) { string result = Path.Combine(root, name); Directory.CreateDirectory(result); return result; }
    private static void Isolated(string root, string name, Action<WindowsRegistrationStore, string, string> run)
    {
        string testKey = @"Software\LightView.Tests\" + Guid.NewGuid().ToString("N");
        string data = Dir(root, name), exe = Path.Combine(data, "中文 [# &]", "LightView.exe");
        using (var existed = Registry.CurrentUser.OpenSubKey(testKey)) Check(existed is null, "Test scope unexpectedly exists.");
        try { run(new WindowsRegistrationStore(testKey), data, exe); }
        finally
        {
            if (!testKey.StartsWith(@"Software\LightView.Tests\", StringComparison.Ordinal) || !Guid.TryParseExact(testKey.Split('\\').Last(), "N", out _)) throw new InvalidOperationException("Unsafe registry test cleanup.");
            Registry.CurrentUser.DeleteSubKeyTree(testKey, false);
        }
    }
    private static void Plan(string root)
    {
        string exe = Path.Combine(root, "中文 # & []", "LightView.exe"); var plan = AppRegistration.Plan(exe);
        Check(plan.Count == 24 && plan.All(e => !e.Key.Contains("UserChoice", StringComparison.OrdinalIgnoreCase) && (e.Key.StartsWith(@"Software\Classes\") || e.Key.StartsWith(@"Software\LightView\Capabilities") || e.Key == @"Software\RegisteredApplications")), "Unexpected registration scope.");
        Check(plan.Where(e => e.Key.EndsWith(@"\shell\open\command")).All(e => e.Value == "\"" + exe + "\" \"%1\""), "Unsafe shell command quoting.");
        File.WriteAllText(Path.Combine(root, "actual-code-plan.json"), JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }
    private static void Native(string root) => Isolated(root, "native", (store, data, exe) =>
    {
        string key = @"Software\Classes\" + AppRegistration.ProgId;
        var before = new RegistryDatum(RegistryValueKind.DWord, JsonSerializer.SerializeToElement(42)); store.Write(key, "", before); store.Write(key, "foreign", RegistryDatum.Text("keep"));
        var app = new AppRegistration(store, exe, data, false); app.Register(); app.Register();
        Check(store.Read(key, "")?.Same(RegistryDatum.Text("LightView 图片")) == true, "Registration missing.");
        // A later external change must survive uninstall.
        string appKey = @"Software\Classes\Applications\LightView.exe"; store.Write(appKey, "FriendlyAppName", RegistryDatum.Text("external-later"));
        app.Unregister();
        Check(store.Read(key, "")?.Same(before) == true && store.Read(key, "foreign")?.Data.GetString() == "keep", "Original typed/unrelated value not restored.");
        Check(store.Read(appKey, "FriendlyAppName")?.Data.GetString() == "external-later", "Later external value was removed.");
        Check(store.Read(@"Software\RegisteredApplications", "LightView") is null, "Owned registration value leaked.");
    });
    private static void Failure(string root) => Isolated(root, "failure", (store, data, exe) =>
    {
        string key = @"Software\Classes\" + AppRegistration.ProgId;
        var wrapped = new FailingStore(store, () => store.Write(key, "", RegistryDatum.Text("external-after-first-write")));
        bool failed = false; try { new AppRegistration(wrapped, exe, data, false).Register(); } catch (IOException) { failed = true; }
        Check(failed && store.Read(key, "")?.Data.GetString() == "external-after-first-write", "Failure rollback overwrote external change.");
        Check(store.Read(key, "AppUserModelID") is null && !File.Exists(Path.Combine(data, "registration-pending.json")), "Failed attempt was not safely rolled back.");
    });
    private static void Pending(string root) => Isolated(root, "pending", (store, data, exe) =>
    {
        var entry = AppRegistration.Plan(exe)[0];
        var pending = new RegistrationReceipt { Executable = exe, AttemptId = Guid.NewGuid().ToString("N"), Registered = true,
            Changes = [new(entry.Key, entry.Name, RegistryDatum.Text("old-owner"), RegistryDatum.Text(entry.Value))] };
        File.WriteAllText(Path.Combine(data, "registration-pending.json"), JsonSerializer.Serialize(pending, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        store.Write(entry.Key, entry.Name, RegistryDatum.Text(entry.Value));
        var app = new AppRegistration(store, exe, data, false); app.Register();
        // Simulate a crash after ownership commit and before journal removal.
        File.Copy(Path.Combine(data, "registration-owner.json"), Path.Combine(data, "registration-pending.json"));
        app.Register(); app.Unregister();
        Check(store.Read(entry.Key, entry.Name)?.Data.GetString() == "old-owner", "Pending recovery lost original owner across repairs.");
    });
    private static void Tampered(string root) => Isolated(root, "tampered", (store, data, exe) =>
    {
        var fake = new RegistrationReceipt { Executable = exe, AttemptId = Guid.NewGuid().ToString("N"), Registered = true,
            Changes = [new(@"Software\OtherApp", "value", null, RegistryDatum.Text("bad"))] };
        File.WriteAllText(Path.Combine(data, "registration-owner.json"), JsonSerializer.Serialize(fake, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        bool refused = false; try { new AppRegistration(store, exe, data, false).Unregister(); } catch (IOException) { refused = true; }
        Check(refused && !store.KeyExists(@"Software\OtherApp"), "Tampered receipt escaped own scope.");
    });
    private static void Stable(string root) => Isolated(root, "unstable", (store, data, exe) =>
    {
        bool refused = false; try { new AppRegistration(store, exe, data).Register(); } catch (InvalidOperationException) { refused = true; }
        Check(refused && !store.KeyExists(@"Software\RegisteredApplications"), "Temporary binary was registered.");
    });
    private static void InstallLocation(string root)
    {
        string directory = Dir(root, "自定义 安装目录"), exe = Path.Combine(directory, "LightView.exe"), uninstall = Path.Combine(directory, "unins000.exe");
        File.WriteAllText(exe, "synthetic executable"); File.WriteAllText(uninstall, "synthetic uninstaller");
        string marker = Path.Combine(directory, ".lightview-install");
        Check(InstallationLocator.Validate(directory, "LightView", uninstall) is null, "Unmarked directory accepted");
        File.WriteAllText(marker, InstallationLocator.Identity + Environment.NewLine);
        Check(InstallationLocator.Validate(directory, "LightView", "\"" + uninstall + "\"") == exe, "Coherent custom installation refused");
        Check(InstallationLocator.Validate(directory, "LightView Install Test", uninstall) is null &&
            InstallationLocator.Validate(directory, "LightView", Path.Combine(root, "unins000.exe")) is null, "Foreign uninstall record accepted");
        File.WriteAllText(Path.Combine(directory, "portable"), "");
        Check(InstallationLocator.Validate(directory, "LightView", uninstall) is null, "Portable directory accepted");
    }
    private static void OldLocation(string root) => Isolated(root, "old-owner", (store, data, exe) =>
    {
        new AppRegistration(store, exe, data, false).Register();
        byte[] owner = File.ReadAllBytes(Path.Combine(data, "registration-owner.json"));
        bool refused = false;
        try { new AppRegistration(store, Path.Combine(root, "old-location", "LightView.exe"), data, false).Unregister(); }
        catch (IOException) { refused = true; }
        Check(refused && File.ReadAllBytes(Path.Combine(data, "registration-owner.json")).SequenceEqual(owner) &&
            store.Read(@"Software\Classes\" + AppRegistration.ProgId + @"\shell\open\command", "")?.Data.GetString() == "\"" + exe + "\" \"%1\"", "Older uninstall removed current registration");
    });
    private static void ReinstallLocation(string root) => Isolated(root, "reinstall-location", (store, data, exe) =>
    {
        var old = new AppRegistration(store, exe, data, false); old.Register(); old.Unregister();
        string nextExe = Path.Combine(root, "新安装位置", "LightView.exe");
        var next = new AppRegistration(store, nextExe, data, false); next.Register();
        Check(store.Read(@"Software\Classes\" + AppRegistration.ProgId + @"\shell\open\command", "")?.Data.GetString() == "\"" + nextExe + "\" \"%1\"", "Reinstalled path not registered");
        next.Unregister();
        Check(store.Read(@"Software\RegisteredApplications", AppRegistration.AppName) is null, "Reinstalled application did not unregister cleanly");
    });
    private static void ActiveOtherLocation(string root) => Isolated(root, "active-other-location", (store, data, exe) =>
    {
        var old = new AppRegistration(store, exe, data, false); old.Register();
        byte[] receipt = File.ReadAllBytes(Path.Combine(data, "registration-owner.json"));
        bool refused = false;
        try { new AppRegistration(store, Path.Combine(root, "another-active", "LightView.exe"), data, false).Register(); }
        catch (IOException) { refused = true; }
        Check(refused && File.ReadAllBytes(Path.Combine(data, "registration-owner.json")).SequenceEqual(receipt), "Active foreign owner was replaced");
        old.Unregister();
    });
    private static void MalformedRetired(string root) => Isolated(root, "malformed-retired", (store, data, exe) =>
    {
        var receipt = new RegistrationReceipt { Executable = "", AttemptId = Guid.NewGuid().ToString("N"), Registered = false };
        File.WriteAllText(Path.Combine(data, "registration-owner.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        bool refused = false;
        try { new AppRegistration(store, exe, data, false).Register(); } catch (IOException) { refused = true; }
        Check(refused && !store.KeyExists(@"Software\RegisteredApplications"), "Malformed retired receipt was accepted");
    });
    private static void Probe(string root)
    {
        string data = Path.Combine(root, "probe-data");
        string executable = Path.Combine(AppContext.BaseDirectory, "LightView.exe");
        if (!File.Exists(executable)) executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/LightView.App/bin/Debug/net10.0-windows/LightView.exe"));
        int Run()
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            FrameworkApi.AddArgument(start, "--check-running"); start.Environment["LIGHTVIEW_DATA_DIR"] = data;
            using var child = Process.Start(start)!; Check(child.WaitForExit(10000), "Probe timed out."); return child.ExitCode;
        }
        using (var owner = new SingleInstanceSession(data)) { Check(owner.IsPrimary && Run() == 2, "Probe missed same-data old owner."); }
        Check(Run() == 0 && !Directory.Exists(data), "Probe did not become ready or wrote user data.");
    }
    private static void Defaults()
    {
        var none = AppRegistration.Extensions.Select(e => new FileAssociationState(e, "other", Target: AssociationTarget.OtherApplication)).ToArray();
        Check(AppRegistration.DescribeDefaults(none) == "尚未设为默认", "None misreported.");
        none[0] = new(none[0].Extension, AppRegistration.ProgId, Target: AssociationTarget.InstalledLightView); Check(AppRegistration.DescribeDefaults(none) == "部分类型已设为默认", "Partial misreported.");
        var all = AppRegistration.Extensions.Select(e => new FileAssociationState(e, AppRegistration.ProgId, Target: AssociationTarget.InstalledLightView)).ToArray(); Check(AppRegistration.DescribeDefaults(all) == "日常安装版已设为默认", "All misreported.");
        all[0] = new(all[0].Extension, null); Check(AppRegistration.DescribeDefaults(all).Contains("无法读取"), "Unknown reported as success.");
    }
    private static void DefaultPaths(string root)
    {
        string stable = Path.Combine(Dir(root, "stable"), "LightView.exe"), old = Path.Combine(Dir(root, "old-copy"), "LightView.exe");
        File.Copy(typeof(RegistrationValidation).Assembly.Location, stable); File.Copy(stable, old);
        FileAssociationState Assess(string? id, string? path) => AppRegistration.AssessDefault(".jpg", id, path, stable);
        foreach (string id in new[] { AppRegistration.ProgId, @"Applications\LightView.exe", "custom-alias" })
            Check(Assess(id, stable).IsLightView == true, "Actual installed path not recognized through " + id);
        var other = Assess(@"Applications\LightView.exe", old);
        Check(other.IsLightView == false && other.Target == AssociationTarget.OtherLocation && AppRegistration.DescribeAssociation(other).Contains(old), "Same-name copy mislabeled installed or its path hidden");
        Check(Assess(AppRegistration.ProgId, Path.Combine(root, "removed", "LightView.exe")).Target == AssociationTarget.UnavailablePath, "Missing executable called current");
        Check(Assess(AppRegistration.ProgId, null).IsLightView is null && Assess(AppRegistration.ProgId, "LightView.exe").IsLightView is null, "Missing/relative executable identity guessed");
        Check(Assess(null, stable).IsLightView is null, "Unknown default reported success");
        Check(Assess("other", null).IsLightView == false, "Known packaged alternative requires nonexistent executable");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(stable)!, "portable"), "");
        Check(Assess(AppRegistration.ProgId, stable).Target == AssociationTarget.OtherLocation, "Portable marker ignored for daily handler status");
    }
    private static void NativeDefaultLookup()
    {
        string expected = Path.Combine(LightView.App.Persistence.AppPaths.InstallDirectory, "LightView.exe");
        Check(File.Exists(expected), "This live acceptance check requires the installed candidate");
        Check(AppRegistration.ResolveDefaultExecutable(@"Applications\LightView.exe")?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true,
            "Applications alias resolved to generic exefile instead of installed app");
        Check(AppRegistration.ResolveDefaultExecutable(AppRegistration.ProgId)?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true, "Owned ProgID does not resolve installed app");
        foreach (var state in AppRegistration.QueryDefaults())
        {
            Check(state.ProgId == DefaultAssociationQuery.Read(state.Extension), "Status did not use the current effective default");
            if (state.ProgId?.Equals(@"Applications\LightView.exe", StringComparison.OrdinalIgnoreCase) == true)
                Check(state.IsLightView == true && state.Executable?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true, "Actual installed alias misreported as unknown");
        }
    }
    private static RegistrationReceipt SeedLegacy(WindowsRegistrationStore store, string data, string exe)
    {
        var legacy = new RegistrationReceipt { Executable = exe, AttemptId = Guid.NewGuid().ToString("N"), Registered = true };
        foreach (var current in AppRegistration.Plan(exe))
        {
            var entry = current.Key.StartsWith(AppRegistration.CapabilityPath, StringComparison.OrdinalIgnoreCase)
                ? current with { Key = AppRegistration.LegacyCapabilityPath + current.Key.Substring(AppRegistration.CapabilityPath.Length) }
                : current.Key == @"Software\RegisteredApplications" ? current with { Value = AppRegistration.LegacyCapabilityPath } : current;
            var before = store.Read(entry.Key, entry.Name); var applied = RegistryDatum.Text(entry.Value);
            legacy.Changes.Add(new(entry.Key, entry.Name, before, applied)); store.Write(entry.Key, entry.Name, applied);
        }
        File.WriteAllText(Path.Combine(data, "registration-owner.json"), JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return legacy;
    }
    private static void Migration(string root) => Isolated(root, "capability-migration", (store, data, exe) =>
    {
        var original = new RegistryDatum(RegistryValueKind.DWord, JsonSerializer.SerializeToElement(42));
        store.Write(AppRegistration.LegacyCapabilityPath, "ApplicationName", original);
        SeedLegacy(store, data, exe);
        store.Write(AppRegistration.LegacyCapabilityPath, "ApplicationDescription", RegistryDatum.Text("external-later"));
        var app = new AppRegistration(store, exe, data, false); app.Register();
        Check(store.Read(@"Software\RegisteredApplications", AppRegistration.AppName)?.Data.GetString() == AppRegistration.CapabilityPath, "Registration pointer did not migrate");
        Check(store.Read(AppRegistration.LegacyCapabilityPath, "ApplicationName")?.Same(original) == true &&
            store.Read(AppRegistration.LegacyCapabilityPath, "ApplicationDescription")?.Data.GetString() == "external-later", "Migration lost prior typed value or external edit");
        Check(store.Read(AppRegistration.LegacyCapabilityPath + @"\FileAssociations", ".jpg") is null &&
            store.Read(AppRegistration.CapabilityPath + @"\FileAssociations", ".jpg")?.Data.GetString() == AppRegistration.ProgId, "Duplicate or missing capability data");
        app.Register(); app.Unregister();
        Check(store.Read(AppRegistration.CapabilityPath, "ApplicationName") is null &&
            store.Read(AppRegistration.LegacyCapabilityPath, "ApplicationName")?.Same(original) == true &&
            store.Read(@"Software\RegisteredApplications", AppRegistration.AppName) is null, "Uninstall after migration removed originals or retained its new values");
    });
    private static void MigrationFailure(string root) => Isolated(root, "capability-migration-failure", (store, data, exe) =>
    {
        SeedLegacy(store, data, exe); byte[] owner = File.ReadAllBytes(Path.Combine(data, "registration-owner.json"));
        bool failed = false;
        try { new AppRegistration(new FailingStore(store, () => { }), exe, data, false).Register(); } catch (IOException) { failed = true; }
        Check(failed && File.ReadAllBytes(Path.Combine(data, "registration-owner.json")).SequenceEqual(owner), "Failed migration committed new ownership");
        Check(store.Read(@"Software\RegisteredApplications", AppRegistration.AppName)?.Data.GetString() == AppRegistration.LegacyCapabilityPath &&
            store.Read(AppRegistration.CapabilityPath + @"\FileAssociations", ".jpg") is null, "Failed migration changed pointer or retained partial new values");
        new AppRegistration(store, exe, data, false).Unregister();
        Check(store.Read(AppRegistration.LegacyCapabilityPath, "ApplicationName") is null, "Original legacy receipt no longer uninstallable");
    });
    private sealed class FailingStore(IRegistrationStore inner, Action externalChange) : IRegistrationStore
    {
        private int writes;
        public RegistryDatum? Read(string key, string name) => inner.Read(key, name);
        public bool KeyExists(string key) => inner.KeyExists(key);
        public void Write(string key, string name, RegistryDatum value)
        { if (++writes == 2) { externalChange(); throw new IOException("Injected registry failure"); } inner.Write(key, name, value); }
        public void DeleteValue(string key, string name) => inner.DeleteValue(key, name);
        public void DeleteKeyIfEmpty(string key) => inner.DeleteKeyIfEmpty(key);
    }
}
