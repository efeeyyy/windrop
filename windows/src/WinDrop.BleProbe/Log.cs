namespace WinDrop.BleProbe;

internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write(message, null);

    public static void Error(string message) => Write(message, ConsoleColor.Red);

    private static void Write(string message, ConsoleColor? color)
    {
        lock (Gate)
        {
            var prev = Console.ForegroundColor;
            if (color is { } c) Console.ForegroundColor = c;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            if (color is not null) Console.ForegroundColor = prev;
        }
    }
}
