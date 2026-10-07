using Readmd.Terminal;

namespace Readmd.Tests;

public class InputParsingTests
{
    private static List<KeyEvent> Parse(string ansi) =>
        KeyReader.ParsePortableForTest(System.Text.Encoding.ASCII.GetBytes(ansi));

    [Fact]
    public void Arrow_keys_are_decoded()
    {
        var evs = Parse("\e[A\e[B\e[C\e[D");
        Assert.Equal(
            new[] { KeyKind.Up, KeyKind.Down, KeyKind.Right, KeyKind.Left },
            evs.Select(e => e.Kind));
    }

    [Fact]
    public void Home_end_pageup_pagedown_are_decoded()
    {
        var evs = Parse("\e[H\e[F\e[5~\e[6~");
        Assert.Equal(
            new[] { KeyKind.Home, KeyKind.End, KeyKind.PageUp, KeyKind.PageDown },
            evs.Select(e => e.Kind));
    }

    [Fact]
    public void Plain_letters_become_char_events()
    {
        var evs = Parse("jkq");
        Assert.Equal(new[] { 'j', 'k', 'q' }, evs.Select(e => e.Char));
        Assert.All(evs, e => Assert.Equal(KeyKind.Char, e.Kind));
    }

    [Fact]
    public void Ctrl_letter_is_decoded_with_modifier()
    {
        // Ctrl+D = 0x04
        var evs = Parse("\u0004");
        Assert.Single(evs);
        Assert.Equal(KeyKind.Char, evs[0].Kind);
        Assert.Equal('d', evs[0].Char);
        Assert.True(evs[0].Ctrl);
    }

    [Fact]
    public void Sgr_mouse_wheel_up_and_down()
    {
        // CSI < 64 ; col ; row M  -> wheel up;  65 -> wheel down
        var up = Parse("\e[<64;10;5M");
        var down = Parse("\e[<65;10;5M");
        Assert.Equal(KeyKind.MouseScrollUp, Assert.Single(up).Kind);
        Assert.Equal(KeyKind.MouseScrollDown, Assert.Single(down).Kind);
    }

    [Fact]
    public void Sgr_mouse_left_click_carries_zero_based_position()
    {
        // CSI < 0 ; 12 ; 7 M  -> left press at col 12,row 7 (1-based) => 11,6 (0-based)
        var evs = Parse("\e[<0;12;7M");
        var e = Assert.Single(evs);
        Assert.Equal(KeyKind.MouseClick, e.Kind);
        Assert.Equal(11, e.MouseCol);
        Assert.Equal(6, e.MouseRow);
    }

    [Fact]
    public void Sgr_ctrl_left_click_sets_ctrl_modifier()
    {
        // The Ctrl modifier is bit 0x10, so a Ctrl+left-click button code is 0 | 16 = 16.
        // Ctrl+click opens links without the confirmation prompt.
        var plain = Assert.Single(Parse("\e[<0;12;7M"));
        Assert.Equal(KeyKind.MouseClick, plain.Kind);
        Assert.False(plain.Ctrl);

        var ctrl = Assert.Single(Parse("\e[<16;12;7M"));
        Assert.Equal(KeyKind.MouseClick, ctrl.Kind);
        Assert.True(ctrl.Ctrl);
        Assert.Equal(11, ctrl.MouseCol);
        Assert.Equal(6, ctrl.MouseRow);
    }

    [Fact]
    public void Sgr_mouse_right_click_is_decoded()
    {
        var evs = Parse("\e[<2;3;4M");
        Assert.Equal(KeyKind.MouseRightClick, Assert.Single(evs).Kind);
    }

    [Fact]
    public void Sgr_mouse_left_drag_and_release()
    {
        // 32 = motion + left button held (drag); 'm' final on button 0 = release.
        var drag = Parse("\e[<32;5;5M");
        var release = Parse("\e[<0;5;5m");
        Assert.Equal(KeyKind.MouseDrag, Assert.Single(drag).Kind);
        Assert.Equal(KeyKind.MouseDragEnd, Assert.Single(release).Kind);
    }

    [Fact]
    public void Escape_followed_by_a_char_in_the_same_read_is_alt_key()
    {
        // xterm "meta" encoding: Alt+q arrives as ESC q in one read.
        var evs = Parse("\eq");
        var e = Assert.Single(evs);
        Assert.Equal(KeyKind.Char, e.Kind);
        Assert.Equal('q', e.Char);
        Assert.True(e.Alt);
    }

    [Fact]
    public void Lone_escape_at_end_of_input_is_an_escape_key()
    {
        var evs = Parse("\e");
        Assert.Equal(KeyKind.Escape, Assert.Single(evs).Kind);
    }

    [Fact]
    public void Csi_modifier_parameters_set_shift_alt_ctrl()
    {
        // 1;2 = shift, 1;3 = alt, 1;5 = ctrl, 1;6 = shift+ctrl
        var shift = Assert.Single(Parse("\e[1;2A"));
        Assert.Equal(KeyKind.Up, shift.Kind); Assert.True(shift.Shift); Assert.False(shift.Ctrl);
        var alt = Assert.Single(Parse("\e[1;3C"));
        Assert.Equal(KeyKind.Right, alt.Kind); Assert.True(alt.Alt);
        var ctrl = Assert.Single(Parse("\e[1;5D"));
        Assert.Equal(KeyKind.Left, ctrl.Kind); Assert.True(ctrl.Ctrl);
        var both = Assert.Single(Parse("\e[5;6~"));
        Assert.Equal(KeyKind.PageUp, both.Kind); Assert.True(both.Shift); Assert.True(both.Ctrl);
    }

    [Fact]
    public void Back_tab_is_shift_tab()
    {
        var e = Assert.Single(Parse("\e[Z"));
        Assert.Equal(KeyKind.Tab, e.Kind);
        Assert.True(e.Shift);
    }

    [Fact]
    public void Delete_insert_and_function_keys_are_decoded()
    {
        var evs = Parse("\e[3~\e[2~\eOP\e[15~\e[24~");
        Assert.Equal(new[] { KeyKind.Delete, KeyKind.Insert, KeyKind.Function, KeyKind.Function, KeyKind.Function }, evs.Select(e => e.Kind));
        Assert.Equal(new[] { 1, 5, 12 }, evs.Where(e => e.Kind == KeyKind.Function).Select(e => e.Number));
    }

    [Fact]
    public void Bracketed_paste_is_a_single_paste_event_not_keystrokes()
    {
        // A pasted "q" must not quit: the whole paste arrives as one Paste event with the text.
        var evs = Parse("\e[200~hello q\nworld\e[201~j");
        Assert.Equal(2, evs.Count);
        Assert.Equal(KeyKind.Paste, evs[0].Kind);
        Assert.Equal("hello q\nworld", evs[0].Text);
        Assert.Equal('j', evs[1].Char);
    }

    [Fact]
    public void Window_pixel_size_report_is_swallowed_and_recorded()
    {
        // Reply to CSI 14 t: CSI 4 ; height ; width t — must never surface as a 't' keystroke.
        var evs = Parse("\e[4;600;1200t");
        Assert.Empty(evs);
        Assert.Equal((1200, 600), KeyReader.WindowPixelSize);
        // Reply to CSI 16 t: CSI 6 ; cellH ; cellW t
        Assert.Empty(Parse("\e[6;20;10t"));
        Assert.Equal((10, 20), KeyReader.CellPixelSize);
    }

    [Fact]
    public void Device_attributes_and_mode_reports_are_swallowed()
    {
        Assert.Empty(Parse("\e[?64;1;4;22c"));          // DA1 (Sixel-capable)
        Assert.Empty(Parse("\e[?2026;2$y"));           // DECRPM
        Assert.Empty(Parse("\e]11;rgb:1e1e/1e1e/1e1em\e\\")); // OSC 11 reply
        Assert.Empty(Parse("\e_Gi=1;OK\e\\"));          // kitty graphics reply
    }

    [Fact]
    public void Focus_events_are_decoded()
    {
        var evs = Parse("\e[I\e[O");
        Assert.Equal(new[] { KeyKind.FocusIn, KeyKind.FocusOut }, evs.Select(e => e.Kind));
    }

    [Fact]
    public void Utf8_multibyte_char_is_one_event()
    {
        var evs = KeyReader.ParsePortableForTest(System.Text.Encoding.UTF8.GetBytes("é"));
        Assert.Equal('é', Assert.Single(evs).Char);
    }
}
