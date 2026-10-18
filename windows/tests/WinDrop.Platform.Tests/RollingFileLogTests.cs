using System.Diagnostics;
using Xunit;

namespace WinDrop.Platform.Tests;

public class RollingFileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "windrop-log-" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_dir, "app.log");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Two_writers_same_file_lose_no_lines()
    {
        var a = new RollingFileLog(LogPath);
        var b = new RollingFileLog(LogPath);
        Parallel.Invoke(
            () => { for (int i = 0; i < 200; i++) a.Write("A" + i); },
            () => { for (int i = 0; i < 200; i++) b.Write("B" + i); });
        Assert.Equal(400, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public async Task Recovers_after_exclusive_lock_released()
    {
        var log = new RollingFileLog(LogPath);
        log.Write("once");
        var fs = new FileStream(LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(() => { Thread.Sleep(80); fs.Dispose(); });
        log.Write("during-lock");   // yeniden dener
        await release;
        log.Write("after");
        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(new[] { "once", "during-lock", "after" }, lines);
    }

    [Fact]
    public void Failure_does_not_disable_log_permanently()
    {
        var log = new RollingFileLog(LogPath);
        Directory.CreateDirectory(_dir);
        var fs = new FileStream(LogPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        log.Write("lost");          // tüm denemeler başarısız olabilir; fırlatmamalı
        fs.Dispose();
        log.Write("back");
        Assert.Contains("back", File.ReadAllLines(LogPath));
    }

    [Fact]
    public void Reader_with_open_handle_does_not_block_writer()
    {
        var log = new RollingFileLog(LogPath);
        log.Write("x");
        using var reader = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        log.Write("y");
        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public void Rotates_to_dot1_over_limit()
    {
        var log = new RollingFileLog(LogPath, maxBytes: 200);
        for (int i = 0; i < 30; i++) log.Write("line-" + i + "-xxxxxxxxxx");
        Assert.True(File.Exists(log.RotatedPath));
        Assert.True(new FileInfo(LogPath).Length < 400);
        Assert.Equal("app.1.log", Path.GetFileName(log.RotatedPath));
    }
}
