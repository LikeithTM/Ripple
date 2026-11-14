using System.Globalization;
using System.Text.RegularExpressions;

namespace Ripple.Core.Services;

public enum CommandResultKind
{
    Calculator = 0,
    UnitConversion = 1,
    AppLaunch = 2,
    RippleAction = 3,
    WebSearch = 4
}

public sealed record CommandPaletteItem(
    string Id,
    string Title,
    string Subtitle,
    string Icon,
    CommandResultKind Kind,
    string ExecutePayload);

public sealed record QuickAppEntry(
    string Id,
    string Name,
    string TargetPath,
    string IconGlyph = "◈",
    string Category = "App",
    bool IsFolder = false);

/// <summary>
/// Fast type-to-search command palette engine handling inline math, unit/currency conversion,
/// Ripple commands, installed/pinned quick apps, and web search fallback.
/// </summary>
public static partial class CommandPaletteEngine
{
    private static readonly Regex UnitRegex = MyUnitRegex();

    private static readonly Dictionary<string, (double FactorToBase, string Category, string DisplayUnit)> UnitTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["km"] = (1000.0, "length", "km"),
        ["m"] = (1.0, "length", "m"),
        ["cm"] = (0.01, "length", "cm"),
        ["mm"] = (0.001, "length", "mm"),
        ["mi"] = (1609.344, "length", "mi"),
        ["ft"] = (0.3048, "length", "ft"),
        ["in"] = (0.0254, "length", "in"),
        ["kg"] = (1.0, "mass", "kg"),
        ["g"] = (0.001, "mass", "g"),
        ["lb"] = (0.45359237, "mass", "lb"),
        ["oz"] = (0.0283495, "mass", "oz"),
        ["usd"] = (1.0, "currency", "USD"),
        ["eur"] = (1.08, "currency", "EUR"),
        ["gbp"] = (1.27, "currency", "GBP"),
        ["jpy"] = (0.0065, "currency", "JPY"),
        ["gb"] = (1024.0 * 1024.0 * 1024.0, "data", "GB"),
        ["mb"] = (1024.0 * 1024.0, "data", "MB"),
        ["kb"] = (1024.0, "data", "KB")
    };

    public static IReadOnlyList<CommandPaletteItem> Search(
        string query,
        IReadOnlyList<QuickAppEntry> quickApps)
    {
        var results = new List<CommandPaletteItem>();
        var q = query?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(q))
        {
            if (ClipboardSmartDetector.TryEvaluateMath(q, out var mathAnswer))
            {
                results.Add(new CommandPaletteItem(
                    "calc",
                    $"= {mathAnswer}",
                    $"Copy calculation result ({q})",
                    "∑",
                    CommandResultKind.Calculator,
                    mathAnswer));
            }

            if (TryConvertUnits(q, out var convertedText, out var rawConverted))
            {
                results.Add(new CommandPaletteItem(
                    "unit",
                    convertedText,
                    "Unit / Currency Conversion (Enter to copy)",
                    "⇄",
                    CommandResultKind.UnitConversion,
                    rawConverted));
            }
        }

        // Built-in Ripple power actions
        var builtIns = new[]
        {
            new CommandPaletteItem("act:settings", "Open Ripple Settings", "Configure appearance, modules, physics, profiles", "⚙", CommandResultKind.RippleAction, "settings"),
            new CommandPaletteItem("act:music_fs", "Fullscreen Music Visualizer", "Launch immersive lyrics & aurora visualizer", "♫", CommandResultKind.RippleAction, "fullscreen_music"),
            new CommandPaletteItem("act:timer25", "Start 25m Focus Pomodoro", "Begin focus session with rest progress ring", "⏱", CommandResultKind.RippleAction, "timer:25"),
            new CommandPaletteItem("act:tuner", "Open MotionTuner Debug Panel", "Adjust spring stiffness, damping & inspect frame graph", "∿", CommandResultKind.RippleAction, "motion_tuner"),
            new CommandPaletteItem("act:tour", "Start Onboarding Tour", "Replay the 60-second animated tour", "✦", CommandResultKind.RippleAction, "onboarding"),
            new CommandPaletteItem("act:doctor", "Run Notch Doctor Diagnostics", "Inspect hooks, API, fullscreen & performance health", "✚", CommandResultKind.RippleAction, "doctor")
        };

        foreach (var item in builtIns)
        {
            if (string.IsNullOrEmpty(q) ||
                item.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                item.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(item);
            }
        }

        foreach (var app in quickApps)
        {
            if (string.IsNullOrEmpty(q) ||
                app.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                app.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new CommandPaletteItem(
                    $"app:{app.Id}",
                    app.Name,
                    app.TargetPath,
                    app.IconGlyph,
                    CommandResultKind.AppLaunch,
                    app.TargetPath));
            }
        }

        if (!string.IsNullOrEmpty(q))
        {
            results.Add(new CommandPaletteItem(
                "web",
                $"Search web for \"{q}\"",
                "Open default browser",
                "⌕",
                CommandResultKind.WebSearch,
                $"https://www.google.com/search?q={Uri.EscapeDataString(q)}"));
        }

        return results;
    }

    public static bool TryConvertUnits(string query, out string display, out string copyValue)
    {
        display = string.Empty;
        copyValue = string.Empty;
        var m = UnitRegex.Match(query.Trim());
        if (!m.Success)
        {
            return false;
        }

        var val = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var fromUnit = m.Groups[2].Value;
        var toUnit = m.Groups[3].Value;

        if (string.Equals(fromUnit, "c", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(toUnit, "f", StringComparison.OrdinalIgnoreCase))
        {
            var f = val * 9.0 / 5.0 + 32.0;
            copyValue = $"{f:F1} °F";
            display = $"{val:G6} °C = {copyValue}";
            return true;
        }

        if (string.Equals(fromUnit, "f", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(toUnit, "c", StringComparison.OrdinalIgnoreCase))
        {
            var c = (val - 32.0) * 5.0 / 9.0;
            copyValue = $"{c:F1} °C";
            display = $"{val:G6} °F = {copyValue}";
            return true;
        }

        if (UnitTable.TryGetValue(fromUnit, out var from) &&
            UnitTable.TryGetValue(toUnit, out var to) &&
            from.Category == to.Category)
        {
            var converted = val * from.FactorToBase / to.FactorToBase;
            copyValue = $"{converted:G6} {to.DisplayUnit}";
            display = $"{val:G6} {from.DisplayUnit} = {copyValue}";
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^(\-?\d+(?:\.\d+)?)\s*([a-zA-Z]+)\s+(?:to|in)\s+([a-zA-Z]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MyUnitRegex();
}
