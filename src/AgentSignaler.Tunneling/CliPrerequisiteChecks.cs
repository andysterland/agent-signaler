namespace AgentSignaler.Tunneling;

internal static class CliPrerequisiteChecks
{
    internal static async Task CheckVersionAsync(ITunnelProcessRunner runner, string path,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        VerifyVersion(await ReadVersionAsync(runner, path, timeout, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<CliCommandResult> ReadVersionAsync(ITunnelProcessRunner runner, string path,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(path, ["--version"], timeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal static void VerifyVersion(CliCommandResult result)
    {
        VerifySuccess(result);
        TunnelValidation.VerifyCliVersion(result);
    }

    internal static async Task<string> CheckAccountAsync(ITunnelProcessRunner runner, string path,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(path, ["user", "show", "--json"], timeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        VerifySuccess(result);
        return TunnelValidation.AccountOwner(result.StandardOutput);
    }

    private static void VerifySuccess(CliCommandResult result)
    {
        if (result.ExitCode != 0)
            throw new TunnelException("The CLI command failed. Check explicit CLI sign-in, connectivity, permissions, and quota, then retry. Saved identity was retained.")
            { FailureKind = CliFailureKind.CommandFailed, ErrorCode = result.ExitCode };
    }
}
