using WinDrop.Core.Protocol;
using WinDrop.Core.Tests.Support;
using WinDrop.Core.Transfer;
using Xunit;

namespace WinDrop.Core.Tests.Transfer;

/// <summary>Güvenlik incelemesi düzeltmeleri: boş alan (iki sürücü) ve taşımadan ÖNCE çağrılan commit kancası (MOTW penceresi).</summary>
public sealed class StorageSecurityTests : IDisposable
{
    private readonly Rig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void MinFreeSpace_DifferentDrives_UsesTheSmaller()
    {
        long Free(string root) => root == @"D:\" ? 500 : 100;
        Assert.Equal(100, FileSystemPartialStore.MinFreeSpace(@"D:\", @"C:\", Free));   // kısmi kök (C:) daha dar
        Assert.Equal(100, FileSystemPartialStore.MinFreeSpace(@"C:\", @"D:\", Free));   // hedef (C:) daha dar
        Assert.Equal(500, FileSystemPartialStore.MinFreeSpace(@"D:\", @"d:\", Free));   // aynı sürücü: tek ölçüm
    }

    [Fact]
    public void GetFreeSpaceBytes_SameDrive_MeasuresOnce_AndOverrideStillWins()
    {
        var calls = new List<string>();
        var store = new FileSystemPartialStore(_rig.Dir("p"), _rig.Dir("f"))
        {
            DriveFreeSpace = root => { calls.Add(root); return 1234; },
        };
        Assert.Equal(1234, store.GetFreeSpaceBytes());
        Assert.Single(calls);

        Assert.Equal(7, _rig.NewStore("o", () => 7).GetFreeSpaceBytes());   // test sağlayıcısı değişmedi
    }

    private sealed class OrderHook : IFileCommitHook
    {
        public List<string> Events { get; } = [];
        public string? PartSeenBefore;
        public bool PartExistedBefore;
        public bool NoFinalYetBefore;
        public string FinalDir = "";

        public ValueTask OnBeforeCommitAsync(string partPath, CancellationToken cancellationToken)
        {
            Events.Add("before");
            PartSeenBefore = partPath;
            PartExistedBefore = File.Exists(partPath);
            NoFinalYetBefore = !Directory.EnumerateFiles(FinalDir).Any();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnFileCommittedAsync(string fullPath, CancellationToken cancellationToken)
        {
            Events.Add("after");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Commit_CallsBeforeHook_OnPartFile_BeforeMove_ThenAfterHook()
    {
        var hook = new OrderHook { FinalDir = _rig.Dir("h-final") };
        var store = new FileSystemPartialStore(_rig.Dir("h-partial"), hook.FinalDir, hook, () => long.MaxValue / 2);
        var key = new PartialKey(_rig.PhoneId, _rig.TransferId, 1);
        await using var file = await store.CreateAsync(key, 3, 1, default);
        await file.AppendAsync(new byte[] { 1, 2, 3 }, default);
        await file.SyncAckAsync(3, default);

        var dest = await file.CommitAsync("a.bin", null, new string('0', 64), default);

        Assert.Equal(["before", "after"], hook.Events);
        Assert.EndsWith(".part", hook.PartSeenBefore);     // MOTW .part dosyasına, taşımadan önce yazılabilir
        Assert.True(hook.PartExistedBefore);
        Assert.True(hook.NoFinalYetBefore);                // hedefte henüz dosya yok
        Assert.True(File.Exists(dest));
        Assert.False(File.Exists(hook.PartSeenBefore));
    }

    [Fact]
    public async Task Commit_HookWithoutBeforeOverride_StillWorks()
    {
        var hook = new RecordingAfterHook();
        var store = new FileSystemPartialStore(_rig.Dir("r-partial"), _rig.Dir("r-final"), hook, () => long.MaxValue / 2);
        var key = new PartialKey(_rig.PhoneId, _rig.TransferId, 2);
        await using var file = await store.CreateAsync(key, 1, 1, default);
        await file.AppendAsync(new byte[] { 9 }, default);
        await file.SyncAckAsync(1, default);
        var dest = await file.CommitAsync("b.bin", null, new string('0', 64), default);
        Assert.Equal([dest], hook.Paths);
    }

    private sealed class RecordingAfterHook : IFileCommitHook
    {
        public List<string> Paths { get; } = [];
        public ValueTask OnFileCommittedAsync(string fullPath, CancellationToken cancellationToken)
        {
            Paths.Add(fullPath);
            return ValueTask.CompletedTask;
        }
    }
}
