using System.IO.Pipes;
using System.Text;

namespace WinDrop.App.Infrastructure;

/// <summary>Komut satırı: <c>--hidden</c>, <c>--send &lt;dosyalar...&gt;</c>.</summary>
public sealed record AppOptions(bool Hidden, IReadOnlyList<string> SendPaths)
{
    public static AppOptions Parse(string[] args)
    {
        bool hidden = false;
        var paths = new List<string>();
        bool sending = false;
        foreach (var a in args)
        {
            if (!sending && a.Equals("--hidden", StringComparison.OrdinalIgnoreCase)) hidden = true;
            else if (!sending && a.Equals("--send", StringComparison.OrdinalIgnoreCase)) sending = true;
            else if (sending && !string.IsNullOrWhiteSpace(a))
            {
                try { paths.Add(Path.GetFullPath(a)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        return new AppOptions(hidden, paths);
    }
}

/// <summary>Tek örnek: Mutex + adlı boru. İkinci örnek isteğini ilkine iletir.</summary>
public sealed class SingleInstance : IDisposable
{
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
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var paths = new List<string>();
                    bool hasSend = false;
                    string? line;
                    while ((line = await reader.ReadLineAsync(_cts.Token)) != null)
                    {
                        if (line == "SEND") hasSend = true;
                        else if (line.StartsWith("PATH ", StringComparison.Ordinal)) paths.Add(line[5..]);
                    }
                    AppLog.Write($"ikinci örnekten istek alındı: send={hasSend}, {paths.Count} yol");
                    onRequest(new AppOptions(false, hasSend ? paths : []));
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

    /// <summary>İkinci örnekte: isteği ilk örneğe iletir.</summary>
    public static bool Forward(AppOptions options)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { NewLine = "\n" };
            if (options.SendPaths.Count > 0)
            {
                writer.WriteLine("SEND");
                foreach (var p in options.SendPaths) writer.WriteLine("PATH " + p);
            }
            else writer.WriteLine("SHOW");
            writer.Flush();
            AppLog.Write($"ilk örneğe iletildi: {options.SendPaths.Count} yol");
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            AppLog.Write($"ilk örneğe iletilemedi: {ex.Message}");
            return false;
        }
    }

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
