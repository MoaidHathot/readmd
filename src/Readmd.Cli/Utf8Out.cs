using System.Text;

/// <summary>
/// Writes UTF-8 straight to the stdout handle. <c>Console.Out</c> transcodes to the console's code
/// page, which on Windows turns "✓" and "·" into "?" when output is piped or the code page isn't
/// 65001 — and changing <c>Console.OutputEncoding</c> would alter the user's console for good.
/// </summary>
internal static class Utf8Out
{
    private static readonly Stream Stdout = Console.OpenStandardOutput();

    public static void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Stdout.Write(bytes, 0, bytes.Length);
        Stdout.Flush();
    }
}