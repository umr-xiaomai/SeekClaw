using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SeekClaw.Runtime.Tools;

/// <summary>
/// Cross-platform process scope that on Windows encapsulates a Job Object
/// with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE to atomically kill child process trees on exit/timeout.
/// On non-Windows platforms it tracks a process-group id when the caller launched the
/// process as a group leader (for example through setsid).
/// </summary>
public sealed class ProcessJobScope : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int JobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private readonly IntPtr _handle;
    private int _processGroupId;
    private bool _disposed;

    public ProcessJobScope()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero) return;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            var length = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal((int)length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, length);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            _handle = IntPtr.Zero;
        }
    }

    public void AssignProcess(Process process, bool processGroupLeader = false)
    {
        if (_disposed) return;
        if (!OperatingSystem.IsWindows())
        {
            // Only signal a negative PGID when the child is actually its group leader.
            // Using an arbitrary PID as a PGID can terminate an unrelated process group.
            _processGroupId = processGroupLeader ? process.Id : 0;
            return;
        }
        if (_handle == IntPtr.Zero) return;
        try
        {
            if (!process.HasExited)
            {
                AssignProcessToJobObject(_handle, process.Handle);
            }
        }
        catch
        {
            // Process may have already exited or access denied
        }
    }

    /// <summary>
    /// Kills the process and every descendant. On Unix the shell is started in its own
    /// session/process group, so signalling the negative PGID also catches children that
    /// have already been reparented after the shell exits.
    /// </summary>
    public void Terminate(Process? process)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
            return;
        }

        if (_processGroupId > 0)
        {
            try { kill(-_processGroupId, 9); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }

        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (OperatingSystem.IsWindows() && _handle != IntPtr.Zero)
        {
            try
            {
                CloseHandle(_handle);
            }
            catch
            {
                // Handle close failure suppression
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryLimit;
        public UIntPtr PeakJobMemoryLimit;
    }
}
