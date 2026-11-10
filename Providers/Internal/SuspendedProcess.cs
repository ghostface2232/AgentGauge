using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Gauge.Providers.Internal;

/// <summary>
/// Launches a process suspended via <c>CreateProcessW</c> so the caller can place it under a
/// <see cref="JobObject"/> before it runs a single instruction. This closes the race the
/// language server would otherwise open: it spawns sidecar children during startup, and a
/// process started normally could fork one in the window before it is assigned to the job,
/// leaking it past cleanup. Suspended-then-assign-then-resume guarantees every descendant is
/// born inside the job.
/// </summary>
internal sealed class SuspendedProcess : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int UseStandardHandles = 0x00000100;
    private static readonly IntPtr HandleListAttribute = new(0x00020002);
    private const uint StillActive = 259;

    private IntPtr _processHandle;
    private IntPtr _threadHandle;
    private bool _resumed;

    private SuspendedProcess(int processId, IntPtr processHandle, IntPtr threadHandle)
    {
        ProcessId = processId;
        _processHandle = processHandle;
        _threadHandle = threadHandle;
    }

    public int ProcessId { get; }

    public IntPtr Handle => _processHandle;

    public bool IsAlive => GetExitCodeProcess(_processHandle, out var code) && code == StillActive;

    public static SuspendedProcess Create(
        string executablePath, IReadOnlyList<string> arguments, string? workingDirectory)
        => CreateCore(executablePath, WindowsCommandLine.Join(executablePath, arguments), workingDirectory);

    internal static SuspendedProcess CreateRedirected(string executablePath, string arguments,
        string? workingDirectory, string environment, IntPtr input, IntPtr output, IntPtr error)
        => CreateCore(executablePath, WindowsCommandLine.Join(executablePath, []) + " " + arguments,
            workingDirectory, environment, [input, output, error]);

    private static SuspendedProcess CreateCore(string executablePath, string commandLine, string? workingDirectory,
        string? environment = null, IntPtr[]? handles = null)
    {
        var startupInfo = new STARTUPINFOEX { StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() } };
        var flags = CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment;
        var environmentPtr = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        var handleList = IntPtr.Zero;

        // CreateProcessW may write to the command-line buffer, so pass a mutable native copy.
        var commandLinePtr = Marshal.StringToHGlobalUni(commandLine);
        try
        {
            if (environment is not null) environmentPtr = Marshal.StringToHGlobalUni(environment);
            if (handles is not null)
            {
                // Limit inheritance to this probe's stdio even when several providers launch concurrently.
                var size = UIntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributes = Marshal.AllocHGlobal(checked((nint)size));
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                startupInfo.AttributeList = attributes;
                var bytes = handles.Length * IntPtr.Size;
                handleList = Marshal.AllocHGlobal(bytes);
                Marshal.Copy(handles, 0, handleList, handles.Length);
                if (!UpdateProcThreadAttribute(attributes, 0, HandleListAttribute, handleList, (UIntPtr)bytes, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                startupInfo.StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    dwFlags = UseStandardHandles,
                    hStdInput = handles[0],
                    hStdOutput = handles[1],
                    hStdError = handles[2],
                };
                flags |= ExtendedStartupInfoPresent;
            }
            var created = CreateProcessW(
                executablePath,
                commandLinePtr,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: handles is not null,
                flags,
                environmentPtr,
                workingDirectory,
                ref startupInfo,
                out var processInformation);

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
            }

            return new SuspendedProcess(
                processInformation.dwProcessId,
                processInformation.hProcess,
                processInformation.hThread);
        }
        finally
        {
            if (startupInfo.AttributeList != IntPtr.Zero) DeleteProcThreadAttributeList(startupInfo.AttributeList);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(handleList);
            Marshal.FreeHGlobal(environmentPtr);
            Marshal.FreeHGlobal(commandLinePtr);
        }
    }

    /// <summary>Starts the suspended primary thread. Call only after assigning to a job.</summary>
    public void Resume()
    {
        if (_threadHandle != IntPtr.Zero)
        {
            ResumeThread(_threadHandle);
            CloseHandle(_threadHandle);
            _threadHandle = IntPtr.Zero;
            _resumed = true;
        }
    }

    public void Dispose()
    {
        // A process disposed before it was resumed never got into a job and would otherwise sit
        // suspended forever once its handles close, so terminate it explicitly. After resume the
        // owning job is responsible for the kill, so we only release our handles here.
        if (!_resumed && _processHandle != IntPtr.Zero && IsAlive)
        {
            TerminateProcess(_processHandle, 1);
        }

        if (_threadHandle != IntPtr.Zero)
        {
            CloseHandle(_threadHandle);
            _threadHandle = IntPtr.Zero;
        }

        if (_processHandle != IntPtr.Zero)
        {
            CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        IntPtr lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref UIntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
        UIntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
