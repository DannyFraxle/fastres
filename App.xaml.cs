using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FastRes;

public partial class App : Application
{
    private const string MutexName = @"Local\FastRes.SingleInstance";

    private enum MenuId
    {
        DisplaySettings = 1,
        ConfirmChanges,
        ShowAllResolutions,
        StartWithWindows,
        Exit,
    }

    public static bool IsExiting { get; private set; }

    private Mutex? _mutex;
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private Settings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--list", StringComparer.OrdinalIgnoreCase))
        {
            Console.Out.Write(DescribeDisplays());
            Console.Out.Flush();
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running: ask the existing instance to open its flyout.
            var existing = Native.FindWindow(null, TrayIcon.WindowTitle);
            if (existing != IntPtr.Zero)
            {
                Native.AllowSetForegroundWindow(-1);
                Native.PostMessage(existing, TrayIcon.WM_OPEN_FLYOUT, IntPtr.Zero, IntPtr.Zero);
            }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        _settings = Settings.Load();
        Theme.Refresh(Resources);

        _tray = new TrayIcon();
        _flyout = new FlyoutWindow(_settings)
        {
            ApplyHandler = ApplyAsync,
            HdrHandler = SetHdrAsync,
            OutputDepthHandler = SetOutputDepthAsync,
            OptionsHandler = ShowOptionsMenu,
        };

        _tray.Activated += rect =>
        {
            if (ConfirmWindow.Current is { } pending) pending.BringToFront();
            else _flyout.Toggle(rect);
        };
        _tray.ContextMenuRequested += pt => ShowOptionsMenu(_tray.Handle, pt, false);
        _tray.OpenRequested += () => _flyout.ShowAt(_tray.GetIconRect());
        _tray.DisplaySettingsChanged += () =>
        {
            RefreshTrayIcon();
            RefreshToolTip();
            _flyout.OnDisplaySettingsChanged();
        };
        _tray.ThemeChanged += () =>
        {
            Theme.Refresh(Resources);
            Theme.ApplyWindowEffects(_flyout);
            if (ConfirmWindow.Current is { } pending) Theme.ApplyWindowEffects(pending);
            RefreshTrayIcon();
        };

        RefreshTrayIcon();
        RefreshToolTip();
        _tray.Show();

        if (e.Args.Contains("--open", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _flyout.ShowAt(_tray.GetIconRect()));    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        _tray?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Switches a display to a new mode, then (optionally) asks the user to keep it.</summary>
    private async Task ApplyAsync(MonitorInfo monitor, DisplayMode mode)
    {
        var previous = monitor.Current with { Bpp = monitor.CurrentBpp };
        if (mode.Bpp == 0) mode = mode with { Bpp = previous.Bpp };
        if (mode == previous) return;

        var result = await Task.Run(() => Displays.Apply(monitor.DeviceName, mode));
        RefreshToolTip();

        if (!result.Success)
        {
            _flyout?.ShowError(result.Message);
            return;
        }
        if (result.NeedsRestart)
        {
            _flyout?.ShowError(result.Message);
            return;
        }

        if (!_settings.ConfirmChanges) return;

        _flyout?.HideFlyout();
        bool keep = await ConfirmWindow.AskAsync(monitor, mode);
        if (!keep)
        {
            var revert = await Task.Run(() => Displays.Apply(monitor.DeviceName, previous));
            if (!revert.Success)
                MessageBox.Show($"Couldn't restore {previous}.\n\n{revert.Message}", "FastRes",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshToolTip();
    }

    private async Task SetHdrAsync(ColorInfo color, bool enable)
    {
        var result = await Task.Run(() => Displays.SetHdr(color, enable));
        if (!result.Success) _flyout?.ShowError(result.Message);
    }

    /// <summary>Changes output bits per channel through the GPU driver, with the same keep/revert safety net.</summary>
    private async Task SetOutputDepthAsync(MonitorInfo monitor, int bpc)
    {
        if (monitor.Target is not { } target) return;
        int previous = monitor.OutputDepth?.CurrentBpc ?? 0;
        if (bpc == previous) return;

        var result = await Task.Run(() => OutputDepthControl.Set(target, bpc));
        if (!result.Success)
        {
            _flyout?.ShowError(result.Message);
            return;
        }
        if (!_settings.ConfirmChanges || previous == 0) return;

        _flyout?.HideFlyout();
        bool keep = await ConfirmWindow.AskAsync(monitor.DeviceName,
            $"{monitor.ShortName} is now receiving {bpc}-bit color (was {previous}-bit).");
        if (!keep)
        {
            var revert = await Task.Run(() => OutputDepthControl.Set(target, previous));
            if (!revert.Success)
                MessageBox.Show($"Couldn't restore {previous}-bit output.\n\n{revert.Message}", "FastRes",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowOptionsMenu(IntPtr owner, Native.POINT at, bool alignRight)
    {
        int choice = TrayIcon.ShowMenu(owner, at, new[]
        {
            new MenuEntry((int)MenuId.DisplaySettings, "Display settings"),
            MenuEntry.Separator,
            new MenuEntry((int)MenuId.ConfirmChanges, "Ask before keeping changes", _settings.ConfirmChanges),
            new MenuEntry((int)MenuId.ShowAllResolutions, "Show low resolutions", _settings.ShowAllResolutions),
            new MenuEntry((int)MenuId.StartWithWindows, "Start with Windows", Settings.StartWithWindows),
            MenuEntry.Separator,
            new MenuEntry((int)MenuId.Exit, "Exit FastRes"),
        }, alignRight);

        switch ((MenuId)choice)
        {
            case MenuId.DisplaySettings:
                _flyout?.HideFlyout();
                OpenDisplaySettings();
                break;
            case MenuId.ConfirmChanges:
                _settings.ConfirmChanges = !_settings.ConfirmChanges;
                _settings.Save();
                break;
            case MenuId.ShowAllResolutions:
                _settings.ShowAllResolutions = !_settings.ShowAllResolutions;
                _settings.Save();
                if (_flyout?.IsVisible == true)
                {
                    _flyout.Reload();
                    _flyout.Reposition();
                }
                break;
            case MenuId.StartWithWindows:
                try
                {
                    Settings.StartWithWindows = !Settings.StartWithWindows;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Couldn't update the startup setting.\n\n{ex.Message}", "FastRes",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                break;
            case MenuId.Exit:
                IsExiting = true;
                Shutdown();
                break;
        }
    }

    public static void OpenDisplaySettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
        }
        catch
        {
            // Settings app unavailable; nothing sensible to fall back to.
        }
    }

    private void RefreshTrayIcon()
    {
        if (_tray is null) return;
        _tray.SetIcon(IconRenderer.CreateTrayIcon(_tray.IconSize, Theme.TaskbarIsDark));
    }

    private void RefreshToolTip()
    {
        if (_tray is null) return;
        var modes = Displays.GetActiveModes();
        var sb = new StringBuilder("FastRes");
        foreach (var (number, _, mode) in modes)
        {
            sb.Append('\n');
            if (modes.Count > 1) sb.Append($"{number}: ");
            sb.Append($"{mode.Width} × {mode.Height} @ {mode.Hz} Hz");
        }
        _tray.SetToolTip(sb.ToString());
    }

    private static string DescribeDisplays()
    {
        var sb = new StringBuilder();
        foreach (var m in Displays.GetMonitors(includeLowResolutions: true))
        {
            sb.AppendLine($"{m.DeviceName}  #{m.Number}  \"{m.ShortName}\"{(m.IsPrimary ? "  [primary]" : "")}");
            sb.AppendLine($"  current:   {m.Current}");
            sb.AppendLine($"  preferred: {(m.Preferred is { } p ? $"{p.Width} × {p.Height}" : "unknown")}");
            sb.AppendLine($"  desktop depth: {m.CurrentBpp}-bit (available: {string.Join(", ", m.ColorDepths)})");
            sb.AppendLine(m.Color is { } c
                ? $"  output: {c.BitsPerChannel} bpc {c.Encoding}, HDR supported={c.HdrSupported} on={c.HdrOn}"
                : "  output: unknown");
            sb.AppendLine($"  gpu: {OutputDepthControl.Name(m.Target?.Vendor ?? GpuVendor.Unknown)}  " +
                (m.OutputDepth is { } d
                    ? $"driver output depth: {d.CurrentBpc}-bit (supports {string.Join("/", d.SupportedBpc)})"
                    : "driver output depth: not available"));
            foreach (var (w, h) in m.Resolutions)
                sb.AppendLine($"  {w,5} × {h,-5} {Displays.AspectRatio(w, h),-6} {string.Join(", ", m.RatesFor(w, h))} Hz");
        }
        return sb.ToString();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch
        {
            // Ignore logging failures.
        }
        e.Handled = true;
    }
}
