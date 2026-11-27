using SeekClaw.Cli.Ui;

namespace SeekClaw.Cli.Tests;

public sealed class MarkdownMathTests
{
    [Theory]
    [InlineData(@"$\mathcal{F}\left\{f(t)\right\}(\omega)=\int_{-\infty}^{\infty} f(t)e^{-i\omega t}\,\mathrm{d}t$",
        "ℱ{f(t)}(ω)=∫_{-∞}^{∞} f(t)e^{-iω t} dt")]
    [InlineData(@"$\mathcal{L}\bigl[f(t)\bigr](s)=\int_0^\infty e^{-st}f(t)\,\mathrm dt$",
        "ℒ[f(t)](s)=∫₀^{∞} e⁻ˢᵗf(t) dt")]
    [InlineData(@"$\left\langle\widehat{\Psi}^2\right\rangle$", "⟨Ψ\u0302²⟩")]
    [InlineData(@"$\mathcal{ACZ}^{2}$", @"$\mathcal{ACZ}^{2}$")]
    [InlineData(@"$\widehat{xyz}$", @"$\widehat{xyz}$")]
    [InlineData(@"$\left\alpha x\right\rangle$", @"$\left\alpha x\right\rangle$")]
    public void TransformAndDelimiterMarkup_RendersOnlyWhenItsMeaningIsPreserved(string source, string expected)
    {
        Assert.Equal([expected], MarkdownAnsi.Render(source, 100).Select(Ansi.StripStyles));
    }
}
