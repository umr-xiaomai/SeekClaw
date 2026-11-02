using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SeekClaw.Runtime.Execution;

/// <summary>Windows 10+ ConPTY. Native handles are owned here; command sessions own the streams.</summary>
internal sealed class WindowsPseudoConsole : IDisposable
{
    private nint _handle;
    private WindowsPseudoConsole(nint handle) => _handle = handle;

    public static CommandProcess Start(string command, string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!CreatePipe(out var inputRead, out var inputWrite, 0, 0)) throw new Win32Exception();
        using (inputRead)
        {
            if (!CreatePipe(out var outputRead, out var outputWrite, 0, 0))
            { inputWrite.Dispose(); throw new Win32Exception(); }
            using (outputWrite)
            {
                WindowsPseudoConsole? console = null;
                FileStream? input = null, output = null;
                nint attributes = 0;
                var initialized = false;
                try
                {
                    Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coord(120, 30), inputRead, outputWrite, 0, out var handle));
                    console = new WindowsPseudoConsole(handle);
                    nuint size = 0;
                    InitializeProcThreadAttributeList(0, 1, 0, ref size);
                    attributes = Marshal.AllocHGlobal(checked((nint)size));
                    if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
                    initialized = true;
                    if (!UpdateProcThreadAttribute(attributes, 0, 0x00020016, handle, (nuint)nint.Size, 0, 0))
                        throw new Win32Exception();
                    var startup = new StartupInfoEx
                    {
                        StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() },
                        Attributes = attributes,
                    };
                    // EncodedCommand avoids Windows command-line quoting ambiguity for arbitrary scripts.
                    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
                    var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "WindowsPowerShell", "v1.0", "powershell.exe");
                    var launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                    // Native hosts can have redirected/invalid standard handles. Reopen the attached
                    // console devices in the child rather than inheriting those handles.
                    var commandLine = new StringBuilder($"\"{launcher}\" /d /s /c \"\"{shell}\" -NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded} <CONIN$ >CONOUT$ 2>&1\"");
                    if (!CreateProcess(launcher, commandLine, 0, 0, false, 0x00080000 | 0x00000400,
                            0, directory, ref startup, out var info)) throw new Win32Exception();
                    using var processHandle = new SafeFileHandle(info.Process, true);
                    using var threadHandle = new SafeFileHandle(info.Thread, true);
                    var process = Process.GetProcessById(info.ProcessId);
                    try
                    {
                        input = new FileStream(inputWrite, FileAccess.Write, 4096, false);
                        output = new FileStream(outputRead, FileAccess.Read, 4096, false);
                        return new CommandProcess(process, input, output, null, true, console: console);
                    }
                    catch { try { process.Kill(true); } catch (InvalidOperationException) { } process.Dispose(); throw; }
                }
                catch
                {
                    // Close the reader before ConPTY to avoid a blocked synchronous output flush.
                    output?.Dispose(); outputRead.Dispose();
                    input?.Dispose(); inputWrite.Dispose();
                    console?.Dispose();
                    throw;
                }
                finally
                {
                    if (initialized) DeleteProcThreadAttributeList(attributes);
                    if (attributes != 0) Marshal.FreeHGlobal(attributes);
                }
            }
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) ClosePseudoConsole(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y) { public readonly short X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public nint Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint security, uint size);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(nint console);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, nint processSecurity,
        nint threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation info);
}
