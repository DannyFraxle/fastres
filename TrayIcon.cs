using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FastRes;

public readonly record struct MenuEntry(int Id, string Text, bool Checked = false, bool Enabled = true)
{
    public static MenuEntry Separator => new(0, string.Empty);
    public bool IsSeparator => Id == 0;
}

/// <summary>
/// A notification-area icon built directly on Shell_NotifyIcon (version 4) so it can report the icon's
/// position, survive Explorer restarts and show a native — Windows 11 styled — context menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public const string WindowTitle = "FastRes.TrayWindow";
    public const uint WM_TRAY = Native.WM_APP + 1;
    public const uint WM_OPEN_FLYOUT = Native.WM_APP + 2;
    private const uint IconId = 1;

    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private IntPtr _hIcon;
    private string _tip = "FastRes";
    private bool _added;

    /// <summary>Left click / Enter on the icon. Argument is the icon's screen rectangle (physical pixels).</summary>
    public event Action<Native.RECT>? Activated;
    /// <summary>Right click / context-menu key on the icon. Argument is the anchor point (physical pixels).</summary>
    public event Action<Native.POINT>? ContextMenuRequested;
    public event Action? DisplaySettingsChanged;
    public event Action? ThemeChanged;
    public event Action? OpenRequested;

    public TrayIcon()
    {
        // A hidden top-level popup (not message-only) so it receives TaskbarCreated broadcasts
        // and can own popup menus.
        var p = new HwndSourceParameters(WindowTitle)
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    }

    public IntPtr Handle => _source.Handle;

    public int IconSize
    {
        get
        {
            uint dpi = Native.GetDpiForWindow(Handle);
            if (dpi == 0) dpi = 96;
            return Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, dpi);
        }
    }

    public void SetIcon(IntPtr hIcon)
    {
        var old = _hIcon;
        _hIcon = hIcon;
        if (_added) Notify(Native.NIM_MODIFY);
        if (old != IntPtr.Zero) Native.DestroyIcon(old);
    }

    public void SetToolTip(string tip)
    {
        _tip = tip.Length > 127 ? tip[..127] : tip;
        if (_added) Notify(Native.NIM_MODIFY);
    }

    public void Show()
    {
        Notify(Native.NIM_DELETE); // in case a stale icon survived a crash
        _added = Notify(Native.NIM_ADD);
        if (_added)
        {
            var data = NewData();
            data.uVersion = Native.NOTIFYICON_VERSION_4;
            Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
        }
    }

    /// <summary>Screen rectangle of the icon, or a small box around the cursor if it is hidden in the overflow.</summary>
    public Native.RECT GetIconRect()
    {
        var id = new Native.NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(),
            hWnd = Handle,
            uID = IconId,
        };
        if (Native.Shell_NotifyIconGetRect(ref id, out var rect) == 0 && rect.Width > 0)
            return rect;
        Native.GetCursorPos(out var pt);
        return new Native.RECT { Left = pt.X - 8, Top = pt.Y - 8, Right = pt.X + 8, Bottom = pt.Y + 8 };
    }

    /// <summary>Shows a native popup menu and returns the chosen id (0 if dismissed).</summary>
    public static int ShowMenu(IntPtr owner, Native.POINT at, IEnumerable<MenuEntry> entries, bool alignRight = false)
    {
        IntPtr menu = Native.CreatePopupMenu();
        try
        {
            foreach (var e in entries)
            {
                if (e.IsSeparator)
                {
                    Native.AppendMenu(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }
                uint flags = Native.MF_STRING;
                if (e.Checked) flags |= Native.MF_CHECKED;
                if (!e.Enabled) flags |= Native.MF_GRAYED;
                Native.AppendMenu(menu, flags, (UIntPtr)(uint)e.Id, e.Text);
            }

            // Required so the menu closes when the user clicks elsewhere.
            Native.SetForegroundWindow(owner);
            uint align = alignRight || Native.GetSystemMetrics(Native.SM_MENUDROPALIGNMENT) != 0
                ? Native.TPM_RIGHTALIGN : Native.TPM_LEFTALIGN;
            int cmd = Native.TrackPopupMenuEx(menu,
                Native.TPM_RETURNCMD | Native.TPM_NONOTIFY | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN | align,
                at.X, at.Y, owner, IntPtr.Zero);
            Native.PostMessage(owner, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return cmd;
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    private Native.NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = Handle,
        uID = IconId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private bool Notify(uint message)
    {
        var data = NewData();
        if (message != Native.NIM_DELETE)
        {
            data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
            data.uCallbackMessage = WM_TRAY;
            data.hIcon = _hIcon;
            data.szTip = _tip;
        }
        return Native.Shell_NotifyIcon(message, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        uint m = (uint)msg;
        if (m == WM_TRAY)
        {
            uint evt = (uint)Native.LoWord(lParam) & 0xFFFF;
            switch (evt)
            {
                case Native.NIN_SELECT:
                case Native.NIN_KEYSELECT:
                    Activated?.Invoke(GetIconRect());
                    break;
                case Native.WM_CONTEXTMENU:
                    ContextMenuRequested?.Invoke(new Native.POINT(Native.LoWord(wParam), Native.HiWord(wParam)));
                    break;
            }
            handled = true;
        }
        else if (m == _taskbarCreated && _taskbarCreated != 0)
        {
            Show(); // Explorer restarted
        }
        else if (m == WM_OPEN_FLYOUT)
        {
            OpenRequested?.Invoke();
            handled = true;
        }
        else if (m == Native.WM_DISPLAYCHANGE || m == Native.WM_DPICHANGED)
        {
            DisplaySettingsChanged?.Invoke();
        }
        else if (m == Native.WM_SETTINGCHANGE)
        {
            var area = lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
            if (area == "ImmersiveColorSet") ThemeChanged?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added) Notify(Native.NIM_DELETE);
        _added = false;
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
        _hIcon = IntPtr.Zero;
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
