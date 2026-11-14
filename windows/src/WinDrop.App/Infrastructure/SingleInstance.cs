using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace WinDrop.App.Infrastructure;

/// <summary>Komut satırı: <c>--hidden</c>, <c>--quit</c> (çalışan örneği kapat), <c>--send &lt;dosyalar...&gt;</c>.</summary>
public sealed record AppOptions(bool Hidden, IReadOnlyList<string> SendPaths, bool Quit = false)
{
    public static AppOptions Parse(string[] args)
    {
        bool hidden = false, quit = false;
        var paths = new List<string>();
        bool sending = false;
        foreach (var a in args)
        {
            if (!sending && a.Equals("--hidden", StringComparison.OrdinalIgnoreCase)) hidden = true;
            else if (!sending && a.Equals("--quit", StringComparison.OrdinalIgnoreCase)) quit = true;
            else if (!sending && a.Equals("--send", StringComparison.OrdinalIgnoreCase)) sending = true;
            else if (sending && !string.IsNullOrWhiteSpace(a))
            {
                try { paths.Add(Path.GetFullPath(a)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        return new AppOptions(hidden, paths, quit);
    }
}

/// <summary>
/// Tek örnek: Mutex + adlı boru. İkinci örnek isteğini ilkine iletir. Boru satırları: <c>SHOW</c>, <c>QUIT</c>,
/// <c>SEND</c> + <c>PATH &lt;yol&gt;</c>... (kurulum/kaldırma betikleri <c>QUIT</c> ile uygulamayı zarifçe kapatır).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int MaxLines = 2000;           // tek istekte en çok yol sayısı
    private const int MaxLineLength = 32_768;    // tek satır (yol) sınırı
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private static readonly string Suffix = Environment.UserName + Environment.GetEnvironmentVariable("WINDROP_INSTANCE");   // WINDROP_INSTANCE: yalnız deneme için ayrı örnek
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private bool _owned;
    private bool _disposed;

    public static string MutexName { get; } = @"Local\WinDrop.App.SingleInstance." + Suffix;
    public static string PipeName { get; } = "WinDrop.App.Pipe." + Suffix;

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out _owned);
    }

    public bool IsFirst => _owned;

    /// <summary>İlk örnekte: boruyu dinler. <paramref name="onRequest"/> boru iş parçacığında çağrılır.</summary>
    public void StartServer(Action<AppOptions> onRequest)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    // Bağlanıp yazmayan/kapanmayan istemci döngüyü tıkamasın: okuma 2 sn ile sınırlı.
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    readCts.CancelAfter(ReadTimeout);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var paths = new List<string>();
                    bool hasSend = false, quit = false;
                    string? line;
                    int lines = 0;
                    try
                    {
                        while ((line = await reader.ReadLineAsync(readCts.Token)) != null)
                        {
                            if (++lines > MaxLines || line.Length > MaxLineLength)
                            {
                                AppLog.Write("boru isteği çok büyük; yok sayıldı.");
                                hasSend = false; quit = false; paths.Clear();
                                break;
                            }
                            if (line == "SEND") hasSend = true;
                            else if (line == "QUIT") quit = true;
                            else if (line.StartsWith("PATH ", StringComparison.Ordinal)) paths.Add(line[5..]);
                        }
                    }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                    {
                        AppLog.Write("boru okuma zaman aşımı; istek yok sayıldı.");
                        continue;
                    }
                    AppLog.Write($"ikinci örnekten istek alındı: send={hasSend}, quit={quit}, {paths.Count} yol");
                    onRequest(new AppOptions(false, hasSend ? paths : [], quit));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Write($"boru hatası: {ex.Message}");
                    await Task.Delay(500);
                }
            }
        });
    }

    /// <summary>İkinci örnekte: isteği ilk örneğe iletir. <c>--hidden</c> tek başına pencereyi öne getirmez (iletilmez).</summary>
    public static bool Forward(AppOptions options)
    {
        if (options is { Hidden: true, Quit: false, SendPaths.Count: 0 }) return true;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            AllowForeground(client);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { NewLine = "\n" };
            if (options.Quit) writer.WriteLine("QUIT");
            else if (options.SendPaths.Count > 0)
            {
                writer.WriteLine("SEND");
                foreach (var p in options.SendPaths) writer.WriteLine("PATH " + p);
            }
            else writer.WriteLine("SHOW");
            writer.Flush();
            AppLog.Write($"ilk örneğe iletildi: quit={options.Quit}, {options.SendPaths.Count} yol");
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            AppLog.Write($"ilk örneğe iletilemedi: {ex.Message}");
            return false;
        }
    }

    /// <summary>Boru sunucusu sürecine ön plana geçme hakkı verir (ikinci örnek kullanıcı eylemiyle başladı); pencere yalnız yanıp sönmesin.</summary>
    private static void AllowForeground(NamedPipeClientStream client)
    {
        try
        {
            if (GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out uint pid)) AllowSetForegroundWindow(pid);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    public void Dispose()
    {
        if (_disposed) return;   // yeniden başlatmada kilit erken bırakılır; using sonunda ikinci çağrı zararsız olmalı
        _disposed = true;
        _cts.Cancel();
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _owned = false;
        }
        _mutex.Dispose();
        _cts.Dispose();
    }
}
