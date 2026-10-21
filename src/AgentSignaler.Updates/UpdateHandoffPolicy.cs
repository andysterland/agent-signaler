using System.Collections.Immutable;

namespace AgentSignaler.Updates;

public sealed record UpdateSignatureEvidence(bool TrustedSignature, bool TimestampValid, string Publisher,
    bool SelfSigned, ImmutableArray<string> EnhancedKeyUsages);

// Production native verification and cross-language policy fixtures share these predicates.
// Evidence is supplied only by WinTrust in the production adapter, never by a handoff file.
public static class UpdateHandoffPolicy
{
    public const int SchemaVersion = 1;
    public const int DependencyPolicyVersion = 1;
    public const int ClientUpdateExitCode = 42;
    public const string ReleasePublisher = "Andy Sterland";
    public const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";
    public static readonly ImmutableArray<string> RequiredScripts =
        ["Update-AgentSignaler.ps1", "UpdaterPolicy.ps1", "UpdateHandoff.ps1"];

    public static int GetClientExitCode(bool ordinaryExit, bool cleanUpdateShutdown, bool shutdownFailed) =>
        ordinaryExit ? 0 : shutdownFailed ? 1 : cleanUpdateShutdown ? ClientUpdateExitCode : 0;

    public static void RequireSignature(UpdateSignatureEvidence evidence)
    {
        if (!evidence.TrustedSignature || !evidence.TimestampValid || evidence.SelfSigned ||
            evidence.Publisher != ReleasePublisher || evidence.EnhancedKeyUsages.IsDefault ||
            evidence.EnhancedKeyUsages.Length > 32 || evidence.EnhancedKeyUsages.Count(oid => oid == CodeSigningEku) != 1)
            throw new UnauthorizedAccessException("update_script_publisher");
    }
}