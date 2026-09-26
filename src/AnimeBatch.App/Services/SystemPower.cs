using System.Runtime.InteropServices;

namespace AnimeBatch.App.Services;

/// <summary>
/// Ações "quando terminar" da fila que tocam a sessão/máquina (bloquear, logoff, modo
/// espera, hibernar, desligar). Tudo por API do Windows direto — nada de shell. O app
/// roda como processo interativo do usuário: o privilégio SeShutdown existe no token,
/// mas vem DESLIGADO — o Shutdown() o habilita antes do ExitWindowsEx.
/// </summary>
internal static class SystemPower
{
    public static void Run(string action)
    {
        switch (action)
        {
            case "shutdown": Shutdown(); break;
            case "hibernate": Hibernate(); break;
            case "sleep": Sleep(); break;
            case "logoff": LogOff(); break;
            case "lock": Lock(); break;
            // "exit" é tratado pelo AppServices (fecha a janela na UI thread)
        }
    }

    public static void Lock() => LockWorkStation();

    public static void LogOff() => ExitWindowsEx(EwxLogOff, ShutdownReason);

    public static void Sleep() => SetSuspendState(false, false, false);

    public static void Hibernate() => SetSuspendState(true, false, false);

    public static void Shutdown()
    {
        EnableShutdownPrivilege();
        ExitWindowsEx(EwxShutdown | EwxPowerOff, ShutdownReason);
    }

    private const uint EwxLogOff = 0x00000000;
    private const uint EwxPowerOff = 0x00000008;
    private const uint EwxShutdown = 0x00000001;
    private const uint ShutdownReason = 0x80040000; // PLANNED | MAJOR_APPLICATION | MINOR_OTHER

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, bool disableAllPrivileges,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    /// <summary>Habilita SeShutdownPrivilege no token do processo (presente mas desligado
    /// no token interativo — sem isso o ExitWindowsEx de desligar falha com acesso negado).</summary>
    private static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            return;
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                return;
            var newState = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };
            AdjustTokenPrivileges(token, false, ref newState, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
