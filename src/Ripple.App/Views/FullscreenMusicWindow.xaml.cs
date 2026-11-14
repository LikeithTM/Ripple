using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ripple.App.ViewModels;
using WpfColor = System.Windows.Media.Color;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Ripple.App.Views;

public partial class FullscreenMusicWindow : Window
{
    private readonly RippleCoordinator _coord;
    private readonly DispatcherTimer _timer;
    private readonly List<Border> _bars = [];

    public FullscreenMusicWindow(RippleCoordinator coord)
    {
        InitializeComponent();
        _coord = coord;

        for (var i = 0; i < 24; i++)
        {
            var bar = new Border
            {
                Width = 10,
                Height = 12,
                CornerRadius = new CornerRadius(5),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 5, 0),
                Background = new SolidColorBrush(WpfColor.FromRgb(108, 140, 255))
            };
            VisualizerPanel.Children.Add(bar);
            _bars.Add(bar);
        }

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => RefreshUi();
        Loaded += (_, _) => { RefreshUi(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void RefreshUi()
    {
        var s = _coord.Media.Snapshot;
        SourceBadge.Text = s.SourceApp.ToUpperInvariant();
        TrackTitle.Text = s.Title;
        TrackArtist.Text = s.Artist;
        ArtworkImage.Source = s.Artwork;
        CurrentLyricText.Text = string.IsNullOrWhiteSpace(s.CurrentLyric) ? "♪ Instrumental / Live Audio Visualizer" : s.CurrentLyric;
        NextLyricText.Text = s.NextLyric;
        PlayPauseBtn.Content = s.IsPlaying ? "⏸ Pause" : "▶ Play";

        AuroraStop1.Color = WpfColor.FromArgb(115, s.PrimaryColor.R, s.PrimaryColor.G, s.PrimaryColor.B);
        AuroraStop2.Color = WpfColor.FromArgb(65, s.SecondaryColor.R, s.SecondaryColor.G, s.SecondaryColor.B);

        var bands = _coord.Media.EqualizerBands;
        for (var i = 0; i < _bars.Count; i++)
        {
            var level = bands[i % bands.Count];
            _bars[i].Height = Math.Max(8, level * 68);
            _bars[i].Background = new SolidColorBrush(i % 2 == 0 ? s.PrimaryColor : s.SecondaryColor);
        }
    }

    private void OnKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Space) _ = _coord.Media.TogglePlayPauseAsync();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
    private void OnPrevClicked(object sender, RoutedEventArgs e) => _ = _coord.Media.PreviousAsync();
    private void OnPlayPauseClicked(object sender, RoutedEventArgs e) => _ = _coord.Media.TogglePlayPauseAsync();
    private void OnNextClicked(object sender, RoutedEventArgs e) => _ = _coord.Media.NextAsync();
}
