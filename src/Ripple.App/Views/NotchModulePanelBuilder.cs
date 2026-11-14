using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Ripple.App.Services;
using Ripple.App.ViewModels;
using Ripple.Core.Motion;
using Ripple.Core.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfThickness = System.Windows.Thickness;

namespace Ripple.App.Views;

/// <summary>
/// Builds rich, responsive interactive WPF views for all 11 Ripple modules,
/// plus the live MotionTuner overlay and loaded SDK plugin cards.
/// </summary>
public static class NotchModulePanelBuilder
{
    private static readonly System.Windows.Media.FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static UIElement BuildForModule(
        string moduleId,
        RippleCoordinator coord,
        Action openFullscreenMusic,
        Action openCommandPalette,
        Action openSettings)
    {
        if (coord.IsMotionTunerVisible)
        {
            return BuildMotionTunerView(coord);
        }

        return moduleId switch
        {
            "music" => BuildMusicView(coord, openFullscreenMusic),
            "system" => BuildSystemView(coord),
            "bluetooth" => BuildBluetoothView(coord),
            "clipboard" => BuildClipboardView(coord),
            "shelf" => BuildShelfView(coord),
            "apps" => BuildQuickAppsView(coord, openCommandPalette),
            "timer" => BuildTimerView(coord),
            "mixer" => BuildMixerView(coord),
            "notifications" => BuildNotificationsView(coord),
            "notchy" => BuildNotchyView(coord),
            "utilities" => BuildUtilitiesView(coord),
            _ => BuildMusicView(coord, openFullscreenMusic)
        };
    }

    private static UIElement BuildMusicView(RippleCoordinator coord, Action openFullscreenMusic)
    {
        var snap = coord.Media.Snapshot;
        var accent = new SolidColorBrush(snap.PrimaryColor);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top row: Artwork + Metadata + Source/Fullscreen buttons
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var artBorder = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(WpfColor.FromArgb(65, snap.PrimaryColor.R, snap.PrimaryColor.G, snap.PrimaryColor.B)),
            Margin = new WpfThickness(0, 0, 12, 0),
            ClipToBounds = true
        };
        if (snap.Artwork is not null)
        {
            artBorder.Child = new Image { Source = snap.Artwork, Stretch = Stretch.UniformToFill };
        }
        else
        {
            artBorder.Child = new TextBlock { Text = "♫", FontSize = 26, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        top.Children.Add(artBorder);

        var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        meta.Children.Add(new TextBlock { Text = snap.Title, Foreground = WpfBrushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        meta.Children.Add(new TextBlock { Text = $"{snap.Artist} • {snap.SourceApp}", Foreground = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184)), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new WpfThickness(0, 2, 0, 0) });

        if (!string.IsNullOrWhiteSpace(snap.CurrentLyric))
        {
            meta.Children.Add(new TextBlock { Text = $"♪ {snap.CurrentLyric}", Foreground = accent, FontSize = 11, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new WpfThickness(0, 3, 0, 0) });
        }

        // Live inline audio visualizer bars (driven by EqualizerBands while expanded)
        var eqPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = string.IsNullOrWhiteSpace(snap.CurrentLyric) ? 18 : 14,
            Margin = new WpfThickness(0, 4, 0, 0),
            Tag = "eq"
        };
        for (var i = 0; i < 18; i++)
        {
            var barH = Math.Max(3, coord.Media.EqualizerBands[i % coord.Media.EqualizerBands.Count] * (string.IsNullOrWhiteSpace(snap.CurrentLyric) ? 16 : 12));
            eqPanel.Children.Add(new Border
            {
                Width = 3,
                Height = barH,
                CornerRadius = new CornerRadius(1.5),
                Background = accent,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new WpfThickness(0, 0, 2, 0)
            });
        }
        meta.Children.Add(eqPanel);

        Grid.SetColumn(meta, 1);
        top.Children.Add(meta);

        var rightBtns = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var fsBtn = MakeIconButton("\uE740", "Immersive", () => openFullscreenMusic(), "#24FFFFFF");
        var likeBtn = MakeIconButton(coord.Media.IsLiked ? "\uEB52" : "\uEB51", coord.Media.IsLiked ? "Liked" : "Like", () => coord.Media.ToggleLike(), "#1AFFFFFF");
        likeBtn.Margin = new WpfThickness(0, 4, 0, 0);
        rightBtns.Children.Add(fsBtn);
        rightBtns.Children.Add(likeBtn);
        Grid.SetColumn(rightBtns, 2);
        top.Children.Add(rightBtns);
        root.Children.Add(top);

        // Middle row: Scrubbable seek slider + timestamps
        var seekGrid = new Grid { Margin = new WpfThickness(0, 8, 0, 6) };
        seekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        seekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        seekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var posTxt = new TextBlock { Text = FormatSpan(snap.Position), Foreground = WpfBrushes.Silver, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
        var durTxt = new TextBlock { Text = FormatSpan(snap.Duration), Foreground = WpfBrushes.Silver, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = Math.Max(1, snap.Duration.TotalSeconds),
            Value = Math.Clamp(snap.Position.TotalSeconds, 0, Math.Max(1, snap.Duration.TotalSeconds)),
            Margin = new WpfThickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.PreviewMouseLeftButtonUp += (_, _) => _ = coord.Media.SeekAsync(TimeSpan.FromSeconds(slider.Value));

        Grid.SetColumn(posTxt, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(durTxt, 2);
        seekGrid.Children.Add(posTxt);
        seekGrid.Children.Add(slider);
        seekGrid.Children.Add(durTxt);
        Grid.SetRow(seekGrid, 1);
        root.Children.Add(seekGrid);

        // Bottom row: Transport + Source switcher
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new WpfThickness(0, 2, 0, 0) };
        controls.Children.Add(MakeIconButton("\uE8B1", snap.IsShuffle ? "On" : null, () => _ = coord.Media.ToggleShuffleAsync(), snap.IsShuffle ? "#336C8CFF" : "#16FFFFFF"));
        controls.Children.Add(MarginLeft(MakeIconButton("\uE892", null, () => _ = coord.Media.PreviousAsync(), "#1FFFFFFF"), 6));
        controls.Children.Add(MarginLeft(MakeIconButton(snap.IsPlaying ? "\uE769" : "\uE768", snap.IsPlaying ? "Pause" : "Play", () => _ = coord.Media.TogglePlayPauseAsync(), "#38FFFFFF"), 6));
        controls.Children.Add(MarginLeft(MakeIconButton("\uE893", null, () => _ = coord.Media.NextAsync(), "#1FFFFFFF"), 6));
        if (snap.AvailableSources.Count > 1)
        {
            foreach (var src in snap.AvailableSources.Take(2))
            {
                controls.Children.Add(MarginLeft(MakeSmallButton(src, () => coord.Media.SelectSource(src), src == snap.SourceApp ? "#336C8CFF" : "#14FFFFFF"), 6));
            }
        }
        Grid.SetRow(controls, 2);
        root.Children.Add(controls);

        return root;
    }

    private static UIElement BuildSystemView(RippleCoordinator coord)
    {
        var s = coord.SystemMonitor.Snapshot;
        var root = new StackPanel();

        // Metrics row
        var metrics = new UniformGrid { Columns = 4, Margin = new WpfThickness(0, 0, 0, 8) };
        metrics.Children.Add(MakeStatCard("CPU", $"{s.CpuPercent:F0}%", "#6C8CFF"));
        metrics.Children.Add(MakeStatCard("RAM", $"{s.RamPercent:F0}%", "#A78BFA", $"{s.RamUsedGb:F1}/{s.RamTotalGb:F0} GB"));
        metrics.Children.Add(MakeStatCard("NET ↓/↑", $"{s.NetworkDownMbps:F1}M", "#22D3EE", $"↑ {s.NetworkUpMbps:F1} MB/s"));
        metrics.Children.Add(MakeStatCard("BATTERY", $"{s.BatteryPercent}%", s.IsCharging ? "#34D399" : "#F59E0B", s.IsCharging ? "Charging" : $"Disk {s.DiskActivePercent:F0}%"));
        root.Children.Add(metrics);

        // 60s CPU Sparkline
        var sparkBorder = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(WpfColor.FromArgb(24, 255, 255, 255)),
            Padding = new WpfThickness(6, 4, 6, 4),
            Margin = new WpfThickness(0, 0, 0, 8)
        };
        sparkBorder.Child = BuildSparkline(s.CpuHistory, WpfColor.FromRgb(108, 140, 255), 390, 24);
        root.Children.Add(sparkBorder);

        // Top 3 resource processes with confirmed End Task
        var header = new TextBlock { Text = "Top Processes (Click End to terminate)", Foreground = WpfBrushes.Silver, FontSize = 10.5, Margin = new WpfThickness(0, 0, 0, 4) };
        root.Children.Add(header);

        foreach (var proc in s.TopProcesses.Take(3))
        {
            var row = new Grid { Margin = new WpfThickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock { Text = $"{proc.Name} (PID {proc.Pid})", Foreground = WpfBrushes.White, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            var stat = new TextBlock { Text = $"{proc.CpuPercent:F1}% • {proc.MemoryMb:F0} MB", Foreground = WpfBrushes.Silver, FontSize = 11, Margin = new WpfThickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(stat, 1);
            row.Children.Add(stat);

            var endBtn = MakeSmallButton("End", () =>
            {
                var confirm = System.Windows.MessageBox.Show($"End process '{proc.Name}' (PID {proc.Pid})?", "Ripple Task Control", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm == MessageBoxResult.Yes)
                {
                    coord.SystemMonitor.TryEndProcess(proc.Pid, out _);
                }
            }, "#33EF4444");
            Grid.SetColumn(endBtn, 2);
            row.Children.Add(endBtn);
            root.Children.Add(row);
        }

        return root;
    }

    private static UIElement BuildBluetoothView(RippleCoordinator coord)
    {
        var root = new StackPanel();
        var topRow = new Grid { Margin = new WpfThickness(0, 0, 0, 6) };
        topRow.Children.Add(new TextBlock { Text = "Bluetooth Devices & Audio Outputs", Foreground = WpfBrushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold });
        var refreshBtn = MakeSmallButton("↻ Refresh", () => _ = coord.Bluetooth.RefreshDevicesAsync(), "#1FFFFFFF");
        refreshBtn.HorizontalAlignment = HorizontalAlignment.Right;
        topRow.Children.Add(refreshBtn);
        root.Children.Add(topRow);

        var scroll = new ScrollViewer { MaxHeight = 128, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var list = new StackPanel();

        if (coord.Bluetooth.Devices.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "No paired Bluetooth peripherals detected (or Bluetooth radio is off).",
                Foreground = WpfBrushes.Silver,
                FontSize = 11.5,
                Margin = new WpfThickness(0, 6, 0, 6)
            });
        }
        else
        {
            foreach (var d in coord.Bluetooth.Devices.Take(6))
            {
                var row = new Border
                {
                    Background = new SolidColorBrush(WpfColor.FromArgb(d.IsConnected ? (byte)36 : (byte)16, 255, 255, 255)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new WpfThickness(8, 5, 8, 5),
                    Margin = new WpfThickness(0, 2, 0, 2)
                };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = $"{d.Icon} {d.Name}", Foreground = WpfBrushes.White, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
                var status = d.BatteryPercent is { } b ? $"{(d.IsConnected ? "Connected" : "Paired")} • {b}%" : (d.IsConnected ? "Connected" : "Paired");
                var st = new TextBlock { Text = status, Foreground = d.IsConnected ? new SolidColorBrush(WpfColor.FromRgb(56, 189, 248)) : WpfBrushes.Silver, FontSize = 11 };
                Grid.SetColumn(st, 1);
                g.Children.Add(st);
                row.Child = g;
                list.Children.Add(row);
            }
        }

        if (coord.Bluetooth.OutputDevices.Count > 0)
        {
            list.Children.Add(new TextBlock { Text = "Active Audio Endpoints:", Foreground = WpfBrushes.Silver, FontSize = 10.5, Margin = new WpfThickness(0, 6, 0, 2) });
            foreach (var od in coord.Bluetooth.OutputDevices.Take(3))
            {
                list.Children.Add(new TextBlock
                {
                    Text = $"{(od.IsDefault ? "●" : "○")} {od.Name}",
                    Foreground = od.IsDefault ? new SolidColorBrush(WpfColor.FromRgb(52, 211, 153)) : WpfBrushes.Silver,
                    FontSize = 11
                });
            }
        }

        scroll.Content = list;
        root.Children.Add(scroll);
        return root;
    }

    private static UIElement BuildClipboardView(RippleCoordinator coord)
    {
        var root = new StackPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new WpfThickness(0, 0, 0, 6) };
        bar.Children.Add(MakeSmallButton(coord.Clipboard.IsIncognito ? "🕵 Incognito ON" : "🕵 Incognito", () => coord.Clipboard.IsIncognito = !coord.Clipboard.IsIncognito, coord.Clipboard.IsIncognito ? "#44A78BFA" : "#1EFFFFFF"));
        bar.Children.Add(MarginLeft(MakeSmallButton("🗑 Clear Unpinned", () => coord.Clipboard.ClearUnpinned(), "#24EF4444"), 6));
        bar.Children.Add(MarginLeft(new TextBlock { Text = $"{coord.Clipboard.Entries.Count} items (AES-256)", Foreground = WpfBrushes.Silver, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center }, 8));
        root.Children.Add(bar);

        var scroll = new ScrollViewer { MaxHeight = 132, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var list = new StackPanel();

        if (coord.Clipboard.Entries.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "Copy text, URLs, #hex colors, math (12*4.5), JSON, or 6-digit OTPs — Ripple detects smart actions automatically.", Foreground = WpfBrushes.Silver, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            foreach (var entry in coord.Clipboard.Entries.Take(10))
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(WpfColor.FromArgb(24, 255, 255, 255)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new WpfThickness(8, 5, 8, 5),
                    Margin = new WpfThickness(0, 2, 0, 2)
                };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel();
                var preview = entry.Text.Replace('\r', ' ').Replace('\n', ' ');
                if (preview.Length > 52) preview = preview[..52] + "…";
                left.Children.Add(new TextBlock { Text = preview, Foreground = WpfBrushes.White, FontSize = 11.5 });
                left.Children.Add(new TextBlock { Text = $"{entry.Analysis.BadgeText} • {entry.SourceApp}", Foreground = new SolidColorBrush(WpfColor.FromRgb(108, 140, 255)), FontSize = 10 });
                g.Children.Add(left);

                var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                btns.Children.Add(MakeSmallButton(entry.Analysis.ActionLabel, () => coord.Clipboard.CopyEntry(entry), "#286C8CFF"));
                btns.Children.Add(MarginLeft(MakeSmallButton(entry.IsPinned ? "★" : "☆", () => coord.Clipboard.TogglePin(entry.Id), "#1AFFFFFF"), 4));
                Grid.SetColumn(btns, 1);
                g.Children.Add(btns);

                card.Child = g;
                list.Children.Add(card);
            }
        }

        scroll.Content = list;
        root.Children.Add(scroll);
        return root;
    }

    private static UIElement BuildShelfView(RippleCoordinator coord)
    {
        var root = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new WpfThickness(0, 0, 0, 6) };
        top.Children.Add(new TextBlock { Text = $"Drop Shelf ({coord.Shelf.Items.Count} held)", Foreground = WpfBrushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(MarginLeft(MakeSmallButton("🗜 Zip All", () => coord.Shelf.ZipItems(coord.Shelf.Items), "#246C8CFF"), 10));
        top.Children.Add(MarginLeft(MakeSmallButton("Clear", () => coord.Shelf.ClearAll(), "#24EF4444"), 6));
        root.Children.Add(top);

        var scroll = new ScrollViewer { MaxHeight = 128, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var list = new StackPanel();

        if (coord.Shelf.Items.Count == 0)
        {
            list.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(80, 108, 140, 255)),
                BorderThickness = new WpfThickness(1.5),
                CornerRadius = new CornerRadius(12),
                Padding = new WpfThickness(14),
                Child = new TextBlock
                {
                    Text = "Drag & drop files, folders, images, text, or URLs onto the notch anytime to hold them here.",
                    Foreground = WpfBrushes.Silver,
                    FontSize = 11.5,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }
        else
        {
            foreach (var item in coord.Shelf.Items.Take(8))
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(WpfColor.FromArgb(24, 255, 255, 255)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new WpfThickness(8, 5, 8, 5),
                    Margin = new WpfThickness(0, 2, 0, 2)
                };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = $"{(item.Kind == ShelfItemKind.Folder ? "📁" : item.Kind == ShelfItemKind.Image ? "🖼" : "📄")} {item.DisplayName}", Foreground = WpfBrushes.White, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

                var acts = new StackPanel { Orientation = Orientation.Horizontal };
                acts.Children.Add(MakeSmallButton("Open", () => coord.Shelf.OpenItem(item), "#1FFFFFFF"));
                acts.Children.Add(MarginLeft(MakeSmallButton("Copy Path", () => System.Windows.Clipboard.SetText(item.Payload), "#1FFFFFFF"), 4));
                if (item.Kind == ShelfItemKind.Image)
                {
                    acts.Children.Add(MarginLeft(MakeSmallButton("→PNG", () => coord.Shelf.ConvertImageToPng(item), "#2638BDF8"), 4));
                }
                acts.Children.Add(MarginLeft(MakeSmallButton("✕", () => coord.Shelf.RemoveItem(item.Id), "#22EF4444"), 4));
                Grid.SetColumn(acts, 1);
                g.Children.Add(acts);
                card.Child = g;
                list.Children.Add(card);
            }
        }

        scroll.Content = list;
        root.Children.Add(scroll);
        return root;
    }

    private static UIElement BuildQuickAppsView(RippleCoordinator coord, Action openPalette)
    {
        var root = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new WpfThickness(0, 0, 0, 8) };
        top.Children.Add(new TextBlock { Text = "Quick Apps & Launcher", Foreground = WpfBrushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(MarginLeft(MakeSmallButton("⌘ Open Command Palette", openPalette, "#336C8CFF"), 10));
        root.Children.Add(top);

        var wrap = new WrapPanel();
        foreach (var app in coord.QuickApps.Apps)
        {
            var running = coord.QuickApps.IsAppRunning(app);
            var btn = new Button
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(running ? (byte)48 : (byte)24, 255, 255, 255)),
                Foreground = WpfBrushes.White,
                BorderThickness = new WpfThickness(0),
                Padding = new WpfThickness(12, 8, 12, 8),
                Margin = new WpfThickness(0, 0, 8, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{app.IconGlyph}  {app.Name}", FontSize = 12, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = running ? "● Running" : app.Category, FontSize = 9.5, Foreground = running ? new SolidColorBrush(WpfColor.FromRgb(52, 211, 153)) : WpfBrushes.Silver }
                    }
                }
            };
            btn.Click += (_, _) => coord.QuickApps.Launch(app);
            wrap.Children.Add(btn);
        }
        root.Children.Add(wrap);
        return root;
    }

    private static UIElement BuildTimerView(RippleCoordinator coord)
    {
        var ft = coord.FocusTimer;
        var root = new StackPanel();

        var top = new Grid { Margin = new WpfThickness(0, 0, 0, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var clockTxt = new TextBlock
        {
            Text = $"{(int)ft.Remaining.TotalMinutes:D2}:{ft.Remaining.Seconds:D2}",
            Foreground = WpfBrushes.White,
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        top.Children.Add(clockTxt);

        var controls = new WrapPanel { Margin = new WpfThickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(MakeSmallButton(ft.IsRunning ? "⏸ Pause" : "▶ Start", () => ft.TogglePause(), "#386C8CFF"));
        controls.Children.Add(MarginLeft(MakeSmallButton("25m", () => ft.StartTimer(25, "Pomodoro"), "#1FFFFFFF"), 4));
        controls.Children.Add(MarginLeft(MakeSmallButton("5m", () => ft.StartTimer(5, "Short Break"), "#1FFFFFFF"), 4));
        controls.Children.Add(MarginLeft(MakeSmallButton("Stopwatch", () => ft.StartStopwatch(), "#1FFFFFFF"), 4));
        controls.Children.Add(MarginLeft(MakeSmallButton(ft.FocusModeEnabled ? "🌙 Focus ON" : "🌙 Focus", () => ft.FocusModeEnabled = !ft.FocusModeEnabled, ft.FocusModeEnabled ? "#44A78BFA" : "#1AFFFFFF"), 4));
        Grid.SetColumn(controls, 1);
        top.Children.Add(controls);
        root.Children.Add(top);

        if (ft.NextMeeting is { } mtg)
        {
            var mins = (int)Math.Ceiling((mtg.StartTime - DateTimeOffset.Now).TotalMinutes);
            var cal = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(30, 108, 140, 255)),
                CornerRadius = new CornerRadius(10),
                Padding = new WpfThickness(8, 5, 8, 5),
                Margin = new WpfThickness(0, 0, 0, 6)
            };
            var cg = new Grid();
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cg.Children.Add(new TextBlock { Text = $"📅 {mtg.Title} (in {mins}m)", Foreground = WpfBrushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            var joinBtn = MakeSmallButton("Join Call", () => ft.JoinNextMeeting(), "#4434D399");
            Grid.SetColumn(joinBtn, 1);
            cg.Children.Add(joinBtn);
            cal.Child = cg;
            root.Children.Add(cal);
        }

        // Daily To-Do List
        var todoInputRow = new Grid { Margin = new WpfThickness(0, 0, 0, 4) };
        todoInputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        todoInputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox
        {
            Background = new SolidColorBrush(WpfColor.FromArgb(24, 255, 255, 255)),
            Foreground = WpfBrushes.White,
            BorderThickness = new WpfThickness(0),
            Padding = new WpfThickness(6, 3, 6, 3),
            FontSize = 11
        };
        var addBtn = MakeSmallButton("+ Task", () => { ft.AddTodo(box.Text); box.Clear(); }, "#28FFFFFF");
        addBtn.Margin = new WpfThickness(4, 0, 0, 0);
        Grid.SetColumn(addBtn, 1);
        todoInputRow.Children.Add(box);
        todoInputRow.Children.Add(addBtn);
        root.Children.Add(todoInputRow);

        foreach (var t in ft.Todos.Take(3))
        {
            var cb = new CheckBox
            {
                Content = t.Title,
                IsChecked = t.IsDone,
                Foreground = t.IsDone ? WpfBrushes.Gray : WpfBrushes.White,
                FontSize = 11,
                Margin = new WpfThickness(0, 2, 0, 2)
            };
            cb.Click += (_, _) => ft.ToggleTodo(t.Id);
            root.Children.Add(cb);
        }

        return root;
    }

    private static UIElement BuildMixerView(RippleCoordinator coord)
    {
        var root = new StackPanel();
        var masterRow = new Grid { Margin = new WpfThickness(0, 0, 0, 8) };
        masterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        masterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        masterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var masterText = new TextBlock
        {
            Text = $"🔊 Master {(int)Math.Round(coord.Mixer.MasterVolume * 100)}%",
            Foreground = WpfBrushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        masterRow.Children.Add(masterText);

        var mSlider = new Slider { Minimum = 0, Maximum = 1, Value = coord.Mixer.MasterVolume, VerticalAlignment = VerticalAlignment.Center, Margin = new WpfThickness(6, 0, 6, 0) };
        mSlider.ValueChanged += (_, e) =>
        {
            coord.AudioMixer.SetMasterVolume(e.NewValue);
            masterText.Text = $"🔊 Master {(int)Math.Round(e.NewValue * 100)}%";
        };
        Grid.SetColumn(mSlider, 1);
        masterRow.Children.Add(mSlider);

        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(MakeIconButton(
            coord.Mixer.IsMasterMuted ? "\uE74F" : "\uE767",
            coord.Mixer.IsMasterMuted ? "Unmute" : "Mute",
            () =>
            {
                coord.AudioMixer.ToggleMasterMute();
                coord.Mixer.RefreshNow();
            },
            coord.Mixer.IsMasterMuted ? "#44EF4444" : "#28EF4444"));
        btns.Children.Add(MarginLeft(MakeSmallButton(
            coord.AudioMixer.IsDuckOthersActive ? "Duck ON" : "Duck",
            () =>
            {
                coord.AudioMixer.ToggleDuckOthers();
                coord.Mixer.RefreshNow();
            },
            coord.AudioMixer.IsDuckOthersActive ? "#446C8CFF" : "#1EFFFFFF"), 4));
        Grid.SetColumn(btns, 2);
        masterRow.Children.Add(btns);
        root.Children.Add(masterRow);

        var scroll = new ScrollViewer { MaxHeight = 128, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var list = new StackPanel();
        if (coord.Mixer.Sessions.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "No active per-app audio sessions right now.", Foreground = WpfBrushes.Silver, FontSize = 11.5 });
        }
        else
        {
            var activeBrush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#34D399"));
            activeBrush.Freeze();
            var mutedBrush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#94A3B8"));
            mutedBrush.Freeze();

            foreach (var app in coord.Mixer.Sessions)
            {
                var isPlaying = !app.IsMuted && (app.IsActive || app.PeakLevel > 0.005);
                var r = new Grid { Margin = new WpfThickness(0, 3, 0, 3) };
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(115) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                r.Children.Add(new TextBlock
                {
                    Text = isPlaying ? $"● {app.AppName}" : app.AppName,
                    Foreground = app.IsMuted ? mutedBrush : (isPlaying ? activeBrush : WpfBrushes.White),
                    FontSize = 11.5,
                    FontWeight = isPlaying ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                var pctText = new TextBlock
                {
                    Text = $"{(int)Math.Round(app.Volume * 100)}%",
                    Foreground = WpfBrushes.Silver,
                    FontSize = 10.5,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new WpfThickness(0, 0, 6, 0)
                };

                var sl = new Slider { Minimum = 0, Maximum = 1, Value = app.Volume, Margin = new WpfThickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                sl.ValueChanged += (_, e) =>
                {
                    coord.AudioMixer.SetAppVolume(app.ProcessId, e.NewValue);
                    pctText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
                };
                Grid.SetColumn(sl, 1);
                r.Children.Add(sl);

                Grid.SetColumn(pctText, 2);
                r.Children.Add(pctText);

                var muteBtn = MakeIconButton(
                    app.IsMuted ? "\uE74F" : "\uE767",
                    null,
                    () =>
                    {
                        coord.AudioMixer.ToggleAppMute(app.ProcessId);
                        coord.Mixer.RefreshNow();
                    },
                    app.IsMuted ? "#33EF4444" : "#1EFFFFFF");
                Grid.SetColumn(muteBtn, 3);
                r.Children.Add(muteBtn);
                list.Children.Add(r);
            }
        }
        scroll.Content = list;
        root.Children.Add(scroll);
        return root;
    }

    private static UIElement BuildNotificationsView(RippleCoordinator coord)
    {
        var root = new StackPanel();
        var top = new Grid { Margin = new WpfThickness(0, 0, 0, 6) };
        top.Children.Add(new TextBlock { Text = coord.Notifications.ListenerStatus, Foreground = WpfBrushes.Silver, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 310 });
        var clearBtn = MakeSmallButton("Clear All", () => coord.Notifications.ClearAll(), "#24EF4444");
        clearBtn.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(clearBtn);
        root.Children.Add(top);

        var scroll = new ScrollViewer { MaxHeight = 128, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var list = new StackPanel();
        if (coord.Notifications.History.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "No notifications yet. Local API & agent events appear here automatically.", Foreground = WpfBrushes.Silver, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            foreach (var n in coord.Notifications.History.Take(8))
            {
                var card = new Border { Background = new SolidColorBrush(WpfColor.FromArgb(24, 255, 255, 255)), CornerRadius = new CornerRadius(10), Padding = new WpfThickness(8, 5, 8, 5), Margin = new WpfThickness(0, 2, 0, 2) };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{n.AppName} • {n.Title}", Foreground = WpfBrushes.White, FontSize = 11.5, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = n.Body, Foreground = WpfBrushes.Silver, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis }
                    }
                });
                var acts = new StackPanel { Orientation = Orientation.Horizontal };
                acts.Children.Add(MakeSmallButton("Mute App", () => coord.Notifications.MuteApp(n.AppName), "#1AFFFFFF"));
                acts.Children.Add(MarginLeft(MakeSmallButton("✕", () => coord.Notifications.Dismiss(n.Id), "#22EF4444"), 4));
                Grid.SetColumn(acts, 1);
                g.Children.Add(acts);
                card.Child = g;
                list.Children.Add(card);
            }
        }
        scroll.Content = list;
        root.Children.Add(scroll);
        return root;
    }

    private static UIElement BuildNotchyView(RippleCoordinator coord)
    {
        var root = new StackPanel();
        var isLive = string.Equals(coord.Notchy.LiveWatcherStatus, "Live", StringComparison.OrdinalIgnoreCase);

        var liveColor = WpfColor.FromRgb(52, 211, 153);
        var skyColor = WpfColor.FromRgb(56, 189, 248);
        var mutedColor = WpfColor.FromRgb(100, 116, 139);
        var subColor = WpfColor.FromRgb(148, 163, 184);

        var top = new Grid { Margin = new WpfThickness(0, 0, 0, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerMeta = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new WpfThickness(0, 0, 8, 0) };
        headerMeta.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(isLive ? liveColor : mutedColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new WpfThickness(0, 0, 7, 0)
        });
        headerMeta.Children.Add(new TextBlock
        {
            Text = "AI Agent",
            Foreground = WpfBrushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        headerMeta.Children.Add(new TextBlock
        {
            Text = $" · {coord.Notchy.LiveWatcherStatus}",
            Foreground = new SolidColorBrush(isLive ? skyColor : subColor),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (!string.IsNullOrWhiteSpace(coord.Notchy.GitRepoSummary))
        {
            var gitPill = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(22, 255, 255, 255)),
                CornerRadius = new CornerRadius(8),
                Padding = new WpfThickness(6, 2, 6, 2),
                Margin = new WpfThickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = coord.Notchy.GitRepoSummary,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(203, 213, 225)),
                    FontSize = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            headerMeta.Children.Add(gitPill);
        }
        top.Children.Add(headerMeta);

        var hookBtn = MakeSmallButton("CLI Hook", () =>
        {
            var path = coord.Notchy.InstallClaudeHookScript();
            coord.EventBus.Publish(new Ripple.Core.Events.LiveActivityPayload("hook:ok", "notchy", Ripple.Core.Events.LiveActivityKind.Generic, "Hook Script Installed", path, "✓", "#34D399"));
        }, "#18FFFFFF");
        Grid.SetColumn(hookBtn, 1);
        hookBtn.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(hookBtn);
        root.Children.Add(top);

        var scroll = new ScrollViewer { MaxHeight = 136, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        var list = new StackPanel();

        var ordered = coord.Notchy.Sessions
            .OrderBy(s => s.Status switch
            {
                AgentRunStatus.PermissionNeeded => 0,
                AgentRunStatus.Running => 1,
                AgentRunStatus.Error => 2,
                AgentRunStatus.Completed => 3,
                _ => 4
            })
            .Take(3);

        foreach (var s in ordered)
        {
            var (statusColor, badgeText) = s.Status switch
            {
                AgentRunStatus.PermissionNeeded => (WpfColor.FromRgb(245, 158, 11), "APPROVAL"),
                AgentRunStatus.Running => (WpfColor.FromRgb(34, 211, 238), "RUNNING"),
                AgentRunStatus.Completed => (WpfColor.FromRgb(52, 211, 153), "DONE"),
                AgentRunStatus.Error => (WpfColor.FromRgb(239, 68, 68), "ERROR"),
                _ => (WpfColor.FromRgb(100, 116, 139), "IDLE")
            };

            var card = new Border
            {
                Background = new SolidColorBrush(s.Status == AgentRunStatus.PermissionNeeded
                    ? WpfColor.FromArgb(40, 245, 158, 11)
                    : WpfColor.FromArgb(20, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(s.Status == AgentRunStatus.PermissionNeeded
                    ? WpfColor.FromArgb(110, 245, 158, 11)
                    : WpfColor.FromArgb(26, 255, 255, 255)),
                BorderThickness = new WpfThickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new WpfThickness(11, 7, 11, 7),
                Margin = new WpfThickness(0, 0, 0, 5)
            };

            var cardBody = new StackPanel();
            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(statusColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new WpfThickness(0, 0, 8, 0)
            };
            topRow.Children.Add(dot);

            var nameTxt = new TextBlock
            {
                Text = s.AgentName,
                Foreground = WpfBrushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameTxt, 1);
            topRow.Children.Add(nameTxt);

            var cleanHead = AgentAndDevService.CleanHeadline(s);
            if (!string.IsNullOrWhiteSpace(cleanHead))
            {
                var headTxt = new TextBlock
                {
                    Text = $"  ·  {cleanHead}",
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(226, 232, 240)),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(headTxt, 2);
                topRow.Children.Add(headTxt);
            }

            if (s.Status == AgentRunStatus.PermissionNeeded)
            {
                var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new WpfThickness(8, 0, 0, 0) };
                btns.Children.Add(MakeSmallButton("Approve", () => coord.Notchy.ResolveApproval(s.AgentName, true), "#4434D399"));
                btns.Children.Add(MarginLeft(MakeSmallButton("Deny", () => coord.Notchy.ResolveApproval(s.AgentName, false), "#44EF4444"), 4));
                Grid.SetColumn(btns, 3);
                topRow.Children.Add(btns);
            }
            else
            {
                var pill = new Border
                {
                    Background = new SolidColorBrush(WpfColor.FromArgb(38, statusColor.R, statusColor.G, statusColor.B)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new WpfThickness(6, 2, 6, 2),
                    Margin = new WpfThickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = badgeText,
                        Foreground = new SolidColorBrush(statusColor),
                        FontSize = 9.5,
                        FontWeight = FontWeights.Bold
                    }
                };
                Grid.SetColumn(pill, 3);
                topRow.Children.Add(pill);
            }

            cardBody.Children.Add(topRow);

            if (!string.IsNullOrWhiteSpace(s.Detail))
            {
                cardBody.Children.Add(new TextBlock
                {
                    Text = s.Detail.Replace("`", ""),
                    Foreground = new SolidColorBrush(subColor),
                    FontSize = 11,
                    Margin = new WpfThickness(16, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            card.Child = cardBody;
            list.Children.Add(card);
        }

        scroll.Content = list;
        root.Children.Add(scroll);
        return root;
    }

    private static UIElement BuildUtilitiesView(RippleCoordinator coord)
    {
        var root = new StackPanel();
        var grid = new UniformGrid { Columns = 3, Margin = new WpfThickness(0, 0, 0, 8) };
        grid.Children.Add(MakeSmallButton("✂ Region Capture", () => UtilitiesService.LaunchRegionScreenshot(), "#24FFFFFF"));
        grid.Children.Add(MakeSmallButton("🎨 Eyedropper", () => coord.Utilities.PickColorAtCursor(), "#24FFFFFF"));
        grid.Children.Add(MakeSmallButton("📸 Snap to Shelf", () => coord.Utilities.CaptureQuickScreenshotToShelf(coord.Shelf), "#24FFFFFF"));
        grid.Children.Add(MakeSmallButton(coord.Utilities.IsKeepAwake ? "☕ Awake ON" : "☕ Keep Awake", () => coord.Utilities.ToggleKeepAwake(), coord.Utilities.IsKeepAwake ? "#44F59E0B" : "#24FFFFFF"));
        grid.Children.Add(MakeSmallButton("🌗 Toggle Dark Mode", () => coord.Utilities.ToggleDarkMode(), "#24FFFFFF"));
        grid.Children.Add(MakeSmallButton(coord.Utilities.IsMicMuted ? "🎙✕ Mic Muted" : "🎙 Mute Mic", () => coord.Utilities.ToggleMicMute(), coord.Utilities.IsMicMuted ? "#44EF4444" : "#24FFFFFF"));
        root.Children.Add(grid);

        var infoRow = new Grid();
        infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var clocks = new StackPanel();
        clocks.Children.Add(new TextBlock { Text = $"🌍 UTC {DateTimeOffset.UtcNow:HH:mm}  |  NYC {TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Eastern Standard Time"):HH:mm}  |  TYO {TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Tokyo Standard Time"):HH:mm}", Foreground = WpfBrushes.White, FontSize = 11 });
        clocks.Children.Add(new TextBlock { Text = $"☁ {coord.Utilities.WeatherSummary} • Last Color: {coord.Utilities.LastPickedColorHex}", Foreground = WpfBrushes.Silver, FontSize = 10.5, Margin = new WpfThickness(0, 4, 0, 0) });
        infoRow.Children.Add(clocks);

        // Quick QR preview for active clipboard or URL
        var qrText = coord.Clipboard.Entries.FirstOrDefault()?.Text ?? "https://github.com/ripple";
        var qrImg = new Image
        {
            Source = UtilitiesService.GenerateDeterministicMatrixBitmap(qrText),
            Width = 54,
            Height = 54,
            ToolTip = "QR Matrix for latest clipboard item"
        };
        Grid.SetColumn(qrImg, 1);
        infoRow.Children.Add(qrImg);
        root.Children.Add(infoRow);
        return root;
    }

    private static UIElement BuildMotionTunerView(RippleCoordinator coord)
    {
        var root = new StackPanel();
        var top = new Grid { Margin = new WpfThickness(0, 0, 0, 6) };
        top.Children.Add(new TextBlock { Text = "∿ MotionTuner (Spring Physics & Frame-Time Graph)", Foreground = new SolidColorBrush(WpfColor.FromRgb(56, 189, 248)), FontSize = 12, FontWeight = FontWeights.SemiBold });
        var closeBtn = MakeSmallButton("Done", () => coord.IsMotionTunerVisible = false, "#33EF4444");
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(closeBtn);
        root.Children.Add(top);

        root.Children.Add(MakeSliderRow("Expand Stiffness", 120, 700, coord.Settings.ExpandStiffness, v => coord.Settings.ExpandStiffness = v));
        root.Children.Add(MakeSliderRow("Expand Damping", 10, 60, coord.Settings.ExpandDamping, v => coord.Settings.ExpandDamping = v));
        root.Children.Add(MakeSliderRow("Expand Mass", 0.4, 2.5, coord.Settings.ExpandMass, v => coord.Settings.ExpandMass = v));

        var avgMs = coord.FrameTimesMs.Count > 0 ? coord.FrameTimesMs.Average() : 16.6;
        root.Children.Add(new TextBlock { Text = $"Frame-Time History (avg {avgMs:F2} ms / ~{(avgMs > 0.1 ? 1000 / avgMs : 60):F0} Hz)", Foreground = WpfBrushes.Silver, FontSize = 10.5, Margin = new WpfThickness(0, 4, 0, 2) });
        root.Children.Add(BuildSparkline(coord.FrameTimesMs, WpfColor.FromRgb(52, 211, 153), 400, 32));
        return root;
    }

    private static UIElement MakeSliderRow(string label, double min, double max, double val, Action<double> onChanged)
    {
        var g = new Grid { Margin = new WpfThickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

        var valTxt = new TextBlock { Text = $"{val:F1}", Foreground = WpfBrushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        var sl = new Slider { Minimum = min, Maximum = max, Value = val };
        sl.ValueChanged += (_, e) => { onChanged(e.NewValue); valTxt.Text = $"{e.NewValue:F1}"; };

        g.Children.Add(new TextBlock { Text = label, Foreground = WpfBrushes.Silver, FontSize = 11 });
        Grid.SetColumn(sl, 1);
        Grid.SetColumn(valTxt, 2);
        g.Children.Add(sl);
        g.Children.Add(valTxt);
        return g;
    }

    private static UIElement MakeStatCard(string title, string value, string hex, string? sub = null)
    {
        var c = (WpfColor)ColorConverter.ConvertFromString(hex);
        var b = new Border
        {
            Background = new SolidColorBrush(WpfColor.FromArgb(24, 255, 255, 255)),
            CornerRadius = new CornerRadius(10),
            Padding = new WpfThickness(8, 6, 8, 6),
            Margin = new WpfThickness(2)
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Foreground = WpfBrushes.Silver, FontSize = 9.5, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(new TextBlock { Text = value, Foreground = new SolidColorBrush(c), FontSize = 15, FontWeight = FontWeights.Bold });
        if (!string.IsNullOrEmpty(sub))
        {
            sp.Children.Add(new TextBlock { Text = sub, Foreground = WpfBrushes.Silver, FontSize = 9.5 });
        }
        b.Child = sp;
        return b;
    }

    private static Polyline BuildSparkline(IReadOnlyList<double> values, WpfColor color, double width, double height)
    {
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.6,
            Width = width,
            Height = height
        };
        if (values.Count < 2) return line;
        var max = Math.Max(1.0, values.Max());
        for (var i = 0; i < values.Count; i++)
        {
            var x = (double)i / (values.Count - 1) * width;
            var y = height - (Math.Clamp(values[i] / max, 0, 1) * (height - 2)) - 1;
            line.Points.Add(new WpfPoint(x, y));
        }
        return line;
    }

    private static WpfButton MakeIconButton(string glyph, string? label, Action onClick, string bgHex)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrEmpty(label))
        {
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new WpfThickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        return MakeSmallButton(panel, onClick, bgHex);
    }

    private static WpfButton MakeSmallButton(object content, Action onClick, string bgHex)
    {
        var c = (WpfColor)ColorConverter.ConvertFromString(bgHex);
        var bg = new SolidColorBrush(c);
        bg.Freeze();
        var btn = new WpfButton
        {
            Content = content,
            Background = bg,
            Foreground = WpfBrushes.White,
            BorderThickness = new WpfThickness(0),
            Padding = new WpfThickness(8, 4, 8, 4),
            Margin = new WpfThickness(2),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static T MarginLeft<T>(T el, double left) where T : FrameworkElement
    {
        el.Margin = new WpfThickness(left, el.Margin.Top, el.Margin.Right, el.Margin.Bottom);
        return el;
    }

    private static string FormatSpan(TimeSpan ts) =>
        ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
}
