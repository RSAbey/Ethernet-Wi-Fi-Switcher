using System.Diagnostics;

namespace EthernetWifiSwitcher.Core;

internal readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>StdOut if present, otherwise StdErr — for log messages.</summary>
    public string Text => string.IsNullOrWhiteSpace(StdOut) ? StdErr.Trim() : StdOut.Trim();
}

/// <summary>
/// Runs a console tool (netsh) with no visible window and returns its output.
/// </summary>
internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Deliberately not setting StandardOutputEncoding: the default follows the
            // console code page, which is what netsh actually writes. Passwords are read
            // from an exported UTF-8 XML file instead, so they are never affected by this.
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
        }

        // Read both streams before waiting, or a full pipe buffer can deadlock the child.
        Task<string> outTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> errTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        string stdout = await outTask.ConfigureAwait(false);
        string stderr = await errTask.ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    public static Task<ProcessResult> NetshAsync(string arguments, CancellationToken ct = default)
        => RunAsync("netsh.exe", arguments, ct);
}
