using AgentSignaler.Contracts;

namespace AgentSignaler.Service.Tests;

public sealed class TranscriptIngressTests
{
    [Fact]
    public void GlobalConcurrencyHasFourImmediateSlotsAndNoWaitQueue()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), $"Transcript-ingress-{Guid.NewGuid():N}");
        try
        {
            using var machines = new MachineStore(Path.Combine(directory, "state.db"));
            using var store = new TranscriptStore((_, _, _) => ValueTask.FromResult(true));
            var ingress = new TranscriptIngress(store, machines);
            for (var i = 0; i < 4; i++) Assert.True(ingress.TryEnter());
            Assert.False(ingress.TryEnter());
            ingress.Exit(null);
            Assert.True(ingress.TryEnter());
            for (var i = 0; i < 4; i++) ingress.Exit(null);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void GlobalRateHasFortyBurstTwentyPerSecondBeforeAnyMachineBucket()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), $"Transcript-rate-{Guid.NewGuid():N}");
        try
        {
            using var machines = new MachineStore(Path.Combine(directory, "state.db"));
            using var store = new TranscriptStore((_, _, _) => ValueTask.FromResult(true));
            var clock = new Clock();
            var ingress = new TranscriptIngress(store, machines, clock);
            for (var i = 0; i < 40; i++) { Assert.True(ingress.TryEnter()); ingress.Exit(null); }
            Assert.False(ingress.TryEnter());
            clock.Advance(TimeSpan.FromSeconds(1));
            for (var i = 0; i < 20; i++) { Assert.True(ingress.TryEnter()); ingress.Exit(null); }
            Assert.False(ingress.TryEnter());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task KnownMachineGetsOneSlotTenBurstAndFivePerSecond()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), $"Transcript-machine-rate-{Guid.NewGuid():N}");
        try
        {
            using var machines = new MachineStore(Path.Combine(directory, "state.db"));
            var id = Guid.NewGuid();
            await machines.AcceptAsync(new PresenceReport
            {
                ProtocolVersion = 3, Kind = PresenceKind.Started, EventId = Guid.NewGuid(), MachineId = id,
                MachineName = "synthetic", Client = "agent-signaler", ClientVersion = "test", Generation = 1,
                Sequence = 1, ReportedAtUtc = TranscriptContractTests.Now, HeartbeatIntervalSeconds = 300, Sessions = []
            });
            using var store = new TranscriptStore((_, _, _) => ValueTask.FromResult(true));
            var clock = new Clock();
            var ingress = new TranscriptIngress(store, machines, clock);
            Assert.True(ingress.TryEnter());
            Assert.True(await ingress.TryEnterMachineAsync(id, default));
            Assert.True(ingress.TryEnter());
            Assert.False(await ingress.TryEnterMachineAsync(id, default));
            ingress.Exit(null);
            ingress.Exit(id);
            for (var i = 0; i < 9; i++)
            {
                Assert.True(ingress.TryEnter());
                Assert.True(await ingress.TryEnterMachineAsync(id, default));
                ingress.Exit(id);
            }
            Assert.True(ingress.TryEnter());
            Assert.False(await ingress.TryEnterMachineAsync(id, default));
            ingress.Exit(null);
            clock.Advance(TimeSpan.FromSeconds(1));
            for (var i = 0; i < 5; i++)
            {
                Assert.True(ingress.TryEnter());
                Assert.True(await ingress.TryEnterMachineAsync(id, default));
                ingress.Exit(id);
            }
            Assert.True(ingress.TryEnter());
            Assert.False(await ingress.TryEnterMachineAsync(id, default));
            ingress.Exit(null);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = TranscriptContractTests.Now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
