using System.Diagnostics;
using System.Text;

namespace InsightsExportGui;

public enum ExportKind { Threads, TimerStats, Counters }

public sealed class ExportOptions
{
    public required string TracePath { get; init; }
    public required string OutDir { get; init; }
    public required string InsightsExe { get; init; }
    public required IReadOnlyList<ExportKind> Exports { get; init; }
    public int Retries { get; init; } = 5;
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public bool KeepLog { get; init; }
}

public sealed class ExportFailedException(string message) : Exception(message);

/// <summary>
/// Native port of export.sh: headless UnrealInsights.exe run that dumps TimingInsights.Export* CSVs.
/// Hard-won rules (see ANALYSIS_GUIDE / research doc): rsp-file form of -ExecOnAnalysisCompleteCmd is
/// mandatory, -ABSLOG is mandatory, trailing -log, and never let two Insights instances overlap.
/// </summary>
public static class Exporter
{
    public const string ResponseFileName = "cmds.rsp";
    public const string LogFileName = "insights.log";

    private const string ProcessName = "UnrealInsights";
    private const int StableSecondsRequired = 3;          // CSV sizes unchanged this long => written & closed
    private const int ExitGraceSeconds = 3;               // after process exit, wait this long for CSVs to appear
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    public static string CsvFileName(ExportKind kind) => kind switch
    {
        ExportKind.Threads => "threads.csv",
        ExportKind.TimerStats => "timerstats.csv",
        ExportKind.Counters => "counters.csv",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string ConsoleCommand(ExportKind kind) => kind switch
    {
        ExportKind.Threads => "TimingInsights.ExportThreads",
        ExportKind.TimerStats => "TimingInsights.ExportTimerStatistics",
        ExportKind.Counters => "TimingInsights.ExportCounters",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static Process[] FindRunningInsights() => Process.GetProcessesByName(ProcessName);

    /// <summary>Kill every UnrealInsights.exe. Overlapping instances silently break the export.</summary>
    public static void KillStrays()
    {
        foreach (var p in FindRunningInsights())
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch
            {
                // Already exited or access denied; the next attempt re-checks.
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    /// <summary>Newest C:\Program Files\Epic Games\UE_5.x install that has UnrealInsights.exe.</summary>
    public static string? AutoDetectInsightsExe()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
        if (!Directory.Exists(root))
            return null;

        return Directory.GetDirectories(root, "UE_5*")
            .Select(dir => (dir, ver: Version.TryParse(Path.GetFileName(dir)[3..], out var v) ? v : new Version(0, 0)))
            .OrderByDescending(x => x.ver)
            .Select(x => Path.Combine(x.dir, "Engine", "Binaries", "Win64", "UnrealInsights.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Default UE trace store (where the editor writes .utrace captures).</summary>
    public static string DefaultTraceStore => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnrealEngine", "Common", "UnrealTrace", "Store", "001");

    public static async Task<IReadOnlyList<string>> RunAsync(ExportOptions o, IProgress<string> log, CancellationToken ct)
    {
        string outDir = Path.GetFullPath(o.OutDir);
        Directory.CreateDirectory(outDir);
        var csvs = o.Exports.Select(k => Path.Combine(outDir, CsvFileName(k))).ToList();
        string rsp = Path.Combine(outDir, ResponseFileName);
        string logPath = Path.Combine(outDir, LogFileName);

        log.Report("[1/4] Killing any stray UnrealInsights.exe processes...");
        KillStrays();
        await Task.Delay(1000, ct);

        log.Report("[2/4] Writing response file...");
        WriteResponseFile(rsp, o.Exports, outDir);

        log.Report($"[3/4] Running UnrealInsights.exe (headless export, up to {o.Retries} attempts)...");
        bool success = false;
        for (int attempt = 1; attempt <= o.Retries && !success; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            log.Report($"  attempt {attempt}/{o.Retries}...");

            KillStrays();
            await Task.Delay(1000, ct);

            // Delete previous outputs so a stale CSV from an earlier run can't pass the stability check.
            foreach (var f in csvs.Append(logPath))
                TryDelete(f);

            using var proc = StartInsights(o, rsp, logPath);
            try
            {
                success = await WaitForStableCsvsAsync(proc, csvs, logPath, o.AttemptTimeout, log, ct);
            }
            finally
            {
                KillProcess(proc);
            }

            if (!success)
                log.Report("  export not confirmed, retrying...");
        }

        KillStrays();

        if (!success)
            throw new ExportFailedException(
                $"Export did not produce stable CSVs after {o.Retries} attempts. Check {logPath}");

        if (!o.KeepLog)
            TryDelete(logPath);

        log.Report($"[4/4] Done. CSVs written to: {outDir}");
        foreach (var f in csvs)
            log.Report($"  {Path.GetFileName(f),-16} {new FileInfo(f).Length,14:N0} bytes");
        return csvs;
    }

    private static void WriteResponseFile(string rsp, IReadOnlyList<ExportKind> exports, string outDir)
    {
        // Forward-slash paths inside the rsp (the known-working form).
        string outFwd = outDir.Replace('\\', '/');
        var sb = new StringBuilder();
        foreach (var kind in exports)
            sb.Append(ConsoleCommand(kind)).Append(' ').Append(QuoteIfNeeded($"{outFwd}/{CsvFileName(kind)}")).Append('\n');

        string text = sb.ToString();
        // Plain ASCII when possible (matches the proven rsp); UTF-8 with BOM for non-ASCII paths so UE decodes it.
        bool ascii = text.All(c => c < 128);
        File.WriteAllText(rsp, text, ascii ? Encoding.ASCII : new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static Process StartInsights(ExportOptions o, string rsp, string logPath)
    {
        // Built as a raw string, not ArgumentList: UE parses -Key="value with spaces", whereas .NET's
        // ArgumentList would emit "-Key=value with spaces" (quote before the dash), which UE truncates.
        string args = string.Join(' ',
            $"-OpenTraceFile={QuoteIfNeeded(Path.GetFullPath(o.TracePath))}",
            $"-ABSLOG={QuoteIfNeeded(logPath)}",
            "-NoUI",
            "-AutoQuit",
            $"-ExecOnAnalysisCompleteCmd={QuoteIfNeeded("@=" + rsp)}",
            "-log");

        var psi = new ProcessStartInfo(o.InsightsExe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(o.InsightsExe)!,
        };
        return Process.Start(psi) ?? throw new ExportFailedException("Failed to start UnrealInsights.exe");
    }

    /// <summary>
    /// Success when every requested CSV exists, is non-empty, and its size is unchanged for
    /// <see cref="StableSecondsRequired"/> seconds — whether or not -AutoQuit actually quit.
    /// </summary>
    private static async Task<bool> WaitForStableCsvsAsync(
        Process proc, List<string> csvs, string logPath, TimeSpan timeout, IProgress<string> log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var nextHeartbeat = HeartbeatInterval;
        long[]? lastSizes = null;
        int stableSeconds = 0;
        TimeSpan? exitedAt = null;
        var logWatch = new LogWatcher(logPath);

        while (true)
        {
            await Task.Delay(1000, ct);

            foreach (var line in logWatch.PollMilestones())
                log.Report("  " + line);

            var sizes = csvs.Select(FileSize).ToArray();
            bool allPresent = sizes.All(s => s > 0);
            stableSeconds = allPresent && lastSizes != null && sizes.SequenceEqual(lastSizes) ? stableSeconds + 1 : 0;
            lastSizes = sizes;

            if (stableSeconds >= StableSecondsRequired)
                return true;

            if (proc.HasExited)
            {
                exitedAt ??= sw.Elapsed;
                // -AutoQuit race: process died before the CSVs landed.
                if (!allPresent && sw.Elapsed - exitedAt > TimeSpan.FromSeconds(ExitGraceSeconds))
                {
                    log.Report($"  UnrealInsights.exe exited (code {proc.ExitCode}) before all CSVs were written.");
                    return false;
                }
            }

            if (sw.Elapsed > timeout)
            {
                log.Report($"  attempt timed out after {timeout.TotalMinutes:0} min.");
                return false;
            }

            if (sw.Elapsed >= nextHeartbeat)
            {
                log.Report($"  still analyzing... {sw.Elapsed:mm\\:ss} elapsed");
                nextHeartbeat += HeartbeatInterval;
            }
        }
    }

    private static long FileSize(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static void KillProcess(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
        }
        catch
        {
            // Already gone.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Locked or missing; a locked stale CSV will simply fail the attempt and get retried.
        }
    }

    private static string QuoteIfNeeded(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>Tails the Insights log for the lines that show the export actually fired.</summary>
    private sealed class LogWatcher(string path)
    {
        private static readonly string[] Milestones =
        [
            "Executing commands using response file",
            "Exported",
        ];

        private long _offset;

        public List<string> PollMilestones()
        {
            string appended;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length <= _offset)
                    return [];
                fs.Seek(_offset, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                appended = reader.ReadToEnd();
                _offset = fs.Length;
            }
            catch
            {
                return [];   // log not created yet, or briefly locked
            }

            return appended.Split('\n')
                .Where(line => Milestones.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase)))
                .Select(line => "log: " + line.Trim())
                .ToList();
        }
    }
}
