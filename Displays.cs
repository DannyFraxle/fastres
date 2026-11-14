using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace FastRes;

/// <param name="Bpp">Desktop colour depth in bits per pixel; 0 = keep the current depth.</param>
public readonly record struct DisplayMode(int Width, int Height, int Hz, int Bpp = 0)
{
    public override string ToString() => $"{Width} × {Height} @ {Hz} Hz{(Bpp > 0 ? $", {Bpp}-bit" : "")}";
}

/// <summary>Output colour state from the DisplayConfig API (bit depth per channel, HDR).</summary>
public sealed record ColorInfo(
    int BitsPerChannel, string Encoding, bool HdrSupported, bool HdrOn, bool HdrLimitedByPolicy,
    bool UsesHdrStateApi, Native.LUID Adapter, uint TargetId);

public sealed class MonitorInfo
{
    public required string DeviceName { get; init; }      // \\.\DISPLAY1
    public required int Number { get; init; }
    public required string FriendlyName { get; init; }
    public required bool IsPrimary { get; init; }
    public required DisplayMode Current { get; init; }
    public (int Width, int Height)? Preferred { get; init; }
    public required IReadOnlyList<DisplayMode> Modes { get; init; }

    /// <summary>Current desktop colour depth in bits per pixel (normally 32).</summary>
    public int CurrentBpp { get; init; }

    /// <summary>Desktop colour depths the driver offers at the current resolution/refresh (e.g. 8, 16, 32).</summary>
    public IReadOnlyList<int> ColorDepths { get; init; } = Array.Empty<int>();

    public ColorInfo? Color { get; init; }

    /// <summary>DisplayConfig identity of this display (adapter, target, GPU vendor).</summary>
    public DisplayTarget? Target { get; init; }

    /// <summary>Output bits per channel from the GPU vendor's driver library, when available.</summary>
    public OutputDepthInfo? OutputDepth { get; init; }

    public IEnumerable<(int Width, int Height)> Resolutions =>
        Modes.Select(m => (m.Width, m.Height)).Distinct()
             .OrderByDescending(r => r.Width * r.Height).ThenByDescending(r => r.Width);

    public IEnumerable<int> RatesFor(int width, int height) =>
        Modes.Where(m => m.Width == width && m.Height == height).Select(m => m.Hz).Distinct().OrderBy(h => h);

    /// <summary>Highest-quality refresh rate to use when switching to a resolution.</summary>
    public int BestRateFor(int width, int height, int preferHz)
    {
        var rates = RatesFor(width, height).ToList();
        if (rates.Count == 0) return preferHz;
        return rates.Contains(preferHz) ? preferHz : rates.Max();
    }

    public string ShortName => string.IsNullOrWhiteSpace(FriendlyName) ? $"Display {Number}" : FriendlyName;
}

public readonly record struct ApplyResult(bool Success, bool NeedsRestart, string Message);

public static class Displays
{
    /// <summary>Enumerates active monitors attached to the desktop and their supported modes.</summary>
    public static List<MonitorInfo> GetMonitors(bool includeLowResolutions)
    {
        var names = QueryTargetInfo();
        var result = new List<MonitorInfo>();

        var dd = Native.DISPLAY_DEVICE.Create();
        for (uint i = 0; Native.EnumDisplayDevices(null, i, ref dd, 0); i++, dd = Native.DISPLAY_DEVICE.Create())
        {
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;

            var cur = Native.DEVMODE.Create();
            if (!Native.EnumDisplaySettingsEx(dd.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref cur, 0)) continue;

            var current = new DisplayMode(cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency);
            var modes = EnumerateModes(dd.DeviceName, cur.dmBitsPerPel);

            // A rotated (portrait) display may report its mode list in landscape terms.
            bool rotated = cur.dmDisplayOrientation is 1 or 3;
            if (rotated && !modes.Any(m => m.Width == current.Width && m.Height == current.Height)
                        && modes.Any(m => m.Width == current.Height && m.Height == current.Width))
            {
                modes = modes.Select(m => new DisplayMode(m.Height, m.Width, m.Hz)).ToList();
            }

            if (!includeLowResolutions)
                modes = modes.Where(m => Math.Max(m.Width, m.Height) >= 800 && Math.Min(m.Width, m.Height) >= 600 || m == current).ToList();

            if (!modes.Contains(current) && current.Hz > 1) modes.Add(current);

            names.TryGetValue(dd.DeviceName, out var info);
            (int, int)? preferred = info.PreferredWidth > 0 ? ((int)info.PreferredWidth, (int)info.PreferredHeight) : null;
            if (preferred is { } p && rotated && !modes.Any(m => m.Width == p.Item1 && m.Height == p.Item2))
                preferred = (p.Item2, p.Item1);

            // Prefer the EDID model name; internal panels usually have none, so name them like Settings does.
            string friendly = info.NameFromEdid ? info.FriendlyName ?? string.Empty : string.Empty;
            if (friendly.Trim() is "Display" or "Monitor" or "DISPLAY" or "MONITOR")
                friendly = string.Empty; // uninformative EDID names
            if (string.IsNullOrWhiteSpace(friendly) && info.IsInternal)
                friendly = "Built-in display";
            if (string.IsNullOrWhiteSpace(friendly))
            {
                var mon = Native.DISPLAY_DEVICE.Create();
                if (Native.EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) &&
                    !mon.DeviceString.Contains("Generic", StringComparison.OrdinalIgnoreCase))
                    friendly = mon.DeviceString;
            }

            result.Add(new MonitorInfo
            {
                DeviceName = dd.DeviceName,
                Number = ParseNumber(dd.DeviceName, result.Count + 1),
                FriendlyName = friendly.Trim(),
                IsPrimary = (dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                Current = current,
                Preferred = preferred,
                Modes = modes,
                CurrentBpp = cur.dmBitsPerPel,
                ColorDepths = EnumerateDepths(dd.DeviceName, cur),
                Color = info.Color,
                Target = info.Target,
                OutputDepth = OutputDepthControl.Query(info.Target),
            });
        }

        return result.OrderBy(m => m.Number).ToList();
    }

    /// <summary>Cheap snapshot of each active display's current mode (no mode enumeration).</summary>
    public static List<(int Number, bool IsPrimary, DisplayMode Mode)> GetActiveModes()
    {
        var list = new List<(int, bool, DisplayMode)>();
        var dd = Native.DISPLAY_DEVICE.Create();
        for (uint i = 0; Native.EnumDisplayDevices(null, i, ref dd, 0); i++, dd = Native.DISPLAY_DEVICE.Create())
        {
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;
            if (GetCurrent(dd.DeviceName) is { } mode)
                list.Add((ParseNumber(dd.DeviceName, list.Count + 1), (dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0, mode));
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    public static DisplayMode? GetCurrent(string deviceName)
    {
        var dm = Native.DEVMODE.Create();
        return Native.EnumDisplaySettingsEx(deviceName, Native.ENUM_CURRENT_SETTINGS, ref dm, 0)
            ? new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency)
            : null;
    }

    /// <summary>Validates and applies a mode, persisting it to the registry.</summary>
    public static ApplyResult Apply(string deviceName, DisplayMode mode)
    {
        var dm = Native.DEVMODE.Create();
        if (!Native.EnumDisplaySettingsEx(deviceName, Native.ENUM_CURRENT_SETTINGS, ref dm, 0))
            return new ApplyResult(false, false, "Couldn't read the current display settings.");

        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.Hz;
        dm.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYFREQUENCY;
        if (mode.Bpp > 0)
        {
            dm.dmBitsPerPel = mode.Bpp;
            dm.dmFields |= Native.DM_BITSPERPEL;
        }

        int test = Native.ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, Native.CDS_TEST, IntPtr.Zero);
        if (test != Native.DISP_CHANGE_SUCCESSFUL && test != Native.DISP_CHANGE_RESTART)
            return new ApplyResult(false, false, Describe(test, mode));

        int r = Native.ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
        return r switch
        {
            Native.DISP_CHANGE_SUCCESSFUL => new ApplyResult(true, false, string.Empty),
            Native.DISP_CHANGE_RESTART => new ApplyResult(true, true, "Restart Windows to finish applying this mode."),
            _ => new ApplyResult(false, false, Describe(r, mode)),
        };
    }

    /// <summary>Checks a mode with the driver without applying it.</summary>
    public static ApplyResult Test(string deviceName, DisplayMode mode)
    {
        var dm = Native.DEVMODE.Create();
        if (!Native.EnumDisplaySettingsEx(deviceName, Native.ENUM_CURRENT_SETTINGS, ref dm, 0))
            return new ApplyResult(false, false, "Couldn't read the current display settings.");
        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.Hz;
        dm.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYFREQUENCY;
        if (mode.Bpp > 0)
        {
            dm.dmBitsPerPel = mode.Bpp;
            dm.dmFields |= Native.DM_BITSPERPEL;
        }
        int r = Native.ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, Native.CDS_TEST, IntPtr.Zero);
        return new ApplyResult(r is Native.DISP_CHANGE_SUCCESSFUL or Native.DISP_CHANGE_RESTART, r == Native.DISP_CHANGE_RESTART, Describe(r, mode));
    }

    public static string AspectRatio(int w, int h)
    {
        if (w <= 0 || h <= 0) return string.Empty;
        bool portrait = h > w;
        double r = portrait ? (double)h / w : (double)w / h;
        (double ratio, string label)[] known =
        {
            (4.0 / 3, "4:3"), (5.0 / 4, "5:4"), (3.0 / 2, "3:2"), (16.0 / 10, "16:10"),
            (16.0 / 9, "16:9"), (21.0 / 9, "21:9"), (64.0 / 27, "21:9"), (43.0 / 18, "21:9"),
            (32.0 / 10, "32:10"), (32.0 / 9, "32:9"), (5.0 / 3, "5:3"), (1.0, "1:1"),
        };
        var best = known.OrderBy(k => Math.Abs(k.ratio - r)).First();
        string label = Math.Abs(best.ratio - r) / best.ratio < 0.02 ? best.label : Reduce(w, h);
        if (portrait && label.Length > 0)
        {
            var parts = label.Split(':');
            if (parts.Length == 2) label = $"{parts[1]}:{parts[0]}";
        }
        return label;

        static string Reduce(int a, int b)
        {
            int g = Gcd(a, b);
            int x = a / g, y = b / g;
            return x <= 64 && y <= 64 ? $"{x}:{y}" : string.Empty;
        }
        static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
    }

    private static List<int> EnumerateDepths(string device, Native.DEVMODE cur)
    {
        var set = new SortedSet<int>();
        var dm = Native.DEVMODE.Create();
        for (int i = 0; Native.EnumDisplaySettingsEx(device, i, ref dm, 0); i++, dm = Native.DEVMODE.Create())
        {
            bool sameMode = (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight)
                         || (dm.dmPelsWidth == cur.dmPelsHeight && dm.dmPelsHeight == cur.dmPelsWidth);
            if (sameMode && dm.dmDisplayFrequency == cur.dmDisplayFrequency && dm.dmBitsPerPel >= 8)
                set.Add(dm.dmBitsPerPel);
        }
        if (cur.dmBitsPerPel > 0) set.Add(cur.dmBitsPerPel);
        return set.Reverse().ToList();
    }

    /// <summary>Turns HDR on or off for a display (Windows 10 1709+; uses the 24H2 HDR-state API when present).</summary>
    public static ApplyResult SetHdr(ColorInfo color, bool enable)
    {
        var packet = new Native.DISPLAYCONFIG_SET_STATE { value = enable ? 1u : 0u };
        packet.header.type = color.UsesHdrStateApi
            ? Native.DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE
            : Native.DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE;
        packet.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SET_STATE>();
        packet.header.adapterId = color.Adapter;
        packet.header.id = color.TargetId;
        int r = Native.DisplayConfigSetDeviceInfo(ref packet);
        return r == 0
            ? new ApplyResult(true, false, string.Empty)
            : new ApplyResult(false, false, $"Windows couldn't turn HDR {(enable ? "on" : "off")} (error {r}).");
    }

    private static ColorInfo? QueryColor(Native.LUID adapter, uint targetId)
    {
        // Windows 11 24H2+: separates HDR from SDR "advanced colour" (ACM).
        var info2 = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2();
        info2.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2;
        info2.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>();
        info2.header.adapterId = adapter;
        info2.header.id = targetId;
        if (Native.DisplayConfigGetDeviceInfo(ref info2) == 0)
        {
            return new ColorInfo((int)info2.bitsPerColorChannel, EncodingName(info2.colorEncoding),
                HdrSupported: (info2.value & 0x10) != 0, HdrOn: (info2.value & 0x20) != 0,
                HdrLimitedByPolicy: (info2.value & 0x08) != 0, UsesHdrStateApi: true, adapter, targetId);
        }

        var info = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
        info.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
        info.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
        info.header.adapterId = adapter;
        info.header.id = targetId;
        if (Native.DisplayConfigGetDeviceInfo(ref info) == 0)
        {
            return new ColorInfo((int)info.bitsPerColorChannel, EncodingName(info.colorEncoding),
                HdrSupported: (info.value & 0x1) != 0, HdrOn: (info.value & 0x2) != 0,
                HdrLimitedByPolicy: (info.value & 0x8) != 0, UsesHdrStateApi: false, adapter, targetId);
        }
        return null;
    }

    private static string EncodingName(uint encoding) => encoding switch
    {
        0 => "RGB",
        1 => "YCbCr 4:4:4",
        2 => "YCbCr 4:2:2",
        3 => "YCbCr 4:2:0",
        _ => string.Empty,
    };

    private static List<DisplayMode> EnumerateModes(string device, int bitsPerPel)
    {
        var set = new HashSet<DisplayMode>();
        var dm = Native.DEVMODE.Create();
        for (int i = 0; Native.EnumDisplaySettingsEx(device, i, ref dm, 0); i++, dm = Native.DEVMODE.Create())
        {
            if (bitsPerPel > 0 && dm.dmBitsPerPel != bitsPerPel) continue;
            if ((dm.dmDisplayFlags & Native.DM_INTERLACED) != 0) continue;
            if (dm.dmDisplayFrequency <= 1) continue; // 0/1 = "hardware default"
            set.Add(new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        }
        return set.ToList();
    }

    private static int ParseNumber(string deviceName, int fallback)
    {
        int i = deviceName.Length;
        while (i > 0 && char.IsDigit(deviceName[i - 1])) i--;
        return int.TryParse(deviceName.AsSpan(i), out var n) ? n : fallback;
    }

    private static string Describe(int code, DisplayMode mode) => code switch
    {
        Native.DISP_CHANGE_SUCCESSFUL => string.Empty,
        Native.DISP_CHANGE_RESTART => "Restart Windows to finish applying this mode.",
        Native.DISP_CHANGE_BADMODE => $"{mode.Width} × {mode.Height} at {mode.Hz} Hz isn't supported by this display.",
        Native.DISP_CHANGE_BADDUALVIEW => "This mode isn't supported in the current multi-display setup.",
        Native.DISP_CHANGE_NOTUPDATED => "Windows couldn't save the new display settings.",
        _ => $"The display driver refused the change (code {code}).",
    };

    private struct TargetInfo
    {
        public string? FriendlyName;
        public bool NameFromEdid;
        public bool IsInternal;
        public uint PreferredWidth;
        public uint PreferredHeight;
        public ColorInfo? Color;
        public DisplayTarget? Target;
    }

    private static GpuVendor AdapterVendor(Native.LUID adapter)
    {
        var name = new Native.DISPLAYCONFIG_ADAPTER_NAME { adapterDevicePath = string.Empty };
        name.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME;
        name.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_ADAPTER_NAME>();
        name.header.adapterId = adapter;
        return Native.DisplayConfigGetDeviceInfo(ref name) == 0
            ? OutputDepthControl.VendorFromDevicePath(name.adapterDevicePath)
            : GpuVendor.Unknown;
    }

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY values for panels built into the device.
    private static bool IsInternalTechnology(uint tech) =>
        tech is 6 /* LVDS */ or 11 /* DP embedded */ or 13 /* UDI embedded */ or 0x80000000 /* internal */;

    /// <summary>Maps GDI device names (\\.\DISPLAYn) to EDID friendly names and preferred (native) modes.</summary>
    private static Dictionary<string, TargetInfo> QueryTargetInfo()
    {
        var map = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
                return map;
            var paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];
            if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return map;

            for (int i = 0; i < pathCount; i++)
            {
                var p = paths[i];

                var src = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME { viewGdiDeviceName = string.Empty };
                src.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                src.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                src.header.adapterId = p.sourceInfo.adapterId;
                src.header.id = p.sourceInfo.id;
                if (Native.DisplayConfigGetDeviceInfo(ref src) != 0 || string.IsNullOrEmpty(src.viewGdiDeviceName)) continue;
                if (map.ContainsKey(src.viewGdiDeviceName)) continue; // first target wins for cloned displays

                var info = new TargetInfo();

                var tgt = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME { monitorFriendlyDeviceName = string.Empty, monitorDevicePath = string.Empty };
                tgt.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                tgt.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                tgt.header.adapterId = p.targetInfo.adapterId;
                tgt.header.id = p.targetInfo.id;
                if (Native.DisplayConfigGetDeviceInfo(ref tgt) == 0)
                {
                    info.FriendlyName = tgt.monitorFriendlyDeviceName;
                    info.NameFromEdid = (tgt.flags & 0x1) != 0; // friendlyNameFromEdid
                }
                info.IsInternal = IsInternalTechnology(p.targetInfo.outputTechnology);

                var pref = new Native.DISPLAYCONFIG_TARGET_PREFERRED_MODE();
                pref.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE;
                pref.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_PREFERRED_MODE>();
                pref.header.adapterId = p.targetInfo.adapterId;
                pref.header.id = p.targetInfo.id;
                if (Native.DisplayConfigGetDeviceInfo(ref pref) == 0)
                {
                    info.PreferredWidth = pref.width;
                    info.PreferredHeight = pref.height;
                }

                info.Color = QueryColor(p.targetInfo.adapterId, p.targetInfo.id);
                info.Target = new DisplayTarget(src.viewGdiDeviceName, p.targetInfo.adapterId, p.targetInfo.id,
                    AdapterVendor(p.targetInfo.adapterId));

                map[src.viewGdiDeviceName] = info;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Pre-Windows 7: names fall back to EnumDisplayDevices.
        }
        return map;
    }
}
