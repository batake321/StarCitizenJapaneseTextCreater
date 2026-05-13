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
