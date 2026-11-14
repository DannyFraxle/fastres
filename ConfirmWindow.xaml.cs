using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FastRes;

/// <summary>"Keep these display settings?" prompt that reverts automatically if not confirmed.</summary>
public partial class ConfirmWindow : Window
{
    public static ConfirmWindow? Current { get; private set; }

    private readonly TaskCompletionSource<bool> _result = new();
    private readonly DispatcherTimer _timer;
    private readonly string _device;
    private readonly int _seconds;
    private int _remaining;
    private bool _decided;
    private bool _closed;

    private ConfirmWindow(string deviceName, string message, int seconds)
    {
        InitializeComponent();
        _device = deviceName;
        _seconds = _remaining = seconds;

        ModeText.Text = message;
        UpdateCountdown();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0) Decide(false);
            else UpdateCountdown();
        };

        SourceInitialized += (_, _) => Theme.ApplyWindowEffects(this);
        Closed += (_, _) =>
        {
            _closed = true;
            Decide(false);
        };
    }

    /// <summary>Shows the prompt centred on the given display; completes with true to keep the new mode.</summary>
    public static Task<bool> AskAsync(MonitorInfo monitor, DisplayMode mode, int seconds = 15)
    {
        string depth = mode.Bpp > 0 && mode.Bpp != monitor.CurrentBpp ? $" in {mode.Bpp}-bit color" : string.Empty;
        return AskAsync(monitor.DeviceName,
            $"{monitor.ShortName} is now using {mode.Width} × {mode.Height} at {mode.Hz} Hz{depth}.", seconds);
    }

    /// <summary>Shows the prompt with a custom message, centred on the given display (GDI device name).</summary>
    public static Task<bool> AskAsync(string deviceName, string message, int seconds = 15)
    {
        var window = new ConfirmWindow(deviceName, message, seconds);
        Current = window;
        window.ShowCentered();
        return window._result.Task;
    }

    public void BringToFront()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetForegroundWindow(hwnd);
        Activate();
    }

    private void ShowCentered()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Theme.Cloak(hwnd, true);
        Show();

        var work = WorkAreaOf(_device);
        Native.SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        UpdateLayout();
        if (Native.GetWindowRect(hwnd, out var r))
        {
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST,
                work.Left + (work.Width - r.Width) / 2, work.Top + (work.Height - r.Height) / 2, 0, 0,
                Native.SWP_NOSIZE);
        }

        Native.SetForegroundWindow(hwnd);
        Activate();
        KeepButton.Focus();
        Theme.Cloak(hwnd, false);

        Progress.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromSeconds(_seconds)));
        _timer.Start();
    }

    private static Native.RECT WorkAreaOf(string device)
    {
        Native.RECT? found = null;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = Native.MONITORINFOEX.Create();
            if (Native.GetMonitorInfo(hMon, ref mi) && string.Equals(mi.szDevice, device, StringComparison.OrdinalIgnoreCase))
            {
                found = mi.rcWork;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (found is { } rect) return rect;

        var primary = Native.MONITORINFOEX.Create();
        Native.GetMonitorInfo(Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTONEAREST), ref primary);
        return primary.rcWork;
    }

    private void UpdateCountdown() =>
        CountdownText.Text = $"Reverting to your previous settings in {_remaining} second{(_remaining == 1 ? "" : "s")}.";

    private void Keep_Click(object sender, RoutedEventArgs e) => Decide(true);

    private void Revert_Click(object sender, RoutedEventArgs e) => Decide(false);

    private void Decide(bool keep)
    {
        if (_decided) return;
        _decided = true;
        _timer.Stop();
        if (Current == this) Current = null;
        _result.TrySetResult(keep);
        if (!_closed) Close();
    }
}
