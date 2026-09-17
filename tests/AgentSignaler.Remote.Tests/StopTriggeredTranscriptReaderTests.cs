using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Xunit;
using static AgentSignaler.Tests.CurrentUserOwnedTranscriptFixture;

namespace AgentSignaler.Remote.Tests;

public sealed class StopTriggeredTranscriptReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-data", "reader-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<TranscriptReaderOutput> _output = new();
    private readonly SyntheticTranscriptFileAdapter _adapter;
    private readonly TranscriptScratchBudget _scratch = new();
    private long _revision = 1;
    private bool _ready = true;
    private string FilePath => Path.Combine(_root, _adapter.GetExpectedFileName("session-a"));

    public StopTriggeredTranscriptReaderTests()
    {
        CreateOwnedDirectory(_root);
        _adapter = new(_root);
    }

    private LocalTranscriptReference Reference(string session = "session-a") => new(_adapter.Source, session,
        DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), _revision,
        Path.Combine(_root, _adapter.GetExpectedFileName(session)));

    private StopTriggeredTranscriptReader Reader(TimeProvider? time = null,
        Func<TranscriptReaderOutput, CancellationToken, ValueTask<bool>>? callback = null) => new(
        _adapter, reference => _ready && reference.SettingsRevision == _revision,
        callback ?? ((value, _) => { _output.Enqueue(value); return ValueTask.FromResult(true); }), _scratch, time);

    private async Task Stop(StopTriggeredTranscriptReader reader)
    {
        Assert.Null(reader.TrySchedule(Reference()));
        await reader.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private string[] Replies => _output.Where(value => value.AssistantText is not null).Select(value => value.AssistantText!).ToArray();
    private bool Has(string category) => _output.Any(value => value.Category == category);

    [Fact]
    public async Task FirstStopBaselinesAndNewCompleteAssistantOnlyIsEmittedOnce()
    {
        WriteTranscriptFile(FilePath, SyntheticTranscriptFileAdapter.Record("OLD-HISTORY"));
        await using var reader = Reader();
        await Stop(reader);
        Assert.Empty(Replies);
        Assert.True(Has(TranscriptReaderCategories.Baseline));
        File.AppendAllText(FilePath, SyntheticTranscriptFileAdapter.Record("ALLOWED-SYNTHETIC") +
            SyntheticTranscriptFileAdapter.Record("EXCLUDED", kind: "tool") +
            SyntheticTranscriptFileAdapter.Record("EXCLUDED", kind: "user") +
            SyntheticTranscriptFileAdapter.Record("EXCLUDED", kind: "reasoning") +
            SyntheticTranscriptFileAdapter.Record("EXCLUDED", kind: "subagent") +
            SyntheticTranscriptFileAdapter.Record("EXCLUDED", complete: false));
        var expected = File.ReadAllBytes(FilePath);
        await Stop(reader);
        await Stop(reader);
        Assert.Equal(["ALLOWED-SYNTHETIC"], Replies);
        Assert.Equal(expected, File.ReadAllBytes(FilePath));
        Assert.Equal(0, _scratch.UsedBytes);
    }

    [Fact]
    public async Task BaselineInsideFrameDiscardsItsLaterCompletion()
    {
        var old = SyntheticTranscriptFileAdapter.Record("PRE-BASELINE");
        WriteTranscriptFile(FilePath, old[..20]);
        await using var reader = Reader();
        await Stop(reader);
        File.AppendAllText(FilePath, old[20..] + SyntheticTranscriptFileAdapter.Record("NEW"));
        await Stop(reader);
        Assert.Equal(["NEW"], Replies);
    }

    [Fact]
    public async Task IncompleteAppendWaitsForLaterStopWithoutRetainedBufferOrPolling()
    {
        WriteTranscriptFile(FilePath, "");
        await using var reader = Reader();
        await Stop(reader);
        var data = SyntheticTranscriptFileAdapter.Record("FINISHED-LATER");
        File.AppendAllText(FilePath, data[..30]);
        await Stop(reader);
        Assert.True(Has(TranscriptReaderCategories.Waiting));
        Assert.Empty(Replies);
        Assert.Equal(0, _scratch.UsedBytes);
        File.AppendAllText(FilePath, data[30..]);
        Assert.Empty(Replies);
        await Stop(reader);
        await Stop(reader);
        Assert.Equal(["FINISHED-LATER"], Replies);
    }

    [Fact]
    public async Task DelayedFlushIsReadByOneOfTheFixedRetries()
    {
        WriteTranscriptFile(FilePath, "");
        await using var reader = Reader();
        await Stop(reader);
        var data = SyntheticTranscriptFileAdapter.Record("DELAYED");
        File.AppendAllText(FilePath, data[..20]);
        var clock = new RetryTimeProvider(() => File.AppendAllText(FilePath, data[20..]));
        await using var retryReader = Reader(clock);
        // Establish this reader's baseline before appending a new partial frame.
        WriteTranscriptFile(FilePath, "");
        await Stop(retryReader);
        File.AppendAllText(FilePath, data[..20]);
        await Stop(retryReader);
        Assert.Equal(["DELAYED"], Replies);
        Assert.Contains(TimeSpan.FromMilliseconds(100), clock.RetryDueTimes);
    }

    [Fact]
    public async Task ReplacementAndTruncationRebaselineWithoutHistoryReplay()
    {
        WriteTranscriptFile(FilePath, SyntheticTranscriptFileAdapter.Record(new string('a', 200)));
        await using var reader = Reader();
        await Stop(reader);
        WriteTranscriptFile(FilePath, "");
        await Stop(reader);
        Assert.True(Has(TranscriptReaderCategories.Truncated));
        File.Delete(FilePath);
        WriteTranscriptFile(FilePath, SyntheticTranscriptFileAdapter.Record("REPLACEMENT-HISTORY"));
        await Stop(reader);
        Assert.True(Has(TranscriptReaderCategories.IdentityChanged));
        File.AppendAllText(FilePath, SyntheticTranscriptFileAdapter.Record("NEW-REPLY"));
        await Stop(reader);
        Assert.Equal(["NEW-REPLY"], Replies);
    }

    [Fact]
    public async Task InvalidEncodingAndSessionMismatchAreGapsNotReplies()
    {
        WriteTranscriptFile(FilePath, "");
        await using var reader = Reader();
        await Stop(reader);
        using (var file = File.OpenWrite(FilePath)) file.Write([0xC3, 0x28, 0x0A]);
        await Stop(reader);
        Assert.True(Has(TranscriptReaderCategories.FormatChanged));
        File.AppendAllText(FilePath, SyntheticTranscriptFileAdapter.Record("WRONG", session: "other"));
        await Stop(reader);
        Assert.True(Has(TranscriptReaderCategories.SessionMismatch));
        Assert.Empty(Replies);
    }

    [Fact]
    public async Task ReplyAndEncodedRecordBudgetsDropBacklogAndRebaseline()
    {
        WriteTranscriptFile(FilePath, "");
        await using var reader = Reader();
        await Stop(reader);
        File.AppendAllText(FilePath, string.Concat(Enumerable.Range(0, 20).Select(i =>
            SyntheticTranscriptFileAdapter.Record("message-" + i, id: "id-" + i))));
        await Stop(reader);
        Assert.Equal(16, Replies.Length);
        Assert.True(Has(TranscriptReaderCategories.Budget));
        await Stop(reader);
        Assert.Equal(16, Replies.Length);
        File.AppendAllText(FilePath, new string('x', StopTriggeredTranscriptReader.MaximumRecordBytes + 1) + "\n");
        await Stop(reader);
        File.AppendAllText(FilePath, SyntheticTranscriptFileAdapter.Record("AFTER-GAP"));
        await Stop(reader);
        Assert.Equal("AFTER-GAP", Replies.Last());
    }

    [Fact]
    public async Task EncodedRecordLimitIncludesFramingByte()
    {
        WriteTranscriptFile(FilePath, "");
        var overhead = Encoding.UTF8.GetByteCount(SyntheticTranscriptFileAdapter.Record(""));
        var text = new string('x', StopTriggeredTranscriptReader.MaximumRecordBytes - overhead);
        var exact = SyntheticTranscriptFileAdapter.Record(text);
        Assert.Equal(StopTriggeredTranscriptReader.MaximumRecordBytes, Encoding.UTF8.GetByteCount(exact));
        await using var reader = Reader();
        await Stop(reader);
        File.AppendAllText(FilePath, exact);
        await Stop(reader);
        Assert.Equal([text], Replies);
        File.AppendAllText(FilePath, SyntheticTranscriptFileAdapter.Record(text + "x"));
        await Stop(reader);
        Assert.Single(Replies);
        Assert.True(Has(TranscriptReaderCategories.Budget));
    }

    [Fact]
    public async Task RecordBudgetCountsIgnoredRecords()
    {
        WriteTranscriptFile(FilePath, "");
        await using var reader = Reader();
        await Stop(reader);
        File.AppendAllText(FilePath, string.Concat(Enumerable.Repeat(
            SyntheticTranscriptFileAdapter.Record("IGNORED", kind: "user"), 129)) +
            SyntheticTranscriptFileAdapter.Record("BACKLOG"));
        await Stop(reader);
        Assert.True(Has(TranscriptReaderCategories.Budget));
        Assert.Empty(Replies);
        await Stop(reader);
        Assert.Empty(Replies);
    }

    [Fact]
    public async Task InspectedByteBudgetIncludesIgnoredRecordsAndSkipsBacklog()
    {
        WriteTranscriptFile(FilePath, "");
        var parsed = 0;
        _adapter.Parsing = () => parsed++;
        await using var reader = Reader();
        await Stop(reader);
        File.AppendAllText(FilePath, string.Concat(Enumerable.Repeat(
            SyntheticTranscriptFileAdapter.Record(new string('x', 60 * 1024), kind: "tool"), 40)));
        await Stop(reader);
        Assert.InRange(parsed, 1, 34);
        Assert.True(Has(TranscriptReaderCategories.Budget));
        File.AppendAllText(FilePath, SyntheticTranscriptFileAdapter.Record("AFTER-BYTE-GAP"));
        await Stop(reader);
        Assert.Equal(["AFTER-BYTE-GAP"], Replies);
    }

    [Fact]
    public async Task NormalizedOutputBudgetIsSharedAcrossRecords()
    {
        WriteTranscriptFile(FilePath, "");
        await using var reader = Reader();
        await Stop(reader);
        File.AppendAllText(FilePath, string.Concat(Enumerable.Repeat(
            SyntheticTranscriptFileAdapter.Record(new string('x', 20 * 1024)), 16)));
        await Stop(reader);
        Assert.Equal(12, Replies.Length);
        Assert.True(Replies.Sum(text => Encoding.UTF8.GetByteCount(text)) <= StopTriggeredTranscriptReader.MaximumOutputBytes);
        Assert.True(Has(TranscriptReaderCategories.Budget));
    }

    [Fact]
    public async Task PassDeadlineCancelsAdmissionAndRebaselinesNextStop()
    {
        WriteTranscriptFile(FilePath, "");
        var clock = new AdmissionDeadlineTimeProvider();
        await using var reader = Reader(clock, async (value, token) =>
        {
            if (value.AssistantText is not null)
            {
                clock.FireDeadline();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            _output.Enqueue(value);
            return true;
        });
        await Stop(reader);
        File.AppendAllText(FilePath, SyntheticTranscriptFileAdapter.Record("TIMED-OUT"));
        await Stop(reader);
        Assert.True(Has(TranscriptReaderCategories.Budget));
        await Stop(reader);
        Assert.Empty(Replies);
        Assert.Equal(0, _scratch.UsedBytes);
    }

    [Fact]
    public async Task QueueHasSixteenSlotsAndExpiredRequestsDoNotOpenFiles()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new OffsetTimeProvider();
        WriteTranscriptFile(FilePath, "");
        await using var reader = Reader(clock, async (value, token) =>
        {
            _output.Enqueue(value);
            if (value.Category == TranscriptReaderCategories.Baseline)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return true;
        });
        Assert.Null(reader.TrySchedule(Reference()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 16; i++)
            Assert.Null(reader.TrySchedule(Reference("pending-" + i)));
        Assert.Equal(TranscriptReaderCategories.Capacity, reader.TrySchedule(Reference("overflow")));
        clock.Advance(TimeSpan.FromSeconds(3));
        release.TrySetResult();
        await reader.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(16, _output.Count(value => value.Category == TranscriptReaderCategories.Expired));
        Assert.False(Has(TranscriptReaderCategories.Unavailable));
    }

    [Fact]
    public async Task MissingFileUsesOnlyThreeAttemptsAndNoOngoingPolling()
    {
        var clock = new RetryTimeProvider(() => { });
        await using var reader = Reader(clock);
        await Stop(reader);
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)], clock.RetryDueTimes);
        Assert.True(Has(TranscriptReaderCategories.Unavailable));
        WriteTranscriptFile(FilePath, SyntheticTranscriptFileAdapter.Record("CREATED-LATER"));
        Assert.Empty(Replies);
        await Stop(reader);
        Assert.Empty(Replies);
        Assert.True(Has(TranscriptReaderCategories.Baseline));
    }

    [Fact]
    public async Task ExitCancelsRetryWaitAndReleasesScratch()
    {
        WriteTranscriptFile(FilePath, "");
        var clock = new BlockedRetryTimeProvider();
        var reader = Reader(clock);
        await Stop(reader);
        File.AppendAllText(FilePath, "{\"incomplete\":");
        Assert.Null(reader.TrySchedule(Reference()));
        await clock.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await reader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, _scratch.UsedBytes);
        Assert.Equal(TranscriptReaderCategories.Ineligible, reader.TrySchedule(Reference()));
        Assert.Empty(Replies);
    }

    [Fact]
    public async Task FilesAboveSizeCeilingNeverParse()
    {
        WriteTranscriptFile(FilePath, "");
        using (var file = File.OpenWrite(FilePath))
            file.SetLength(StopTriggeredTranscriptReader.MaximumFileBytes + 1L);
        _adapter.Parsing = () => Assert.Fail("An oversized file must not parse");
        await using var reader = Reader();
        await Stop(reader);
        Assert.True(Has(TranscriptReaderCategories.Budget));
    }

    [Fact]
    public async Task OutputRejectionCommitsOffsetWithGapInsteadOfRereading()
    {
        WriteTranscriptFile(FilePath, "");
        await using var reader = Reader(callback: (value, _) =>
        {
            _output.Enqueue(value);
            return ValueTask.FromResult(value.AssistantText is null);
        });
        await Stop(reader);
        File.AppendAllText(FilePath, SyntheticTranscriptFileAdapter.Record());
        await Stop(reader);
        await Stop(reader);
        Assert.Single(Replies);
        Assert.True(Has(TranscriptReaderCategories.QueueRejected));
    }

    [Fact]
    public async Task ReadinessAndRevisionAreRecheckedBeforeOutput()
    {
        WriteTranscriptFile(FilePath, "");
        await using var reader = Reader();
        await Stop(reader);
        File.AppendAllText(FilePath, SyntheticTranscriptFileAdapter.Record("STALE"));
        _adapter.Parsing = () => Interlocked.Increment(ref _revision);
        await Stop(reader);
        Assert.Empty(Replies);
        _ready = false;
        Assert.Equal(TranscriptReaderCategories.Ineligible, reader.TrySchedule(Reference()));
    }

    [Fact]
    public async Task ResetCancelsBlockedAdmissionAndReenableStartsFresh()
    {
        WriteTranscriptFile(FilePath, "");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reader = Reader(callback: async (value, token) =>
        {
            if (value.AssistantText is not null)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            _output.Enqueue(value);
            return true;
        });
        await Stop(reader);
        File.AppendAllText(FilePath, SyntheticTranscriptFileAdapter.Record("NEVER-ADMITTED"));
        Assert.Null(reader.TrySchedule(Reference()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reader.Reset();
        await reader.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Stop(reader);
        Assert.Empty(Replies);
        Assert.Equal(0, _scratch.UsedBytes);
    }

    [Fact]
    public async Task PendingStopsCoalesceAndOnlyOneSuccessorPerSessionRuns()
    {
        WriteTranscriptFile(FilePath, "");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reader = Reader(callback: async (value, token) =>
        {
            _output.Enqueue(value);
            if (value.Category == TranscriptReaderCategories.Baseline)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return true;
        });
        Assert.Null(reader.TrySchedule(Reference()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var last = Reference();
        for (var i = 0; i < 100; i++) Assert.Null(reader.TrySchedule(last));
        File.AppendAllText(FilePath, SyntheticTranscriptFileAdapter.Record("COALESCED"));
        release.TrySetResult();
        await reader.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(Replies);
        Assert.Equal(last.CaptureId, _output.Single(value => value.AssistantText is not null).Reference.CaptureId);
    }

    [Fact]
    public async Task SharedScratchCapacityDoesNotOpenOrRetainFileBuffers()
    {
        WriteTranscriptFile(FilePath, "HISTORY");
        using var occupied = _scratch.TryReserve(128 * 1024);
        await using var reader = Reader();
        await Stop(reader);
        Assert.True(Has(TranscriptReaderCategories.Capacity));
        Assert.Equal(128 * 1024, _scratch.UsedBytes);
    }

    [Fact]
    public async Task ContextLimitIsEightPerSourceAndExpiryRequiresFreshBaseline()
    {
        var clock = new OffsetTimeProvider();
        await using var reader = Reader(clock);
        for (var i = 0; i < 9; i++)
        {
            var reference = Reference("session-" + i);
            WriteTranscriptFile(reference.Path, "");
            Assert.Null(reader.TrySchedule(reference));
            await reader.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(8, _output.Count(value => value.Category == TranscriptReaderCategories.Baseline));
        Assert.True(Has(TranscriptReaderCategories.Capacity));
        clock.Advance(TimeSpan.FromMinutes(31));
        var resumed = Reference("session-0");
        File.AppendAllText(resumed.Path, SyntheticTranscriptFileAdapter.Record("EXPIRED-HISTORY", session: "session-0"));
        Assert.Null(reader.TrySchedule(resumed));
        await reader.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Has("cursor-expired"));
        Assert.Empty(Replies);
    }

    [Fact]
    public async Task GlobalContextLimitCannotBeBypassedUsingMoreSourceScopes()
    {
        var adapters = Enumerable.Range(0, 5).Select(i =>
            new SyntheticTranscriptFileAdapter(_root, "scope-" + i)).ToArray();
        await using var reader = new StopTriggeredTranscriptReader(new MultipleRegistry(adapters), _ => true,
            (value, _) => { _output.Enqueue(value); return ValueTask.FromResult(true); });
        foreach (var adapter in adapters)
        {
            for (var i = 0; i < 8; i++)
            {
                var reference = Reference("session-" + i) with { Source = adapter.Source };
                WriteTranscriptFile(reference.Path, "");
                Assert.Null(reader.TrySchedule(reference));
                await reader.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Assert.Equal(32, _output.Count(value => value.Category == TranscriptReaderCategories.Baseline));
        Assert.Equal(8, _output.Count(value => value.Category == TranscriptReaderCategories.Capacity));
    }

    [Theory]
    [InlineData(@"relative.synthetic")]
    [InlineData(@"\\server\share\session-a.synthetic")]
    [InlineData(@"\\?\C:\session-a.synthetic")]
    [InlineData(@"C:\root\session-a.synthetic:ads")]
    [InlineData(@"C:\root\..\session-a.synthetic")]
    public void RejectsUnsafeSuppliedPathsWithoutIO(string path)
    {
        Assert.False(TranscriptFileBoundary.IsExpectedPath(Reference() with { Path = path }, _adapter));
    }

    [Fact]
    public void WrongFilenameSessionSourceAndVersionAreRejected()
    {
        var reference = Reference();
        Assert.False(TranscriptFileBoundary.IsExpectedPath(reference with { SessionId = "other" }, _adapter));
        Assert.False(TranscriptFileBoundary.IsExpectedPath(reference with
        {
            Source = reference.Source with { Version = "unverified-version" }
        }, _adapter));
        Assert.False(TranscriptFileBoundary.IsExpectedPath(reference with
        {
            Path = reference.Path.ToUpperInvariant().Replace("SESSION-A", "OTHER")
        }, _adapter));
    }

    [Fact]
    public async Task AncestorReparsePointsAreRejected()
    {
        // Directory junction creation does not require symbolic-link privileges.
        var nested = Path.Combine(_root, "nested");
        CreateOwnedDirectory(nested);
        var junction = Path.Combine(_root, "junction");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/c mklink /J \"{junction}\" \"{nested}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        var adapter = new SyntheticTranscriptFileAdapter(junction);
        var reference = Reference() with { Path = Path.Combine(junction, "session-a.synthetic") };
        WriteTranscriptFile(Path.Combine(nested, "session-a.synthetic"), "");
        var error = Assert.Throws<TranscriptFileBoundaryException>(() => TranscriptFileBoundary.Open(reference, adapter));
        Assert.Equal(TranscriptReaderCategories.PathRejected, error.Category);
        Directory.Delete(junction);
    }

    [Fact]
    public void HardLinksAreRejectedOnHandleAndCurrentUserOwnedFileIsReadable()
    {
        if (!OperatingSystem.IsWindows()) return;
        WriteTranscriptFile(FilePath, "");
        using var current = WindowsIdentity.GetCurrent();
        Assert.Equal(current.User, new DirectoryInfo(_root).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal(current.User, new FileInfo(FilePath).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        var hardLink = Path.Combine(_root, "linked.synthetic");
        Assert.True(CreateHardLink(hardLink, FilePath, IntPtr.Zero));
        var error = Assert.Throws<TranscriptFileBoundaryException>(() => TranscriptFileBoundary.Open(Reference(), _adapter));
        Assert.Equal(TranscriptReaderCategories.PathRejected, error.Category);
        File.Delete(hardLink);
        using var valid = TranscriptFileBoundary.Open(Reference(), _adapter);
        Assert.False(valid.Stream.CanWrite);
    }

    [Fact]
    public void ReadOnlyHandleAllowsHostWritingAndDeletionAndCannotWrite()
    {
        WriteTranscriptFile(FilePath, "");
        using var file = TranscriptFileBoundary.Open(Reference(), _adapter);
        Assert.False(file.Stream.CanWrite);
        using (var writer = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            writer.Write(Encoding.UTF8.GetBytes(SyntheticTranscriptFileAdapter.Record()));
        File.Delete(FilePath);
        Assert.False(File.Exists(FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class OffsetTimeProvider : TimeProvider
    {
        private long _offset;
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp() + _offset;
        public void Advance(TimeSpan duration) => _offset += (long)(duration.TotalSeconds * TimestampFrequency);
    }

    private sealed class RetryTimeProvider(Action onRetry) : TimeProvider
    {
        private bool _flushed;
        private long _timestamp;
        public List<TimeSpan> RetryDueTimes { get; } = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime < TimeSpan.FromMilliseconds(350))
            {
                _timestamp += dueTime.Ticks;
                RetryDueTimes.Add(TimeSpan.FromTicks(_timestamp));
                if (!_flushed) { _flushed = true; onRetry(); }
                return base.CreateTimer(callback, state, TimeSpan.Zero, period);
            }
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class BlockedRetryTimeProvider : TimeProvider
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime < TimeSpan.FromMilliseconds(350))
            {
                Waiting.TrySetResult();
                return base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);
            }
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class MultipleRegistry(SyntheticTranscriptFileAdapter[] adapters) : ITranscriptFileAdapterRegistry
    {
        public ITranscriptFileAdapter? Find(AgentSignaler.Contracts.SourceDescriptor source) =>
            adapters.SingleOrDefault(adapter => adapter.Source == source);
    }

    private sealed class AdmissionDeadlineTimeProvider : TimeProvider
    {
        private Action? _deadline;
        public void FireDeadline() => _deadline!();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == TimeSpan.FromMilliseconds(750))
            {
                _deadline = () => callback(state);
                return base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period);
            }
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr security);
}
