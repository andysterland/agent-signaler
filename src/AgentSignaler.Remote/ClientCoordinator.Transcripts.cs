using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public sealed partial class ClientCoordinator
{
    private TranscriptDeliveryCoordinator _transcripts = null!;
    private StopTriggeredTranscriptReader _transcriptReader = null!;
    private ITranscriptFileAdapterRegistry _transcriptAdapters = null!;
    private readonly object _transcriptConfigurationGate = new();
    private TranscriptRun? _transcriptRun;
    private string? _transcriptSuspensionHash;
    private Task? _transcriptMonitor;
    private long _transcriptRevision;
    private int _transcriptSuspended = 1;
    private int _transcriptExplicitSuspension;
    private int _transcriptPresenceStarted;
    public TranscriptScratchBudget TranscriptScratch { get; } = new();
    public string TranscriptAvailability => Volatile.Read(ref _transcriptExplicitSuspension) != 0
        ? "sharing-suspended-preference-not-confirmed" : _transcripts.Status;
    public string TranscriptStatusText => TranscriptAvailability switch
    {
        "ready" => "Details: partial capture; assistant formats unverified",
        "legacy-status-only" => "Details: status-only configuration; upgrade required",
        "sharing-disabled" => "Details: sharing disabled",
        "https-required" => "HTTPS required for details",
        "waiting-for-presence" or "not-started" => "Details: waiting for managed status acknowledgement",
        "sharing-suspended-preference-not-confirmed" => "Details: suspended; preference not confirmed",
        "receiver-disabled" => "Details: receiver disabled",
        "tls-validation-failed" => "Details: HTTPS validation failed",
        "redirect-refused" => "Details: redirects are not permitted",
        "stopped" => "Details: stopped",
        _ => "Details unavailable: " + TranscriptAvailability
    };

    private void InitializeTranscripts(ITranscriptTransport? transport, ITranscriptFileAdapterRegistry? adapters)
    {
        _transcriptAdapters = adapters ?? VerifiedTranscriptFileAdapterRegistry.Production;
        _transcripts = new(transport ?? new TranscriptTransport(timeProvider: _clock), IsTranscriptEligible, _clock);
        _transcriptReader = new(_transcriptAdapters,
            reference => IsTranscriptEligible(reference.Source, reference.SettingsRevision) && _transcripts.IsReady,
            AdmitReaderOutputAsync, TranscriptScratch, _clock);
        _transcripts.Invalidated += TranscriptInvalidated;
    }

    private void TranscriptInvalidated(SourceDescriptor? source, string category) => _transcriptReader.Reset();

    private long SuspendTranscripts(string category, bool explicitSuspension = false, TranscriptRun? expectedRun = null)
    {
        lock (_transcriptConfigurationGate)
        {
            if (expectedRun is not null && !ReferenceEquals(expectedRun, _transcriptRun)) return _transcriptRevision;
            Interlocked.Exchange(ref _transcriptSuspended, 1);
            if (explicitSuspension)
            {
                Interlocked.Exchange(ref _transcriptExplicitSuspension, 1);
                _transcriptSuspensionHash = _transcriptRun?.Hash;
            }
            var revision = Interlocked.Increment(ref _transcriptRevision);
            _transcriptReader.Reset();
            _transcripts.Suspend(category);
            return revision;
        }
    }

    private void ConfigureTranscripts(RemoteConfiguration configuration, string hash, long generation, bool presenceStarted)
    {
        lock (_transcriptConfigurationGate)
        {
            var revision = Interlocked.Increment(ref _transcriptRevision);
            Volatile.Write(ref _transcriptRun, new(configuration, hash, generation, revision));
            Interlocked.Exchange(ref _transcriptPresenceStarted, 0);
            Interlocked.Exchange(ref _transcriptSuspended, 0);
            _transcripts.Configure(configuration, generation, revision);
            if (Volatile.Read(ref _transcriptExplicitSuspension) != 0)
            {
                _transcripts.Suspend("sharing-suspended-preference-not-confirmed");
                Interlocked.Exchange(ref _transcriptSuspended, 1);
            }
            else if (presenceStarted) TranscriptStartedAcknowledged();
        }
    }

    private void TranscriptStartedAcknowledged()
    {
        var run = Volatile.Read(ref _transcriptRun);
        if (run is null || run.Generation != _generation || run.Configuration.BaseUri != _configuration.BaseUri) return;
        if (Interlocked.Exchange(ref _transcriptPresenceStarted, 1) == 0)
            _transcripts.StartedAcknowledged();
    }

    private bool IsTranscriptEligible(SourceDescriptor source, long revision)
    {
        var run = Volatile.Read(ref _transcriptRun);
        if (run is null || Volatile.Read(ref _transcriptSuspended) != 0 ||
            revision != run.Revision || revision != Interlocked.Read(ref _transcriptRevision) ||
            !run.Configuration.IsDetailedReportingEnabled ||
            run.Configuration.BaseUri.Scheme != Uri.UriSchemeHttps || source is not { IsValid: true })
            return false;
        try
        {
            if (ClientConfigurationRevision.Read(_configPath) != run.Hash) return false;
            var targets = run.Configuration.Integrations.Where(target =>
                HookPayloadAdapters.IsEnabled(target) && target.Kind == source.Kind &&
                (source == SourceDescriptor.LegacyCli || target.ScopeId == source.ScopeId)).ToArray();
            if (targets.Length == 0) return false;
            if (source.Kind == "visual-studio" && source.Version != "shared") return false;
            if (source.Kind != "copilot-cli")
                foreach (var target in targets) AutomaticHookConfiguration.RequireInstallable(target, _configPath);
            return true;
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { return false; }
    }

    private async Task MonitorTranscriptConfigurationAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, _lifetime.Token);
                var run = Volatile.Read(ref _transcriptRun);
                if (run is null || Volatile.Read(ref _transcriptSuspended) != 0) continue;
                try
                {
                    var (_, hash) = LoadRevision(_configPath);
                    if (hash != run.Hash) SuspendTranscripts("configuration-reconciliation-required", expectedRun: run);
                }
                catch (Exception ex) when (RemoteFailure.IsExpected(ex))
                {
                    SuspendTranscripts("configuration-unavailable", expectedRun: run);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private ClientIpcResponse HandleTranscript(ClientIpcRequest request)
    {
        if (request.Version != ClientIpc.Version || request.Event is not null || request.Hook is not null ||
            request.ProbeId is not null || request.Command != "transcript-reload" && request.ExpectedRevision is not null)
            return new(false, "invalid");
        if (request.Command == "transcript-reload")
        {
            if (request.Transcript is not null || request.TranscriptSource is not null || request.TranscriptRead is not null)
                return new(false, "invalid");
            if (request.ExpectedRevision == "pause")
            {
                SuspendTranscripts("settings-reloading");
                return new(true, "transcript-suspended");
            }
            if (request.ExpectedRevision == "suspend")
            {
                SuspendTranscripts("sharing-suspended-preference-not-confirmed", explicitSuspension: true);
                return new(true, "transcript-suspended");
            }
            var reloadRevision = SuspendTranscripts("settings-reloading");
            try
            {
                var (config, hash) = LoadRevision(_configPath);
                if (hash != request.ExpectedRevision) return new(false, "revision-mismatch");
                lock (_sync)
                lock (_transcriptConfigurationGate)
                {
                    if (_stopping || _worker is null || config.MachineId != _configuration.MachineId)
                        return new(false, "unavailable");
                    if (reloadRevision != _transcriptRevision) return new(false, "revision-mismatch");
                    if (_transcriptExplicitSuspension != 0 && config.IsDetailedReportingEnabled &&
                        hash == _transcriptSuspensionHash)
                        return new(false, "sharing-suspended-preference-not-saved");
                    Interlocked.Exchange(ref _transcriptExplicitSuspension, 0);
                    _transcriptSuspensionHash = null;
                    ConfigureTranscripts(config, hash, _generation,
                        _startedAcknowledged && config.BaseUri == _configuration.BaseUri);
                }
                return new(true, "transcript-reloaded", hash);
            }
            catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { return new(false, "configuration-unavailable"); }
        }
        if (request.Command == "transcript-negotiate")
        {
            if (request.Transcript is not null || request.TranscriptRead is not null ||
                request.TranscriptSource is not { IsValid: true } source) return new(false, "invalid");
            var revision = Interlocked.Read(ref _transcriptRevision);
            var allowed = IsTranscriptEligible(source, revision);
            var enabled = allowed && _transcripts.IsReady;
            var fields = enabled ? _transcripts.Capabilities : null;
            var profile = enabled ? _transcriptAdapters.Find(source) : null;
            return new(true, enabled ? "transcript-ready" : "transcript-unavailable")
            {
                Transcript = new(revision, enabled, fields?.UserMessages == true, fields?.ToolNames == true,
                    source.Kind == "vscode" && fields?.ToolCorrelation == true,
                    profile is not null && fields?.MainAssistantCompleteMessages == true,
                    profile?.ProfileId, enabled ? profile is null ? "format-unverified" : "waiting-for-next-stop" :
                    !allowed ? "source-or-configuration-unavailable" : TranscriptAvailability)
            };
        }
        if (request.TranscriptSource is not null) return new(false, "invalid");
        if (request.Command == "transcript-hook" && request.TranscriptRead is null)
        {
            var observation = request.Transcript;
            if (!TranscriptHookProjection.IsValid(observation) ||
                !IsTranscriptEligible(observation!.Source, observation.SettingsRevision) ||
                !AllowedTranscriptEvent(observation)) return new(false, "transcript-rejected");
            var admitted = _transcripts.TryAccept(Project(observation), observation.SettingsRevision);
            if (admitted && observation.Payload is TranscriptLifecycle { Signal: TranscriptLifecycleSignal.StopObserved })
                AdmitReaderGap(observation.Source, observation.SessionId, observation.HostEventName,
                    observation.ObservedAtUtc, observation.CaptureId, observation.SettingsRevision,
                    _transcriptAdapters.Find(observation.Source) is null
                        ? TranscriptGapReason.FormatUnverified : TranscriptGapReason.FileUnavailable);
            return new(admitted, admitted ? "transcript-accepted-volatile" : "transcript-unavailable");
        }
        if (request.Command == "transcript-read" && request.Transcript is null &&
            request.TranscriptRead is { IsValid: true } reference &&
            Guid.TryParseExact(reference.CaptureId, "N", out var captureId) && captureId != Guid.Empty)
        {
            var eventName = reference.Source.Kind switch
            {
                "copilot-cli" => "agentStop",
                "vscode" => "Stop",
                _ => null
            };
            if (eventName is null || !IsTranscriptEligible(reference.Source, reference.SettingsRevision))
                return new(false, "transcript-rejected");
            var observation = new TranscriptHookObservation(reference.Source, reference.SessionId, eventName,
                reference.StopTimestampUtc, captureId, reference.SettingsRevision,
                new TranscriptLifecycle(TranscriptLifecycleSignal.StopObserved));
            if (!TranscriptHookProjection.IsValid(observation) || !AllowedTranscriptEvent(observation) ||
                !_transcripts.TryAccept(Project(observation), reference.SettingsRevision))
                return new(false, "transcript-unavailable");
            var category = _transcriptReader.TrySchedule(reference);
            if (category is not null)
                AdmitReaderGap(reference.Source, reference.SessionId, eventName, reference.StopTimestampUtc,
                    captureId, reference.SettingsRevision, ReaderGap(category));
            return new(true, category ?? "transcript-read-queued");
        }
        return new(false, "invalid");
    }

    private bool AllowedTranscriptEvent(TranscriptHookObservation observation)
    {
        var run = Volatile.Read(ref _transcriptRun);
        return run is not null && HookPayloadAdapters.IsAllowed(run.Configuration,
            HookPayloadAdapters.Event(observation.Source.Kind, observation.HostEventName), observation.Source);
    }

    private static TranscriptEvent Project(TranscriptHookObservation observation) => new()
    {
        Source = observation.Source, SessionId = observation.SessionId, Payload = observation.Payload,
        Provenance = new(observation.HostEventName, HookAdapters.Version, observation.ObservedAtUtc,
            observation.ObservedAtUtc, TranscriptOrdering.Arrival, CaptureOrigin.Hook, "hook-v1", observation.CaptureId)
    };

    private bool AdmitReaderGap(SourceDescriptor source, string session, string hostEvent, DateTimeOffset observed,
        Guid captureId, long revision, TranscriptGapReason category) =>
        _transcripts.TryAccept(Project(new(source, session, hostEvent, observed, captureId, revision,
            new TranscriptGap(null, null, category))), revision);

    private ValueTask<bool> AdmitReaderOutputAsync(TranscriptReaderOutput output, CancellationToken token)
    {
        if (token.IsCancellationRequested || !IsTranscriptEligible(output.Reference.Source, output.Reference.SettingsRevision) ||
            !_transcripts.IsReady || !Guid.TryParseExact(output.Reference.CaptureId, "N", out var captureId))
            return ValueTask.FromResult(false);
        var hostEvent = output.Reference.Source.Kind == "vscode" ? "Stop" : "agentStop";
        if (output.Category != TranscriptReaderCategories.Assistant || output.AssistantText is null)
            return ValueTask.FromResult(AdmitReaderGap(output.Reference.Source, output.Reference.SessionId, hostEvent,
                output.Reference.StopTimestampUtc, captureId, output.Reference.SettingsRevision, ReaderGap(output.Category)));
        var value = new TranscriptEvent
        {
            Source = output.Reference.Source, SessionId = output.Reference.SessionId,
            Provenance = new(hostEvent, HookAdapters.Version, output.Reference.StopTimestampUtc, _clock.GetUtcNow(),
                TranscriptOrdering.Arrival, CaptureOrigin.TranscriptFile, output.FormatProfileId, captureId),
            Payload = new TranscriptMessage(TranscriptRole.Assistant, output.NativeMessageId ?? Guid.NewGuid().ToString("N"),
                output.NativeMessageId is null ? TranscriptMessageIdOrigin.Local : TranscriptMessageIdOrigin.Host,
                output.AssistantText)
        };
        return ValueTask.FromResult(!token.IsCancellationRequested &&
            _transcripts.TryAccept(value, output.Reference.SettingsRevision));
    }

    private static TranscriptGapReason ReaderGap(string category) => category switch
    {
        TranscriptReaderCategories.FormatUnverified => TranscriptGapReason.FormatUnverified,
        TranscriptReaderCategories.Baseline => TranscriptGapReason.BaselineEstablished,
        TranscriptReaderCategories.FormatChanged => TranscriptGapReason.FormatChanged,
        TranscriptReaderCategories.Budget => TranscriptGapReason.ReadBudgetExceeded,
        TranscriptReaderCategories.Capacity => TranscriptGapReason.Capacity,
        TranscriptReaderCategories.Expired or "cursor-expired" => TranscriptGapReason.Expired,
        TranscriptReaderCategories.IdentityChanged or TranscriptReaderCategories.Truncated => TranscriptGapReason.FileReset,
        TranscriptReaderCategories.QueueRejected => TranscriptGapReason.QueueOverflow,
        TranscriptReaderCategories.Waiting => TranscriptGapReason.PartialWrite,
        _ => TranscriptGapReason.FileUnavailable
    };

    private sealed record TranscriptRun(RemoteConfiguration Configuration, string Hash, long Generation, long Revision);
}
