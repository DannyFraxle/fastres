using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace FastRes;

/// <summary>Follows the Windows light/dark mode and accent colour, and applies Windows 11 window effects.</summary>
internal static class Theme
{
    public static bool IsDark { get; private set; } = true;
    public static bool TaskbarIsDark { get; private set; } = true;

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE (acrylic/mica) is available from Windows 11 22H2.</summary>
    public static bool BackdropSupported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Rounded window corners are available from Windows 11 21H2.</summary>
    public static bool RoundedCornersSupported => Environment.OSVersion.Version.Build >= 22000;

    public static void Refresh(ResourceDictionary res)
    {
        const string personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        IsDark = Registry.GetValue(personalize, "AppsUseLightTheme", 1) is int apps && apps == 0;
        TaskbarIsDark = !(Registry.GetValue(personalize, "SystemUsesLightTheme", 0) is int sys && sys == 1);

        var (accentDark, accentLight) = ReadAccent();
        var accent = IsDark ? accentLight : accentDark;

        void Set(string key, Color c) => res[key] = Frozen(c);
        void SetHex(string key, uint argb) => Set(key, FromArgb(argb));

        Set("AccentFill", accent);
        Set("AccentFillHover", WithAlpha(accent, 0xE6));
        Set("AccentFillPressed", WithAlpha(accent, 0xCC));
        res["AccentColor"] = accent;

        if (IsDark)
        {
            SetHex("TextPrimary", 0xFFFFFFFF);
            SetHex("TextSecondary", 0xC5FFFFFF);
            SetHex("TextTertiary", 0x87FFFFFF);
            SetHex("TextOnAccent", 0xFF000000);
            SetHex("SubtleHover", 0x0FFFFFFF);
            SetHex("SubtlePressed", 0x0AFFFFFF);
            SetHex("SubtleSelected", 0x12FFFFFF);
            SetHex("ControlFill", 0x0FFFFFFF);
            SetHex("ControlFillHover", 0x15FFFFFF);
            SetHex("ControlFillPressed", 0x08FFFFFF);
            SetHex("ControlStroke", 0x12FFFFFF);
            SetHex("Divider", 0x15FFFFFF);
            SetHex("FooterFill", 0x29000000);
            SetHex("CardFill", 0x0DFFFFFF);
            SetHex("ScrollThumb", 0x8BFFFFFF);
            SetHex("ErrorText", 0xFFFF99A4);
            SetHex("ErrorFill", 0x33F1707B);
            SetHex("SolidBackground", 0xFF2C2C2C);
            SetHex("FocusStroke", 0xFFFFFFFF);
        }
        else
        {
            SetHex("TextPrimary", 0xE4000000);
            SetHex("TextSecondary", 0x9E000000);
            SetHex("TextTertiary", 0x72000000);
            SetHex("TextOnAccent", 0xFFFFFFFF);
            SetHex("SubtleHover", 0x09000000);
            SetHex("SubtlePressed", 0x06000000);
            SetHex("SubtleSelected", 0x0C000000);
            SetHex("ControlFill", 0xB3FFFFFF);
            SetHex("ControlFillHover", 0x80F9F9F9);
            SetHex("ControlFillPressed", 0x4DF9F9F9);
            SetHex("ControlStroke", 0x14000000);
            SetHex("Divider", 0x0F000000);
            SetHex("FooterFill", 0x0D000000);
            SetHex("CardFill", 0x80FFFFFF);
            SetHex("ScrollThumb", 0x72000000);
            SetHex("ErrorText", 0xFFC42B1C);
            SetHex("ErrorFill", 0x1FC42B1C);
            SetHex("SolidBackground", 0xFFF9F9F9);
            SetHex("FocusStroke", 0xE4000000);
        }

        // With a system backdrop the window itself is see-through; otherwise paint a solid surface.
        res["RootBackground"] = BackdropSupported ? Brushes.Transparent : res["SolidBackground"];

        ApplyMenuTheme();
    }

    /// <summary>Styles a window like a Windows 11 flyout: acrylic backdrop, rounded corners, no taskbar/Alt+Tab entry.</summary>
    public static void ApplyWindowEffects(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        // No system menu → DWM doesn't draw a caption close button over the extended frame.
        long style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
        if ((style & Native.WS_SYSMENU) != 0)
        {
            Native.SetWindowLong(hwnd, Native.GWL_STYLE, style & ~Native.WS_SYSMENU);
            Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        }

        int dark = IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        if (RoundedCornersSupported)
        {
            int corner = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }

        if (BackdropSupported)
        {
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
                target.BackgroundColor = Colors.Transparent;
            var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            int backdrop = Native.DWMSBT_TRANSIENTWINDOW;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        }
    }

    /// <summary>Hides/reveals a window at the compositor level so it can be positioned without flicker.</summary>
    public static void Cloak(IntPtr hwnd, bool cloak)
    {
        int value = cloak ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_CLOAK, ref value, sizeof(int));
    }

    private static void ApplyMenuTheme()
    {
        try
        {
            Native.SetPreferredAppMode(IsDark ? 2 : 3);
            Native.FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Older Windows: menus keep the classic light look.
        }
    }

    /// <summary>Returns the accent shades Windows uses for controls in light mode (dark1) and dark mode (light2).</summary>
    private static (Color dark1, Color light2) ReadAccent()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                    "AccentPalette", null) is byte[] p && p.Length >= 32)
            {
                // Eight RGBA entries: light3, light2, light1, base, dark1, dark2, dark3, (unused)
                Color At(int i) => Color.FromRgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
                return (At(4), At(1));
            }
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
            {
                var c = Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
                return (c, Lighten(c, 0.45));
            }
        }
        catch
        {
            // Fall through to defaults.
        }
        return (Color.FromRgb(0x00, 0x67, 0xC0), Color.FromRgb(0x4C, 0xC2, 0xFF));
    }

    private static Color Lighten(Color c, double t) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    private static Color FromArgb(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
