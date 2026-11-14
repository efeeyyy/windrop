using System.Windows;
using WinForms = System.Windows.Forms;

namespace WinDrop.App.Infrastructure;

/// <summary>Bildirim alanı simgesi: Aç, Gönder…, Çıkış. Tek tık = Aç.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _drawingIcon;
    private Action? _balloonClick;

    public TrayIcon(Action open, Action send, Action exit)
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))
                   ?? throw new InvalidOperationException("Simge kaynağı bulunamadı.");
        using (info.Stream)
            _drawingIcon = new System.Drawing.Icon(info.Stream, WinForms.SystemInformation.SmallIconSize);

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Strings.Get("Tray.Open"), null, (_, _) => open());
        menu.Items.Add(Strings.Get("Tray.Send"), null, (_, _) => send());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Strings.Get("Tray.Exit"), null, (_, _) => exit());

        _icon = new WinForms.NotifyIcon
        {
            Icon = _drawingIcon,
            Text = Strings.Get("App.Title"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) open(); };
        _icon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
    }

    /// <summary>Bildirim balonu; <paramref name="onClick"/> balona tıklanınca çalışır (ör. klasörde göster).</summary>
    public void ShowBalloon(string text, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.BalloonTipTitle = Strings.Get("App.Title");
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _drawingIcon.Dispose();
    }
}
