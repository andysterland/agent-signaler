using System.Text;
using System.Text.Json;
using AgentSignaler.Service;

namespace AgentSignaler.Dashboard;

internal sealed record AzureAccount(string Upn, Guid Tenant, Guid Subscription)
{
    public static async Task<AzureAccount> GetAsync(IAzureCliProcess process, CancellationToken cancellationToken,
        WindowsAppConnection? expectedMapping = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = AzureCliCommand.AccountShow();
        var result = await process.RunAsync(command, command.Timeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        AzureCliResponse.CheckResult(result, account: true);
        return Parse(result.StandardOutput, expectedMapping);
    }

    public static AzureAccount Parse(string json, WindowsAppConnection? expectedMapping = null)
    {
        using var document = AzureCliResponse.ParseJson(json);
        var root = document.RootElement;
        var subscription = AzureCliResponse.RequiredString(root, "id");
        var tenant = AzureCliResponse.RequiredString(root, "tenantId");
        var state = AzureCliResponse.RequiredString(root, "state");
        var user = AzureCliResponse.RequiredProperty(root, "user");
        var upn = AzureCliResponse.RequiredString(user, "name");
        var type = AzureCliResponse.RequiredString(user, "type");
        if (!Guid.TryParse(subscription, out var subscriptionId) || subscriptionId == Guid.Empty ||
            !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
            throw new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable);
        if (!type.Equals("user", StringComparison.Ordinal))
            throw new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable,
                "The selected account is not an interactive user. Sign in with the user assigned to the Dev Box, not a service principal or managed identity.");
        if (!state.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
            throw new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable,
                "The selected subscription is not enabled. Select an enabled Azure subscription and retry.");
        if (expectedMapping is not null &&
            (!upn.Equals(expectedMapping.AzureAccountUpn, StringComparison.OrdinalIgnoreCase) ||
             tenantId != expectedMapping.AzureTenantId))
            throw new WindowsAppConnectionException(WindowsAppFailure.AccountMismatch);
        try { WindowsAppConnection.ValidateUpn(upn); }
        catch (ArgumentException) { throw AzureCliResponse.Malformed(); }
        return new(upn.ToLowerInvariant(), tenantId, subscriptionId);
    }

    public override string ToString() => "Validated Azure account (identity withheld)";
}

internal static class AzureCliResponse
{
    public static JsonElement RequiredProperty(JsonElement element, string name) =>
        OptionalProperty(element, name) ?? throw Malformed();

    public static JsonElement? OptionalProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Malformed();
        JsonElement? match = null;
        foreach (var property in element.EnumerateObject())
        {
            if (PropertyName(property) != name) continue;
            if (match is not null) throw Malformed();
            match = property.Value;
        }
        return match;
    }

    public static string RequiredString(JsonElement element, string name) =>
        StringValue(RequiredProperty(element, name));

    public static string PropertyName(JsonProperty property)
    {
        try { return property.Name; }
        catch (InvalidOperationException) { throw Malformed(); }
    }

    public static string StringValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Malformed();
        // JsonDocument defers escaped UTF-16 validation until a string is decoded.
        try { return value.GetString()!; }
        catch (InvalidOperationException) { throw Malformed(); }
    }

    public static JsonDocument ParseJson(string json)
    {
        CheckBound(json);
        try { return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw Malformed(); }
    }

    public static void CheckBound(string output)
    {
        if (output is null || output.Length > AzureCliProcess.MaximumOutputBytes ||
            Encoding.UTF8.GetByteCount(output) > AzureCliProcess.MaximumOutputBytes)
            throw Malformed();
    }

    public static void CheckResult(AzureCliResult result, bool account)
    {
        CheckBound(result.StandardOutput);
        CheckBound(result.StandardError);
        if (result.ExitCode == 0) return;
        var error = result.StandardError + "\n" + result.StandardOutput;
        if (new[] { "az login", "AADSTS50058", "AADSTS50076", "AADSTS700082", "AADSTS70043", "InteractiveAuthenticationRequired", "LoginRequired" }
            .Any(code => error.Contains(code, StringComparison.OrdinalIgnoreCase)))
            throw new WindowsAppConnectionException(WindowsAppFailure.SignInRequired);
        if (account) throw new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable);
        if (new[] { "401", "403", "404", "Unauthorized", "Forbidden", "NotFound", "Not Found", "AccessDenied" }
            .Any(code => error.Contains(code, StringComparison.OrdinalIgnoreCase)))
            throw new WindowsAppConnectionException(WindowsAppFailure.DevBoxUnavailable);
        throw new WindowsAppConnectionException(WindowsAppFailure.ApiUnavailable);
    }

    public static WindowsAppConnectionException Malformed() => new(WindowsAppFailure.MalformedResponse);
}
