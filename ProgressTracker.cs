using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

public class ProgressTracker
{
    private readonly string _path;
    private readonly object _lock = new();
    private int _done;

    public ProgressTracker(string path)
    {
        _path = path;
        Load();
    }

    public int Done
    {
        get { lock (_lock) return _done; }
    }

    public void Update(int additionalDone)
    {
        lock (_lock)
        {
            _done += additionalDone;
            File.WriteAllText(_path, JsonSerializer.Serialize(new { done = _done }));
        }
    }

    public void SetDone(int value)
    {
        lock (_lock)
        {
            _done = value;
            File.WriteAllText(_path, JsonSerializer.Serialize(new { done = _done }));
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) { _done = 0; return; }
        try
        {
            var json = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(json);
            _done = doc.RootElement.GetProperty("done").GetInt32();
        }
        catch { _done = 0; }
    }
}
