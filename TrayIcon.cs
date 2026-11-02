using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResetExplode;

/// <summary>
/// Ícone nativo na Bandeja do Sistema (System Tray) via Win32 Shell_NotifyIconW + Menu de Contexto nativo,
/// sem depender de Windows Forms nem gerar conflitos de namespace com WPF.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public const int WM_TRAYICON = 0x8001;
    public const int WM_RESTORE_APP = 0x8002;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_DEFAULT = 0x00001000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_BOTTOMALIGN = 0x0020;

    public const int CMD_OPEN = 1;
    public const int CMD_G15 = 2;
    public const int CMD_FAN_ADAPT = 3;
    public const int CMD_FAN_GMODE = 4;
    public const int CMD_GPU_P1 = 5;
    public const int CMD_GPU_P2 = 6;
    public const int CMD_OVERLAY = 7;
    public const int CMD_EXIT = 99;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    private readonly IntPtr _hwnd;
    private readonly uint _wmTaskbarCreated;
    private IntPtr _hIcon;
    private NOTIFYICONDATAW _nid;
    private bool _added;
    private bool _balloonShownOnce;

    public event Action? OpenRequested;
    public event Action<int>? MenuCommandRequested;
    public Func<(bool gmode, int gpuProfile, bool overlayOn)>? QueryState;

    public TrayIcon(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _wmTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (!string.IsNullOrEmpty(exePath))
        {
            ExtractIconExW(exePath, 0, out IntPtr large, out IntPtr small, 1);
            _hIcon = small != IntPtr.Zero ? small : large;
            if (small != IntPtr.Zero && large != IntPtr.Zero) DestroyIcon(large);
        }

        _nid = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "Araújo Control Suite · G15 Control",
            szInfo = "",
            szInfoTitle = ""
        };

        _added = Shell_NotifyIconW(NIM_ADD, ref _nid);
    }

    public void ShowMinimizedBalloonOnce()
    {
        if (!_added || _balloonShownOnce) return;
        _balloonShownOnce = true;
        _nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_INFO;
        _nid.szInfoTitle = "Araújo Control Suite";
        _nid.szInfo = "Rodando em segundo plano na bandeja. Clique no ícone para abrir ou botão direito para sair.";
        _nid.dwInfoFlags = 0x00000001; // NIIF_INFO
        Shell_NotifyIconW(NIM_MODIFY, ref _nid);
        _nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    }

    public bool HandleWndProc(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (_wmTaskbarCreated != 0 && (uint)msg == _wmTaskbarCreated)
        {
            _added = Shell_NotifyIconW(NIM_ADD, ref _nid);
            return false;
        }

        if (msg == WM_RESTORE_APP)
        {
            OpenRequested?.Invoke();
            return true;
        }

        if (msg != WM_TRAYICON) return false;

        int ev = (int)(lParam.ToInt64() & 0xFFFF);
        if (ev == WM_LBUTTONUP || ev == WM_LBUTTONDBLCLK)
        {
            OpenRequested?.Invoke();
            return true;
        }
        if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU)
        {
            ShowContextMenu();
            return true;
        }
        return false;
    }

    private void ShowContextMenu()
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            var state = QueryState?.Invoke() ?? (false, 1, false);

            AppendMenuW(hMenu, MF_STRING, (UIntPtr)CMD_OPEN, "Abrir Araújo Control Suite");
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)CMD_G15, "Abrir aba G15 Control");
            SetMenuDefaultItem(hMenu, CMD_OPEN, 0);

            AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(hMenu, MF_STRING | (!state.gmode ? MF_CHECKED : 0), (UIntPtr)CMD_FAN_ADAPT, "Ventoinhas · Modo Adaptativo (8 pts)");
            AppendMenuW(hMenu, MF_STRING | (state.gmode ? MF_CHECKED : 0), (UIntPtr)CMD_FAN_GMODE, "Ventoinhas · G-Mode (Turbina 100%)");

            AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(hMenu, MF_STRING | (state.gpuProfile == 1 ? MF_CHECKED : 0), (UIntPtr)CMD_GPU_P1, "GPU · Perfil 1 (850 mV Undervolt)");
            AppendMenuW(hMenu, MF_STRING | (state.gpuProfile == 2 ? MF_CHECKED : 0), (UIntPtr)CMD_GPU_P2, "GPU · Perfil 2 (1000 mV)");

            AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(hMenu, MF_STRING | (state.overlayOn ? MF_CHECKED : 0), (UIntPtr)CMD_OVERLAY, "Overlay de FPS (Ctrl+Shift+O)");

            AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(hMenu, MF_STRING, (UIntPtr)CMD_EXIT, "Sair completamente");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hwnd);
            int cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL para fechar o menu nativo corretamente

            if (cmd > 0)
            {
                MenuCommandRequested?.Invoke(cmd);
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            Shell_NotifyIconW(NIM_DELETE, ref _nid);
            _added = false;
        }
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
