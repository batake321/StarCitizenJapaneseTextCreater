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
    // VOICEVOX の読み上げ速度 (audio_query の speedScale。1.0 が標準)。Web チャットと［ゲームログ］の読み上げの両方で使う
    public double VoiceVoxSpeedScale { get; set; } = 1.0;

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
    // 休戦ゾーンに進入は、船を操縦中のときだけ鳴らす (徒歩では鳴らさない)。ジャンプポイント脇の休憩所 (RR_JP_…) では休戦・到着とも鳴らさない
    public bool ArmisticeSoundEnabled { get; set; } = true;
    public string ArmisticeSoundPath { get; set; } = "";
    // 使わない (徒歩の休戦ゾーンの音は鳴らさなくなった)。既存の appsettings.json との互換のため残す
    public string ArmisticeOnFootSoundPath { get; set; } = "";
    public bool ArrivalSoundEnabled { get; set; } = true;
    public string ArrivalSoundPath { get; set; } = "";
    // ［ゲームログ］の読み上げ: VOICEVOX で場面ごとの文を読み上げるか (URL は Web チャットと同じ VoiceVoxUrl)、既定の声 (VOICEVOX のスタイルの id。16 = 九州そら ノーマル)、
    // 場面ごとの設定 (GameLogVoiceItem。保存の無い場面は既定 (GameLogVoiceScenes.All))。読み上げられないときは休戦ゾーン進入と到着だけ上の音のファイルを鳴らす。
    // 休戦ゾーン進入と到着を読むかは上の ArmisticeSoundEnabled / ArrivalSoundEnabled (設定タブの表の「読む」と同じもの)
    public bool GameLogVoiceVoxEnabled { get; set; } = false;
    public int GameLogVoiceVoxSpeakerId { get; set; } = 16;
    public List<GameLogVoiceItem> GameLogVoiceItems { get; set; } = new();

    // 最上位のタブの並び (TabItem の x:Name の順。無い名は無視、載っていないタブは末尾) と、最後に見ていたタブ (x:Name。無ければ先頭)
    public List<string> TabOrder { get; set; } = new();
    public string LastTab { get; set; } = "";

    // PC 間の同期: 個人データの書き出し・取り込みに使う共有フォルダ (空なら同期しない)
    public string SyncFolder { get; set; } = "";

    // 自動更新: 起動したときと毎日 AutoUpdateTime (HH:mm) に、新しい版を確かめる (AppUpdater)
    public bool AutoUpdateEnabled { get; set; } = true;
    public string AutoUpdateTime { get; set; } = "08:00";
}

public class TranslationConfig
{
    public int MaxRetries { get; set; } = 3;
    public List<BackendConfig> Backends { get; set; } = new();
    // E2B / E4B (モデル名に gemma4:e2b / gemma4:e4b を含むもの) を AI 翻訳に使うか (既定は使わない)
    public bool UseSmallModelsForTranslation { get; set; } = false;
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
