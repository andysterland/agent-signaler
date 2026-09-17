using AgentSignaler.Service;
using AgentSignaler.Contracts;

namespace AgentSignaler.Dashboard;

public sealed partial class DashboardRuntime
{
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), options.TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                try { await RefreshMachinesAsync(lifetime.Token).ConfigureAwait(false); }
                catch (Exception error) when (IsExpected(error) || error is OperationCanceledException && !lifetime.IsCancellationRequested)
                {
                    RuntimeInvalidation? changed = null;
                    lock (machineSync)
                    {
                        if (!machines.IsStale)
                        {
                            machines = machines with { IsStale = true, Revision = checked(machines.Revision + 1) };
                            changed = new(HostInstanceId, "machines", machines.Revision);
                        }
                    }
                    if (changed is not null) Publish(changed);
                    Problem?.Invoke(new(1008, "machines", true));
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    internal async Task RefreshMachinesAsync(CancellationToken cancellationToken = default)
    {
        if (store is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        await machineRefresh.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var observed = await (options.ReadMachinesAsync?.Invoke(store, linked.Token) ??
                store.GetMachinesAsync(linked.Token)).ConfigureAwait(false);
            var now = options.TimeProvider.GetUtcNow();
            var items = observed.OrderBy(m => m.MachineId).Select(machine => new RuntimeMachine(
                machine with { Sessions = Array.AsReadOnly(machine.Sessions.ToArray()) },
                Array.AsReadOnly(machine.Sessions.OrderBy(s => s.Source?.ToString(), StringComparer.Ordinal)
                    .ThenBy(s => s.SessionId, StringComparer.Ordinal)
                    .Select(s => new RuntimeSession(s, StateReducer.Effective(s, now))).ToArray()))).ToArray();
            var changes = new List<RuntimeInvalidation>();
            lock (machineSync)
            {
                foreach (var item in items)
                {
                    var before = machines.State.FirstOrDefault(m => m.Machine.MachineId == item.Machine.MachineId);
                    if (before is null || !SameMachine(before, item))
                    {
                        var revision = machineRevisions[item.Machine.MachineId] = checked(++nextMachineRevision);
                        changes.Add(new(HostInstanceId, "machines", revision, item.Machine.MachineId));
                    }
                }
                foreach (var removed in machineRevisions.Keys.Except(items.Select(m => m.Machine.MachineId)).ToArray())
                {
                    machineRevisions.Remove(removed);
                    changes.Add(new(HostInstanceId, "machines", checked(++nextMachineRevision), removed));
                }
                if (changes.Count > 0 || machines.IsStale)
                {
                    machines = new(HostInstanceId, "machines", checked(machines.Revision + 1), Array.AsReadOnly(items));
                    changes.Add(new(HostInstanceId, "machines", machines.Revision));
                }
            }
            foreach (var item in items)
            {
                connections?.Observe(item.Machine.MachineId, item.Machine.WindowsAppConnection);
                UpdateWindowsState(item.Machine.MachineId);
            }
            connections?.ForgetExcept(items.Select(item => item.Machine.MachineId).ToHashSet());
            lock (windowsSync)
                foreach (var removed in windowsDomains.Keys.Except(items.Select(m => m.Machine.MachineId)).ToArray())
                    windowsDomains.Remove(removed);
            foreach (var change in changes) Publish(change);
        }
        finally { machineRefresh.Release(); }
    }

    private static bool SameMachine(RuntimeMachine left, RuntimeMachine right) =>
        left.Machine with { Sessions = Array.Empty<SessionSnapshot>() } ==
        right.Machine with { Sessions = Array.Empty<SessionSnapshot>() } &&
        left.Sessions.SequenceEqual(right.Sessions);

    public DomainSnapshot<RuntimePage<RuntimeMachine>> GetMachines(int offset = 0, int limit = 100, long? expectedRevision = null)
    {
        lock (machineSync)
        {
            ValidatePage(offset, limit, expectedRevision, machines.Revision);
            return new(HostInstanceId, "machines", machines.Revision, Page(machines.State, offset, limit), machines.IsStale);
        }
    }

    public DomainSnapshot<RuntimeMachine> GetMachine(Guid id)
    {
        lock (machineSync)
        {
            var machine = machines.State.FirstOrDefault(m => m.Machine.MachineId == id)
                ?? throw new RuntimeCommandException(new(1002, "machineId", false));
            return new(HostInstanceId, "machines", machineRevisions[id], machine, machines.IsStale);
        }
    }

    public DomainSnapshot<RuntimePage<RuntimeSession>> GetSessions(Guid id, int offset = 0, int limit = 100,
        long? expectedRevision = null)
    {
        lock (machineSync)
        {
            var machine = GetMachine(id);
            ValidatePage(offset, limit, expectedRevision, machine.Revision);
            return new(HostInstanceId, "machines", machine.Revision, Page(machine.State.Sessions, offset, limit), machine.IsStale);
        }
    }

    public DomainSnapshot<RuntimeNote> GetNote(Guid id, int offset = 0, int length = 16384, long? expectedRevision = null)
    {
        lock (machineSync)
        {
            var machine = GetMachine(id);
            var note = machine.State.Machine.Note ?? "";
            if (offset < 0 || offset > note.Length || length is < 1 or > 16384)
                throw new RuntimeCommandException(new(1001, "note", false));
            if (offset > 0 && offset < note.Length && char.IsLowSurrogate(note[offset]) && char.IsHighSurrogate(note[offset - 1]))
                throw new RuntimeCommandException(new(1001, "offset", false));
            ValidateExpectedPage(offset, expectedRevision, machine.Revision);
            var count = Math.Min(length, note.Length - offset);
            if (count > 0 && offset + count < note.Length && char.IsHighSurrogate(note[offset + count - 1]) &&
                char.IsLowSurrogate(note[offset + count])) count--;
            if (count == 0 && offset < note.Length) throw new RuntimeCommandException(new(1011, "length", false));
            return new(HostInstanceId, "machines", machine.Revision,
                new(note.Substring(offset, count), note.Length, offset + count < note.Length ? offset + count : null), machine.IsStale);
        }
    }

    private static RuntimePage<T> Page<T>(IReadOnlyList<T> items, int offset, int limit)
    {
        if (offset > items.Count) throw new RuntimeCommandException(new(1001, "offset", false));
        var page = items.Skip(offset).Take(limit).ToArray();
        return new(Array.AsReadOnly(page), items.Count, offset + page.Length < items.Count ? offset + page.Length : null);
    }

    private static void ValidatePage(int offset, int limit, long? expected, long revision)
    {
        if (offset < 0 || limit is < 1 or > 250) throw new RuntimeCommandException(new(1001, "pagination", false));
        ValidateExpectedPage(offset, expected, revision);
    }
    private static void ValidateExpectedPage(int offset, long? expected, long revision)
    {
        if (offset > 0 && expected is null) throw new RuntimeCommandException(new(1001, "expectedRevision", false));
        if (expected is not null && expected != revision) throw new RuntimeCommandException(new(1004, "expectedRevision", true));
    }

    private void RequireMachine(Guid id, Guid expectedHost, long expectedRevision)
    {
        RequireInstance(expectedHost);
        var snapshot = GetMachine(id);
        if (snapshot.IsStale) throw new RuntimeCommandException(new(1005, "machines", true));
        if (snapshot.Revision != expectedRevision) throw new RuntimeCommandException(new(1004, "expectedRevision", true));
        if (store is null) throw new RuntimeCommandException(new(1005, "storage", true));
    }

    public Task<RuntimeResult<RuntimeMachine>> UpdateMachineAsync(Guid id, string? displayName, string? note,
        Guid expectedHostInstanceId, long expectedRevision, CancellationToken cancellationToken = default) =>
        RunCommandAsync("machines", [$"machine:{id:D}"], TimeSpan.FromSeconds(10), async (token, commit) =>
        {
            RequireMachine(id, expectedHostInstanceId, expectedRevision);
            var name = displayName?.Trim();
            if (name is { Length: > 128 } || name?.Any(char.IsControl) == true)
                throw new RuntimeCommandException(new(1001, "displayName", false));
            if (note is { Length: > 16384 } && note != GetMachine(id).State.Machine.Note)
                throw new RuntimeCommandException(new(1001, "note", false));
            token.ThrowIfCancellationRequested();
            commit.State = RuntimeCommitState.Unknown;
            if (note is { Length: > 16384 })
                await store!.RenameAsync(id, displayName, token).ConfigureAwait(false);
            else
                await store!.UpdateDetailsAsync(id, displayName, note, token).ConfigureAwait(false);
            commit.State = RuntimeCommitState.Committed;
            await RefreshMachinesAsync(token).ConfigureAwait(false);
            return GetMachine(id);
        }, cancellationToken);

    public Task<RuntimeResult<RuntimePage<RuntimeMachine>>> RemoveMachineAsync(Guid id, bool confirmed,
        Guid expectedHostInstanceId, long expectedRevision, CancellationToken cancellationToken = default) =>
        RunCommandAsync("machines", [$"machine:{id:D}"], TimeSpan.FromSeconds(10), async (token, commit) =>
        {
            if (!confirmed) throw new RuntimeCommandException(new(1009, "confirmed", false));
            RequireMachine(id, expectedHostInstanceId, expectedRevision);
            token.ThrowIfCancellationRequested();
            commit.State = RuntimeCommitState.Unknown;
            await store!.RemoveAsync(id, token).ConfigureAwait(false);
            commit.State = RuntimeCommitState.Committed;
            server?.Transcripts.ClearMachine(id, removed: true);
            await RefreshMachinesAsync(token).ConfigureAwait(false);
            return GetMachines();
        }, cancellationToken);
}
