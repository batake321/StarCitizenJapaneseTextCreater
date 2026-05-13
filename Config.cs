namespace StarCitizenJapaneseTextCreater;

public class AppConfig
{
    public string GamePath { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string OutputLanguage { get; set; } = "japanese_(japan)";
    public TranslationConfig Translation { get; set; } = new();
    public List<string> ForceEnglishPatterns { get; set; } = new();
}

public class TranslationConfig
{
    public int MaxRetries { get; set; } = 3;
    public List<BackendConfig> Backends { get; set; } = new();
}

public class BackendConfig
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public int BatchSize { get; set; } = 20;
    public bool Enabled { get; set; } = false;
}
