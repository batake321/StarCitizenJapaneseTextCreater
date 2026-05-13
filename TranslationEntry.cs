using System.Text.Json.Serialization;

namespace StarCitizenJapaneseTextCreater;

public class TranslationEntry
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("en")]
    public string English { get; set; } = "";

    [JsonPropertyName("ja")]
    public string? Japanese { get; set; }
}

public class BatchInputItem
{
    [JsonPropertyName("k")]
    public string Key { get; set; } = "";

    [JsonPropertyName("e")]
    public string English { get; set; } = "";
}

public class BatchOutputItem
{
    [JsonPropertyName("k")]
    public string Key { get; set; } = "";

    [JsonPropertyName("j")]
    public string Japanese { get; set; } = "";
}
