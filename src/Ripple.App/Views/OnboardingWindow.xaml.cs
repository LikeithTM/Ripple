using System.Windows;
using Ripple.App.ViewModels;
using Ripple.Core.Events;
using Ripple.Core.StateMachine;

namespace Ripple.App.Views;

public partial class OnboardingWindow : Window
{
    private readonly RippleCoordinator _coord;
    private int _step;

    private static readonly (string Icon, string Title, string Body, string ActionLabel)[] Steps =
    [
        ("✦", "Hover to Bloom, Safe Corridor Built In",
         "Move your cursor near the black notch at the top of your screen. It gently swells into Peek mode, then smoothly expands on hover — and never collapses when moving diagonally toward controls.",
         "Expand Notch Now"),
        ("📥", "Drag & Drop Anything onto the Shelf",
         "Drag files, folders, images, or links straight onto the top notch! It expands magnetically to hold them temporarily so you can Zip, Convert to PNG, Copy Path, or drag back out anywhere.",
         "Demo Drop Activity"),
        ("⌘", "Instant Hotkeys & Per-App Control",
         "Press Ctrl+Alt+N anytime to toggle/pin the notch, or Ctrl+Alt+Space for the Command Palette (math, unit conversions, quick apps). Scroll your mouse wheel over the notch to adjust master volume.",
         "Test Volume HUD"),
        ("🤖", "Notchy AI Agents & Fullscreen Protection",
         "Ripple automatically hides in fullscreen games/videos, dodges out of the way of window title-bar buttons, and surfaces live AI agent prompts (Approve / Deny) via local hooks and `ripple` CLI.",
         "Demo Agent Pulse")
    ];

    public OnboardingWindow(RippleCoordinator coord)
    {
        InitializeComponent();
        _coord = coord;
        RenderStep();
    }

    private void RenderStep()
    {
        var s = Steps[_step];
        StepBadge.Text = $"STEP {_step + 1} OF {Steps.Length}";
        StepIcon.Text = s.Icon;
        StepTitle.Text = s.Title;
        StepBody.Text = s.Body;
        TryActionBtn.Content = s.ActionLabel;
        NextBtn.Content = _step == Steps.Length - 1 ? "Get Started ✓" : "Next →";
    }

    private void OnTryActionClicked(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 0:
                _coord.StateMachine.HandleTrigger(NotchInteractionTrigger.PinToggle);
                break;
            case 1:
                _coord.EventBus.Publish(new LiveActivityPayload("tour:drop", "shelf", LiveActivityKind.Generic, "Drop Shelf Ready", "Drag any file onto the notch", "📥", "#38BDF8"));
                break;
            case 2:
                _coord.EventBus.Publish(new LiveActivityPayload("tour:vol", "mixer", LiveActivityKind.VolumeHud, "Volume 72%", "Scroll wheel controls master volume", "🔊", "#6C8CFF", Progress: 0.72));
                break;
            case 3:
                _coord.Notchy.HandleAgentEvent(new Ripple.Core.Services.ApiAgentEventRequest("Claude Code", "permission", "Run unit tests?", "dotnet test Ripple.sln", "tour-act", 1240));
                break;
        }
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        if (_step < Steps.Length - 1)
        {
            _step++;
            RenderStep();
        }
        else
        {
            _coord.Settings.OnboardingCompleted = true;
            _coord.SettingsStore.Save(_coord.Settings);
            Close();
        }
    }

    private void OnSkipClicked(object sender, RoutedEventArgs e)
    {
        _coord.Settings.OnboardingCompleted = true;
        _coord.SettingsStore.Save(_coord.Settings);
        Close();
    }
}
