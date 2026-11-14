# FastRes

A tiny Windows tray app for switching screen resolution, refresh rate and colour mode in one click — a modern take on the old Windows 98 *QuickRes* tray icon.

- **Left-click** the tray icon → a Windows 11-style acrylic flyout:
  - **Resolution** list (current one highlighted, native mode marked *Recommended*, aspect ratio shown)
  - **Refresh rate** chips for the current resolution
  - **Color**: output bit depth (**8 / 10 / 12-bit** per channel) on NVIDIA, AMD and Intel GPUs, SDR / HDR toggle on HDR-capable displays, and classic 32/16/8-bit desktop depths if your driver still offers them
  - A display picker when more than one monitor is connected
- **Right-click** (or the `···` button) → options: *Ask before keeping changes*, *Show low resolutions*, *Start with Windows*, *Exit*.
- After a resolution/refresh/depth change you get a **"Keep these display settings?"** prompt that reverts automatically after 15 s — so a blank screen fixes itself. Turn it off in the options for instant switching.
- Follows light/dark mode and your accent colour. Single instance: launching it again opens the flyout.

## About 8-bit vs 10-bit

Windows has no public API (or Settings option) to choose the per-channel output depth — it's owned by the GPU driver. FastRes talks to each vendor's driver library directly, loading it only from `System32` where the driver installs it:

| GPU    | Library        | Call                                   |
|--------|----------------|----------------------------------------|
| NVIDIA | `nvapi64.dll`  | `NvAPI_Disp_ColorControl`              |
| AMD    | `atiadlxx.dll` | `ADL2_Display_ColorDepth_Get/Set`      |
| Intel  | `ControlLib.dll` | `ctlGetSetWireFormat` (IGCL)         |

The buttons only appear for depths the driver reports as supported, and every change goes through the same 15-second keep/revert prompt. On other GPUs (or if the library is missing) FastRes shows the current depth read-only. The NVIDIA path has been tested; AMD and Intel follow their SDK headers but haven't been run on that hardware yet.

## Build

Requires the .NET 9 SDK (Windows).

```
dotnet build
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

`publish\FastRes.exe` needs the .NET 9 Desktop Runtime. `FastRes.exe --list` prints every display, mode and colour capability without changing anything.

On Windows 11 new tray icons start in the overflow (`^`) area — drag it onto the taskbar, or enable it under *Settings → Personalization → Taskbar → Other system tray icons*.

`tools/make-icon.ps1` regenerates `Assets/FastRes.ico`.

Settings live in `%APPDATA%\FastRes\settings.json`.
