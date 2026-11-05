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
    // 日本語化の適応範囲 (［日本語化］の最初のダイアログ。2026-11-05): false なら「全て翻訳」、true なら「一部翻訳」。
    // 一部翻訳のとき、PartialTranslationUnchecked に入っている種類 (TranslationScope.Categories の Id) は訳さない (英語の ini の原文のまま)。
    // チェックを外した種類を持つのは、初めての状態 (空) を「チェックは全部付き」にするため
    public bool PartialTranslation { get; set; } = false;
    public List<string> PartialTranslationUnchecked { get; set; } = new();

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
    // ［ゲームログ］の読み上げの出力先 (GameLogVoiceExternal.OutputVoiceVox = VOICEVOX だけ / OutputExternal = 外のアプリだけ / OutputBoth = 両方)。
    // 外のアプリの呼び方 (KindExe / KindApi)、exe のパスと引数のひな形、API の URL のひな形・方法 (GET / POST)・POST の本文のひな形。
    // ひな形の {talk} {scene} {speaker} を、読み上げる文・場面の表示名・話者 ID に置き換える (GameLogVoiceExternal)。読み上げは上の GameLogVoiceVoxEnabled が入っているときだけ
    public string GameLogVoiceOutput { get; set; } = GameLogVoiceExternal.OutputVoiceVox;
    public string GameLogVoiceExternalKind { get; set; } = GameLogVoiceExternal.KindExe;
    public string GameLogVoiceExternalExePath { get; set; } = "";
    public string GameLogVoiceExternalExeArgs { get; set; } = "";
    public string GameLogVoiceExternalUrl { get; set; } = "";
    public string GameLogVoiceExternalMethod { get; set; } = "GET";
    public string GameLogVoiceExternalBody { get; set; } = "";

    // 最上位のタブの並び (TabItem の x:Name の順。無い名は無視、載っていないタブは末尾) と、最後に見ていたタブ (x:Name。無ければ先頭)
    public List<string> TabOrder { get; set; } = new();
    public string LastTab { get; set; } = "";

    // ［マップ］: 最後の表示 ("3" = 3 星系の並び、ほかは 1 つだけ大きく出す星系の名前。空なら 3 星系) と、
    // 星系図ごとに最後に操作した表示 (中心・幅。メートル。MapPanel が図のスクリプトから受けて入れる。無い星系は全体の表示)
    public string MapView { get; set; } = "";
    public List<MapViewState> MapViews { get; set; } = new();
    // ［マップ］の最後の現在位置 (ゲームの /showlocation の座標と取得した時刻。Utc が 0 なら無し) と、惑星・衛星ごとの自転の角度の補正値 (最新の 1 つ)
    public MapCurrentState MapCurrent { get; set; } = new();
    // ［マップ］の Location の番号ごとの「どこにいたか」(［現在位置］で座標を取ったときに覚える。番号ごとに最新の 1 つ)
    public List<MapLocationMemory> MapLocationMemories { get; set; } = new();
    // ［マップ］の「QT の前後で自動で現在位置を取る」(既定はオフ)
    public bool MapAutoCapture { get; set; } = false;
    public List<MapSpinCorrection> MapSpinCorrections { get; set; } = new();

    // PC 間の同期: 個人データの書き出し・取り込みに使う共有フォルダ (空なら同期しない)
    public string SyncFolder { get; set; } = "";

    // 船の 3D 表示 (Ship3DExports): GLB のキャッシュの置き場所 (空なら既定 = TEMP の中の StarCitizenJapaneseTextCreater\ship3d) と、
    // 最大容量 (MB。0 なら未設定で、初期値 = 置き場所のドライブの残り容量の 1/3、ただし最大 2GB)
    public string Ship3DFolder { get; set; } = "";
    public long Ship3DMaxMB { get; set; } = 0;
    // (2026-11-04、利用者の決定「設定で取り込み可能」「デフォルトは保存」) true なら 3D モデルを保存しない (表示のたびに Data.p4k から直接読む)
    public bool Ship3DNoSave { get; set; } = false;
    // 船・装備・武器の 3D の色付け (2026-11-05、利用者の指示「"船・装備・武器の 3D は常に色付け" これは設定で切れるようにしてください。」):
    // true (既定) ならパレットの色 (本体のパレットの A・B・C、塗装のパレット) を当てた姿、false なら当てない姿 (材質そのものの色) で出し、塗装の一覧は出さない。
    // ステーションの 3D の「色付け」スイッチ (view3d の view3d.pal) とは別。次に 3D を開いたときから効く (Ship3DWeb)
    public bool Ship3DPalette { get; set; } = true;
    // ステーションの 3D (新しい方式) の、地点ごとの取り出し結果の保存 (StationView3DCache) の上限 (GB)。負なら未設定で、初期値 = 置き場所のドライブの残りの 1/3、ただし最大 10 GB。
    // 0 なら保存しない (先読みもしない)
    public double Station3DCacheMaxGB { get; set; } = -1;
    // ステーションの 3D (新しい方式) に貼るゲームの画像 (テクスチャ) の長い辺の上限 (px。既定 512。0 なら画像を貼らない = 単色) (2026-11-05)
    public int Station3DTextureMaxPx { get; set; } = StationView3DTextures.DefaultMaxPx;
    // マップのデバッグ表示 (2026-11-05、利用者の指示「この3D の下部の細かいテキストも設定でデフォルトでオフのマップのデバッグ表示という設定を付けてほしい」):
    // true ならステーションの 3D (view3d.html) の下部の枠 (#pick。押した部品の名前・判定の面・押した所の高さなど) を出す。既定は出さない
    public bool Station3DDebugView { get; set; } = false;

    // 自動更新: 起動したときと毎日 AutoUpdateTime (HH:mm) に、新しい版を確かめる (AppUpdater)
    public bool AutoUpdateEnabled { get; set; } = true;
    public string AutoUpdateTime { get; set; } = "08:00";

    // 「新しいパッチを読み取る」で最後に取り込んだゲームの版 (読んだチャンネルの build_manifest.id の Branch の末尾 例 "4.10.2") と
    // 変更番号 (RequestedP4ChangeNum 例 "12913470")。空なら記録なし (新しい版かどうかは gamedata_meta.build_p4_change_num と比べる)
    public string ExtractedBuildVersion { get; set; } = "";
    public string ExtractedBuildChangeNum { get; set; } = "";
}

// ［マップ］の星系図 1 つの、最後に操作した表示 (System: 星系の名前、Cx / Cy: 中心のゲームの座標、W: 枠の幅に入る幅。どれもメートル)
public class MapViewState
{
    public string System { get; set; } = "";
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double W { get; set; }
}

// ［マップ］の現在位置 (X / Y / Z: 恒星を原点にしたメートル、Utc: 取得した時刻 (UNIX 秒)。0 なら無し、
// LocationId: 取ったときの Game.log の Location の番号 (分からなければ "")、
// System: 量子ジャンプの途中 (目的地を選んでから到着するまで) に取ったとき、線の星系で決めた星系の名前 (例 Pyro。そうでなければ ""))
public class MapCurrentState
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Utc { get; set; }
    public string LocationId { get; set; } = "";
    public string System { get; set; } = "";
}

// ［マップ］の Location の番号 1 つの「どこにいたか」(LocationId: Game.log の Location の番号、Record: 場所・天体・星系 (恒星) の record 名、
// Kind: "inside" = その場所の中、"near" = その天体の付近、"space" = その星系の宇宙、Utc: 覚えた時刻 (UNIX 秒))
public class MapLocationMemory
{
    public string LocationId { get; set; } = "";
    public string Record { get; set; } = "";
    public string Kind { get; set; } = "";
    public double Utc { get; set; }
}

// ［マップ］の惑星・衛星の自転の角度の補正値 1 つ (CurrentLocation.Locate):
//   Body: Planet・Moon の record 名、OffsetDeg: 補正値 (実測の θ − データの式の θ。度。-180〜180)、Utc: 実測した時刻 (UNIX 秒)、
//   Ref: 実測の基準の種類 ("log" = Game.log の居場所、"near" = 近くの場所)、RefName: その場所の名前
public class MapSpinCorrection
{
    public string Body { get; set; } = "";
    public double OffsetDeg { get; set; }
    public double Utc { get; set; }
    public string Ref { get; set; } = "";
    public string RefName { get; set; } = "";
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
