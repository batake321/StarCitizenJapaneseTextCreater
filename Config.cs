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

    // Window state
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = double.NaN;
    public double WindowHeight { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; } = false;

    // Trade search params
    public string TradeShipName { get; set; } = "";
    public int TradeScu { get; set; } = 100;
    public string TradeBudget { get; set; } = "1000000";
    public string TradeBuySystem { get; set; } = "全て";
    public string TradeSellSystem { get; set; } = "全て";

    // UI
    public double MissionDetailFontSize { get; set; } = 14;

    // 装備名の後ろに分類の印 [軍1C] を付けるか
    public bool EquipmentClassMarks { get; set; } = true;
    // 設計図がもらえるミッション名の後ろに [BP] を付けるか
    public bool BlueprintMissionMarks { get; set; } = true;
    // ミッション名の後ろに派閥と貢献度 (BP) を付けるか
    public bool MissionReputationMarks { get; set; } = false;

    // UEX Capture
    public string UexApiKey { get; set; } = "";
    public string PreferredOcrEngine { get; set; } = "Windows OCR";

    // ［ゲームログ］の音: 休戦ゾーンに進入・到着で鳴らすか、鳴らす wav のパス (空なら同梱の sounds\armistice_zone.wav / sounds\arrival.wav)。
    // 休戦ゾーンに進入は、船を操縦中なら ArmisticeSoundPath、徒歩 (操縦中でない) なら ArmisticeOnFootSoundPath (空なら同梱の sounds\armistice_zone_short.wav)。
    // 鳴らすかどうかはどちらも ArmisticeSoundEnabled
    public bool ArmisticeSoundEnabled { get; set; } = true;
    public string ArmisticeSoundPath { get; set; } = "";
    public string ArmisticeOnFootSoundPath { get; set; } = "";
    public bool ArrivalSoundEnabled { get; set; } = true;
    public string ArrivalSoundPath { get; set; } = "";

    // 最上位のタブの並び (TabItem の x:Name の順。無い名は無視、載っていないタブは末尾) と、最後に見ていたタブ (x:Name。無ければ先頭)
    public List<string> TabOrder { get; set; } = new();
    public string LastTab { get; set; } = "";

    // PC 間の同期: 個人データの書き出し・取り込みに使う共有フォルダ (空なら同期しない)
    public string SyncFolder { get; set; } = "";
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
