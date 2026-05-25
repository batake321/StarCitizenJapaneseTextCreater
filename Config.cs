namespace StarCitizenJapaneseTextCreater;

public class AppConfig
{
    public string GamePath { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string OutputLanguage { get; set; } = "japanese_(japan)";
    public TranslationConfig Translation { get; set; } = new();
    public List<string> ForceEnglishPatterns { get; set; } = new();
    public string ScApiKey { get; set; } = "";
    public string LastChatBackend { get; set; } = "";
    public int WebServerPort { get; set; } = 8099;
    public int WebServerHttpsPort { get; set; } = 8100;
    public bool WebServerAutoStart { get; set; } = false;
    public string VoiceVoxUrl { get; set; } = "http://localhost:50021";
    public int VoiceVoxSpeakerId { get; set; } = 0;
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

    public bool SupportsSkills => Type.ToLowerInvariant() switch
    {
        "claude" or "gemini" or "openai" => true,
        "ollama" => true,
        _ => false
    };

    public override string ToString() => $"{Name} ({Model})";
}
