using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SeekClaw.Runtime.Configuration;

namespace SeekClaw.Runtime.Mcp;

/// <summary>OAuth tokens never enter project configuration or protocol responses.</summary>
public sealed class McpCredentialStore(string? directory = null)
{
    private readonly string _directory = directory ?? Path.Combine(SeekClawPaths.Home, "credentials", "mcp");
    internal string LockDirectory => Path.Combine(_directory, "locks");
    private string PathFor(string key) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".json");

    public JsonObject? Get(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (OperatingSystem.IsWindows()) bytes = Protect(bytes, decrypt: true);
        return JsonNode.Parse(bytes) as JsonObject;
    }

    public void Put(string key, JsonObject value)
    {
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = PathFor(key);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
        if (OperatingSystem.IsWindows()) bytes = Protect(bytes, decrypt: false);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options)) { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Remove(string key) => File.Delete(PathFor(key));

    private static byte[] Protect(byte[] input, bool decrypt)
    {
        var memory = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, memory, input.Length);
            var blob = new DataBlob { Size = input.Length, Data = memory };
            var ok = decrypt ? CryptUnprotectData(ref blob, 0, 0, 0, 0, 1, out var output)
                : CryptProtectData(ref blob, null, 0, 0, 0, 1, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, nint entropy, nint reserved,
        nint prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description, nint entropy, nint reserved,
        nint prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
