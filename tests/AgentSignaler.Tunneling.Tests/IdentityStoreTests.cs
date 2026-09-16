using AgentSignaler.Tunneling;
using Xunit;

namespace AgentSignaler.Tunneling.Tests;

public sealed class IdentityStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tunneling-state-tests", Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(directory, "tunnel-state.json");

    [Fact]
    public async Task MarkerIsDurablyCreatedAndReused()
    {
        var first = await new TunnelIdentityStore(StatePath).LoadOrCreateAsync();
        var second = await new TunnelIdentityStore(StatePath).LoadOrCreateAsync();
        Assert.Equal(first, second);
        Assert.True(Guid.TryParseExact(first.InstallationMarker, "N", out var marker));
        Assert.NotEqual(Guid.Empty, marker);
        Assert.Empty(first.OwnerHash);
        Assert.Null(first.TunnelId);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task PendingIntentAndFullIdentityRoundTripWithoutSecrets()
    {
        var store = new TunnelIdentityStore(StatePath);
        var first = await store.LoadOrCreateAsync();
        var pending = first with { OwnerHash = new string('a', 64), PendingTunnelId = "agentsignaler-pending" };
        await store.SaveAsync(pending);
        Assert.Equal(pending, await store.LoadOrCreateAsync());
        var full = pending with { TunnelId = "agentsignaler-pending.usw2" };
        await store.SaveAsync(full);
        Assert.Equal(full, await store.LoadOrCreateAsync());
        var text = await File.ReadAllTextAsync(StatePath);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"version":2,"identity":{}}""")]
    [InlineData("""{"version":1,"version":2,"identity":{}}""")]
    [InlineData("""{"version":1,"Version":2,"identity":{}}""")]
    [InlineData("""{"version":1,"identity":{"ownerHash":"","installationMarker":"33333333333333333333333333333333","tunnelId":"unbound.usw2"}}""")]
    [InlineData("""{"version":1,"identity":{"ownerHash":"","installationMarker":"33333333333333333333333333333333","token":"secret"}}""")]
    public async Task CorruptionOrUnknownStateIsNotSilentlyReset(string content)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(StatePath, content);
        await Assert.ThrowsAsync<TunnelException>(() => new TunnelIdentityStore(StatePath).LoadOrCreateAsync());
        Assert.Equal(content, await File.ReadAllTextAsync(StatePath));
    }

    [Fact]
    public async Task CancelledSaveKeepsPreviousDurableIdentity()
    {
        var store = new TunnelIdentityStore(StatePath);
        var first = await store.LoadOrCreateAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(first with { OwnerHash = new string('b', 64) }, new CancellationToken(canceled: true)));
        Assert.Equal(first, await store.LoadOrCreateAsync());
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task InconsistentPendingAndFullIdsCannotBeSaved()
    {
        var store = new TunnelIdentityStore(StatePath);
        var first = await store.LoadOrCreateAsync();
        await Assert.ThrowsAsync<TunnelException>(() => store.SaveAsync(first with
        {
            OwnerHash = new string('a', 64), TunnelId = "other.usw2", PendingTunnelId = "pending"
        }));
        Assert.Equal(first, await store.LoadOrCreateAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
