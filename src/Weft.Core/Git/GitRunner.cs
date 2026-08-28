using System.Diagnostics;
using System.Text;

namespace Weft.Core.Git;

/// <summary>Outcome of one git invocation.</summary>
public readonly record struct GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Non-empty stdout lines.</summary>
    public string[] Lines => StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                   .Select(l => l.TrimEnd('\r'))
                                   .ToArray();
}

/// <summary>
/// Runs the git binary.
/// </summary>
/// <remarks>
/// <para>weft shells out to git rather than linking a git library, on purpose.
/// libgit2 diverges from git on gitignore edge cases, worktrees and sparse
/// checkout, and it honours neither hooks, nor the credential helper, nor the
/// SSH agent. The reference tree has eleven worktrees across one repository,
/// six of them under the system temp directory: exactly where a reimplementation
/// drifts from the real thing.</para>
///
/// <para>Correctness here is not negotiable, and the measured cost of asking git
/// is about 100 ms for 49 repositories in parallel.</para>
/// </remarks>
public sealed class GitRunner
{
    private readonly string _git;
    private readonly TimeSpan _timeout;

    public GitRunner(string gitPath = "git", TimeSpan? timeout = null)
    {
        _git = gitPath;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<GitResult> RunAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _git,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        // Keep git non-interactive and independent of the ambient environment.
        // A credential prompt inside a background scan would hang forever, and
        // an alias or a pager configured by the user would corrupt the output we
        // are about to parse.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";   // never take index.lock just to read
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["LC_ALL"] = "C";               // stable, parseable messages

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            return new GitResult(-1, "", "could not start git");

        // stdout and stderr are drained concurrently. Reading one to completion
        // before the other deadlocks as soon as git fills the pipe it is not
        // being read from, which happens on large repositories and not on small
        // ones: the failure would only appear under load.
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            return new GitResult(-1, "", $"git timed out after {_timeout.TotalSeconds:0}s: git {string.Join(' ', args)}");
        }

        return new GitResult(proc.ExitCode,
            await outTask.ConfigureAwait(false),
            await errTask.ConfigureAwait(false));
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
        catch (NotSupportedException) { /* platform refuses tree kill */ }
    }

    /// <summary>Whether a usable git binary is on PATH, and which version.</summary>
    public async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await RunAsync(Environment.CurrentDirectory, ct, "--version").ConfigureAwait(false);
            return r.Ok ? r.StdOut.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }   // not on PATH
    }
}
