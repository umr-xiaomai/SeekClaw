using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SeekClaw.Cli.Ui;

/// <summary>Reads the active terminal background when it can be inferred without querying stdin.</summary>
internal static class TerminalBackground
{
    private static readonly JsonDocumentOptions SettingsJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // Windows Terminal allows a user-defined scheme with a built-in name. Consult JSON first.
    private static readonly Dictionary<string, int> BuiltInSchemeBackgrounds = new(StringComparer.Ordinal)
    {
        ["Campbell"] = 0x0C0C0C,
        ["Campbell Powershell"] = 0x012456,
        ["CGA"] = 0x000000,
        ["Dark+"] = 0x1E1E1E,
        ["Dimidium"] = 0x141414,
        ["IBM 5153"] = 0x000000,
        ["One Half Dark"] = 0x282C34,
        ["One Half Light"] = 0xFAFAFA,
        ["Ottosson"] = 0x000000,
        ["Solarized Dark"] = 0x002B36,
        ["Solarized Light"] = 0xFDF6E3,
        ["Tango Dark"] = 0x000000,
        ["Tango Light"] = 0xFFFFFF,
        ["Vintage"] = 0x000000,
        ["VSCode Dark Modern"] = 0x1F1F1F,
        ["VSCode Light Modern"] = 0xFFFFFF,
    };

    internal static CliPalette Detect()
    {
        if (Console.IsOutputRedirected) return CliPalette.Auto;
        if (OperatingSystem.IsWindows())
        {
            var windows = ReadClassicConsole() ?? ReadWindowsTerminal();
            if (windows is not null) return windows;
        }

        return CliPalette.ForTerminalBackground(Environment.GetEnvironmentVariable("COLORFGBG"));
    }

    [SupportedOSPlatform("windows")]
    private static CliPalette? ReadWindowsTerminal()
    {
        if (!Guid.TryParse(Environment.GetEnvironmentVariable("WT_PROFILE_ID"), out var profileId) ||
            profileId == Guid.Empty) return null;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length == 0) return null;

        string[] settingsPaths =
        [
            @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json",
            @"Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json",
            @"Packages\Microsoft.WindowsTerminalCanary_8wekyb3d8bbwe\LocalState\settings.json",
            @"Microsoft\Windows Terminal\settings.json",
        ];
        var systemLight = WindowsUsesLightAppTheme();
        CliPalette? result = null;
        foreach (var path in settingsPaths)
        {
            var palette = ReadSettings(Path.Combine(local, path), profileId, systemLight);
            if (palette is null) continue;
            // A profile GUID can be shared by two installed variants. Conflicting results are ambiguous.
            if (result is not null && result != palette) return null;
            result = palette;
        }
        return result;
    }

    private static CliPalette? ReadSettings(string path, Guid profileId, bool? systemLight)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Keep optional startup detection bounded even if this is an unexpected or corrupt file.
            if (file.Length > 4 * 1024 * 1024) return null;
            using var reader = new StreamReader(file);
            return FromWindowsTerminalSettings(reader.ReadToEnd(), profileId, systemLight);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    internal static CliPalette? FromWindowsTerminalSettings(string json, Guid profileId, bool? systemLight)
    {
        try
        {
            using var document = JsonDocument.Parse(json, SettingsJson);
            var root = document.RootElement;
            var profiles = Property(root, "profiles");
            var profileDefaults = Property(profiles, "defaults");
            var profileList = profiles.ValueKind == JsonValueKind.Array ? profiles : Property(profiles, "list");
            var profile = Find(profileList, item => Guid.TryParse(Text(Property(item, "guid")), out var id) && id == profileId);
            if (profile.ValueKind == JsonValueKind.Undefined) return null;

            // A background field overrides the selected scheme, even when inherited from profile defaults.
            var background = Text(Property(profile, "background")) ?? Text(Property(profileDefaults, "background"));
            if (background is not null) return FromHex(background);

            var profileScheme = Property(profile, "colorScheme");
            var inheritedScheme = Property(profileDefaults, "colorScheme");
            var schemes = Property(root, "schemes");
            CliPalette? Resolve(bool appLight)
            {
                var name = ThemedName(profileScheme, appLight) ?? ThemedName(inheritedScheme, appLight) ?? "Campbell";
                var scheme = Find(schemes, item => Text(Property(item, "name")) == name);
                if (scheme.ValueKind != JsonValueKind.Undefined)
                    return FromHex(Text(Property(scheme, "background")));
                return BuiltInSchemeBackgrounds.TryGetValue(name, out var rgb) ? FromRgb24(rgb) : null;
            }

            if (ApplicationUsesLightTheme(root, systemLight) is { } appLight) return Resolve(appLight);
            var dark = Resolve(false);
            return dark == Resolve(true) ? dark : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool? ApplicationUsesLightTheme(JsonElement root, bool? systemLight)
    {
        var theme = ThemedName(Property(root, "theme"), systemLight);
        if (theme == "system") return systemLight;
        if (theme == "light") return true;
        if (theme == "dark") return false;
        if (theme is null) return null;

        var custom = Find(Property(root, "themes"), item => Text(Property(item, "name")) == theme);
        if (custom.ValueKind == JsonValueKind.Undefined) return null;
        return Text(Property(Property(custom, "window"), "applicationTheme")) switch
        {
            "light" => true,
            "dark" => false,
            _ => systemLight,
        };
    }

    private static string? ThemedName(JsonElement value, bool? light) => Text(value) ?? (light switch
    {
        false => Text(Property(value, "dark")),
        true => Text(Property(value, "light")),
        _ => null,
    });

    private static JsonElement Property(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement item) => item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static JsonElement Find(JsonElement array, Func<JsonElement, bool> predicate)
    {
        if (array.ValueKind != JsonValueKind.Array) return default;
        foreach (var item in array.EnumerateArray())
            if (predicate(item)) return item;
        return default;
    }

    private static CliPalette? FromHex(string? color)
    {
        if (color is null || color.Length is not (4 or 7) || color[0] != '#' ||
            !int.TryParse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
            return null;
        return color.Length == 4
            ? CliPalette.ForTerminalBackground((rgb >> 8) * 17, ((rgb >> 4) & 15) * 17, (rgb & 15) * 17)
            : FromRgb24(rgb);
    }

    private static CliPalette FromRgb24(int rgb) => CliPalette.ForTerminalBackground(rgb >> 16, (rgb >> 8) & 255, rgb & 255);

    [SupportedOSPlatform("windows")]
    private static bool? WindowsUsesLightAppTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") switch
            {
                0 => false,
                1 => true,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static CliPalette? ReadClassicConsole()
    {
        // A pseudoconsole can return its own palette, unrelated to the color the user sees.
        var window = GetConsoleWindow();
        if (window == 0) return null;
        var name = new StringBuilder(32);
        if (GetClassNameW(window, name, name.Capacity) == 0 || name.ToString() != "ConsoleWindowClass") return null;

        const int stdout = -11;
        var info = new ConsoleScreenBufferInfo
        {
            Size = (uint)Marshal.SizeOf<ConsoleScreenBufferInfo>(),
            ColorTable = new uint[16],
        };
        if (!GetConsoleScreenBufferInfoEx(GetStdHandle(stdout), ref info)) return null;
        var color = info.ColorTable[(info.Attributes >> 4) & 15];
        // COLORREF stores the red channel in its least significant byte.
        return CliPalette.ForTerminalBackground((int)(color & 255), (int)((color >> 8) & 255), (int)((color >> 16) & 255));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public uint Size;
        public short Columns, Rows, CursorX, CursorY;
        public ushort Attributes;
        public short Left, Top, Right, Bottom, MaxColumns, MaxRows;
        public ushort PopupAttributes;
        public int FullscreenSupported;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public uint[] ColorTable;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfoEx(nint console, ref ConsoleScreenBufferInfo info);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, StringBuilder className, int capacity);
}
