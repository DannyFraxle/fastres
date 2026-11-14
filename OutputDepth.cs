using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace FastRes;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

/// <summary>Identifies one active display path: GDI source name plus the DisplayConfig adapter/target.</summary>
public sealed record DisplayTarget(string GdiName, Native.LUID Adapter, uint TargetId, GpuVendor Vendor);

/// <summary>Output bit depth per colour channel, as reported by the GPU driver.</summary>
public sealed record OutputDepthInfo(GpuVendor Vendor, int CurrentBpc, IReadOnlyList<int> SupportedBpc);

internal interface IOutputDepthProvider
{
    OutputDepthInfo? Query(DisplayTarget target);
    ApplyResult Set(DisplayTarget target, int bpc);
}

/// <summary>
/// Windows has no public API for the link bit depth (8/10/12 bpc) — it belongs to the GPU driver.
/// This routes to each vendor's own driver library: NVAPI (NVIDIA), ADL (AMD) and IGCL (Intel).
/// Every call is best-effort: a missing driver or library simply means "not available".
/// </summary>
public static class OutputDepthControl
{
    private static readonly object Gate = new();
    private static readonly Dictionary<GpuVendor, IOutputDepthProvider?> Providers = new();

    public static OutputDepthInfo? Query(DisplayTarget? target)
    {
        if (target is null || target.Vendor == GpuVendor.Unknown) return null;
        lock (Gate)
        {
            try { return For(target.Vendor)?.Query(target); }
            catch { return null; }
        }
    }

    public static ApplyResult Set(DisplayTarget target, int bpc)
    {
        lock (Gate)
        {
            var provider = For(target.Vendor);
            if (provider is null)
                return new ApplyResult(false, false, "The graphics driver's control library isn't available.");
            try { return provider.Set(target, bpc); }
            catch (Exception ex) { return new ApplyResult(false, false, $"The graphics driver rejected the change ({ex.Message})."); }
        }
    }

    public static GpuVendor VendorFromDevicePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return GpuVendor.Unknown;
        if (path.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Nvidia;
        if (path.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Amd;
        if (path.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Intel;
        return GpuVendor.Unknown;
    }

    public static string Name(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        _ => "GPU",
    };

    private static IOutputDepthProvider? For(GpuVendor vendor)
    {
        if (Providers.TryGetValue(vendor, out var cached)) return cached;
        IOutputDepthProvider? created = null;
        try
        {
            created = vendor switch
            {
                GpuVendor.Nvidia => NvidiaDepth.Create(),
                GpuVendor.Amd => AmdDepth.Create(),
                GpuVendor.Intel => IntelDepth.Create(),
                _ => null,
            };
        }
        catch
        {
            created = null;
        }
        Providers[vendor] = created;
        return created;
    }

    /// <summary>Loads a driver DLL from System32 only (never from the app folder or PATH).</summary>
    internal static IntPtr LoadSystemLibrary(string name) =>
        NativeLibrary.TryLoad(name, typeof(OutputDepthControl).Assembly, DllImportSearchPath.System32, out var handle)
            ? handle : IntPtr.Zero;

    internal static IReadOnlyList<int> WithCurrent(IEnumerable<int> supported, int current)
    {
        var set = new SortedSet<int>(supported);
        if (current > 0) set.Add(current);
        return set.ToList();
    }
}

// ─────────────────────────────── NVIDIA (NVAPI) ───────────────────────────────

internal sealed unsafe class NvidiaDepth : IOutputDepthProvider
{
    // nvapi.h: NV_COLOR_DATA_V5 (24 bytes on x86/x64; the inner struct is 4-byte aligned).
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct NV_COLOR_DATA
    {
        [FieldOffset(0)] public uint version;
        [FieldOffset(4)] public ushort size;
        [FieldOffset(6)] public byte cmd;
        [FieldOffset(8)] public byte colorFormat;
        [FieldOffset(9)] public byte colorimetry;
        [FieldOffset(10)] public byte dynamicRange;
        [FieldOffset(12)] public int bpc;
        [FieldOffset(16)] public int colorSelectionPolicy;
        [FieldOffset(20)] public int depth;
    }

    private const uint NV_COLOR_DATA_VER5 = 24u | (5u << 16); // MAKE_NVAPI_VERSION(NV_COLOR_DATA_V5, 5)
    private const byte NV_COLOR_CMD_GET = 1, NV_COLOR_CMD_SET = 2, NV_COLOR_CMD_IS_SUPPORTED_COLOR = 3;
    private const int NV_COLOR_SELECTION_POLICY_USER = 0;
    private const int NV_DESKTOP_COLOR_DEPTH_DEFAULT = 0; // keep current desktop depth

    // nvapi_interface.h
    private const uint ID_NvAPI_Initialize = 0x0150E828;
    private const uint ID_NvAPI_DISP_GetDisplayIdByDisplayName = 0xAE457190;
    private const uint ID_NvAPI_Disp_ColorControl = 0x92F9D80D;

    private readonly delegate* unmanaged[Cdecl]<byte*, uint*, int> _getDisplayId;
    private readonly delegate* unmanaged[Cdecl]<uint, NV_COLOR_DATA*, int> _colorControl;

    private NvidiaDepth(IntPtr getDisplayId, IntPtr colorControl)
    {
        _getDisplayId = (delegate* unmanaged[Cdecl]<byte*, uint*, int>)getDisplayId;
        _colorControl = (delegate* unmanaged[Cdecl]<uint, NV_COLOR_DATA*, int>)colorControl;
    }

    public static NvidiaDepth? Create()
    {
        var lib = OutputDepthControl.LoadSystemLibrary(IntPtr.Size == 8 ? "nvapi64.dll" : "nvapi.dll");
        if (lib == IntPtr.Zero || !NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qiPtr)) return null;
        var queryInterface = (delegate* unmanaged[Cdecl]<uint, IntPtr>)qiPtr;

        IntPtr init = queryInterface(ID_NvAPI_Initialize);
        IntPtr getId = queryInterface(ID_NvAPI_DISP_GetDisplayIdByDisplayName);
        IntPtr color = queryInterface(ID_NvAPI_Disp_ColorControl);
        if (init == IntPtr.Zero || getId == IntPtr.Zero || color == IntPtr.Zero) return null;
        if (((delegate* unmanaged[Cdecl]<int>)init)() != 0) return null;
        return new NvidiaDepth(getId, color);
    }

    private static int FromNv(int bpc) => bpc switch { 1 => 6, 2 => 8, 3 => 10, 4 => 12, 5 => 16, _ => 0 };
    private static int ToNv(int bpc) => bpc switch { 6 => 1, 8 => 2, 10 => 3, 12 => 4, 16 => 5, _ => 0 };

    private uint? DisplayId(string gdiName)
    {
        var name = Encoding.ASCII.GetBytes(gdiName + "\0");
        uint id;
        fixed (byte* p = name)
            return _getDisplayId(p, &id) == 0 ? id : null;
    }

    private bool TryGet(uint displayId, out NV_COLOR_DATA data)
    {
        data = new NV_COLOR_DATA { version = NV_COLOR_DATA_VER5, size = 24, cmd = NV_COLOR_CMD_GET };
        fixed (NV_COLOR_DATA* p = &data)
            return _colorControl(displayId, p) == 0;
    }

    public OutputDepthInfo? Query(DisplayTarget target)
    {
        if (DisplayId(target.GdiName) is not uint id || !TryGet(id, out var current)) return null;

        var supported = new List<int>();
        foreach (int bpc in new[] { 8, 10, 12 })
        {
            var probe = current;
            probe.cmd = NV_COLOR_CMD_IS_SUPPORTED_COLOR;
            probe.bpc = ToNv(bpc);
            probe.colorSelectionPolicy = NV_COLOR_SELECTION_POLICY_USER;
            probe.depth = NV_DESKTOP_COLOR_DEPTH_DEFAULT;
            if (_colorControl(id, &probe) == 0) supported.Add(bpc);
        }
        int cur = FromNv(current.bpc);
        return new OutputDepthInfo(GpuVendor.Nvidia, cur, OutputDepthControl.WithCurrent(supported, cur));
    }

    public ApplyResult Set(DisplayTarget target, int bpc)
    {
        if (DisplayId(target.GdiName) is not uint id || !TryGet(id, out var data))
            return new ApplyResult(false, false, "NVIDIA driver didn't recognise this display.");

        data.cmd = NV_COLOR_CMD_SET;
        data.bpc = ToNv(bpc);
        data.colorSelectionPolicy = NV_COLOR_SELECTION_POLICY_USER; // required for the driver to honour bpc
        data.depth = NV_DESKTOP_COLOR_DEPTH_DEFAULT;
        int r = _colorControl(id, &data);
        return r == 0
            ? new ApplyResult(true, false, string.Empty)
            : new ApplyResult(false, false, $"The NVIDIA driver didn't accept {bpc}-bit output with the current format (NVAPI {r}).");
    }
}

// ─────────────────────────────── AMD (ADL) ───────────────────────────────

internal sealed unsafe class AmdDepth : IOutputDepthProvider
{
    private const int ADL_MAX_PATH = 256;
    private const int ADL_DISPLAY_DISPLAYINFO_DISPLAYCONNECTED = 0x1;
    private const int ADL_DISPLAY_DISPLAYINFO_DISPLAYMAPPED = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdapterInfo
    {
        public int iSize;
        public int iAdapterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string strUDID;
        public int iBusNumber;
        public int iDeviceNumber;
        public int iFunctionNumber;
        public int iVendorID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string strAdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string strDisplayName;
        public int iPresent;
        public int iExist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string strDriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string strDriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string strPNPString;
        public int iOSDisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct ADLDisplayInfo
    {
        public int iDisplayLogicalIndex;
        public int iDisplayPhysicalIndex;
        public int iDisplayLogicalAdapterIndex;
        public int iDisplayPhysicalAdapterIndex;
        public int iDisplayControllerIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string strDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)] public string strDisplayManufacturerName;
        public int iDisplayType;
        public int iDisplayOutputType;
        public int iDisplayConnector;
        public int iDisplayInfoMask;
        public int iDisplayInfoValue;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr AdlMalloc(int size);

    // ADL allocates result arrays through this callback; we free them with FreeHGlobal.
    private static readonly AdlMalloc Malloc = size => Marshal.AllocHGlobal(size);

    private readonly IntPtr _context;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, int*, int> _numberOfAdapters;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int> _adapterInfo;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, int, int*, IntPtr*, int, int> _displayInfo;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, int, int, int*, int> _getDepth;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, int, int, int, int> _setDepth;
    private readonly delegate* unmanaged[Stdcall]<IntPtr, int, int, int*, int> _supportedDepth; // optional

    private AmdDepth(IntPtr lib, IntPtr context)
    {
        _context = context;
        _numberOfAdapters = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)NativeLibrary.GetExport(lib, "ADL2_Adapter_NumberOfAdapters_Get");
        _adapterInfo = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)NativeLibrary.GetExport(lib, "ADL2_Adapter_AdapterInfo_Get");
        _displayInfo = (delegate* unmanaged[Stdcall]<IntPtr, int, int*, IntPtr*, int, int>)NativeLibrary.GetExport(lib, "ADL2_Display_DisplayInfo_Get");
        _getDepth = (delegate* unmanaged[Stdcall]<IntPtr, int, int, int*, int>)NativeLibrary.GetExport(lib, "ADL2_Display_ColorDepth_Get");
        _setDepth = (delegate* unmanaged[Stdcall]<IntPtr, int, int, int, int>)NativeLibrary.GetExport(lib, "ADL2_Display_ColorDepth_Set");
        _supportedDepth = NativeLibrary.TryGetExport(lib, "ADL2_Display_SupportedColorDepth_Get", out var s)
            ? (delegate* unmanaged[Stdcall]<IntPtr, int, int, int*, int>)s : null;
    }

    public static AmdDepth? Create()
    {
        var lib = OutputDepthControl.LoadSystemLibrary(IntPtr.Size == 8 ? "atiadlxx.dll" : "atiadlxy.dll");
        if (lib == IntPtr.Zero || !NativeLibrary.TryGetExport(lib, "ADL2_Main_Control_Create", out var createPtr)) return null;
        var create = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)createPtr;
        IntPtr context;
        if (create(Marshal.GetFunctionPointerForDelegate(Malloc), 1, &context) != 0 || context == IntPtr.Zero) return null;
        return new AmdDepth(lib, context);
    }

    // ADL_COLORDEPTH_666 = 1, _888 = 2, _101010 = 3, _121212 = 4, _141414 = 5, _161616 = 6
    private static int FromAdl(int v) => v switch { 1 => 6, 2 => 8, 3 => 10, 4 => 12, 5 => 14, 6 => 16, _ => 0 };
    private static int ToAdl(int bpc) => bpc switch { 6 => 1, 8 => 2, 10 => 3, 12 => 4, 14 => 5, 16 => 6, _ => 0 };

    /// <summary>Finds ADL's (adapter index, display index) for a GDI source name such as \\.\DISPLAY1.</summary>
    private (int Adapter, int Display)? Find(string gdiName)
    {
        int count;
        if (_numberOfAdapters(_context, &count) != 0 || count <= 0) return null;

        int size = Marshal.SizeOf<AdapterInfo>();
        IntPtr buffer = Marshal.AllocHGlobal(size * count);
        try
        {
            new Span<byte>((void*)buffer, size * count).Clear();
            if (_adapterInfo(_context, buffer, size * count) != 0) return null;

            for (int i = 0; i < count; i++)
            {
                var adapter = Marshal.PtrToStructure<AdapterInfo>(buffer + i * size);
                if (!string.Equals(adapter.strDisplayName, gdiName, StringComparison.OrdinalIgnoreCase)) continue;

                int displays;
                IntPtr list;
                if (_displayInfo(_context, adapter.iAdapterIndex, &displays, &list, 0) != 0 || list == IntPtr.Zero) continue;
                try
                {
                    int dsize = Marshal.SizeOf<ADLDisplayInfo>();
                    for (int d = 0; d < displays; d++)
                    {
                        var info = Marshal.PtrToStructure<ADLDisplayInfo>(list + d * dsize);
                        const int needed = ADL_DISPLAY_DISPLAYINFO_DISPLAYCONNECTED | ADL_DISPLAY_DISPLAYINFO_DISPLAYMAPPED;
                        if ((info.iDisplayInfoValue & needed) == needed && info.iDisplayLogicalAdapterIndex == adapter.iAdapterIndex)
                            return (adapter.iAdapterIndex, info.iDisplayLogicalIndex);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(list);
                }
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public OutputDepthInfo? Query(DisplayTarget target)
    {
        if (Find(target.GdiName) is not var (adapter, display)) return null;
        int value;
        if (_getDepth(_context, adapter, display, &value) != 0) return null;
        int cur = FromAdl(value);

        var supported = new List<int>();
        int mask;
        if (_supportedDepth != null && _supportedDepth(_context, adapter, display, &mask) == 0 && mask > 0)
        {
            // Reported as a bit field indexed by ADL_COLORDEPTH_* value.
            foreach (int bpc in new[] { 8, 10, 12 })
                if ((mask & (1 << ToAdl(bpc))) != 0) supported.Add(bpc);
        }
        return new OutputDepthInfo(GpuVendor.Amd, cur, OutputDepthControl.WithCurrent(supported, cur));
    }

    public ApplyResult Set(DisplayTarget target, int bpc)
    {
        if (Find(target.GdiName) is not var (adapter, display))
            return new ApplyResult(false, false, "AMD driver didn't recognise this display.");
        int r = _setDepth(_context, adapter, display, ToAdl(bpc));
        return r == 0
            ? new ApplyResult(true, false, string.Empty)
            : new ApplyResult(false, false, $"The AMD driver didn't accept {bpc}-bit output (ADL {r}).");
    }
}

// ─────────────────────────────── Intel (IGCL) ───────────────────────────────

internal sealed unsafe class IntelDepth : IOutputDepthProvider
{
    // igcl_api.h struct sizes for x64 (natural alignment).
    private const int SizeofInitArgs = 36;          // ctl_init_args_t
    private const int SizeofDisplayProperties = 200; // ctl_display_properties_t
    private const int SizeofWireFormat = 16;         // ctl_wire_format_t
    private const int SizeofWireFormatConfig = 92;   // ctl_get_set_wire_format_config_t
    private const int MaxColorModels = 4;            // CTL_MAX_WIREFORMAT_COLOR_MODELS_SUPPORTED
    private const uint CTL_IMPL_VERSION = (1u << 16) | 1u;
    private const uint CTL_DISPLAY_CONFIG_FLAG_DISPLAY_ACTIVE = 1;
    private const int OP_GET = 0, OP_SET = 1;

    private readonly IntPtr _api;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, IntPtr*, int> _enumDevices;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, IntPtr*, int> _enumOutputs;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, int> _displayProperties;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, int> _wireFormat;

    private IntelDepth(IntPtr lib, IntPtr api)
    {
        _api = api;
        _enumDevices = (delegate* unmanaged[Cdecl]<IntPtr, uint*, IntPtr*, int>)NativeLibrary.GetExport(lib, "ctlEnumerateDevices");
        _enumOutputs = (delegate* unmanaged[Cdecl]<IntPtr, uint*, IntPtr*, int>)NativeLibrary.GetExport(lib, "ctlEnumerateDisplayOutputs");
        _displayProperties = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)NativeLibrary.GetExport(lib, "ctlGetDisplayProperties");
        _wireFormat = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)NativeLibrary.GetExport(lib, "ctlGetSetWireFormat");
    }

    public static IntelDepth? Create()
    {
        if (IntPtr.Size != 8) return null; // struct offsets below are for x64
        var lib = OutputDepthControl.LoadSystemLibrary("ControlLib.dll");
        if (lib == IntPtr.Zero || !NativeLibrary.TryGetExport(lib, "ctlInit", out var initPtr)) return null;
        if (!NativeLibrary.TryGetExport(lib, "ctlGetSetWireFormat", out _)) return null; // older driver

        byte* args = stackalloc byte[SizeofInitArgs];
        new Span<byte>(args, SizeofInitArgs).Clear();
        *(uint*)(args + 0) = SizeofInitArgs; // Size
        *(uint*)(args + 8) = CTL_IMPL_VERSION; // AppVersion
        IntPtr api;
        var init = (delegate* unmanaged[Cdecl]<byte*, IntPtr*, int>)initPtr;
        if (init(args, &api) != 0 || api == IntPtr.Zero) return null;
        return new IntelDepth(lib, api);
    }

    private static int FromFlag(uint flag) => flag switch { 1 => 6, 2 => 8, 4 => 10, 8 => 12, _ => 0 };
    private static uint ToFlag(int bpc) => bpc switch { 6 => 1u, 8 => 2u, 10 => 4u, 12 => 8u, _ => 0u };

    private static IntPtr[] Enumerate(delegate* unmanaged[Cdecl]<IntPtr, uint*, IntPtr*, int> fn, IntPtr parent)
    {
        uint count = 0;
        if (fn(parent, &count, null) != 0 || count == 0) return Array.Empty<IntPtr>();
        var handles = new IntPtr[count];
        fixed (IntPtr* p = handles)
            if (fn(parent, &count, p) != 0) return Array.Empty<IntPtr>();
        return handles.Take((int)count).ToArray();
    }

    /// <summary>Finds the active Intel display output whose OS encoder id matches the DisplayConfig target id.</summary>
    private IntPtr FindOutput(uint targetId)
    {
        byte* props = stackalloc byte[SizeofDisplayProperties];
        foreach (var device in Enumerate(_enumDevices, _api))
        {
            foreach (var output in Enumerate(_enumOutputs, device))
            {
                new Span<byte>(props, SizeofDisplayProperties).Clear();
                *(uint*)props = SizeofDisplayProperties;
                if (_displayProperties(output, props) != 0) continue;
                uint encoderId = *(uint*)(props + 8);   // Os_display_encoder_handle.WindowsDisplayEncoderID
                uint config = *(uint*)(props + 48);     // DisplayConfigFlags
                if ((config & CTL_DISPLAY_CONFIG_FLAG_DISPLAY_ACTIVE) != 0 && encoderId == targetId)
                    return output;
            }
        }
        return IntPtr.Zero;
    }

    private static void InitConfig(byte* cfg, int operation)
    {
        new Span<byte>(cfg, SizeofWireFormatConfig).Clear();
        *(uint*)(cfg + 0) = SizeofWireFormatConfig;
        *(int*)(cfg + 8) = operation;
        for (int i = 0; i < MaxColorModels; i++) *(uint*)(cfg + 12 + i * SizeofWireFormat) = SizeofWireFormat;
        *(uint*)(cfg + 76) = SizeofWireFormat;
    }

    public OutputDepthInfo? Query(DisplayTarget target)
    {
        var output = FindOutput(target.TargetId);
        if (output == IntPtr.Zero) return null;

        byte* cfg = stackalloc byte[SizeofWireFormatConfig];
        InitConfig(cfg, OP_GET);
        if (_wireFormat(output, cfg) != 0) return null;

        int model = *(int*)(cfg + 76 + 8);
        int cur = FromFlag(*(uint*)(cfg + 76 + 12));

        uint supportedMask = 0;
        for (int i = 0; i < MaxColorModels; i++)
        {
            byte* entry = cfg + 12 + i * SizeofWireFormat;
            if (*(int*)(entry + 8) == model) supportedMask |= *(uint*)(entry + 12);
        }
        var supported = new[] { 8, 10, 12 }.Where(b => (supportedMask & ToFlag(b)) != 0);
        return new OutputDepthInfo(GpuVendor.Intel, cur, OutputDepthControl.WithCurrent(supported, cur));
    }

    public ApplyResult Set(DisplayTarget target, int bpc)
    {
        var output = FindOutput(target.TargetId);
        if (output == IntPtr.Zero) return new ApplyResult(false, false, "Intel driver didn't recognise this display.");

        byte* cfg = stackalloc byte[SizeofWireFormatConfig];
        InitConfig(cfg, OP_GET);
        if (_wireFormat(output, cfg) != 0) return new ApplyResult(false, false, "Intel driver couldn't read the current output format.");
        int model = *(int*)(cfg + 76 + 8);

        InitConfig(cfg, OP_SET);
        *(int*)(cfg + 76 + 8) = model;          // keep the colour model (RGB / YCbCr)
        *(uint*)(cfg + 76 + 12) = ToFlag(bpc);  // exactly one bpc bit
        int r = _wireFormat(output, cfg);
        return r == 0
            ? new ApplyResult(true, false, string.Empty)
            : new ApplyResult(false, false, $"The Intel driver didn't accept {bpc}-bit output (IGCL 0x{r:X}).");
    }
}
