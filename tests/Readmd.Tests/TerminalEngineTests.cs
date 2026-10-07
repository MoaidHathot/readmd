using Readmd.Terminal;
using SkiaSharp;

namespace Readmd.Tests;

/// <summary>Terminal engine: screen diffing/synchronized output, colour quantization, capability
/// probe parsing, graphics encoders, Sixel palette building, ASCII glyph set, Unicode widths.</summary>
public class TerminalEngineTests
{
    [Fact]
    public void Capture_screen_composes_rows_with_reset_prefix_and_positions()
    {
        var s = AnsiScreen.CreateCapture(20, 3);
        s.BeginFrame();
        s.MoveTo(1, 2).Write("hi");
        s.MoveTo(0, 0).Write("top");
        var rows = AnsiText.ToRows(s.CaptureBuffer, 20, 3);
        Assert.Equal("top", rows[0]);
        Assert.Equal("  hi", rows[1]);
        Assert.Contains("\e[0m\e[2;3H", s.CaptureBuffer);   // row starts clean, then positions
    }

    [Fact]
    public void Color_modes_downgrade_truecolor()
    {
        var s = AnsiScreen.CreateCapture(10, 2);
        s.Colors = ColorMode.Ansi256;
        s.BeginFrame();
        s.MoveTo(0, 0).SetForeground(new Rgb(255, 0, 0)).Write("x");
        Assert.Contains("\e[38;5;196m", s.CaptureBuffer);
        s.Colors = ColorMode.Ansi16;
        s.BeginFrame();
        s.MoveTo(0, 0).SetForeground(new Rgb(255, 0, 0)).SetBackground(new Rgb(0, 0, 0)).Write("x");
        Assert.Contains("\e[31m", s.CaptureBuffer);   // (255,0,0) is nearest the normal red of the 16-colour palette
        Assert.Contains("\e[40m", s.CaptureBuffer);
        s.Colors = ColorMode.None;
        s.BeginFrame();
        s.MoveTo(0, 0).SetForeground(new Rgb(255, 0, 0)).Write("x");
        Assert.DoesNotContain("38;", s.CaptureBuffer);
    }

    [Theory]
    [InlineData(0, 0, 0, 16)]
    [InlineData(255, 255, 255, 231)]
    [InlineData(128, 128, 128, 244)]
    [InlineData(255, 0, 0, 196)]
    [InlineData(0, 255, 0, 46)]
    [InlineData(0, 0, 255, 21)]
    public void Ansi256_quantization_hits_expected_indices(int r, int g, int b, int expected)
    {
        Assert.Equal(expected, ColorQuantizer.ToAnsi256(new Rgb((byte)r, (byte)g, (byte)b)));
    }

    [Fact]
    public void Hyperlinks_emit_osc8_pairs_and_can_be_disabled()
    {
        var s = AnsiScreen.CreateCapture(20, 2);
        s.BeginFrame();
        s.MoveTo(0, 0).BeginHyperlink("https://example.com", "l1").Write("link").EndHyperlink();
        Assert.Contains("\e]8;id=l1;https://example.com\e\\link\e]8;;\e\\", s.CaptureBuffer);
        s.Hyperlinks = false;
        s.BeginFrame();
        s.MoveTo(0, 0).BeginHyperlink("https://example.com").Write("link").EndHyperlink();
        Assert.DoesNotContain("\e]8", s.CaptureBuffer);
    }

    [Fact]
    public void Tmux_passthrough_wraps_and_doubles_escapes()
    {
        var s = AnsiScreen.CreateCapture(20, 2);
        s.TmuxPassthrough = true;
        var wrapped = s.Passthrough("\ePq\e\\");
        Assert.Equal("\ePtmux;\e\ePq\e\e\\\e\\", wrapped);
    }

    [Fact]
    public void Probe_parses_device_attributes_kitty_and_background()
    {
        TerminalProbe.Reset();
        TerminalProbe.OnReport("[?64;1;2;4;6;9;15;22c");
        Assert.True(TerminalProbe.Sixel);
        TerminalProbe.OnReport("_Gi=31;OK");
        Assert.True(TerminalProbe.Kitty);
        TerminalProbe.OnReport("]11;rgb:1e1e/2a2a/3f3f");
        Assert.Equal(new Rgb(0x1e, 0x2a, 0x3f), TerminalProbe.Background);
        TerminalProbe.OnReport("[?2026;2$y");
        Assert.True(TerminalProbe.SynchronizedOutput);
        Assert.Equal(GraphicsMode.Kitty, TerminalProbe.Refine(GraphicsMode.HalfBlock));
        TerminalProbe.Reset();
        TerminalProbe.OnReport("[?62;22c");
        Assert.False(TerminalProbe.Sixel);
        Assert.Equal(GraphicsMode.HalfBlock, TerminalProbe.Refine(GraphicsMode.Sixel));
        TerminalProbe.Reset();
    }

    [Fact]
    public void Portable_reader_routes_replies_to_the_probe()
    {
        TerminalProbe.EnsureHooked();
        TerminalProbe.Reset();
        KeyReader.ParsePortableForTest(System.Text.Encoding.ASCII.GetBytes("\e[?64;4c\e_Gi=31;OK\e\\\e]11;rgb:0000/0000/0000\e\\"));
        Assert.True(TerminalProbe.Sixel);
        Assert.True(TerminalProbe.Kitty);
        Assert.Equal(new Rgb(0, 0, 0), TerminalProbe.Background);
        TerminalProbe.Reset();
    }

    [Fact]
    public void Kitty_encoder_chunks_and_flags_last_chunk()
    {
        using var bmp = new SKBitmap(64, 64);
        bmp.Erase(SKColors.Red);
        var seq = KittyEncoder.Encode(bmp, 8, 4, 123);
        Assert.StartsWith("\e_Ga=T,f=100,q=2,C=1,i=123,c=8,r=4,m=", seq);
        Assert.EndsWith("\e\\", seq);
        Assert.Contains("m=0;", seq);
        Assert.NotEqual(0u, KittyEncoder.IdFor("abc"));
        Assert.Equal(KittyEncoder.IdFor("abc"), KittyEncoder.IdFor("abc"));
    }

    [Fact]
    public void Iterm2_encoder_declares_size_and_cells()
    {
        using var bmp = new SKBitmap(8, 8);
        bmp.Erase(SKColors.Blue);
        var seq = ITerm2Encoder.Encode(bmp, 4, 2);
        Assert.StartsWith("\e]1337;File=inline=1;size=", seq);
        Assert.Contains(";width=4;height=2;", seq);
        Assert.EndsWith("\a", seq);
    }

    [Fact]
    public void Sixel_uses_exact_palette_for_few_colours_and_quantizes_many()
    {
        var few = new int[] { 0xFF0000, 0x00FF00, 0x0000FF, 0xFF0000 };
        var p = SixelEncoder.BuildPalette(few, out bool exact);
        Assert.True(exact);
        Assert.Equal(3, p.Count);

        var many = new int[300 * 300];
        for (int i = 0; i < many.Length; i++) many[i] = ((i % 256) << 16) | (((i / 256) % 256) << 8) | ((i * 7) % 256);
        var q = SixelEncoder.BuildPalette(many, out exact);
        Assert.False(exact);
        Assert.InRange(q.Count, 2, 256);
    }

    [Fact]
    public void Sixel_output_is_valid_dcs_with_registered_palette()
    {
        using var bmp = new SKBitmap(12, 7);
        bmp.Erase(new SKColor(10, 200, 30));
        var seq = SixelEncoder.Encode(bmp, new Rgb(0, 0, 0));
        Assert.StartsWith("\eP0;1;0q\"1;1;12;7", seq);
        Assert.Contains("#0;2;", seq);
        Assert.EndsWith("\e\\", seq);
    }

    [Fact]
    public void Ascii_glyphs_render_without_box_drawing()
    {
        Glyphs.Ascii = true;
        try
        {
            var text = Render.Text("# T\n\n- a\n- [x] b\n\n> q\n\n| h |\n|---|\n| c |\n\n```js\nx\n```");
            Assert.DoesNotContain("│", text);
            Assert.DoesNotContain("─", text);
            Assert.Contains("* a", text);
            Assert.Contains("[x] b", text);
            Assert.Contains("| q", text);
            Assert.Contains("+---", text);
        }
        finally { Glyphs.Ascii = false; }
    }

    [Theory]
    [InlineData("abc", 3)]
    [InlineData("日本語", 6)]
    [InlineData("한국어", 6)]
    [InlineData("🚀", 2)]
    [InlineData("👨‍👩‍👧", 2)]      // ZWJ family: one cluster, two cells
    [InlineData("🇺🇸", 2)]        // flag
    [InlineData("e\u0301", 1)]  // combining accent
    [InlineData("✓", 1)]        // narrow dingbat
    [InlineData("✔️", 2)]       // emoji presentation selector forces wide
    [InlineData("1️⃣", 2)]       // keycap
    [InlineData("\u200b", 0)]   // zero-width space
    public void Display_widths_follow_unicode_rules(string s, int expected)
    {
        Assert.Equal(expected, TextWidthTestHook.Of(s));
    }

    [Fact]
    public void Ambiguous_width_is_opt_in()
    {
        Assert.Equal(1, TextWidthTestHook.Of("①"));
        TextWidthSettings.AmbiguousWide = true;
        try { Assert.Equal(2, TextWidthTestHook.Of("①")); }
        finally { TextWidthSettings.AmbiguousWide = false; }
    }

    [Fact]
    public void Ansi_text_reconstructs_wide_glyphs_and_clears()
    {
        var rows = AnsiText.ToRows("\e[1;1H日本\e[2;1Habc\e[2;2H\e[K", 10, 2);
        Assert.Equal("日本", rows[0]);
        Assert.Equal("a", rows[1]);
    }
}

internal static class TextWidthTestHook
{
    public static int Of(string s) => TextWidth.Of(s);
}
