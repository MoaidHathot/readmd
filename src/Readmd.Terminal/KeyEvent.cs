namespace Readmd.Terminal;

/// <summary>The semantic kind of a parsed input event from the console.</summary>
public enum KeyKind
{
    Char,       // a printable/letter key in <see cref="KeyEvent.Char"/>
    Enter,
    Escape,
    Backspace,
    Tab,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    Delete,
    Insert,
    Function,           // F1..F12 in <see cref="KeyEvent.Number"/>
    Paste,              // bracketed paste: the pasted text is in <see cref="KeyEvent.Text"/>
    FocusIn,            // terminal window gained focus (when focus reporting is enabled)
    FocusOut,           // terminal window lost focus
    MouseScrollUp,
    MouseScrollDown,
    MouseClick,         // left-button press at (MouseRow, MouseCol)
    MouseDrag,          // mouse moved with the left button held — extend a selection
    MouseDragEnd,       // left-button released — finish a drag selection
    MouseRightClick,    // right-button press at (MouseRow, MouseCol)
    MouseMiddleClick,   // middle-button press at (MouseRow, MouseCol)
}

/// <summary>
/// An input event parsed from the console. For <see cref="KeyKind.Char"/>, <see cref="Char"/>
/// holds the character; <see cref="Ctrl"/>, <see cref="Alt"/> and <see cref="Shift"/> report
/// modifiers (Shift is only meaningful for non-character keys — a shifted letter simply arrives as
/// its uppercase character, exactly as the terminal sends it). Mouse wheel events use
/// <see cref="KeyKind.MouseScrollUp"/>/<see cref="KeyKind.MouseScrollDown"/>; mouse position
/// events carry <see cref="MouseRow"/>/<see cref="MouseCol"/>.
/// </summary>
public sealed record KeyEvent(
    KeyKind Kind,
    char Char = '\0',
    bool Ctrl = false,
    int MouseRow = 0,
    int MouseCol = 0,
    bool Alt = false,
    bool Shift = false,
    int Number = 0,
    string? Text = null)
{
    /// <summary>True when no Ctrl/Alt modifier is held (Shift is implied by the character for letters).</summary>
    public bool Plain => !Ctrl && !Alt;
}
