using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmbientBar;

public sealed class Track : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Channel { get; set; }
    public double? Duration { get; set; }

    [JsonIgnore]
    public string DurationText => Duration is double d && d > 0 ? FormatTime(d) : "";

    bool _isCurrent;
    [JsonIgnore]
    public bool IsCurrent
    {
        get => _isCurrent;
        set { _isCurrent = value; PropertyChanged?.Invoke(this, new(nameof(IsCurrent))); }
    }

    bool _isFavorite;
    [JsonIgnore]
    public bool IsFavorite
    {
        get => _isFavorite;
        set { _isFavorite = value; PropertyChanged?.Invoke(this, new(nameof(IsFavorite))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : $"{t:m\\:ss}";
    }
}

public sealed class AppState
{
    public List<Track> Queue { get; set; } = new();
    public List<Track> Favorites { get; set; } = new();
    public string? CurrentId { get; set; }
    public double Position { get; set; }
    public double Volume { get; set; } = 0.6;
    public bool Repeat { get; set; } = true;

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmbientBar");
    static readonly string FilePath = Path.Combine(Dir, "state.json");

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt state: start fresh */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { /* not worth crashing over */ }
    }
}
