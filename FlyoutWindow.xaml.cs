using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FastRes;

/// <summary>The Windows 11-style flyout that pops up above the tray icon.</summary>
public partial class FlyoutWindow : Window
{
    private readonly Settings _settings;
    private readonly IntPtr _hwnd;
    private List<MonitorInfo> _monitors = new();
    private string? _selectedDevice;
    private Native.RECT _anchor;
    private uint _edge = Native.ABE_BOTTOM;
    private bool _busy;
    private DateTime _lastHidden = DateTime.MinValue;

    /// <summary>Applies a mode for a display. Supplied by the app controller.</summary>
    public Func<MonitorInfo, DisplayMode, Task>? ApplyHandler { get; set; }

    /// <summary>Turns HDR on/off for a display. Supplied by the app controller.</summary>
    public Func<ColorInfo, bool, Task>? HdrHandler { get; set; }

    /// <summary>Sets output bits per channel through the GPU driver. Supplied by the app controller.</summary>
    public Func<MonitorInfo, int, Task>? OutputDepthHandler { get; set; }

    /// <summary>Shows the options menu at a screen point. Supplied by the app controller.</summary>
    public Action<IntPtr, Native.POINT, bool>? OptionsHandler { get; set; }

    public FlyoutWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();

        SourceInitialized += (_, _) => Theme.ApplyWindowEffects(this);
        Deactivated += (_, _) => { if (!_busy) HideFlyout(); };
        PreviewKeyDown += OnPreviewKeyDown;
        SizeChanged += (_, _) => { if (IsVisible) Reposition(); };
        // The flyout is reused for the app's lifetime: Alt+F4 just hides it.
        Closing += (_, e) =>
        {
            if (App.IsExiting) return;
            e.Cancel = true;
            HideFlyout();
        };

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
    }

    public IntPtr Handle => _hwnd;

    /// <summary>True right after the flyout lost focus — e.g. because the tray icon itself was clicked.</summary>
    private bool WasJustHidden => (DateTime.UtcNow - _lastHidden).TotalMilliseconds < 400;

    public void Toggle(Native.RECT anchor)
    {
        if (IsVisible) { HideFlyout(); return; }
        if (WasJustHidden) return;
        ShowAt(anchor);
    }

    public void ShowAt(Native.RECT anchor)
    {
        _anchor = anchor;
        HideError();
        Reload();

        Theme.Cloak(_hwnd, true);
        if (!IsVisible) Show();

        // Hop onto the target monitor first so WPF adopts its DPI before we measure.
        var work = MonitorAt(anchor, out _).rcWork;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, work.Left, work.Top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        UpdateLayout();
        Reposition();

        Native.SetForegroundWindow(_hwnd);
        Activate();
        Theme.Cloak(_hwnd, false);

        AnimateIn();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusCurrentResolution);
    }

    public void HideFlyout()
    {
        if (!IsVisible) return;
        _lastHidden = DateTime.UtcNow;
        Hide();
    }

    /// <summary>Re-reads displays and modes (e.g. after a change made elsewhere).</summary>
    public void Reload()
    {
        _monitors = Displays.GetMonitors(_settings.ShowAllResolutions);
        _selectedDevice = _monitors.FirstOrDefault(m => m.DeviceName == (_selectedDevice ?? _settings.LastDevice))?.DeviceName
                          ?? _monitors.FirstOrDefault(m => m.IsPrimary)?.DeviceName
                          ?? _monitors.FirstOrDefault()?.DeviceName;
        Bind();
    }

    public void OnDisplaySettingsChanged()
    {
        if (!IsVisible || _busy) return;
        Reload();
        Reposition();
    }

    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBar.Visibility = Visibility.Collapsed;

    private MonitorInfo? Selected => _monitors.FirstOrDefault(m => m.DeviceName == _selectedDevice);

    private void Bind()
    {
        MonitorList.Visibility = _monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        MonitorList.ItemsSource = _monitors.Select(m => new MonitorItem(
            m.DeviceName, m.Number.ToString(), m.ShortName, m.DeviceName == _selectedDevice,
            $"Display {m.Number}{(m.IsPrimary ? " (main)" : "")} — {m.ShortName}")).ToList();

        var mon = Selected;
        if (mon is null)
        {
            CurrentText.Text = "No display found";
            DetailText.Text = string.Empty;
            ResList.ItemsSource = null;
            RateList.ItemsSource = null;
            return;
        }

        var cur = mon.Current;
        CurrentText.Text = $"{cur.Width} × {cur.Height}";
        DetailText.Text = string.Join("  ·  ", new[]
        {
            $"{cur.Hz} Hz", Displays.AspectRatio(cur.Width, cur.Height), mon.ShortName,
        }.Where(s => !string.IsNullOrEmpty(s)));

        ResList.ItemsSource = mon.Resolutions.Select(r => new ResolutionItem(
            r.Width, r.Height, $"{r.Width} × {r.Height}", Displays.AspectRatio(r.Width, r.Height),
            mon.Preferred is { } p && p.Width == r.Width && p.Height == r.Height,
            r.Width == cur.Width && r.Height == cur.Height)).ToList();

        RateList.ItemsSource = mon.RatesFor(cur.Width, cur.Height)
            .Select(hz => new RateItem(hz, $"{hz} Hz", hz == cur.Hz)).ToList();

        BindColor(mon);
    }

    private void BindColor(MonitorInfo mon)
    {
        var items = new List<ColorItem>();

        // Output bits per channel via the GPU driver (NVIDIA / AMD / Intel).
        var depth = mon.OutputDepth;
        bool depthSwitchable = depth is { SupportedBpc.Count: > 1 };
        if (depthSwitchable)
        {
            string vendor = OutputDepthControl.Name(depth!.Vendor);
            foreach (int bpc in depth.SupportedBpc)
                items.Add(new ColorItem(ColorKind.OutputBpc, bpc, $"{bpc}-bit", bpc == depth.CurrentBpc,
                    $"{bpc} bits per colour channel (set through the {vendor} driver)"));
        }

        // Classic QuickRes-style desktop colour depths — only modern drivers that still expose them.
        if (mon.ColorDepths.Count > 1)
        {
            foreach (int bpp in mon.ColorDepths)
            {
                string label = bpp switch
                {
                    32 => "True Color (32-bit)",
                    24 => "True Color (24-bit)",
                    16 => "High Color (16-bit)",
                    8 => "256 Colors (8-bit)",
                    _ => $"{bpp}-bit",
                };
                items.Add(new ColorItem(ColorKind.Depth, bpp, label, bpp == mon.CurrentBpp, $"{bpp} bits per pixel desktop"));
            }
        }

        var c = mon.Color;
        if (c is { HdrSupported: true })
        {
            items.Add(new ColorItem(ColorKind.Sdr, 0, "SDR", !c.HdrOn, "Standard dynamic range"));
            items.Add(new ColorItem(ColorKind.Hdr, 1, "HDR", c.HdrOn, "High dynamic range (usually 10-bit output)"));
        }

        var detail = new List<string>();
        if (depthSwitchable)
        {
            detail.Add($"Output depth via {OutputDepthControl.Name(depth!.Vendor)} driver");
            if (c is { Encoding.Length: > 0 }) detail.Add(c.Encoding);
        }
        else if (c is { BitsPerChannel: > 0 })
        {
            detail.Add($"{c.BitsPerChannel}-bit per channel output");
            if (!string.IsNullOrEmpty(c.Encoding)) detail.Add(c.Encoding);
        }
        if (c is { HdrLimitedByPolicy: true }) detail.Add("HDR limited by power settings");

        // Bit-depth buttons on one row, SDR/HDR on the next.
        var depthItems = items.Where(i => i.Kind is ColorKind.OutputBpc or ColorKind.Depth).ToList();
        var rangeItems = items.Except(depthItems).ToList();
        DepthList.ItemsSource = depthItems;
        DepthList.Visibility = depthItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ColorList.ItemsSource = rangeItems;
        ColorList.Visibility = rangeItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ColorDetail.Text = string.Join("  ·  ", detail);
        ColorDetail.Visibility = detail.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ColorSection.Visibility = items.Count > 0 || detail.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────────── Interaction ─────────────────────────────

    private async void Resolution_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } mon || (sender as FrameworkElement)?.DataContext is not ResolutionItem item || item.IsCurrent)
            return;
        int hz = mon.BestRateFor(item.Width, item.Height, mon.Current.Hz);
        await RequestApply(mon, new DisplayMode(item.Width, item.Height, hz));
    }

    private async void Rate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } mon || (sender as FrameworkElement)?.DataContext is not RateItem item || item.IsCurrent)
            return;
        await RequestApply(mon, mon.Current with { Hz = item.Hz });
    }

    private async void Color_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } mon || (sender as FrameworkElement)?.DataContext is not ColorItem item || item.IsCurrent)
            return;
        if (item.Kind == ColorKind.OutputBpc)
        {
            if (OutputDepthHandler is { } setDepth) await RunBusy(() => setDepth(mon, item.Value));
        }
        else if (item.Kind == ColorKind.Depth)
            await RequestApply(mon, mon.Current with { Bpp = item.Value });
        else if (mon.Color is { } color && HdrHandler is { } setHdr)
            await RunBusy(() => setHdr(color, item.Kind == ColorKind.Hdr));
    }

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MonitorItem item) return;
        _selectedDevice = item.DeviceName;
        _settings.LastDevice = item.DeviceName;
        _settings.Save();
        HideError();
        Bind();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollCurrentIntoView(focus: false));
    }

    private Task RequestApply(MonitorInfo mon, DisplayMode mode) =>
        ApplyHandler is { } apply ? RunBusy(() => apply(mon, mode)) : Task.CompletedTask;

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Body.IsEnabled = false;
        HideError();
        try
        {
            await action();
        }
        finally
        {
            _busy = false;
            Body.IsEnabled = true;
        }
        if (IsVisible)
        {
            Reload();
            Reposition();
            if (!IsActive) Activate();
        }
    }

    private void DisplaySettings_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        App.OpenDisplaySettings();
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var pt = MoreButton.PointToScreen(new Point(MoreButton.ActualWidth, 0));
        OptionsHandler?.Invoke(_hwnd, new Native.POINT((int)pt.X, (int)pt.Y), true);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideFlyout();
            e.Handled = true;
        }
    }

    // ───────────────────────────── Placement ─────────────────────────────

    private static Native.MONITORINFOEX MonitorAt(Native.RECT anchor, out IntPtr hMonitor)
    {
        var center = new Native.POINT((anchor.Left + anchor.Right) / 2, (anchor.Top + anchor.Bottom) / 2);
        hMonitor = Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST);
        var mi = Native.MONITORINFOEX.Create();
        Native.GetMonitorInfo(hMonitor, ref mi);
        return mi;
    }

    /// <summary>Places the flyout next to the taskbar, centred on the tray icon (physical pixels throughout).</summary>
    public void Reposition()
    {
        if (!Native.GetWindowRect(_hwnd, out var wr)) return;
        int w = wr.Width, h = wr.Height;

        var mi = MonitorAt(_anchor, out var hMonitor);
        var work = mi.rcWork;
        if (Native.GetDpiForMonitor(hMonitor, 0, out uint dpi, out _) != 0) dpi = 96;
        int margin = (int)Math.Round(12 * dpi / 96.0);

        // Which edge is the taskbar on (for this monitor)?
        _edge = Native.ABE_BOTTOM;
        var bar = work;
        var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>() };
        if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) != UIntPtr.Zero)
        {
            var c = new Native.POINT((abd.rc.Left + abd.rc.Right) / 2, (abd.rc.Top + abd.rc.Bottom) / 2);
            if (Native.MonitorFromPoint(c, Native.MONITOR_DEFAULTTONEAREST) == hMonitor)
            {
                _edge = abd.uEdge;
                bar = abd.rc;
            }
        }

        int ax = (_anchor.Left + _anchor.Right) / 2;
        int ay = (_anchor.Top + _anchor.Bottom) / 2;
        int ClampX(int x) => Math.Max(work.Left + margin, Math.Min(x, work.Right - margin - w));
        int ClampY(int y) => Math.Max(work.Top + margin, Math.Min(y, work.Bottom - margin - h));

        int px, py;
        switch (_edge)
        {
            case Native.ABE_TOP:
                px = ClampX(ax - w / 2);
                py = Math.Max(bar.Bottom, work.Top) + margin;
                break;
            case Native.ABE_LEFT:
                px = Math.Max(bar.Right, work.Left) + margin;
                py = ClampY(ay - h / 2);
                break;
            case Native.ABE_RIGHT:
                px = Math.Min(bar.Left, work.Right) - margin - w;
                py = ClampY(ay - h / 2);
                break;
            default:
                px = ClampX(ax - w / 2);
                py = Math.Min(bar.Top, work.Bottom) - margin - h;
                break;
        }

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, px, py, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void AnimateIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        double from = _edge switch
        {
            Native.ABE_TOP => -24,
            Native.ABE_BOTTOM => 24,
            _ => 0,
        };
        Slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        double fromX = _edge switch { Native.ABE_LEFT => -24, Native.ABE_RIGHT => 24, _ => 0 };
        Slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        Root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    private void FocusCurrentResolution() => ScrollCurrentIntoView(focus: true);

    private void ScrollCurrentIntoView(bool focus)
    {
        if (ResList.ItemsSource is not IEnumerable<ResolutionItem> items) return;
        var current = items.FirstOrDefault(i => i.IsCurrent);
        if (current is null || ResList.ItemContainerGenerator.ContainerFromItem(current) is not FrameworkElement container)
        {
            ResScroll.ScrollToTop();
            return;
        }

        var top = container.TransformToAncestor(ResList).Transform(new Point(0, 0)).Y;
        ResScroll.ScrollToVerticalOffset(Math.Max(0, top - (ResScroll.ViewportHeight - container.ActualHeight) / 2));

        if (focus && VisualTreeHelper.GetChildrenCount(container) > 0 && VisualTreeHelper.GetChild(container, 0) is Button b)
            b.Focus();
    }
}
