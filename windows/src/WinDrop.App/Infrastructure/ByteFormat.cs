using System.Globalization;

namespace WinDrop.App.Infrastructure;

public static class ByteFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Size(long bytes)
    {
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return string.Create(Tr, $"{v:0.#} {Units[u]}");
    }

    public static string Speed(double bytesPerSecond) => Size((long)bytesPerSecond) + "/sn";
}
