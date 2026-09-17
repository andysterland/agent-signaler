using System.Net;
using System.Reflection;
using AgentSignaler.RpcHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentSignaler.RpcHost.Tests;

public sealed class RpcHostingTests
{
    [Fact]
    public async Task FrameworkHostConstructsWithoutStartingListeners()
    {
        var stage = "builder";
        try
        {
            var builder = RpcServer.CreateBuilder(51821);
            Assert.IsType<MemoryConfigurationSource>(Assert.Single(builder.Configuration.Sources));
            Assert.Equal(typeof(RpcServer).Assembly.GetName().Name, builder.Configuration[WebHostDefaults.ApplicationKey]);
            Assert.Equal(Environments.Production, builder.Environment.EnvironmentName);
            Assert.Equal(builder.Environment.EnvironmentName, builder.Configuration[WebHostDefaults.EnvironmentKey]);
            Assert.Equal(builder.Environment.ContentRootPath, builder.Configuration[WebHostDefaults.ContentRootKey]);
            Assert.Equal("", builder.Configuration[WebHostDefaults.ServerUrlsKey]);
            Assert.Equal("false", builder.Configuration[WebHostDefaults.PreferHostingUrlsKey], ignoreCase: true);
            Assert.Empty(builder.Configuration.GetSection("Kestrel").GetChildren());
            stage = "frameworkBuild";
            await using var application = builder.Build();
        }
        catch (Exception error)
        {
            var category = error.Message.Contains("application", StringComparison.OrdinalIgnoreCase) ? "applicationConfiguration" :
                error.Message.Contains("environment", StringComparison.OrdinalIgnoreCase) ? "environmentConfiguration" :
                error.Message.Contains("root", StringComparison.OrdinalIgnoreCase) ? "contentRootConfiguration" :
                error.Message.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
                error.Message.Contains("source", StringComparison.OrdinalIgnoreCase) ? "configurationProvider" : "otherConfiguration";
            Assert.Fail($"{stage}:{error.GetType().Name}:{category}:{error.TargetSite?.DeclaringType?.Name}.{error.TargetSite?.Name}");
        }
    }

    [Fact]
    public async Task FrameworkKestrelOptionsRetainOnlyOwnedLoopbacksEvenWithInjectedEndpointConfiguration()
    {
        var builder = RpcServer.CreateBuilder(51821);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:extra:Url"] = "http://0.0.0.0:51999",
            ["Kestrel:Endpoints:receiver:Url"] = "http://0.0.0.0:51820"
        });
        await using var application = builder.Build();
        var options = application.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        options.ConfigurationLoader!.Load();
        // Inspect the configured framework options without starting Kestrel or opening sockets.
        var owned = ReadListeners(options, "CodeBackedListenOptions");
        Assert.Equal(2, owned.Count);
        Assert.Contains(owned, listener => listener.IPEndPoint!.Address.Equals(IPAddress.Loopback));
        Assert.Contains(owned, listener => listener.IPEndPoint!.Address.Equals(IPAddress.IPv6Loopback));
        Assert.All(owned, listener => Assert.Equal(51821, listener.IPEndPoint!.Port));
        Assert.Empty(ReadListeners(options, "ConfigurationBackedListenOptions"));
    }

    private static IReadOnlyList<ListenOptions> ReadListeners(KestrelServerOptions options, string property) =>
        (IReadOnlyList<ListenOptions>)typeof(KestrelServerOptions).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(options)!;
}
