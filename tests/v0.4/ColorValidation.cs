using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightView.App.Imaging;
using LightView.App.Views;

namespace LightView.Tests;

internal static class ColorValidation
{
    public static int Run(string manifestPath)
    {
        using (var profile = new ColorContext(PixelFormats.Bgra32).OpenProfileStream())
        using (var profileOutput = File.Create(Path.Combine(Path.GetDirectoryName(manifestPath)!, "wpf-standard-target.icc")))
            profile.CopyTo(profileOutput);
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var results = new List<object>();
        int failures = 0;
        foreach (var item in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            bool shouldReject = item.TryGetProperty("reject", out var reject) && reject.GetBoolean();
            try
            {
                using (var input = File.OpenRead(item.GetProperty("image").GetString()!))
                {
                    var rawDecoder = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var rawFrame = rawDecoder.Frames[0];
                    var bgra = new FormatConvertedBitmap(rawFrame, PixelFormats.Bgra32, null, 0);
                    byte[] rawPixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
                    bgra.CopyPixels(rawPixels, bgra.PixelWidth * 4, 0);
                    File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(manifestPath)!, name + ".decoded.bgra"), rawPixels);
                    if (rawFrame.ColorContexts is { Count: > 0 } profiles)
                    {
                        using var profileData = profiles[0].OpenProfileStream();
                        using var profileFile = File.Create(Path.Combine(Path.GetDirectoryName(manifestPath)!, name + ".decoded-source.icc"));
                        profileData.CopyTo(profileFile);
                    }
                }
                var target = item.TryGetProperty("targetProfile", out var targetPath)
                    ? DisplayColorTarget.FromProfile(File.ReadAllBytes(targetPath.GetString()!), "测试显示器配置")
                    : DisplayColorTarget.Standard;
                var loaded = new WicImageLoader(() => target).Load(item.GetProperty("image").GetString()!, CancellationToken.None);
                if (shouldReject)
                    throw new InvalidOperationException("Expected explicit unsupported/invalid color rejection, but the image was accepted.");
                int width = item.GetProperty("width").GetInt32();
                int height = item.GetProperty("height").GetInt32();
                if (loaded.PixelWidth != width || loaded.PixelHeight != height)
                    throw new InvalidOperationException($"Expected {width}x{height}, got {loaded.PixelWidth}x{loaded.PixelHeight}.");
                var view = new ImageViewport { Source = loaded.Source, Width = width, Height = height, Background = Brushes.Transparent };
                RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.NearestNeighbor);
                view.Measure(new Size(width, height));
                view.Arrange(new Rect(0, 0, width, height));
                view.UpdateLayout();
                var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                render.Render(view);
                byte[] actual = new byte[width * height * 4];
                render.CopyPixels(actual, width * 4, 0);
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(manifestPath)!, name + ".render.bgra"), actual);
                byte[] expected = File.ReadAllBytes(item.GetProperty("reference").GetString()!);
                int maximum = 0;
                long total = 0;
                for (int p = 0; p < width * height; p++)
                    for (int c = 0; c < 3; c++)
                    {
                        int error = Math.Abs(actual[p * 4 + 2 - c] - expected[p * 3 + c]);
                        maximum = Math.Max(maximum, error);
                        total += error;
                    }
                double mean = (double)total / expected.Length;
                bool passed = maximum <= item.GetProperty("maxError").GetInt32() && mean <= item.GetProperty("meanError").GetDouble();
                int? analyticMaximum = null;
                double? analyticMean = null;
                if (item.TryGetProperty("analytic", out var analyticFlag) && analyticFlag.GetBoolean())
                {
                    byte[] analytic = File.ReadAllBytes(Path.Combine(
                        Path.GetDirectoryName(item.GetProperty("reference").GetString()!)!, name + ".analytic.rgb"));
                    int maxAnalytic = 0;
                    long sumAnalytic = 0;
                    for (int p = 0; p < width * height; p++)
                        for (int c = 0; c < 3; c++)
                        {
                            int error = Math.Abs(actual[p * 4 + 2 - c] - analytic[p * 3 + c]);
                            maxAnalytic = Math.Max(maxAnalytic, error);
                            sumAnalytic += error;
                        }
                    analyticMaximum = maxAnalytic;
                    analyticMean = (double)sumAnalytic / analytic.Length;
                    passed &= analyticMaximum <= item.GetProperty("maxError").GetInt32() &&
                              analyticMean <= item.GetProperty("meanError").GetDouble();
                }
                if (!passed) failures++;
                results.Add(new { name, passed, maximum, mean, analyticMaximum, analyticMean, profiles = loaded.EmbeddedProfileCount });
                Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: max={maximum}, mean={mean:F6}");
            }
            catch (Exception error)
            {
                bool expectedFailure = shouldReject && error is IOException or NotSupportedException or FormatException;
                if (!expectedFailure) failures++;
                results.Add(new { name, passed = expectedFailure, error = error.Message });
                Console.WriteLine($"{(expectedFailure ? "PASS" : "FAIL")} {name}: {error.Message}");
            }
        }
        string output = Path.Combine(Path.GetDirectoryName(manifestPath)!, "wpf-color-results.json");
        File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
}
