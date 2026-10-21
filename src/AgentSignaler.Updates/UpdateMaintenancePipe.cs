using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AgentSignaler.Updates;

public sealed record UpdateMaintenanceMessage(int SchemaVersion, Guid RunId, string Command);
public sealed record UpdateMaintenanceReply(int SchemaVersion, Guid RunId, bool Accepted);

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateMaintenanceTransport(UpdateInstalledPackage installation, IUpdateStorage storage,
    IUpdateProcessAdapter processes, TimeProvider timeProvider) : IUpdateMaintenanceTransport
{
    private readonly UpdateInstalledPackage Installation = installation;
    private readonly IUpdateStorage Storage = storage;
    private readonly IUpdateProcessAdapter Processes = processes;
    private readonly TimeProvider Time = timeProvider;
    public Task<IAsyncDisposable> ListenAsync(UpdateInstanceRegistration registration, IUpdateMaintenanceParticipant participant,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateMaintenanceValidation.Registration(registration, Installation);
        if (!UpdateMaintenanceValidation.SameProcess(Processes.Current, registration.Process))
            throw new UnauthorizedAccessException("update_listener_identity");
        return Task.FromResult<IAsyncDisposable>(new Listener(this, registration, participant));
    }

    public async Task<bool> SendAsync(UpdateInstanceRegistration registration, Guid runId, string command, CancellationToken cancellationToken)
    {
        UpdateMaintenanceValidation.Registration(registration, Installation);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(UpdateMaintenanceValidation.RequestTimeout(command));
        using var pipe = new NamedPipeClientStream(".", registration.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(budget.Token).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != registration.Process.ProcessId ||
            !UpdateMaintenanceValidation.SameProcess(Processes.Find((int)pid), registration.Process))
            throw new UnauthorizedAccessException("update_pipe_server");
        await UpdatePipeFrame.WriteAsync(pipe, new UpdateMaintenanceMessage(1, runId, command), budget.Token).ConfigureAwait(false);
        var reply = await UpdatePipeFrame.ReadAsync<UpdateMaintenanceReply>(pipe, budget.Token).ConfigureAwait(false);
        return reply.SchemaVersion == 1 && reply.RunId == runId && reply.Accepted;
    }

    private sealed class Listener : IAsyncDisposable
    {
        private readonly WindowsUpdateMaintenanceTransport owner;
        private readonly UpdateInstanceRegistration registration;
        private readonly IUpdateMaintenanceParticipant participant;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private NamedPipeServerStream pipe;
        private Guid? prepared;
        private bool commitReserved;
        private DateTimeOffset preparedUntil;

        public Listener(WindowsUpdateMaintenanceTransport owner, UpdateInstanceRegistration registration, IUpdateMaintenanceParticipant participant)
        {
            this.owner = owner; this.registration = registration; this.participant = participant;
            pipe = Create(); // Synchronous creation prevents registration of a squatted endpoint.
            loop = RunAsync();
        }

        private NamedPipeServerStream Create() => new(registration.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 4096, 4096);

        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await ExpirePreparationAsync().ConfigureAwait(false);
                    using var poll = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    poll.CancelAfter(TimeSpan.FromSeconds(1));
                    try { await pipe.WaitForConnectionAsync(poll.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                    {
                        await ExpirePreparationAsync().ConfigureAwait(false);
                        continue;
                    }
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    budget.CancelAfter(TimeSpan.FromSeconds(15));
                    var requestStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) throw new UnauthorizedAccessException("update_pipe_client");
                        var message = await UpdatePipeFrame.ReadAsync<UpdateMaintenanceMessage>(pipe, budget.Token).ConfigureAwait(false);
                        if (message.SchemaVersion != 1 || message.RunId == Guid.Empty) throw new InvalidDataException("update_pipe_request");
                        var gateName = message.Command == "health" ? "restart-" + message.RunId.ToString("N") + ".json" : "gate.json";
                        var gate = UpdateStrictJson.Read<UpdateMaintenanceGate>(owner.Storage.Read(gateName) ?? throw new InvalidDataException("update_gate_missing"));
                        UpdateMaintenanceValidation.Gate(gate, owner.Installation, owner.Time.GetUtcNow());
                        var expected = message.Command == "health" ? gate.Owner : gate.Controller;
                        if (owner.Time.GetUtcNow() >= gate.ExpiresAt || pid != expected.ProcessId ||
                            !UpdateMaintenanceValidation.SameProcess(owner.Processes.Find((int)pid), expected))
                            throw new UnauthorizedAccessException("update_pipe_client");
                        if (message.SchemaVersion != 1 || message.RunId != gate.RunId || message.RunId == Guid.Empty)
                            throw new InvalidDataException("update_pipe_request");
                        if (message.Command == "prepare")
                        {
                            var remaining = UpdateMaintenanceValidation.PrepareTimeout - Stopwatch.GetElapsedTime(requestStarted);
                            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException(budget.Token);
                            budget.CancelAfter(remaining);
                        }
                        var accepted = false;
                        switch (message.Command)
                        {
                            case "health" when participant is IUpdateMaintenanceHealth health && gate.Phase == UpdateMaintenancePhase.Servicing:
                                accepted = await health.IsReadyAsync(budget.Token).ConfigureAwait(false);
                                break;
                            case "prepare" when gate.Phase == UpdateMaintenancePhase.Preparing && (prepared is null || prepared == message.RunId):
                                prepared = message.RunId;
                                var reservation = owner.Time.GetUtcNow() + UpdateMaintenanceValidation.PreparationReservation;
                                preparedUntil = reservation < gate.ExpiresAt ? reservation : gate.ExpiresAt;
                                accepted = await participant.PrepareAsync(message.RunId, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
                                break;
                            case "reserve" when prepared == message.RunId && gate.Phase == UpdateMaintenancePhase.Preparing:
                                accepted = participant is IUpdateMaintenanceCommitConsent consent ? consent.TryAcceptCommit(message.RunId) :
                                    registration.Restart.Application != UpdateApplication.Client;
                                commitReserved = accepted;
                                if (accepted)
                                {
                                    var reservedUntil = owner.Time.GetUtcNow() + UpdateMaintenanceValidation.PreparationReservation;
                                    preparedUntil = reservedUntil < gate.ExpiresAt ? reservedUntil : gate.ExpiresAt;
                                }
                                break;
                            case "cancel" when gate.Phase == UpdateMaintenancePhase.Preparing && (prepared is null || prepared == message.RunId):
                                if (prepared is not null)
                                    await participant.CancelAsync(message.RunId, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
                                prepared = null; commitReserved = false; accepted = true;
                                break;
                            case "commit" when prepared == message.RunId && commitReserved && gate.Phase == UpdateMaintenancePhase.Servicing:
                                // Acknowledge before dispatching exit; the updater still must prove exact process exit.
                                await WriteAcknowledgementAsync(new(1, message.RunId, true), true, budget.Token).ConfigureAwait(false);
                                prepared = null; commitReserved = false;
                                await participant.CommitExitAsync(message.RunId, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
                                continue;
                        }
                        await WriteAcknowledgementAsync(new(1, message.RunId, accepted), message.Command == "reserve", budget.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
                    { /* Fail closed. No raw payload or exception logging. */ }
                    finally
                    {
                        pipe.Dispose();
                        if (!stop.IsCancellationRequested) pipe = Create();
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        private async Task WriteAcknowledgementAsync(UpdateMaintenanceReply reply, bool releaseReservationOnFailure,
            CancellationToken cancellationToken)
        {
            try { await UpdatePipeFrame.WriteAsync(pipe, reply, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (releaseReservationOnFailure && reply.Accepted && prepared == reply.RunId && commitReserved)
                    await CancelPreparationAsync(reply.RunId).ConfigureAwait(false);
                throw;
            }
        }

        private async Task ExpirePreparationAsync()
        {
            if (prepared is not { } runId) return;
            var bytes = owner.Storage.Read("gate.json");
            UpdateMaintenanceGate? gate = null;
            if (bytes is not null)
            {
                gate = UpdateStrictJson.Read<UpdateMaintenanceGate>(bytes);
                UpdateMaintenanceValidation.Gate(gate, owner.Installation, owner.Time.GetUtcNow());
            }
            if (!UpdateMaintenanceValidation.ShouldCancelPreparation(runId, preparedUntil, gate, owner.Time.GetUtcNow())) return;
            await CancelPreparationAsync(runId).ConfigureAwait(false);
        }

        private async Task CancelPreparationAsync(Guid runId)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await participant.CancelAsync(runId, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
            prepared = null; commitReserved = false;
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            pipe.Dispose();
            try { await loop.ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            finally { stop.Dispose(); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}

public static class UpdatePipeFrame
{
    public const int MaximumBytes = 4096;
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > MaximumBytes) throw new InvalidDataException("update_pipe_limit");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return UpdateStrictJson.Read<T>(bytes, MaximumBytes);
    }
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var bytes = UpdateStrictJson.Write(message);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("update_pipe_limit");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}