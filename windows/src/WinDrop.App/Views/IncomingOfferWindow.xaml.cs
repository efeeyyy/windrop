using System.Windows;
using System.Windows.Threading;
using WinDrop.App.Infrastructure;
using WinDrop.App.Services;

namespace WinDrop.App.Views;

/// <summary>Küçük, en üstte onay penceresi. 30 sn içinde yanıt yoksa ret.</summary>
public partial class IncomingOfferWindow : Window
{
    public const int TimeoutSeconds = 30;

    private readonly Action<bool> _onResult;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remaining = TimeoutSeconds;
    private bool _answered;

    public IncomingOfferWindow(IncomingOffer offer, Action<bool> onResult)
    {
        InitializeComponent();
        _onResult = onResult;
        MessageText.Text = Strings.Format("Offer.Text", offer.SenderName, offer.FileCount, ByteFormat.Size(offer.TotalBytes));
        if (offer.VerificationCode is { } code)
        {
            CodeText.Text = Strings.Format("Offer.Code", code) + "\n" + Strings.Get("Offer.Unknown");
            CodeText.Visibility = Visibility.Visible;
        }
        UpdateTimeout();
        _timer.Tick += (_, _) =>
        {
            if (--_remaining <= 0) Answer(false);
            else UpdateTimeout();
        };
        _timer.Start();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };
        Closed += (_, _) => { _timer.Stop(); Answer(false); };
    }

    private void UpdateTimeout() => TimeoutText.Text = Strings.Format("Offer.Timeout", _remaining);

    private void Answer(bool accept)
    {
        if (_answered) return;
        _answered = true;
        _timer.Stop();
        _onResult(accept);
        Close();
    }

    private void OnAccept(object sender, RoutedEventArgs e) => Answer(true);

    private void OnReject(object sender, RoutedEventArgs e) => Answer(false);
}
