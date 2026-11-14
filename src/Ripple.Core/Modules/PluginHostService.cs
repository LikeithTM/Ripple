using System.Text.Json;

namespace Ripple.Core.Modules;

public sealed record RipplePluginManifest(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    string Icon,
    string AccentHex,
    string EntryType,
    string? EndpointUrl = null,
    string? ScriptCommand = null,
    int RefreshIntervalSeconds = 30,
    IReadOnlyList<string>? Permissions = null);

public sealed record LoadedPluginState(
    RipplePluginManifest Manifest,
    string FolderPath,
    bool IsEnabled,
    string LastGlanceText,
    string LastUpdatedText);

/// <summary>
/// Discovers, validates, and manages external manifest-based plugins from the plugins directory.
/// Fault-isolated so malformed manifests or scripts never crash the notch host.
/// </summary>
public sealed class PluginHostService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly List<LoadedPluginState> _plugins = [];

    public IReadOnlyList<LoadedPluginState> Plugins => _plugins;

    public IReadOnlyList<string> LoadErrors { get; private set; } = [];

    public void DiscoverPlugins(string pluginsDirectory)
    {
        _plugins.Clear();
        var errors = new List<string>();

        if (!Directory.Exists(pluginsDirectory))
        {
            LoadErrors = errors;
            return;
        }

        foreach (var dir in Directory.GetDirectories(pluginsDirectory))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<RipplePluginManifest>(json, JsonOpts);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
                {
                    errors.Add($"Invalid manifest in {dir}");
                    continue;
                }

                _plugins.Add(new LoadedPluginState(
                    manifest,
                    dir,
                    IsEnabled: true,
                    LastGlanceText: manifest.Description,
                    LastUpdatedText: DateTimeOffset.Now.ToString("T")));
            }
            catch (Exception ex)
            {
                errors.Add($"Failed loading plugin '{Path.GetFileName(dir)}': {ex.Message}");
            }
        }

        LoadErrors = errors;
    }

    public void SetPluginEnabled(string pluginId, bool enabled)
    {
        var idx = _plugins.FindIndex(p => string.Equals(p.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            _plugins[idx] = _plugins[idx] with { IsEnabled = enabled };
        }
    }
}
