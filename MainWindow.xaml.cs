using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexMeter;

public partial class MainWindow : Window
{
    private readonly CodexUsageClient _client = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Forms.NotifyIcon _trayIcon;
    private bool _reallyClose;
    private bool _refreshing;
    private bool _initialPlacementComplete;

    public MainWindow()
    {
        InitializeComponent();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "Codex Meter",
            Visible = true,
            ContextMenuStrip = CreateTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ToggleVisibility();

        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        Activated += (_, _) => Opacity = 1.0;
        Deactivated += (_, _) => Opacity = 0.30;
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            PlaceAtBottomRight();
            _initialPlacementComplete = true;
        };
    }

    private Forms.ContextMenuStrip CreateTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (_, _) => Dispatcher.Invoke(ToggleVisibility));
        menu.Items.Add("立即刷新", null, async (_, _) => await Dispatcher.InvokeAsync(RefreshAsync));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _reallyClose = true;
            Close();
        }));
        return menu;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在刷新…";

        try
        {
            var usage = await _client.GetUsageAsync();
            Render(usage);
            StatusText.Text = $"更新于 {usage.FetchedAt:HH:mm}";
            PlanText.Text = FormatPlan(usage.PlanType);
            _trayIcon.Text = $"Codex Meter · {usage.Buckets[0].Primary?.RemainingPercent ?? 0}% 剩余";
        }
        catch (Exception ex)
        {
            RenderError(ex.Message);
            StatusText.Text = "读取失败 · 1 分钟后重试";
            PlanText.Text = string.Empty;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _refreshing = false;
        }

        if (_initialPlacementComplete)
            KeepInsideWorkArea();
    }

    private void Render(UsageSnapshot snapshot)
    {
        UsagePanel.Children.Clear();
        foreach (var bucket in snapshot.Buckets)
        {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            section.Children.Add(new TextBlock
            {
                Text = bucket.Name,
                Foreground = Brush("#FAFAFA"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 9)
            });

            if (bucket.Primary is not null)
                section.Children.Add(CreateWindowRow(bucket.Primary));
            if (bucket.Secondary is not null)
                section.Children.Add(CreateWindowRow(bucket.Secondary));
            UsagePanel.Children.Add(section);
        }
    }

    private UIElement CreateWindowRow(UsageWindow window)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = FormatWindow(window.WindowDurationMins),
            Foreground = Brush("#A1A1AA"),
            FontSize = 12
        };
        var value = new TextBlock
        {
            Text = $"剩余 {window.RemainingPercent}%",
            Foreground = RemainingBrush(window.RemainingPercent),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(value, 1);

        var track = new Border
        {
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = Brush("#3F3F46"),
            Margin = new Thickness(0, 6, 0, 0),
            ClipToBounds = true
        };
        var fill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = RemainingBrush(window.RemainingPercent),
            CornerRadius = new CornerRadius(4)
        };
        track.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * window.RemainingPercent / 100d;
        track.Child = fill;
        Grid.SetRow(track, 1);
        Grid.SetColumnSpan(track, 2);

        grid.Children.Add(label);
        grid.Children.Add(value);
        grid.Children.Add(track);
        grid.ToolTip = window.ResetTime is { } reset
            ? $"{reset.LocalDateTime:yyyy-MM-dd HH:mm} 重置"
            : "暂无重置时间";
        return grid;
    }

    private void RenderError(string message)
    {
        UsagePanel.Children.Clear();
        UsagePanel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Brush("#FDA4AF"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 4, 0, 6)
        });
    }

    private void PlaceAtBottomRight()
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        const double margin = 20;
        Left = Math.Max(area.Left + margin, area.Right - ActualWidth - margin);
        Top = Math.Max(area.Top + margin, area.Bottom - ActualHeight - margin);
    }

    private void KeepInsideWorkArea()
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        const double margin = 20;
        var minLeft = area.Left + margin;
        var minTop = area.Top + margin;
        var maxLeft = Math.Max(minLeft, area.Right - ActualWidth - margin);
        var maxTop = Math.Max(minTop, area.Bottom - ActualHeight - margin);

        Left = Math.Min(Math.Max(Left, minLeft), maxLeft);
        Top = Math.Min(Math.Max(Top, minTop), maxTop);
    }

    private void ToggleVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
            HideMainWindow();
        else
            ShowAndActivate();
    }

    internal void ShowAndActivate()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        if (!IsVisible)
            Show();

        if (_initialPlacementComplete)
            KeepInsideWorkArea();

        Topmost = false;
        Topmost = true;
        Activate();
        Focus();
        Opacity = 1.0;
        _trayIcon.Visible = true;
    }

    private void HideMainWindow()
    {
        Hide();
        _trayIcon.Visible = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClose)
        {
            e.Cancel = true;
            HideMainWindow();
            return;
        }
        _timer.Stop();
        _trayIcon.Dispose();
        base.OnClosing(e);
        Application.Current.Shutdown();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void HideButton_Click(object sender, RoutedEventArgs e) => HideMainWindow();

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not Button)
            DragMove();
    }

    private static string FormatWindow(long? minutes) => minutes switch
    {
        300 => "5 小时额度",
        10080 => "每周额度",
        > 0 when minutes % 1440 == 0 => $"{minutes / 1440} 天额度",
        > 0 when minutes % 60 == 0 => $"{minutes / 60} 小时额度",
        > 0 => $"{minutes} 分钟额度",
        _ => "当前额度"
    };

    private static string FormatPlan(string? plan) => plan?.ToLowerInvariant() switch
    {
        "plus" => "PLUS",
        "pro" => "PRO",
        "team" or "business" => "BUSINESS",
        "enterprise" => "ENTERPRISE",
        "edu" or "edu_plus" or "edu_pro" => "EDU",
        null => string.Empty,
        _ => plan.ToUpper(CultureInfo.InvariantCulture)
    };

    private static SolidColorBrush RemainingBrush(int percent) =>
        Brush(percent <= 15 ? "#FB7185" : percent <= 35 ? "#FBBF24" : "#51D88A");

    private static SolidColorBrush Brush(string value) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
}
