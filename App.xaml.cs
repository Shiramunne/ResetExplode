using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace ResetExplode;

public partial class App : Application
{
    // Uma única instância: abrir o ARAÚJO CONTROL SUITE de novo traz a janela que já está aberta (mesmo se estiver na bandeja) para a frente.
    private static Mutex? _single;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    protected override void OnStartup(StartupEventArgs e)
    {
        bool first;
        try { _single = new Mutex(true, @"Local\ResetExplode.SingleInstance", out first); }
        catch (UnauthorizedAccessException) { first = false; } // criado por outra instância com outro nível de permissão

        if (!first)
        {
            IntPtr h = FindWindow(null, "ARAÚJO CONTROL SUITE");
            if (h == IntPtr.Zero) h = FindWindow(null, "RESET EXPLODE");
            if (h != IntPtr.Zero)
            {
                PostMessageW(h, TrayIcon.WM_RESTORE_APP, IntPtr.Zero, IntPtr.Zero);
                if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
                else ShowWindow(h, 5);             // SW_SHOW
                SetForegroundWindow(h);
                Shutdown();
                return;
            }

            // Se outra instância estava presa em outro desktop (ex.: sessão remota/sandbox) sem janela visível aqui,
            // encerra a instância órfã e abre normalmente no desktop atual do usuário.
            int myPid = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("AraujoControlSuite"))
            {
                if (p.Id != myPid)
                {
                    try { p.Kill(); p.WaitForExit(1500); } catch { }
                }
            }
        }
        base.OnStartup(e);
        var win = new MainWindow();
        win.Show();
        if (win.WindowState != WindowState.Minimized)
        {
            win.Activate();
        }
    }
}
