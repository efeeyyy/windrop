using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using WinDrop.App.Infrastructure;
using WinDrop.App.Services;
using WinDrop.Core.Protocol;

namespace WinDrop.App.Views;

/// <summary>
/// Küçük, en üstte, odağı çalmayan onay penceresi. 60 sn içinde yanıt yoksa ret (docs\review\DECISIONS.md madde 3).
/// Varsayılan düğme yoktur: Enter teklifi kabul etmez.
/// </summary>
public partial class IncomingOfferWindow : Window
{
    public const int TimeoutSeconds = 60;
    private const int MaxListedFiles = 5;

    private readonly Action<bool> _onResult;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remaining = TimeoutSeconds;
    private bool _answered;

    public IncomingOfferWindow(IncomingOffer offer, Action<bool> onResult)
    {
        InitializeComponent();
        _onResult = onResult;
        MessageText.Text = Strings.Plural("Offer.Text", offer.FileCount, offer.SenderName, offer.FileCount, ByteFormat.Size(offer.TotalBytes));
        // PROTOCOL §6.7: preview girdisi "/" ile bitiyorsa klasör adıdır. Adlar karşı cihazdan geldiği için temizlenerek gösterilir.
        var preview = (offer.Preview ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        var folders = preview.Where(p => p.EndsWith('/')).Select(p => FileNameSanitizer.Sanitize(p)).ToArray();
        var files = preview.Where(p => !p.EndsWith('/')).Select(p => FileNameSanitizer.Sanitize(p)).ToArray();
        if (files.Length > 0)
        {
            int extra = Math.Max(files.Length, offer.FileCount) - Math.Min(files.Length, MaxListedFiles);
            var text = string.Join(", ", files.Take(MaxListedFiles));
            FilesText.Text = extra > 0 ? Strings.Format("Offer.FilesMore", text, extra) : Strings.Format("Offer.Files", text);
            FilesText.Visibility = Visibility.Visible;
        }
        if (folders.Length > 0)
        {
            FoldersText.Text = Strings.Format("Offer.Folders", string.Join(", ", folders));
            FoldersText.Visibility = Visibility.Visible;
        }
        if (offer.VerificationCode is { } code)
        {
            CodeText.Text = Strings.Format("Offer.Code", code) + "\n" + Strings.Get("Offer.Unknown");
            CodeText.Visibility = Visibility.Visible;
        }
        AutomationProperties.SetName(this, Strings.Get("Offer.Title") + ": " + MessageText.Text);
        AutomationProperties.SetLiveSetting(TimeoutText, AutomationLiveSetting.Off);
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
