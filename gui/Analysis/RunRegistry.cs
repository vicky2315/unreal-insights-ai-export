using System.Text.Json;
using System.Text.Json.Serialization;

namespace InsightsExportGui.Analysis;

/// <summary>One registered export folder. The registry only points at folders; it never owns or deletes files.</summary>
public sealed class RunRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? TracePath { get; set; }
    public long FrameCount { get; set; }
    public string? GitSha { get; set; }
    public bool GitDirty { get; set; }
    public string? Notes { get; set; }

    [JsonIgnore] public string TimerStatsPath => Path.Combine(Folder, TimerStats.FileName);
    [JsonIgnore] public bool Exists => File.Exists(TimerStatsPath);
    [JsonIgnore] public string CommitLabel => GitSha is null ? "" : GitSha + (GitDirty ? "+uncommitted" : "");

    public override string ToString() => $"{Name}  ({CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm})";
}

/// <summary>List of known runs persisted as JSON (runs.json in the app's settings folder).</summary>
public sealed class RunRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public List<RunRecord> Runs { get; private set; } = [];
    /// <summary>Set when runs.json existed but couldn't be read (it is moved aside, never overwritten).</summary>
    public string? LoadWarning { get; private set; }
    public event Action? Changed;

    public RunRegistry(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;
        try
        {
            Runs = JsonSerializer.Deserialize<List<RunRecord>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception ex)
        {
            string backup = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(_path, backup);
                LoadWarning = $"runs.json could not be read ({ex.Message}). It was moved to {backup}; starting with an empty list.";
            }
            catch
            {
                LoadWarning = $"runs.json could not be read ({ex.Message}).";
            }
            Runs = [];
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Runs, JsonOptions));
        File.Move(tmp, _path, overwrite: true);   // atomic replace: a crash never leaves a half-written runs.json
    }

    public RunRecord? FindByFolder(string folder) => Runs.FirstOrDefault(r => SamePath(r.Folder, folder));

    /// <summary>Adds the record, or replaces the one already registered for the same folder (re-export).</summary>
    public void Upsert(RunRecord record)
    {
        int idx = Runs.FindIndex(r => r.Id == record.Id || SamePath(r.Folder, record.Folder));
        if (idx >= 0)
            Runs[idx] = record;
        else
            Runs.Add(record);
        Commit();
    }

    /// <summary>Removes from the list only. Files on disk are untouched.</summary>
    public void Remove(RunRecord record)
    {
        Runs.RemoveAll(r => r.Id == record.Id);
        Commit();
    }

    /// <summary>Call after editing a record in place.</summary>
    public void Commit()
    {
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// Registers <paramref name="parent"/> and its immediate subfolders that contain timerstats.csv and
    /// aren't registered yet. Returns the newly added runs.
    /// </summary>
    public List<RunRecord> ImportFolders(string parent)
    {
        var added = new List<RunRecord>();
        var candidates = new[] { parent }.Concat(Directory.EnumerateDirectories(parent));
        foreach (var dir in candidates)
        {
            string csv = Path.Combine(dir, TimerStats.FileName);
            if (!File.Exists(csv) || FindByFolder(dir) != null)
                continue;

            long frames;
            try
            {
                frames = TimerStatsCache.Get(dir).FrameCount;
            }
            catch
            {
                frames = 0;
            }

            var rec = new RunRecord
            {
                Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)),
                Folder = Path.GetFullPath(dir),
                CreatedUtc = File.GetLastWriteTimeUtc(csv),
                FrameCount = frames,
            };
            Runs.Add(rec);
            added.Add(rec);
        }

        if (added.Count > 0)
            Commit();
        return added;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
