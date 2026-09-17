using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using Microsoft.AspNetCore.Http;
using Xunit.Abstractions;

namespace AgentSignaler.Service.Tests;

public sealed class TranscriptAllocationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodedVisibleWindowPendingPageAndSyntheticRenderCopiesFitReaderPartition(bool escapingHeavy)
    {
        using var store = new TranscriptStore((_, _, _) => ValueTask.FromResult(true));
        store.SetReadiness(true);
        var machine = Guid.NewGuid();
        var open = new TranscriptOpenRequest(1, machine, 1, store.ReceiverEpoch, Guid.NewGuid(),
            TranscriptContractTests.Source, store.OpenRevision, Guid.NewGuid());
        await store.OpenAsync(open);
        var text = escapingHeavy ? string.Concat(Enumerable.Repeat("😀<\"é", 700)) : new string('x', 28_000);
        for (var sequence = 1; sequence <= 64; sequence++)
        {
            var value = TranscriptContractTests.Event(machine, store.ReceiverEpoch, open.StreamId, sequence);
            store.Accept(value with { Payload = ((TranscriptMessage)value.Payload) with { Text = text } });
        }
        var selection = new TranscriptSelection(machine, TranscriptContractTests.Source, "synthetic-session", open.StreamId);
        _ = MeasureWindow(store, selection); // Exclude one-time runtime/serializer metadata initialization.
        WindowMeasurement? last = null;
        long maximumAllocated = 0;
        for (var pass = 0; pass < 64; pass++)
        {
            last = MeasureWindow(store, selection);
            maximumAllocated = Math.Max(maximumAllocated, last.AllocatedBytes);
            Assert.InRange(last.VisibleEvents, 1, 64);
            Assert.InRange(last.VisibleBytes, 1, 256 * 1024);
            Assert.InRange(last.PendingEvents, 1, 32);
            Assert.InRange(last.PendingBytes, 1, 128 * 1024);
            Assert.True(last.AllocatedBytes <= 8 * 1024 * 1024,
                $"Reader/presentation cycle allocated {last.AllocatedBytes} bytes.");
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(last!.ReleasedPage.TryGetTarget(out _));
        Assert.False(last.ReleasedRenderBuffer.TryGetTarget(out _));
        Assert.InRange(store.RetainedAccountedBytes, 1, 48 * 1024 * 1024);
        output.WriteLine("Maximum decoded/window/pending/synthetic-render allocation: {0} bytes across 64 cycles; partition: 8388608.",
            maximumAllocated);
        output.WriteLine("Retired page/render buffers became collectible. These are managed allocation measurements, not WinUI native/RSS measurements.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WindowMeasurement MeasureWindow(TranscriptStore store, TranscriptSelection selection)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        // Reads currently complete synchronously, making this thread-local allocation measurement deterministic.
        var first = store.ReadEventsAsync(selection).GetAwaiter().GetResult();
        var second = store.ReadEventsAsync(selection, first.ContinuationCursor).GetAwaiter().GetResult();
        var pending = store.ReadEventsAsync(selection, second.ContinuationCursor).GetAwaiter().GetResult();
        var visible = first.Events.Concat(second.Events).ToArray();
        var projected = visible.Select(e => new string(((TranscriptMessage)e.Event.Payload).Text.AsSpan())).ToArray();
        // Stand-in copies deliberately charge both presentation text and UTF-16 rendering scratch.
        // Native WinUI glyph/layout/cache allocation still requires separate authorized UI measurement.
        var rendered = projected.Select(Encoding.Unicode.GetBytes).ToArray();
        Assert.True(store.IsCurrent(first));
        Assert.True(store.IsCurrent(second));
        Assert.True(store.IsCurrent(pending));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var result = new WindowMeasurement(allocated, visible.Length, visible.Sum(e => e.SerializedBytes),
            pending.Events.Length, pending.Events.Sum(e => e.SerializedBytes), new(first), new(rendered[0]));
        GC.KeepAlive(first);
        GC.KeepAlive(second);
        GC.KeepAlive(pending);
        GC.KeepAlive(projected);
        GC.KeepAlive(rendered);
        return result;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeasuredIngressAllocationTimesFourFitsScratchPartition(bool escapingHeavy)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), $"Transcript-allocations-{Guid.NewGuid():N}");
        try
        {
            using var machines = new MachineStore(Path.Combine(directory, "state.db"));
            var machine = Guid.NewGuid();
            await machines.AcceptAsync(new PresenceReport
            {
                ProtocolVersion = 3, Kind = PresenceKind.Started, EventId = Guid.NewGuid(), MachineId = machine,
                MachineName = "synthetic", Client = "agent-signaler", ClientVersion = "test", Generation = 1,
                Sequence = 1, ReportedAtUtc = TranscriptContractTests.Now, HeartbeatIntervalSeconds = 300, Sessions = []
            });
            using var store = new TranscriptStore((_, _, _) => ValueTask.FromResult(true));
            store.SetReadiness(true);
            var open = new TranscriptOpenRequest(1, machine, 1, store.ReceiverEpoch, Guid.NewGuid(),
                TranscriptContractTests.Source, store.OpenRevision, Guid.NewGuid());
            await store.OpenAsync(open);
            var ingress = new TranscriptIngress(store, machines);
            var text = escapingHeavy ? string.Concat(Enumerable.Repeat("😀<\"é", 700)) : new string('x', 28_000);
            long maximumAllocated = 0;
            for (var sequence = 1; sequence <= 6; sequence++)
            {
                var value = TranscriptContractTests.Event(machine, store.ReceiverEpoch, open.StreamId, sequence);
                value = value with { Payload = ((TranscriptMessage)value.Payload) with { Text = text } };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value, TranscriptProtocol.Json);
                using var body = new MemoryStream(bytes, writable: false);
                var context = new DefaultHttpContext();
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
                context.Request.Body = body;
                var before = GC.GetAllocatedBytesForCurrentThread();
                // Controlled in-memory body and uncontended SQLite fixture complete without yielding.
                var operation = ingress.HandleAsync(context, "events", default);
                Assert.True(operation.IsCompletedSuccessfully);
                var result = await operation;
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal(202, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
                if (sequence > 1) maximumAllocated = Math.Max(maximumAllocated, allocated);
            }
            Assert.True(maximumAllocated * 4 <= 4 * 1024 * 1024,
                $"Four maximum measured ingress allocations total {maximumAllocated * 4} bytes.");
            output.WriteLine("Maximum ingress allocation: {0}; four-slot upper bound: {1}; scratch partition: 4194304.",
                maximumAllocated, maximumAllocated * 4);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed record WindowMeasurement(long AllocatedBytes, int VisibleEvents, int VisibleBytes,
        int PendingEvents, int PendingBytes, WeakReference<TranscriptEventsPage> ReleasedPage,
        WeakReference<byte[]> ReleasedRenderBuffer);
}
