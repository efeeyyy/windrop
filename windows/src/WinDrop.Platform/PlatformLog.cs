namespace WinDrop.Platform;

/// <summary>
/// Platform katmanı günlüğü. Varsayılan olarak hiçbir yere yazmaz; uygulama <see cref="Sink"/>'i bağlar
/// (App: <c>%LOCALAPPDATA%\WinDrop\logs\app.log</c>). Hata fırlatmaz; gizli değer (parola, anahtar) yazılmaz.
/// </summary>
public static class PlatformLog
{
    public static Action<string>? Sink { get; set; }

    public static void Write(string message)
    {
        try { Sink?.Invoke(message); }
        catch (Exception) { /* günlük asla akışı bozmaz */ }
    }
}
