using System.Windows;

namespace LightView.App.Styles;

internal static class ThemeResources
{
    // Swap only UI brushes; image pixels, canvas and outlined text stay independent.
    public static void Refresh(Application app)
    {
        var colors = new ResourceDictionary { Source = new Uri("/InfinityView;component/Styles/Colors.xaml", UriKind.Relative) };
        if (SystemParameters.HighContrast)
        {
            foreach (string key in new[] { "WindowBrush", "ChromeBrush", "SurfaceBrush", "PressedBrush" }) colors[key] = SystemColors.WindowBrush;
            foreach (string key in new[] { "TextBrush", "SecondaryBrush", "SuccessBrush", "WarningBrush", "ErrorBrush" }) colors[key] = SystemColors.WindowTextBrush;
            colors["MutedBrush"] = SystemColors.GrayTextBrush;
            colors["BorderBrush"] = colors["DividerBrush"] = SystemColors.WindowTextBrush;
            colors["AccentBrush"] = SystemColors.HighlightTextBrush;
            colors["SelectedBrush"] = colors["HoverBrush"] = SystemColors.HighlightBrush;
            colors[SystemColors.HighlightBrushKey] = SystemColors.HighlightBrush;
            colors[SystemColors.HighlightTextBrushKey] = SystemColors.HighlightTextBrush;
            colors[SystemColors.InactiveSelectionHighlightBrushKey] = SystemColors.InactiveSelectionHighlightBrush;
            colors[SystemColors.InactiveSelectionHighlightTextBrushKey] = SystemColors.InactiveSelectionHighlightTextBrush;
        }
        app.Resources.MergedDictionaries[0] = colors;
    }
}
