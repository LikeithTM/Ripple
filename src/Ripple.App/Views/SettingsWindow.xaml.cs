using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Ripple.App.Interop;
using Ripple.App.ViewModels;
using Ripple.Core.Geometry;
using Ripple.Core.Services;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;

namespace Ripple.App.Views;

public partial class SettingsWindow : Window
{
    private readonly RippleCoordinator _coord;
    private readonly Action _repositionNotch;
    private readonly Action _openOnboarding;
    private bool _loading = true;

    public SettingsWindow(RippleCoordinator coord, Action repositionNotch, Action openOnboarding)
    {
        InitializeComponent();
        _coord = coord;
        _repositionNotch = repositionNotch;
        _openOnboarding = openOnboarding;

        LoadFromSettings();
        _loading = false;
    }

    private void LoadFromSettings()
    {
        var s = _coord.Settings;
        ChkStartup.IsChecked = s.StartWithWindows;
        ChkFollowMonitor.IsChecked = s.FollowCursorMonitor;
        ChkBatterySaver.IsChecked = s.BatterySaver30Fps;
        ChkUiSounds.IsChecked = s.SubtleUiSounds;
        ChkLargeTargets.IsChecked = s.LargeTargetMode;

        CmbLayout.SelectedIndex = (int)s.LayoutMode;
        CmbAnchor.SelectedIndex = s.DockEdge == NotchDockEdge.Bottom ? 3 : (int)s.HorizontalAnchor;
        CmbTheme.SelectedIndex = (int)s.Theme;

        SldRestWidth.Value = s.RestWidth;
        SldRestHeight.Value = s.RestHeight;
        SldEarRadius.Value = s.EarRadius;
        SldOffsetX.Value = s.OffsetX;
        ChkGlowEdge.IsChecked = s.EnableGlowEdge;
        ChkAurora.IsChecked = s.EnableAuroraEffect;

        SldIntentDelay.Value = s.HoverIntentDelayMs;
        SldGraceDelay.Value = s.CollapseGraceDelayMs;
        SldProximity.Value = s.ProximityRadiusPx;
        ChkHideFullscreen.IsChecked = s.HideInFullscreen;
        ChkCollisionDodge.IsChecked = s.CollisionAvoidance;
        ChkFocusAssist.IsChecked = s.FocusAssistAware;
        ChkContextMorph.IsChecked = s.ContextAwareMorphing;
        ChkReducedMotion.IsChecked = s.ReducedMotion;

        UpdateSliderLabels();
        RefreshModulesList();
        RefreshProfilesList();

        TxtApiEndpoint.Text = $"REST & WebSocket Endpoint: http://127.0.0.1:{s.LocalApiPort}/api/v1/";
        TxtApiToken.Text = $"Bearer Token (X-Ripple-Token): {s.LocalApiToken}";
        TxtPluginsSummary.Text = $"Discovered Plugins: {_coord.Plugins.Plugins.Count} ({string.Join(", ", _coord.Plugins.Plugins.Select(p => p.Manifest.Name))})";
        TxtDoctorReport.Text = _coord.BuildDiagnosticsReport();
    }

    private void ApplyLiveToSettings()
    {
        if (_loading) return;
        var s = _coord.Settings;

        s.StartWithWindows = ChkStartup.IsChecked == true;
        Win32Interop.SetStartupWithWindows(s.StartWithWindows);

        s.FollowCursorMonitor = ChkFollowMonitor.IsChecked == true;
        s.BatterySaver30Fps = ChkBatterySaver.IsChecked == true;
        s.SubtleUiSounds = ChkUiSounds.IsChecked == true;
        s.LargeTargetMode = ChkLargeTargets.IsChecked == true;

        s.LayoutMode = (NotchLayoutMode)Math.Max(0, CmbLayout.SelectedIndex);
        if (CmbAnchor.SelectedIndex == 3)
        {
            s.DockEdge = NotchDockEdge.Bottom;
            s.HorizontalAnchor = NotchHorizontalAnchor.Center;
        }
        else
        {
            s.DockEdge = NotchDockEdge.Top;
            s.HorizontalAnchor = (NotchHorizontalAnchor)Math.Clamp(CmbAnchor.SelectedIndex, 0, 2);
        }
        s.Theme = (RippleThemePreset)Math.Max(0, CmbTheme.SelectedIndex);

        s.RestWidth = SldRestWidth.Value;
        s.RestHeight = SldRestHeight.Value;
        s.EarRadius = SldEarRadius.Value;
        s.OffsetX = SldOffsetX.Value;
        s.EnableGlowEdge = ChkGlowEdge.IsChecked == true;
        s.EnableAuroraEffect = ChkAurora.IsChecked == true;

        s.HoverIntentDelayMs = (int)SldIntentDelay.Value;
        s.CollapseGraceDelayMs = (int)SldGraceDelay.Value;
        s.ProximityRadiusPx = SldProximity.Value;
        s.HideInFullscreen = ChkHideFullscreen.IsChecked == true;
        s.CollisionAvoidance = ChkCollisionDodge.IsChecked == true;
        s.FocusAssistAware = ChkFocusAssist.IsChecked == true;
        s.ContextAwareMorphing = ChkContextMorph.IsChecked == true;
        s.ReducedMotion = ChkReducedMotion.IsChecked == true;

        _coord.StateMachine.Timing = s.BuildTimingConfig();
        UpdateSliderLabels();
        _repositionNotch();
    }

    private void UpdateSliderLabels()
    {
        LblRestWidth.Text = $"{SldRestWidth.Value:F0}";
        LblRestHeight.Text = $"{SldRestHeight.Value:F0}";
        LblEarRadius.Text = $"{SldEarRadius.Value:F0}";
        LblOffsetX.Text = $"{SldOffsetX.Value:F0}";
        LblIntentDelay.Text = $"{SldIntentDelay.Value:F0}";
        LblGraceDelay.Text = $"{SldGraceDelay.Value:F0}";
        LblProximity.Text = $"{SldProximity.Value:F0}";
    }

    private void OnLiveSettingChanged(object sender, RoutedEventArgs e) => ApplyLiveToSettings();
    private void OnLiveSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyLiveToSettings();
    private void OnLiveSelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyLiveToSettings();

    private void RefreshModulesList()
    {
        ModulesList.Items.Clear();
        foreach (var id in _coord.Settings.ModuleOrder)
        {
            var mod = _coord.Modules.FirstOrDefault(m => m.Id == id);
            if (mod is null) continue;
            var enabled = !_coord.Settings.DisabledModules.Contains(id);
            ModulesList.Items.Add(new ListBoxItem
            {
                Tag = id,
                Content = $"{(enabled ? "[✓]" : "[  ]")} {mod.Icon} {mod.Name} ({mod.Id})"
            });
        }
    }

    private void OnToggleModule(object sender, RoutedEventArgs e)
    {
        if (ModulesList.SelectedItem is ListBoxItem { Tag: string id })
        {
            if (!_coord.Settings.DisabledModules.Add(id))
            {
                _coord.Settings.DisabledModules.Remove(id);
            }
            _coord.ApplyModuleEnabledSettings();
            RefreshModulesList();
        }
    }

    private void OnMoveModuleUp(object sender, RoutedEventArgs e)
    {
        var idx = ModulesList.SelectedIndex;
        if (idx <= 0) return;
        (_coord.Settings.ModuleOrder[idx - 1], _coord.Settings.ModuleOrder[idx]) =
            (_coord.Settings.ModuleOrder[idx], _coord.Settings.ModuleOrder[idx - 1]);
        RefreshModulesList();
        ModulesList.SelectedIndex = idx - 1;
    }

    private void OnMoveModuleDown(object sender, RoutedEventArgs e)
    {
        var idx = ModulesList.SelectedIndex;
        if (idx < 0 || idx >= _coord.Settings.ModuleOrder.Count - 1) return;
        (_coord.Settings.ModuleOrder[idx + 1], _coord.Settings.ModuleOrder[idx]) =
            (_coord.Settings.ModuleOrder[idx], _coord.Settings.ModuleOrder[idx + 1]);
        RefreshModulesList();
        ModulesList.SelectedIndex = idx + 1;
    }

    private void RefreshProfilesList()
    {
        ProfilesList.Items.Clear();
        foreach (var p in _coord.Settings.Profiles)
        {
            var active = p.Name == _coord.Settings.ActiveProfileName ? " [ACTIVE]" : "";
            ProfilesList.Items.Add(new ListBoxItem { Tag = p.Name, Content = $"{p.Name}{active} — {p.Description}" });
        }
    }

    private void OnActivateProfile(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is ListBoxItem { Tag: string name })
        {
            _coord.ApplyProfile(name);
            _loading = true;
            LoadFromSettings();
            _loading = false;
        }
    }

    private void OnCopyApiToken(object sender, RoutedEventArgs e)
    {
        WpfClipboard.SetText(_coord.Settings.LocalApiToken);
        WpfMessageBox.Show("Local API Bearer token copied to clipboard.", "Ripple", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnInstallAgentHook(object sender, RoutedEventArgs e)
    {
        var path = _coord.Notchy.InstallClaudeHookScript();
        WpfMessageBox.Show($"Installed hook script at:\n{path}", "Notchy Hook", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExportBundle(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Ripple Bundle (*.json)|*.json", FileName = "ripple-backup.json" };
        if (dlg.ShowDialog() == true)
        {
            _coord.SettingsStore.ExportBundle(_coord.Settings, dlg.FileName);
        }
    }

    private void OnImportBundle(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Ripple Bundle (*.json)|*.json" };
        if (dlg.ShowDialog() == true)
        {
            _coord.Settings = _coord.SettingsStore.ImportBundle(dlg.FileName);
            _loading = true;
            LoadFromSettings();
            _loading = false;
            _repositionNotch();
        }
    }

    private void OnRefreshDoctor(object sender, RoutedEventArgs e) =>
        TxtDoctorReport.Text = _coord.BuildDiagnosticsReport();

    private void OnReplayTour(object sender, RoutedEventArgs e) => _openOnboarding();

    private void OnSaveClose(object sender, RoutedEventArgs e)
    {
        _coord.SettingsStore.Save(_coord.Settings);
        Close();
    }
}
