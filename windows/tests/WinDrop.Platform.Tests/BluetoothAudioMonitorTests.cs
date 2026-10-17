using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace WinDrop.Platform.Tests;

public class BluetoothAudioMonitorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]    // Bluetooth çıkış varsayılan değil: oturum önemsiz
    [InlineData(false, null, false)]
    [InlineData(true, false, false)]    // Bluetooth çıkış var ama ses çalmıyor
    [InlineData(true, true, true)]
    [InlineData(true, null, true)]      // emin olunamıyor: güvenli taraf
    public void BtAudio_TruthTable(bool defaultBt, bool? active, bool expected) =>
        Assert.Equal(expected, new BluetoothAudioState(defaultBt, active, null).BtAudio);

    private sealed class Probe
    {
        public BluetoothAudioState? Next { get; set; }
        public Exception? Throw { get; set; }
        public int Calls;

        public BluetoothAudioState? Read()
        {
            Interlocked.Increment(ref Calls);
            if (Throw is not null) throw Throw;
            return Next;
        }
    }

    private static BluetoothAudioMonitor Make(Probe p) => new(p.Read, TimeSpan.FromHours(1));

    [Fact]
    public void InitialValue_IsReadSynchronously()
    {
        var probe = new Probe { Next = new BluetoothAudioState(true, true, "AirPods") };
        using var monitor = Make(probe);
        Assert.True(monitor.Current.BtAudio);
        Assert.Equal("AirPods", monitor.Current.DeviceName);
    }

    [Fact]
    public async Task Poke_ReevaluatesImmediately_AndRaisesChangedOnlyWhenBtAudioChanges()
    {
        var probe = new Probe { Next = BluetoothAudioState.None };
        using var monitor = Make(probe);
        var events = new List<bool>();
        var signal = new SemaphoreSlim(0);
        monitor.Changed += s => { lock (events) events.Add(s.BtAudio); signal.Release(); };

        probe.Next = new BluetoothAudioState(true, true, "AirPods");
        monitor.Poke();
        Assert.True(await signal.WaitAsync(T.Wait));
        Assert.True(monitor.Current.BtAudio);

        // aygıt adı değişti ama btAudio aynı → olay yok
        probe.Next = new BluetoothAudioState(true, null, "AirPods 2");
        monitor.Poke();
        await WaitForCallsAsync(probe, 3);

        probe.Next = new BluetoothAudioState(true, false, "AirPods 2");   // ses durdu
        monitor.Poke();
        Assert.True(await signal.WaitAsync(T.Wait));
        Assert.False(monitor.Current.BtAudio);

        lock (events) Assert.Equal([true, false], events);
    }

    [Fact]
    public async Task ProbeFailureOrNull_KeepsPreviousValue()
    {
        var probe = new Probe { Next = new BluetoothAudioState(true, true, "AirPods") };
        using var monitor = Make(probe);

        probe.Throw = new COMException("kaybolan aygıt", unchecked((int)0x88890004));
        monitor.Poke();
        await WaitForCallsAsync(probe, 2);
        Assert.True(monitor.Current.BtAudio);

        probe.Throw = null;
        probe.Next = null;   // okunamadı
        monitor.Poke();
        await WaitForCallsAsync(probe, 3);
        Assert.True(monitor.Current.BtAudio);
    }

    private static async Task WaitForCallsAsync(Probe probe, int calls)
    {
        var deadline = DateTime.UtcNow + T.Wait;
        while (Volatile.Read(ref probe.Calls) < calls)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Ölçüm yapılmadı.");
            await Task.Delay(5);
        }
        await Task.Delay(30);   // sonucun uygulanması için
    }

    [Fact]
    public void RealMonitor_ReadsDefaultOutputWithoutThrowing()
    {
        // Gerçek Core Audio: yalnız okur. Değer makineye bağlı (AirPods bağlıyken true olabilir); burada yalnız çökmemesi sınanır.
        using var monitor = new BluetoothAudioMonitor();
        var s = monitor.Current;
        output.WriteLine($"varsayılanBT={s.DefaultOutputIsBluetooth} aktifOturum={s.ActiveSession?.ToString() ?? "?"} aygıt={s.DeviceName ?? "-"} btAudio={s.BtAudio}");
        if (!s.DefaultOutputIsBluetooth) Assert.False(s.BtAudio);
    }
}
