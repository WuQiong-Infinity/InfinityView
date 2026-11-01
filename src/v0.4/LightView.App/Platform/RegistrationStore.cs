using System.Text.Json;
using Microsoft.Win32;

namespace LightView.App.Platform;

internal sealed record RegistryDatum(RegistryValueKind Kind, JsonElement Data)
{
    public static RegistryDatum Text(string value) => new(RegistryValueKind.String, JsonSerializer.SerializeToElement(value));
    public bool Same(RegistryDatum? other) => other is not null && Kind == other.Kind && Data.GetRawText() == other.Data.GetRawText();
    public object NativeValue() => Kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => Data.GetString() ?? "",
        RegistryValueKind.DWord => Data.GetInt32(), RegistryValueKind.QWord => Data.GetInt64(),
        RegistryValueKind.Binary or RegistryValueKind.None => Data.GetBytesFromBase64(),
        RegistryValueKind.MultiString => Data.Deserialize<string[]>() ?? [],
        _ => throw new InvalidOperationException("注册值类型不受支持。")
    };
}
internal interface IRegistrationStore
{
    RegistryDatum? Read(string key, string name);
    bool KeyExists(string key);
    void Write(string key, string name, RegistryDatum value);
    void DeleteValue(string key, string name);
    void DeleteKeyIfEmpty(string key);
}
internal sealed class WindowsRegistrationStore(string prefix = "") : IRegistrationStore
{
    private string PathFor(string key) => prefix.Length == 0 ? key : prefix.TrimEnd('\\') + "\\" + key;
    public RegistryDatum? Read(string key, string name)
    {
        using var opened = Registry.CurrentUser.OpenSubKey(PathFor(key));
        if (opened is null || !opened.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        var value = opened.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? "";
        return new(opened.GetValueKind(name), JsonSerializer.SerializeToElement(value, value.GetType()));
    }
    public bool KeyExists(string key) { using var opened = Registry.CurrentUser.OpenSubKey(PathFor(key)); return opened is not null; }
    public void Write(string key, string name, RegistryDatum value)
    { using var opened = Registry.CurrentUser.CreateSubKey(PathFor(key), true); opened.SetValue(name, value.NativeValue(), value.Kind); opened.Flush(); }
    public void DeleteValue(string key, string name)
    { using var opened = Registry.CurrentUser.OpenSubKey(PathFor(key), true); opened?.DeleteValue(name, false); opened?.Flush(); }
    public void DeleteKeyIfEmpty(string key)
    {
        string path = PathFor(key);
        using (var opened = Registry.CurrentUser.OpenSubKey(path))
            if (opened is null || opened.SubKeyCount != 0 || opened.ValueCount != 0) return;
        Registry.CurrentUser.DeleteSubKey(path, false);
    }
}
