using System.Windows;
using System.Windows.Threading;
using WinDrop.App.Infrastructure;
using WinDrop.App.Services;

namespace WinDrop.App.Views;

/// <summary>
/// Aktarma sunucusu (internet/hücresel veri) kullanımı için onay (PROTOCOL §6.3). 60 sn içinde yanıt yoksa iptal;
/// onaylanmadan aktarma sunucusuna bağlanılmaz.
/// </summary>
public partial class RelayApprovalWindow : Window
{
    public const int TimeoutSeconds = 60;

    private readonly Action<bool> _onResult;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remaining = TimeoutSeconds;
    private bool _answered;

    public RelayApprovalWindow(RelayApproval approval, Action<bool> onResult)
    {
        InitializeComponent();
        _onResult = onResult;
        IntroText.Text = Strings.Format("Relay.Intro", approval.DeviceName);
        MessageText.Text = Strings.Format("Relay.Warning", ByteFormat.Size(approval.TotalBytes));
        UpdateTimeout();
        _timer.Tick += (_, _) =>
        {
            if (--_remaining <= 0) Answer(false);
            else UpdateTimeout();
        };
        _timer.Start();
        PopupStack.Add(this);
        Closed += (_, _) => { _timer.Stop(); Answer(false); };
    }

    private void UpdateTimeout() => TimeoutText.Text = Strings.Format("Relay.Timeout", _remaining);

    private void Answer(bool allow)
    {
        if (_answered) return;
        _answered = true;
        _timer.Stop();
        _onResult(allow);
        Close();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => Answer(true);

    private void OnCancel(object sender, RoutedEventArgs e) => Answer(false);
}
