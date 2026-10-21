using System.Security.Cryptography;
using System.Text;
using AgentSignaler.Updates;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class UpdateHandoffChecksumTests
{
    private static string Manifest => new string('a', 64) + "  AgentSignaler.Dashboard.msi\n" + new string('b', 64) + "  AgentSignaler.Remote.msi\n";

    [Fact]
    public void PinsExactManifestBytesAndSelectedPackageHash()
    {
        var bytes = Encoding.UTF8.GetBytes(Manifest);
        var pins = UpdateHandoffChecksumReader.Parse(bytes, UpdatePackage.Remote);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), pins.ChecksumSha256);
        Assert.Equal(new string('B', 64), pins.PackageSha256);
    }

    [Theory]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    public void ChecksumByteLimitIsExact(int length, bool accepted)
    {
        var bytes = Encoding.UTF8.GetBytes(Manifest.PadRight(length));
        if (accepted) Assert.Equal(new string('A', 64), UpdateHandoffChecksumReader.Parse(bytes, UpdatePackage.Dashboard).PackageSha256);
        else Assert.Throws<InvalidDataException>(() => UpdateHandoffChecksumReader.Parse(bytes, UpdatePackage.Dashboard));
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void ChecksumEntryLimitIsExact(int count, bool accepted)
    {
        var bytes = Encoding.UTF8.GetBytes(Manifest + new string('\n', count - 3));
        if (accepted) Assert.Equal(new string('B', 64), UpdateHandoffChecksumReader.Parse(bytes, UpdatePackage.Remote).PackageSha256);
        else Assert.Throws<InvalidDataException>(() => UpdateHandoffChecksumReader.Parse(bytes, UpdatePackage.Remote));
    }

    [Fact]
    public void DuplicatePackageHashIsNotLastWriterWins()
    {
        var bytes = Encoding.UTF8.GetBytes(Manifest + new string('c', 64) + "  AgentSignaler.Remote.msi\n");
        Assert.Throws<InvalidDataException>(() => UpdateHandoffChecksumReader.Parse(bytes, UpdatePackage.Remote));
    }

    [Fact]
    public void ForeignAssetOrArbitraryTextIsRejected()
    {
        var bytes = Encoding.UTF8.GetBytes(Manifest + new string('c', 64) + "  execute.ps1\n");
        Assert.Throws<InvalidDataException>(() => UpdateHandoffChecksumReader.Parse(bytes, UpdatePackage.Remote));
    }
}