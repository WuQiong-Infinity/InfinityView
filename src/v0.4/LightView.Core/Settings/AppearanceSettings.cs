using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightView.Core.Settings;

public sealed class AppearanceSettings
{
    public string CanvasBackground { get; set; } = "darkGray";
    public bool TransparencyCheckerboard { get; set; }
    public double DetailsFontSize { get; set; } = 16;
    public double DetailsOutlineThickness { get; set; } = 1.2;
    public double DetailsLineGap { get; set; } = 6;
    public double DetailsWidth { get; set; } = 440;
    public bool DetailsFitSmallWindow { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public DetailsLayout? DetailsLayout { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? UnknownFields { get; set; }
    public AppearanceSettings Clone() => JsonSerializer.Deserialize<AppearanceSettings>(JsonSerializer.Serialize(this))!;
}

public sealed class WindowSettings
{
    public bool RememberPlacement { get; set; } = true;
    public bool FullScreenAutoHide { get; set; } = true;
    public SavedWindowBounds? RestoreBounds { get; set; }
    public double? SavedDpiScale { get; set; }
    public string? MonitorDeviceName { get; set; }
    public bool StartMaximized { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

public sealed record SavedWindowBounds(double Left, double Top, double Width, double Height);
