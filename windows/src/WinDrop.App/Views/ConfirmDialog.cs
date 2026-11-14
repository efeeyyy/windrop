using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using WinDrop.App.Infrastructure;

namespace WinDrop.App.Views;

/// <summary>
/// Uygulama dilinde evet/hayır onayı (<see cref="MessageBox"/> düğmeleri işletim sistemi diliyle çıkar).
/// Varsayılan düğme "Hayır"dır (Enter kazara yıkıcı eylemi onaylamasın), Esc = Hayır.
/// </summary>
public sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string message, string yes, string no)
    {
        Title = Strings.Get("App.Title");
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 420;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var yesButton = new Button { Content = yes, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var noButton = new Button { Content = no, MinWidth = 90, IsDefault = true, IsCancel = true };
        yesButton.Click += (_, _) => { DialogResult = true; };
        noButton.Click += (_, _) => { DialogResult = false; };
        AutomationProperties.SetName(this, message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(yesButton);
        buttons.Children.Add(noButton);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(text);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => noButton.Focus();
    }

    /// <summary>true = onaylandı. <paramref name="owner"/> görünür değilse ortalanır.</summary>
    public static bool Ask(Window? owner, string message, string yes, string no)
    {
        var dialog = new ConfirmDialog(message, yes, no);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }
}
