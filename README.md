# Star Citizen Japanese Text Creator

Star Citizen のゲーム内テキストを AI で日本語に翻訳し、ゲームに適用する Windows デスクトップアプリケーションです。

## 機能

- **Data.p4k 自動抽出** - ゲームの Data.p4k (ZIP64 + ZStd) から英語/日本語の global.ini を自動抽出
- **AI 翻訳** - Claude / Gemini / OpenAI / Ollama (ローカル LLM) の 4 バックエンドに対応、複数同時並列翻訳
- **翻訳エディタ** - AI 翻訳結果の確認・手動編集・検索・フィルタリング
- **用語集** - 固有名詞の翻訳ルールを登録し、一括置換も可能
- **チャット** - AI に Star Citizen の最新データを参照させて日本語で質問（キーバインド検索対応）
- **Web チャット + VoiceVox 読み上げ** - ブラウザ (localhost:8099) からもチャット可能。VoiceVox による音声読み上げ対応
- **ナレッジ (記憶)** - AI に情報を教えて記憶させる機能。バグ情報・用語・Tips を SQLite に保存し、チャットの文脈に自動反映
- **キーバインドエディタ** - 日本語キーボードのビジュアル表示、activation mode 色分け、翻訳連携
- **ゲームデータ連携** - Data.p4k の DCB データベースからオンデマンドで船・武器・コンポーネント情報を取得
- **プロファイル管理** - キャラクターデザイン・キーバインド設定のバックアップ/リストア
- **ゲームパス自動検出** - RSI Launcher のログから Star Citizen のインストール先を自動検出
- **チャンネル切替** - PTU / LIVE / EPTU など複数チャンネルをワンクリックで切替

## ダウンロード

> **[Releases ページ](https://github.com/batake321/StarCitizenJapaneseTextCreater/releases)** から最新の ZIP をダウンロードしてください。
>
> **[翻訳データベース (バックアップ)](https://github.com/batake321/StarCitizenJapaneseTextCreater/raw/main/db_backup/sc_japanese_backup.zip)** — アプリの「インポート (復元)」から取り込めます。

### 動作要件

- Windows 10 / 11
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (デスクトップランタイム)
- Star Citizen がインストール済みであること
- AI 翻訳を利用する場合 (いずれか1つ以上):
  - [Claude API キー](docs/API_Key_取得_Claude.md)
  - [Gemini API キー](docs/API_Key_取得_Gemini.md)
  - [OpenAI (ChatGPT) API キー](docs/API_Key_取得_OpenAI.md)
  - [Ollama](docs/Ollama_Install.md) + [Gemma e4b](docs/Gemma4_e4b_Install.md) or [e2b](docs/Gemma4_e2b_Install.md) (ローカル・無料)

---

## 使い方 (スクリーンショット付き)

### 1. 翻訳タブ — 抽出・翻訳・反映

![翻訳タブ](image/Translate2.png)

1. **GamePath** が正しいことを確認（自動検出済み）。チャンネル (PTU/LIVE) を選択
2. **[1. 抽出]** をクリック — Data.p4k から global.ini を抽出し DB に登録
3. **[2. 翻訳]** をクリック — 未翻訳テキストを AI に送信、複数バックエンドで並列翻訳
4. **[3. 反映]** をクリック — 翻訳結果を統合してゲームディレクトリに配置
5. Star Citizen を起動すれば日本語テキストが適用されます

---

### 2. AI 設定 — 翻訳バックエンドの設定

![AI 設定](image/AIsetting.png)

1. 画面右上の **[AI 設定]** ボタンをクリック
2. 各バックエンドの **チェックボックス** を有効にし、API キーやモデルを設定
3. **Batch** で一度に送る翻訳数を調整（VRAM やレート制限に応じて）
4. 複数バックエンドを有効にすると **並列翻訳** でスピードアップ
5. **[保存]** をクリック

#### バックエンドの追加

- **[バックエンド追加]** ボタンで同じプロバイダーを複数登録できます
- 例: OpenAI を 2 つ追加し、片方を `gpt-4.1-mini`（高速バッチ用）、もう片方を `gpt-4.1`（高品質用）に設定
- 異なる API キーを使い分けることで、レート制限を分散させることも可能です
- 不要なバックエンドは **[削除]** ボタンで削除できます

| バックエンド | 特徴 | 必要なもの |
|---|---|---|
| **Claude** (Anthropic) | 高品質な翻訳 | API キー ([取得方法](docs/API_Key_取得_Claude.md)) |
| **Gemini** (Google) | 高速・大量処理向き | API キー ([取得方法](docs/API_Key_取得_Gemini.md)) |
| **OpenAI** (ChatGPT) | 高品質・バランス型 | API キー ([取得方法](docs/API_Key_取得_OpenAI.md)) |
| **Ollama** (ローカル) | 無料・オフライン対応 | [Ollama](docs/Ollama_Install.md) + モデル |

---

### 3. 翻訳エディタ — 翻訳結果の確認・編集

![翻訳エディタ](image/TransEditor.png)

1. **翻訳エディタ** タブを開く
2. **[DB 読込]** をクリックしてデータを表示
3. **検索** バーでキーワード検索、**フィルタ** で Source (official/ai/manual) や Translator で絞り込み
4. **Japanese 列をダブルクリック** して翻訳を手動編集
5. 不満な翻訳はチェックボックスで選択 → **[選択した翻訳を削除]** で次回再翻訳
6. **CSV Export / Import** で外部エディタとの連携も可能

---

### 4. 用語集 — 翻訳ルールの管理

![用語集](image/kaywards.png)

1. **用語集** タブを開く
2. 下部の **English** / **Japanese** フィールドに用語を入力
3. **[追加/更新]** をクリックして登録
4. 登録した用語は AI 翻訳時のプロンプトに自動挿入されます
5. **[一括置換]** で翻訳済みテキスト内の用語を正しい訳語に一括変換
6. **CSV Export / Import** で用語集の一括管理も可能

---

### 5. プロファイル — バックアップ/リストア

![プロファイル](image/Profile.png)

1. **プロファイル** タブを開く
2. **キャラクターデザイン** (左) と **キーバインド/コントロール設定** (右) を管理
3. **[ゲームから保存]** — 現在のゲーム設定をバックアップ
4. **[ゲームに反映]** — バックアップをゲームに復元
5. **[外部から取込]** — 他の人のファイルをインポート
6. **[詳細設定]** (コントロール側) — キーバインドエディタを開く

---

### 6. キーバインドエディタ — 機能別タブ

![キーバインド 機能別](image/Keybind_Function.png)

1. プロファイルタブで保存を選択 → **[詳細設定]** をクリック
2. **[機能別]** タブ: カテゴリ一覧（左）とバインド一覧（右）を表示
3. 左のカテゴリをクリックすると、そのカテゴリの機能に絞り込み
4. **検索** バーで機能名・キー名を検索
5. **入力フィルタ** でキーボード/マウス/ゲームパッド/未割当のみ等で絞り込み
6. 行をダブルクリックしてバインドを編集
7. 変更済みの行は黄色でハイライト、変更されたキーはオレンジ色で表示
8. **[エクスポート]** で XML / CSV に保存、**[インポート]** で読み込み

---

### 7. キーバインドエディタ — キーボードタブ (New!)

![キーバインド キーボード](image/Keybind_Keyboard2.png)

1. **[キーボード]** タブをクリック
2. **日本語 109 キー配列** がビジュアル表示されます（テンキー付き）
3. **色分け** でバインドの種類が一目でわかります:
   - **青** = タップ / 押下中 (tap / press)
   - **紫** = 長押し (hold / delayed_hold)
   - **緑** = 二度押し (double_tap)
   - **オレンジ** = 修飾キー選択中
   - **グレー** = 未割当
4. **修飾キーのチェックボックス** (L-Shift, R-Ctrl, L-Alt 等) をチェックすると、その修飾キーとの組み合わせバインドを表示
5. キーに **マウスホバー** するとツールチップで割り当て済み機能一覧と activation mode を表示
6. キーを **クリック** すると割り当てダイアログが開き、機能を検索して割り当て/解除が可能
7. 機能名はゲーム内の **日本語翻訳データ (global.ini)** を参照して表示

---

### 8. チャット — AI に質問 (キーバインド検索対応)

![チャット](image/chat.png)

1. **チャット** タブを開く
2. **AI バックエンド** をドロップダウンで選択
3. テキストボックスに質問を入力して **[送信]**
4. AI が自動で必要なツール (スキル) を呼び出してデータを取得・回答
5. **Web チャット** — ブラウザから `http://localhost:8099` にアクセスして利用可能

#### AI 検索スキル (Tool Use)

| スキル | 検索できる内容 | 質問例 |
|---|---|---|
| **search_ship** | 船・機体の検索 | 「ホーネットについて教えて」 |
| **search_commodity** | 商品・資源＋場所別価格 | 「ラナイトの売値は？」 |
| **search_item** | 武器・シールド・QD 等 | 「S3 のリピーターを比較して」 |
| **search_mission** | ミッション・契約 | 「賞金稼ぎのミッション一覧」 |
| **search_price** | アイテムの販売場所・価格 | 「M5A Cannon はどこで買える？」 |
| **search_wiki** | Wiki からの詳細情報 | 「Carrack のスペックは？」 |
| **search_pledge** | RSI プレッジ価格・Warbond | 「Corsair の値段は？」 |
| **search_keybind** | キーバインド検索 | 「ドッキングのキーは何？」「空いているキーは？」 |
| **remember** | ナレッジに情報を保存 | 「覚えて：○○はバグで動かない」 |
| **forget** | ナレッジから情報を削除 | 「直った：○○のバグ」「忘れて：○○」 |

#### ナレッジ (記憶) 機能

AI に情報を教えて記憶させることができます。保存した情報は毎回のチャットに自動で読み込まれます。

- **保存**: 「覚えて：ハーベスターのドアが開かないバグがある」→ AI が `remember` ツールで SQLite に保存
- **削除**: 「直った：ハーベスターのバグ」→ `forget` ツールで削除
- **管理 UI**: アプリの **[記憶管理]** ボタン、または Web チャットのモーダルから一覧・編集・削除
- **Discord 連携**: 選択したナレッジを Discord マークダウン形式でコピー / Discord からテキストを貼り付けて一括インポート

#### VoiceVox 読み上げ (Web チャット)

Web チャットでは VoiceVox による音声読み上げに対応しています。

- 送信時に「おまちください」、応答時に「お待たせしました」+ AI 要約を読み上げ
- AI が `<tts>` タグで簡潔な要約を自動生成、リスト項目は読み上げない
- 話者 ID はアプリ設定と Web UI で自動同期
- **前提条件**: [VoiceVox](https://voicevox.hiroshiba.jp/) がローカルで起動していること (デフォルト: `localhost:50021`)

---

### 9. 設定タブ — 各種設定・データベース管理

![設定](image/Setting.png)

1. **基本設定**: GamePath・Working Directory・Output Language を確認・変更
2. **ゲームデータ連携**: **[インデックス構築]** で Data.p4k からゲームデータを抽出・キャッシュ
3. **AI バックエンド状態**: 設定済みバックエンドの一覧を確認
4. **データベース**: 
   - **[エクスポート (バックアップ)]** — 翻訳 DB を ZIP で保存
   - **[インポート (復元)]** — バックアップ ZIP を読み込み
5. **[設定を保存]** で変更を反映

---

## 最新の主な新機能

### ナレッジ (記憶) システム

- チャットで「覚えて」と言うだけで、バグ情報・用語・Tips を AI に記憶させられる
- 「直った」「忘れて」で不要な情報を削除
- SQLite に保存され、毎回のチャットコンテキストに自動反映
- ナレッジ管理 UI (WPF / Web) で一覧・編集・削除
- Discord エクスポート / インポート対応

### AI チャット + VoiceVox 読み上げ

- **Web チャット** (`localhost:8099`) — ブラウザからもチャットが利用可能
- **VoiceVox TTS**: 送信時に「おまちください」、応答時に「お待たせしました」+ 要約を読み上げ
- AI が `<tts>` タグで簡潔な要約を自動生成
- AudioContext ベースの再生でブラウザ自動再生ポリシーに対応
- 話者 ID のアプリ⇔Web 同期、ON/OFF 状態の永続化

### ロケーション検索の強化

- **Star Citizen Wiki API 連携**: ステーション・都市の施設情報（精錬所・ハンガー・ランディングパッド・フレイトエレベーター等）を自動取得
- UEX のショップ・ターミナル情報 + Wiki のアメニティ情報を統合表示
- 「精錬所はどこ？」のような日本語質問にも対応

### 検索機能の改善

- **StripToAlphaNum**: 「P8SC」「P 8-SC」「Ｐ８ＳＣ」すべてが「P8-SC SMG」にマッチ
- **SplitOnParticles**: 日本語助詞の除去による検索精度向上
- **プロンプト翻訳**: 日本語→英語変換を AI に委任（地名例: オリソン→Orison, ロービル→Lorville 等）

### キーバインドエディタの強化 (v1.9.6)

- **キーボードタブ**: 日本語 109 キー配列 + テンキーをビジュアル表示
- **Activation Mode 対応**: tap / press / hold / double_tap の色分け表示
- **修飾キーチェックボックス**: L-Shift / R-Ctrl / L-Alt 等の組み合わせバインドをワンクリックで確認
- **翻訳連携**: 機能名にゲーム内の日本語翻訳 (global.ini) を使用

---

## Ollama + Gemma4 の設定方法

ローカル LLM を使えば **API キー不要・無料** で翻訳できます。

**1. Ollama をインストール** ([詳細ガイド](docs/Ollama_Install.md))

[ollama.com](https://ollama.com/) からインストーラーをダウンロード・実行します。

**2. 翻訳用モデルをダウンロード**

```bash
# 高品質モデル (VRAM 4GB 以上推奨)
ollama pull gemma4:4b

# 軽量モデル (VRAM 2GB でも動作)
ollama pull gemma4:2b
```

> VRAM に余裕があれば `gemma4:26b` (高精度) や `gemma4:12b` (バランス型) も使えます。

**3. AI 設定ダイアログで設定**

| 項目 | 設定値 | 説明 |
|---|---|---|
| **Type** | `Ollama` | ドロップダウンから選択 |
| **Base URL** | `http://localhost:11434` | Ollama のアドレス (別 PC の場合は IP を変更) |
| **Model** | `gemma4:4b` or `gemma4:2b` | ダウンロードしたモデル名を入力 |
| **Batch** | `15` | 一度に送る翻訳数 (VRAM に応じて調整) |

> **VRAM に関する注意**: Ollama (Gemma4) はGPUのVRAMを使用します。Star Citizen もVRAMを大量に消費するため、**ゲームプレイ中に翻訳やチャットを実行するとVRAMが圧迫され、ゲームのパフォーマンスが低下する場合があります**。ゲーム中の使用を避けるか、軽量モデル (`gemma4:2b`) を使用するか、別PCのOllamaに接続することを推奨します。Claude / Gemini などのクラウドAPIを使用する場合はVRAMへの影響はありません。

---

## データ保存先

全てのデータは Working Directory (設定タブで変更可能) に保存されます。

| ファイル | 内容 |
|---|---|
| `translations.db` | 翻訳データベース (SQLite) — 翻訳テキスト・用語集・ナレッジ |
| `gamedata_cache.db` | ゲームデータキャッシュ (SQLite) — Data.p4k からの取得結果 |
| `saves/characters/` | キャラクターデザインのバックアップ |
| `saves/controls/` | キーバインド設定のバックアップ |
| `output/global.ini` | 統合済み翻訳ファイル |
| `chat_debug.log` | チャット機能のデバッグログ |

---

## ゲームデータ連携 (Data.p4k / StarBreaker)

設定タブの **「インデックス構築」** で、ローカルの Data.p4k からゲーム内データのインデックスを構築します。

- **StarBreaker CLI** (約 4MB) は初回実行時に自動ダウンロードされます
- インデックス構築は約 30 秒で完了します
- **ゲームパッチ適用後は必ずインデックスを再構築してください**
- チャットで質問すると、該当するエンティティの詳細をオンデマンドで取得します

---

## セットアップガイド

### API キー取得

| バックエンド | ガイド |
|---|---|
| **Claude** (Anthropic) | [API_Key_取得_Claude.md](docs/API_Key_取得_Claude.md) |
| **Gemini** (Google) | [API_Key_取得_Gemini.md](docs/API_Key_取得_Gemini.md) |
| **OpenAI** (ChatGPT) | [API_Key_取得_OpenAI.md](docs/API_Key_取得_OpenAI.md) |

### ローカル LLM (Ollama)

| ドキュメント | 内容 |
|---|---|
| [Ollama_Install.md](docs/Ollama_Install.md) | Ollama 本体のインストール |
| [Gemma4_e4b_Install.md](docs/Gemma4_e4b_Install.md) | 高品質モデル (4B) |
| [Gemma4_e2b_Install.md](docs/Gemma4_e2b_Install.md) | 軽量モデル (2B) |

---

## ビルド方法

```bash
git clone https://github.com/batake321/StarCitizenJapaneseTextCreater.git
cd StarCitizenJapaneseTextCreater
dotnet build -c Release
```

---

## 利用している外部サービス

| サービス | 用途 |
|---|---|
| **[StarBreaker](https://github.com/diogotr7/StarBreaker)** | Data.p4k DCB クエリ |
| **[UEX Corp](https://uexcorp.space/)** | 機体・商品・価格データ |
| **[SC Trade Tools](https://sc-trade.tools/)** | アイテム詳細・取引ショップ |
| **[starcitizen.tools](https://starcitizen.tools/)** | Wiki (MediaWiki API) |
| **Anthropic Claude API** | AI 翻訳・チャット |
| **Google Gemini API** | AI 翻訳・チャット |
| **OpenAI API** | AI 翻訳・チャット |
| **[Ollama](https://ollama.com/)** | ローカル LLM |
| **[VoiceVox](https://voicevox.hiroshiba.jp/)** | 音声読み上げ (Web チャット) |

### 注意事項

- **Star Citizen** は Cloud Imperium Games (CIG) の著作物です
- ゲームデータの抽出・解析はファンによるローカライズ目的での利用を想定しています
- 各外部 API は非公式のコミュニティ運営サービスであり、サービスの可用性は保証されません

## ライセンス

MIT License
