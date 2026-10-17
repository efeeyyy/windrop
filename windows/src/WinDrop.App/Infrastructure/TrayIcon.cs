using System.Windows;
using WinForms = System.Windows.Forms;

namespace WinDrop.App.Infrastructure;

/// <summary>Bildirim alanı simgesi: Aç, Gönder…, Çıkış. Çift tık = Aç.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _drawingIcon;

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
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) open(); };
    }

    public void ShowBalloon(string text)
    {
        _icon.BalloonTipTitle = Strings.Get("App.Title");
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(2000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _drawingIcon.Dispose();
    }
}
