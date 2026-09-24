using System.ComponentModel;
using System.Diagnostics;

namespace InsightsExportGui.Analysis;

public sealed record GitCommit(string Sha, bool Dirty)
{
    public string Label => Sha + (Dirty ? "+uncommitted" : "");
}

/// <summary>
/// Read-only commit lookup for the user-linked repo. Only ever runs the two commands below, with
/// GIT_OPTIONAL_LOCKS=0 so `git status` doesn't refresh (write) the repo's index.
/// </summary>
public static class GitInfo
{
    public static readonly string[] RevParseArgs = ["rev-parse", "--short", "HEAD"];
    /// <summary>Tracked changes only; skipping untracked keeps it fast on large UE repos.</summary>
    public static readonly string[] StatusArgs = ["status", "--porcelain", "--untracked-files=no"];
    private const int TimeoutMs = 15000;

    public static string Describe(string[] args) => "git " + string.Join(' ', args);

    public static bool LooksLikeRepo(string dir) =>
        Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

    /// <summary>Null + error when the commit can't be read. A commit with a non-null error = SHA ok, dirty-check failed.</summary>
    public static GitCommit? TryRead(string repo, out string? error)
    {
        if (!Directory.Exists(repo))
        {
            error = $"linked repo folder not found: {repo}";
            return null;
        }

        string? sha = Run(repo, RevParseArgs, out error);
        if (string.IsNullOrEmpty(sha))
        {
            error ??= "git returned no commit";
            return null;
        }

        string? status = Run(repo, StatusArgs, out string? statusError);
        error = statusError is null ? null : $"uncommitted-change check failed: {statusError}";
        return new GitCommit(sha, Dirty: !string.IsNullOrWhiteSpace(status));
    }

    private static string? Run(string repo, string[] args, out string? error)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repo);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already exited */ }
                error = $"{Describe(args)} timed out";
                return null;
            }
            if (p.ExitCode != 0)
            {
                error = $"{Describe(args)} failed: {stderr.Result.Trim()}";
                return null;
            }
            error = null;
            return stdout.Result.Trim();
        }
        catch (Win32Exception)
        {
            error = "git not found on PATH";
            return null;
        }
    }
}
