using System.Windows;

namespace WinDrop.App.Views;

/// <summary>
/// Sağ alt köşedeki bildirim tarzı pencereleri (gelen teklif, aktarma onayı) üst üste bindirmeden yukarı doğru dizer;
/// biri kapanınca kalanlar yeniden dizilir.
/// </summary>
public static class PopupStack
{
    private const double Gap = 12;
    private static readonly List<Window> Open = [];

    public static void Add(Window window)
    {
        Open.Add(window);
        window.Loaded += (_, _) => Layout();
        window.SizeChanged += (_, _) => Layout();
        window.Closed += (_, _) => { Open.Remove(window); Layout(); };
    }

    private static void Layout()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - 16;
        foreach (var w in Open)
        {
            if (!w.IsLoaded) continue;
            w.Left = area.Right - w.ActualWidth - 16;
            w.Top = bottom - w.ActualHeight;
            bottom = w.Top - Gap;
        }
    }
}
