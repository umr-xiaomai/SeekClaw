using SeekClaw.Cli.Ui;

namespace SeekClaw.Cli.Tests;

public class TerminalBackgroundTests
{
    private static readonly Guid SelectedProfile = Guid.Parse("{61c54bbd-c2c6-5271-96e7-009a87ff44bf}");

    [Fact]
    public void ReadsSelectedProfileBackgroundBeforeInheritedBackgroundAndScheme()
    {
        const string settings = """
            {
                // Windows Terminal settings allow comments and trailing commas.
                "profiles": {
                    "defaults": { "background": "#fff" },
                    "list": [
                        { "guid": "{0caa0dad-35be-5f56-a8ff-afceeeaa6101}", "background": "#fff" },
                        {
                            "guid": "{61C54BBD-C2C6-5271-96E7-009A87FF44BF}",
                            "background": "#1a1b2c",
                            "colorScheme": "One Half Light",
                        },
                    ],
                },
            }
            """;

        Assert.False(TerminalBackground.FromWindowsTerminalSettings(settings, SelectedProfile, true)?.IsLight);
    }

    [Fact]
    public void InheritedBackgroundOverridesSelectedProfilesNamedScheme()
    {
        const string settings = """
            {
                "profiles": {
                    "defaults": { "background": "#eee" },
                    "list": [
                        { "guid": "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}", "colorScheme": "Campbell" }
                    ]
                }
            }
            """;

        Assert.True(TerminalBackground.FromWindowsTerminalSettings(settings, SelectedProfile, false)?.IsLight);
    }

    [Fact]
    public void LegacyProfilesCanOverrideBuiltInSchemeDefinitions()
    {
        const string settings = """
            {
                "profiles": [
                    { "guid": "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}", "colorScheme": "Campbell" }
                ],
                "schemes": [ { "name": "Campbell", "background": "#FAFAFA" } ]
            }
            """;

        Assert.True(TerminalBackground.FromWindowsTerminalSettings(settings, SelectedProfile, null)?.IsLight);
    }

    [Theory]
    [InlineData("\"dark\"", null, false)]
    [InlineData("\"system\"", true, true)]
    [InlineData("\"system\"", null, null)]
    [InlineData("\"Paper\"", false, true)]
    [InlineData("{ \"dark\": \"Paper\", \"light\": \"dark\" }", false, true)]
    public void ResolvesSchemePairsAgainstTheTerminalApplicationTheme(string theme, bool? systemLight, bool? expectedLight)
    {
        var settings = $$"""
            {
                "theme": {{theme}},
                "themes": [ { "name": "Paper", "window": { "applicationTheme": "light" } } ],
                "profiles": {
                    "defaults": { "colorScheme": { "light": "One Half Light", "dark": "One Half Dark" } },
                    "list": [ { "guid": "{61c54bbd-c2c6-5271-96e7-009a87ff44bf}" } ]
                }
            }
            """;

        Assert.Equal(expectedLight, TerminalBackground.FromWindowsTerminalSettings(settings, SelectedProfile, systemLight)?.IsLight);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{ \"profiles\": { \"defaults\": { \"background\": \"#fff\" }, \"list\": [] } }")]
    [InlineData("{ \"profiles\": [ { \"guid\": \"{61c54bbd-c2c6-5271-96e7-009a87ff44bf}\", \"colorScheme\": \"External\" } ] }")]
    [InlineData("{ \"profiles\": [ { \"guid\": \"{61c54bbd-c2c6-5271-96e7-009a87ff44bf}\", \"background\": \"#zzzzzz\" } ] }")]
    public void LeavesUnknownProfilesAndUnreadableSettingsUndetected(string settings)
    {
        Assert.Null(TerminalBackground.FromWindowsTerminalSettings(settings, SelectedProfile, true));
    }
}
