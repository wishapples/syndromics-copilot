using System.Text.Json;

namespace Syndromics.Copilot;

/// <summary>What Copilot remembers between sessions: the team's preferences and what it has already reported.
/// Stored as plain JSON so the team can read and edit it.</summary>
public sealed class Memory
{
    public HashSet<string> Ignored { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double AlertThreshold { get; set; } = 3.0;
    public string Audience { get; set; } = "engineer";
    public List<string> Notes { get; set; } = new();
    public HashSet<string> SeenJobs { get; set; } = new();

    string? _path;
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static Memory Load(string path)
    {
        Memory m = File.Exists(path) ? JsonSerializer.Deserialize<Memory>(File.ReadAllText(path)) ?? new() : new();
        m.Ignored = new HashSet<string>(m.Ignored, StringComparer.OrdinalIgnoreCase);
        m._path = path;
        return m;
    }

    public static Memory InMemory() => new();

    public void Save()
    {
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(_path, JsonSerializer.Serialize(this, Opt));
    }

    /// <summary>Applies a preference by name; returns what was stored.</summary>
    public string Set(string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "ignore": Ignored.Add(value.Trim()); break;
            case "unignore": Ignored.Remove(value.Trim()); break;
            case "alert_threshold":
                AlertThreshold = double.Parse(value.TrimEnd('x', '×', ' '), System.Globalization.CultureInfo.InvariantCulture); break;
            case "audience": Audience = value.Trim().ToLowerInvariant().StartsWith('m') ? "manager" : "engineer"; break;
            default: Notes.Add($"{key}: {value}"); break;
        }
        Save();
        return $"Remembered {key} = {value}.";
    }

    public string Describe() =>
        $"alert threshold {AlertThreshold:0.#}×, audience {Audience}, ignoring [{string.Join(", ", Ignored)}]" +
        (Notes.Count > 0 ? $", notes: {string.Join("; ", Notes)}" : "");
}
