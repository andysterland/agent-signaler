using System.Collections.Immutable;
using AgentSignaler.Updates;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class UpdateInventoryTests
{
    private const string Sid = "S-1-5-21-111-222-333-1001";
    private const string Local = @"C:\Users\test\AppData\Local";
    private static UpdateInventoryProduct Product => new(Guid.NewGuid(), UpdatePolicy.UpgradeCode(UpdatePackage.Remote),
        new Version(1, 2, 3), Path.Combine(Local, "Programs", "AgentSignaler", "Remote"), Sid, true, true, "x64", true);

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NativeCatalogSelectionIsLimitedToExpectedUpgradeFamiliesAndBindsProductIdentity()
    {
        var dashboard = Guid.Parse("00000001-0000-0000-0000-000000000001");
        var remote = Guid.Parse("00000002-0000-0000-0000-000000000002");
        var requests = new List<Guid>();
        var products = WindowsUpdateInventoryAdapter.ReadRelatedProductCodes((upgrade, index) =>
        {
            if (index != 0) return (259u, "");
            requests.Add(upgrade);
            return (0u, (upgrade == UpdatePolicy.UpgradeCode(UpdatePackage.Dashboard) ? dashboard : remote).ToString("B"));
        }, default);
        Assert.Equal(Enum.GetValues<UpdatePackage>().Select(UpdatePolicy.UpgradeCode), requests);
        Assert.Equal(2, products.Count);
        Assert.Equal(UpdatePolicy.UpgradeCode(UpdatePackage.Dashboard), products[dashboard]);
        Assert.Equal(UpdatePolicy.UpgradeCode(UpdatePackage.Remote), products[remote]);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NativeRelatedProductEnumerationHasExactPerFamilyBound(int count, bool accepted)
    {
        var calls = 0;
        Dictionary<Guid, Guid> Read() => WindowsUpdateInventoryAdapter.ReadRelatedProductCodes((upgrade, index) =>
        {
            calls++;
            return index == count ? (259u, "") : (0u,
                new Guid((int)index + 1, upgrade == UpdatePolicy.UpgradeCode(UpdatePackage.Dashboard) ? (short)1 : (short)2, 0, new byte[8]).ToString("B"));
        }, default);
        if (accepted)
        {
            Assert.Equal(256, Read().Count);
            Assert.Equal(258, calls);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => Read());
            Assert.Equal(129, calls);
        }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NativeRelatedProductEnumerationRejectsAmbiguousRegistrationAndHonorsCancellation()
    {
        Assert.Throws<InvalidDataException>(() => WindowsUpdateInventoryAdapter.ReadRelatedProductCodes(
            (_, index) => index == 0 ? (0u, Product.ProductCode.ToString("B")) : (1605u, ""), default));
        var code = Guid.NewGuid().ToString("B");
        Assert.Throws<InvalidDataException>(() => WindowsUpdateInventoryAdapter.ReadRelatedProductCodes(
            (_, index) => index == 0 ? (0u, code) : (259u, ""), default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => WindowsUpdateInventoryAdapter.ReadRelatedProductCodes(
            (_, _) => throw new InvalidOperationException("Cancelled discovery must not call native inventory."), cancelled.Token));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NativeCurrentUserSelectionQueriesOnlyExactRelatedProductsNotTheUnrelatedCatalog()
    {
        var dashboard = Guid.NewGuid();
        var remote = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var related = new Dictionary<Guid, Guid>
        {
            [dashboard] = UpdatePolicy.UpgradeCode(UpdatePackage.Dashboard),
            [remote] = UpdatePolicy.UpgradeCode(UpdatePackage.Remote)
        };
        var requests = new List<Guid>();
        var products = WindowsUpdateInventoryAdapter.ReadCurrentUserProductCodes(related, Sid, product =>
        {
            requests.Add(product);
            if (product == unrelated) throw new IOException("An unrelated cached MSI must never be opened.");
            Assert.Contains(product, related.Keys);
            return (0u, product.ToString("B"), 2u, Sid);
        }, default);
        Assert.Equal(related.Keys.Order(), requests.Order());
        Assert.Equal(related.OrderBy(pair => pair.Key), products.OrderBy(pair => pair.Key));
        Assert.DoesNotContain(unrelated, requests);
    }

    [Theory]
    [InlineData(259u)]
    [InlineData(1605u)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NativeCurrentUserSelectionExcludesOnlyDocumentedAbsentContexts(uint status)
    {
        var product = Product;
        var selected = WindowsUpdateInventoryAdapter.ReadCurrentUserProductCodes(
            new Dictionary<Guid, Guid> { [product.ProductCode] = product.UpgradeCode }, Sid,
            _ => (status, "", 0u, ""), default);
        Assert.Empty(selected);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("access")]
    [InlineData("buffer")]
    [InlineData("other-product")]
    [InlineData("invalid-product")]
    [InlineData("managed")]
    [InlineData("machine")]
    [InlineData("other-user")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NativeCurrentUserSelectionRejectsRelevantErrorsAndIdentityMismatches(string mismatch)
    {
        var product = Product;
        var status = mismatch switch { "corrupt" => 1610u, "access" => 5u, "buffer" => 234u, _ => 0u };
        var code = mismatch switch { "other-product" => Guid.NewGuid().ToString("B"), "invalid-product" => "invalid", _ => product.ProductCode.ToString("B") };
        var context = mismatch switch { "managed" => 1u, "machine" => 4u, _ => 2u };
        var sid = mismatch == "other-user" ? "S-1-5-21-999-1001" : Sid;
        var error = Assert.Throws<InvalidDataException>(() => WindowsUpdateInventoryAdapter.ReadCurrentUserProductCodes(
            new Dictionary<Guid, Guid> { [product.ProductCode] = product.UpgradeCode }, Sid,
            _ => (status, code, context, sid), default));
        Assert.Equal("update_inventory_unavailable", error.Message);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NativeCurrentUserSelectionHasExactBoundAndNoUnrelatedCatalogLimit(int count, bool accepted)
    {
        var related = Enumerable.Range(1, count).ToDictionary(index => new Guid(index, 0, 0, new byte[8]),
            _ => UpdatePolicy.UpgradeCode(UpdatePackage.Remote));
        var calls = 0;
        Dictionary<Guid, Guid> Read() => WindowsUpdateInventoryAdapter.ReadCurrentUserProductCodes(related, Sid, product =>
        {
            calls++;
            return (0u, product.ToString("B"), 2u, Sid);
        }, default);
        if (accepted)
        {
            Assert.Equal(256, Read().Count);
            Assert.Equal(256, calls);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => Read());
            Assert.Equal(0, calls);
        }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NativeCurrentUserSelectionHonorsCancellationBeforeAnyInventoryRead()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => WindowsUpdateInventoryAdapter.ReadCurrentUserProductCodes(
            new Dictionary<Guid, Guid> { [Product.ProductCode] = Product.UpgradeCode }, Sid,
            _ => throw new InvalidOperationException("Cancelled selection must not query native inventory."), cancelled.Token));
    }

    [Fact]
    public async Task ExactOwnedRegisteredNondefaultDirectoryIsEligible()
    {
        var product = Product with { InstallDirectory = @"D:\OwnedApplications\AgentSignalerRemote" };
        var actual = await new UpdateInventory(new Fake([product]), Sid, Local).FindAsync(UpdatePackage.Remote);
        Assert.Equal(product.InstallDirectory, actual!.InstallDirectory);
    }

    [Fact]
    public async Task ExactCurrentUserCanonicalX64ProductIsEligible()
    {
        var product = Product;
        var result = await new UpdateInventory(new Fake([product]), Sid, Local).FindAsync(UpdatePackage.Remote);
        Assert.NotNull(result);
        Assert.Equal(product.ProductCode, result.ProductCode);
        Assert.Equal(product.InstallDirectory, result.InstallDirectory);
        Assert.Equal(product.Version, result.Version);
    }

    [Fact]
    public async Task PortableBuildHasNoServicingEligibility() =>
        Assert.Null(await new UpdateInventory(new Fake([]), Sid, Local).FindAsync(UpdatePackage.Remote));

    [Fact]
    public async Task MultipleRelatedProductsAreNotArbitrarilySelected() =>
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateInventory(new Fake([Product, Product]), Sid, Local).FindAsync(UpdatePackage.Remote));

    [Theory]
    [InlineData("machine")]
    [InlineData("different-user")]
    [InlineData("x86")]
    [InlineData("unverified-directory")]
    [InlineData("not-installed")]
    [InlineData("four-part-version")]
    [InlineData("major-overflow")]
    public async Task IneligibleProductFactsFailClosed(string mismatch)
    {
        var product = Product;
        product = mismatch switch
        {
            "machine" => product with { PerUserUnmanaged = false },
            "different-user" => product with { UserSid = "S-1-5-21-999-1001" },
            "x86" => product with { Architecture = "Intel" },
            "unverified-directory" => product with { CanonicalDirectoryVerified = false },
            "not-installed" => product with { Installed = false },
            "four-part-version" => product with { Version = new(1, 2, 3, 0) },
            _ => product with { Version = new(256, 0, 0) }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateInventory(new Fake([product]), Sid, Local).FindAsync(UpdatePackage.Remote));
    }

    [Theory]
    [InlineData(255, 255, 65535)]
    [InlineData(0, 0, 0)]
    public async Task ExactMsiVersionBoundsRemainEligible(int major, int minor, int patch)
    {
        var product = Product with { Version = new(major, minor, patch) };
        var result = await new UpdateInventory(new Fake([product]), Sid, Local).FindAsync(UpdatePackage.Remote);
        Assert.Equal(product.Version, result!.Version);
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public async Task InventoryCapacityIsExact(int count, bool accepted)
    {
        var products = Enumerable.Range(0, count).Select(_ => Product with { UpgradeCode = Guid.NewGuid() }).ToImmutableArray();
        var inventory = new UpdateInventory(new Fake(products), Sid, Local);
        if (accepted) Assert.Null(await inventory.FindAsync(UpdatePackage.Remote));
        else await Assert.ThrowsAsync<InvalidDataException>(() => inventory.FindAsync(UpdatePackage.Remote));
    }

    [Theory]
    [InlineData("com")]
    [InlineData("format")]
    [InlineData("overflow")]
    [InlineData("binder")]
    [InlineData("io")]
    [InlineData("access")]
    public async Task ExpectedInventoryFailuresAreBoundedAndDoNotExposeRegistrationDetails(string kind)
    {
        const string privateDetails = @"C:\Users\private\cached-product.msi";
        Exception failure = kind switch
        {
            "com" => new System.Runtime.InteropServices.COMException(privateDetails),
            "format" => new FormatException(privateDetails),
            "overflow" => new OverflowException(privateDetails),
            "binder" => new Microsoft.CSharp.RuntimeBinder.RuntimeBinderException(privateDetails),
            "io" => new IOException(privateDetails),
            _ => new UnauthorizedAccessException(privateDetails)
        };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new UpdateInventory(new Failing(failure), Sid, Local).FindAsync(UpdatePackage.Remote));
        Assert.Equal("update_inventory_unavailable", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task InventoryCancellationIsNotConvertedToCorruptMetadata()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateInventory(new Fake([]), Sid, Local).FindAsync(UpdatePackage.Remote, cancellation.Token));
    }

    private sealed class Failing(Exception failure) : IUpdateInventoryAdapter
    {
        public Task<ImmutableArray<UpdateInventoryProduct>> ReadAsync(string userSid, CancellationToken cancellationToken) =>
            Task.FromException<ImmutableArray<UpdateInventoryProduct>>(failure);
    }

    private sealed class Fake(ImmutableArray<UpdateInventoryProduct> products) : IUpdateInventoryAdapter
    {
        public Task<ImmutableArray<UpdateInventoryProduct>> ReadAsync(string userSid, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(products); }
    }
}