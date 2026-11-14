using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ripple.App.ViewModels;
using Ripple.Core.Services;
using WpfClipboard = System.Windows.Clipboard;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Ripple.App.Views;

public partial class CommandPaletteWindow : Window
{
    private readonly RippleCoordinator _coord;
    private readonly Action _openSettings;
    private readonly Action _openFullscreenMusic;
    private readonly Action _openOnboarding;

    public CommandPaletteWindow(
        RippleCoordinator coord,
        Action openSettings,
        Action openFullscreenMusic,
        Action openOnboarding)
    {
        InitializeComponent();
        _coord = coord;
        _openSettings = openSettings;
        _openFullscreenMusic = openFullscreenMusic;
        _openOnboarding = openOnboarding;

        Loaded += (_, _) =>
        {
            UpdateResults(string.Empty);
            QueryBox.Focus();
        };
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => UpdateResults(QueryBox.Text);

    private void UpdateResults(string q)
    {
        var items = CommandPaletteEngine.Search(q, _coord.Settings.QuickApps);
        ResultList.ItemsSource = items;
        if (items.Count > 0) ResultList.SelectedIndex = 0;
    }

    private void OnWindowKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.Enter && ResultList.SelectedItem is CommandPaletteItem item)
        {
            ExecuteItem(item);
        }
        else if (e.Key == Key.Down && ResultList.Items.Count > 0)
        {
            ResultList.SelectedIndex = Math.Min(ResultList.Items.Count - 1, ResultList.SelectedIndex + 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && ResultList.Items.Count > 0)
        {
            ResultList.SelectedIndex = Math.Max(0, ResultList.SelectedIndex - 1);
            e.Handled = true;
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultList.SelectedItem is CommandPaletteItem item)
        {
            ExecuteItem(item);
        }
    }

    private void ExecuteItem(CommandPaletteItem item)
    {
        switch (item.Kind)
        {
            case CommandResultKind.Calculator:
            case CommandResultKind.UnitConversion:
                WpfClipboard.SetText(item.ExecutePayload);
                break;

            case CommandResultKind.AppLaunch:
            case CommandResultKind.WebSearch:
                try { Process.Start(new ProcessStartInfo(item.ExecutePayload) { UseShellExecute = true }); } catch { }
                break;

            case CommandResultKind.RippleAction:
                switch (item.ExecutePayload)
                {
                    case "settings": _openSettings(); break;
                    case "fullscreen_music": _openFullscreenMusic(); break;
                    case "timer:25": _coord.FocusTimer.StartTimer(25, "Pomodoro"); break;
                    case "motion_tuner": _coord.IsMotionTunerVisible = true; _coord.StateMachine.HandleTrigger(Ripple.Core.StateMachine.NotchInteractionTrigger.PinToggle); break;
                    case "onboarding": _openOnboarding(); break;
                    case "doctor":
                        System.Windows.MessageBox.Show(_coord.BuildDiagnosticsReport(), "Ripple Notch Doctor", MessageBoxButton.OK, MessageBoxImage.Information);
                        break;
                }
                break;
        }

        Close();
    }
}
