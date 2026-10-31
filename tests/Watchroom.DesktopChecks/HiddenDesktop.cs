using System.ComponentModel;
using System.Runtime.InteropServices;

// Runs the checks again on a desktop of their own, so the windows, popups and video surfaces they open never appear
// on the screen of whoever started them. The new process inherits this one's console and output, and everything it
// starts, such as test servers, runs on that desktop too. The job object ends all of it when this process ends.
// Set WATCHROOM_TEST_VISIBLE=1 to watch the checks instead. CI runners have no one to disturb and keep the old setup,
// and --system-media-controls stays on the user's desktop because it drives the Windows media service there.
static class HiddenDesktop
{
    const string Marker = "WATCHROOM_TEST_DESKTOP", DesktopName = "WatchroomChecks";

    public static bool Relaunch(out int exitCode)
    {
        exitCode = 0;
        if (Environment.GetEnvironmentVariable(Marker) is not null || Environment.GetEnvironmentVariable("WATCHROOM_TEST_VISIBLE") == "1" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" || !Environment.UserInteractive ||
            Environment.GetCommandLineArgs().Contains("--system-media-controls")) return false;
        var desktop = CreateDesktop(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, DesktopAllAccess, IntPtr.Zero);
        if (desktop == IntPtr.Zero) { Console.Error.WriteLine($"Could not create a hidden desktop ({new Win32Exception().Message}); the checks run on this one."); return false; }
        var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (job == IntPtr.Zero || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())) throw new Win32Exception();
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"WinSta0\" + DesktopName, dwFlags = StartfUseStdHandles,
            hStdInput = Inheritable(GetStdHandle(-10)), hStdOutput = Inheritable(GetStdHandle(-11)), hStdError = Inheritable(GetStdHandle(-12)) };
        Environment.SetEnvironmentVariable(Marker, DesktopName);
        if (!CreateProcess(Environment.ProcessPath, Environment.CommandLine, IntPtr.Zero, IntPtr.Zero, true, CreateSuspended, IntPtr.Zero, null, ref startup, out var process))
            throw new Win32Exception();
        try
        {
            if (!AssignProcessToJobObject(job, process.hProcess)) throw new Win32Exception();
            ResumeThread(process.hThread);
            WaitForSingleObject(process.hProcess, uint.MaxValue);
            if (!GetExitCodeProcess(process.hProcess, out var code)) throw new Win32Exception();
            exitCode = (int)code;
            return true;
        }
        finally { CloseHandle(process.hThread); CloseHandle(process.hProcess); CloseHandle(job); CloseDesktop(desktop); }
    }

    static IntPtr Inheritable(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit);
        return handle;
    }

    const uint DesktopAllAccess = 0x01FF | 0x000F0000, CreateSuspended = 0x4, StartfUseStdHandles = 0x100, HandleFlagInherit = 1, JobObjectLimitKillOnJobClose = 0x2000;
    const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string? application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags,
        IntPtr environment, string? directory, ref STARTUPINFO startup, out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}
