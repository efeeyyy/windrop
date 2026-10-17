using WinDrop.Core.Transfer;

namespace WinDrop.Platform;

/// <summary>Alınan her dosyaya Mark-of-the-Web (Zone.Identifier) yazar (PROTOCOL §5).</summary>
public sealed class MotwCommitHook : IFileCommitHook
{
    /// <summary>Taşımadan önce <c>.part</c> dosyasına yazılır: aynı sürücüde akış taşımayla gelir, işaretsiz pencere kalmaz.</summary>
    public ValueTask OnBeforeCommitAsync(string partPath, CancellationToken cancellationToken)
    {
        MarkOfTheWeb.TryMark(partPath);   // NTFS değilse sessizce geçer; taşıma sonrası yeniden denenir
        return ValueTask.CompletedTask;
    }

    public ValueTask OnFileCommittedAsync(string fullPath, CancellationToken cancellationToken)
    {
        if (!MarkOfTheWeb.TryMark(fullPath))
            PlatformLog.Write("MOTW yazılamadı (NTFS olmayan birim olabilir).");
        return ValueTask.CompletedTask;
    }
}
