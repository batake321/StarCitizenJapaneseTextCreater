# Star Citizen Japanese Text Creator

Star Citizen のゲーム内テキストを AI で日本語に翻訳し、ゲームに適用する Windows デスクトップアプリケーションです。

## 機能

- **Data.p4k 自動抽出** - ゲームの Data.p4k (ZIP64 + ZStd) から英語/日本語の global.ini を自動抽出
- **AI 翻訳** - Claude / Gemini / Ollama (ローカル LLM) の 3 バックエンドに対応、複数同時並列翻訳
- **翻訳エディタ** - AI 翻訳結果の確認・手動編集・検索・フィルタリング
- **用語集** - 固有名詞の翻訳ルールを登録し、一括置換も可能
- **チャット** - AI に Star Citizen の最新データを参照させて日本語で質問
- **ゲームデータ連携** - Data.p4k の DCB データベースからオンデマンドで船・武器・コンポーネント情報を取得
- **プロファイル管理** - キャラクターデザイン・キーバインド設定のバックアップ/リストア
- **ゲームパス自動検出** - RSI Launcher のログから Star Citizen のインストール先を自動検出
- **チャンネル切替** - PTU / LIVE / EPTU など複数チャンネルをワンクリックで切替
- **リアルタイム進捗** - 翻訳の進捗・残り時間・完了予測時刻をリアルタイム表示

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
  - [Ollama](docs/Ollama_Install.md) + [Gemma e4b](docs/Gemma4_e4b_Install.md) or [e2b](docs/Gemma4_e2b_Install.md) (ローカル・無料)

---

## 使い方

### 全体フロー

```
┌─────────────────────────────────────────────────────────┐
│                    起動 & 初期設定                        │
│  GamePath 自動検出 → チャンネル選択 → AI 設定            │
└────────────────────────┬────────────────────────────────┘
                         │
                         ▼
              ┌─────────────────────┐
              │   1. 抽出 ボタン     │  Data.p4k → global.ini → DB
              └──────────┬──────────┘
                         │
                         ▼
              ┌─────────────────────┐
              │   2. 翻訳 ボタン     │  DB → AI 翻訳 → DB
              └──────────┬──────────┘
                         │
                         ▼
              ┌─────────────────────┐
              │   3. 反映 ボタン     │  DB → global.ini → ゲームに配置
              └──────────┬──────────┘
                         │
                         ▼
              ┌─────────────────────┐
              │  Star Citizen 起動   │  日本語テキストで遊べる!
              └─────────────────────┘
```

---

### Step 0: 初期設定

```
┌──────────────────────────────────────────────────────────────────┐
│ GamePath: [E:\Games\RSI\StarCitizen ▼] [PTU ▼] [参照] [AI 設定] │
└──────────────────────────────────────────────────────────────────┘
```

1. **GamePath** は RSI Launcher のログから自動検出されます（手動変更も可能）
2. **チャンネル** (PTU / LIVE 等) をドロップダウンで選択
3. **[AI 設定]** をクリックして翻訳バックエンドを設定

#### AI 設定ダイアログ

```
┌─ AI 設定 ───────────────────────────────────────────┐
│                                                      │
│  ┌─ Claude ────────────────────────────────────────┐ │
│  │ Type: Claude    Model: claude-sonnet-4-6        │ │
│  │ API Key: [sk-ant-api03-***]                     │ │
│  │ Batch Size: 50       [x] 有効                   │ │
│  └─────────────────────────────────────────────────┘ │
│                                                      │
│  ┌─ Gemini ────────────────────────────────────────┐ │
│  │ Type: Gemini    Model: gemini-2.5-flash         │ │
│  │ API Key: [AIza***]                              │ │
│  │ Batch Size: 40       [x] 有効                   │ │
│  └─────────────────────────────────────────────────┘ │
│                                                      │
│  ┌─ LocalLLM ──────────────────────────────────────┐ │
│  │ Type: Ollama    Model: gemma4:26b               │ │
│  │ Base URL: [http://localhost:11434]               │ │
│  │ Batch Size: 15       [x] 有効                   │ │
│  └─────────────────────────────────────────────────┘ │
│                                                      │
│          [バックエンド追加]  [保存]  [キャンセル]      │
└──────────────────────────────────────────────────────┘
```

| バックエンド | 特徴 | 必要なもの |
|---|---|---|
| **Claude** (Anthropic) | 高品質な翻訳 | API キー ([取得方法](docs/API_Key_取得_Claude.md)) |
| **Gemini** (Google) | 高速・大量処理向き | API キー ([取得方法](docs/API_Key_取得_Gemini.md)) |
| **Ollama** (ローカル) | 無料・オフライン対応 | [Ollama](docs/Ollama_Install.md) + モデル |

複数バックエンドを有効にすると、バッチを分散して**並列翻訳**します。

#### Ollama + Gemma4 の設定方法

ローカル LLM を使えば **API キー不要・無料** で翻訳できます。

**1. Ollama をインストール** ([詳細ガイド](docs/Ollama_Install.md))

[ollama.com](https://ollama.com/) からインストーラーをダウンロード・実行します。

**2. 翻訳用モデルをダウンロード**

コマンドプロンプトで以下を実行します:

```bash
# 高品質モデル (VRAM 4GB 以上推奨)
ollama pull gemma4:4b

# 軽量モデル (VRAM 2GB でも動作)
ollama pull gemma4:2b
```

> VRAM に余裕があれば `gemma4:26b` (高精度) や `gemma4:12b` (バランス型) も使えます。
> 詳細: [Gemma4 e4b ガイド](docs/Gemma4_e4b_Install.md) / [Gemma4 e2b ガイド](docs/Gemma4_e2b_Install.md)

**3. AI 設定ダイアログで設定**

| 項目 | 設定値 | 説明 |
|---|---|---|
| **Type** | `Ollama` | ドロップダウンから選択 |
| **Base URL** | `http://localhost:11434` | Ollama のアドレス (別 PC の場合は IP を変更) |
| **Model** | `gemma4:4b` or `gemma4:2b` | ダウンロードしたモデル名を入力 |
| **Batch** | `15` | 一度に送る翻訳数 (VRAM に応じて調整) |

> **別 PC の Ollama を使う場合**: Base URL を `http://192.168.x.x:11434` のように変更します。
> Ollama 側で `OLLAMA_HOST=0.0.0.0` の環境変数を設定してください。

> **VRAM に関する注意**: Ollama (Gemma4) はGPUのVRAMを使用します。Star Citizen もVRAMを大量に消費するため、**ゲームプレイ中に翻訳やチャットを実行するとVRAMが圧迫され、ゲームのパフォーマンスが低下する場合があります**。ゲーム中の使用を避けるか、軽量モデル (`gemma4:2b`) を使用するか、別PCのOllamaに接続することを推奨します。Claude / Gemini などのクラウドAPIを使用する場合はVRAMへの影響はありません。

---

## チャットタブ

AI バックエンドを使って Star Citizen について日本語で質問できます。Claude / Gemini では **AI Tool Use (Skills)** により、AI が質問内容を判断して必要なデータを自動取得します。Ollama は従来方式（事前データ取得）で動作します。

### AI 検索スキル (Tool Use)

Claude / Gemini 利用時、AI は以下のスキルを自動的に使い分けてデータを取得します。

| スキル | 検索できる内容 | 質問例 |
|---|---|---|
| **search_ship** | 船・機体の検索（部分一致→番号付き候補リスト） | 「ホーネットについて教えて」「F7C mk2 のスペックは？」 |
| **search_commodity** | 商品・資源の検索＋場所別価格 | 「Stanton でアルミが一番安いのは？」「ラナイトの売値は？」 |
| **search_item** | 武器・シールド・QD・パワープラント等のコンポーネント検索 | 「S3 のリピーターを比較して」「最強のシールドは？」 |
| **search_mission** | ミッション・契約の検索（UEX + Data.p4k DCB） | 「賞金稼ぎのミッション一覧」「報酬が高い契約は？」 |
| **search_price** | アイテムの販売場所・価格 | 「M5A Cannon はどこで買える？」 |
| **search_wiki** | Wiki からの船・アイテム詳細情報 | 「Carrack のハードポイント構成は？」 |
| **search_pledge** | RSI プレッジ価格・Warbond 割引情報 | 「Corsair の値段は？」「Warbond ある？」 |

> **候補提示**: 検索結果が複数ある場合、AI が番号付きリストで候補を提示します。番号で選択するか、別の名前を直接入力できます。

> **⚠️ Warbond・販売情報について**: Warbond の有無や船の販売状況は時期により変動します。search_pledge の結果には公式サイト確認の注意が自動付与されますが、最新の販売・割引情報は必ず [RSI 公式サイト](https://robertsspaceindustries.com/pledge) でご確認ください。

### チャットデバッグログ

チャット機能のデバッグログが Working Directory に自動保存されます。AI がどのツールを呼び出したか、API レスポンスの詳細、ツール実行結果などを確認できます。

| ファイル | 内容 |
|---|---|
| `chat_debug.log` | チャットセッションのデバッグログ（API リクエスト/レスポンス、ツール呼出し、実行結果） |

ログには以下の情報が記録されます：
- 使用バックエンド・モデル名
- AI リクエスト内容（イテレーション番号、ツール強制モード）
- AI レスポンス（stop_reason、tool_use 判定）
- ツール呼出し名・パラメータ・実行結果
- エラー発生時の詳細

> チャットで期待通りの結果が得られない場合、このログを確認することで問題の原因を特定できます。

### データソース

| データソース | API キー | 提供データ | 備考 |
|---|---|---|---|
| **Data.p4k (ローカル)** | 不要 | 全エンティティ定義（船・武器・コンポーネント） | StarBreaker CLI でオンデマンド取得。パッチ直後でも最新 |
| **[UEX Corp](https://uexcorp.space/)** | 不要 | 機体・商品・価格・取引場所・星系 | プレイヤー投稿ベースの経済データ |
| **[SC Trade Tools](https://sc-trade.tools/)** | 不要 | アイテム詳細・商品取引ショップ一覧 | 7,500+ アイテムの名前・説明・タイプ |
| **[starcitizen.tools Wiki](https://starcitizen.tools/)** | 不要 | 船の概要・スペック・ハードポイント | MediaWiki API 経由 |
| **[StarCitizen API](https://starcitizen-api.com/)** | 必要 | 機体・組織・スターマップ | **現在サービス停止中の可能性あり** (後述) |

### ゲームデータ連携 (Data.p4k / StarBreaker)

設定タブの **「インデックス構築」** ボタンで、ローカルの Data.p4k からゲーム内データのインデックスを構築します。

- **StarBreaker CLI** (約 4MB) は初回実行時に自動ダウンロードされます（インストール不要）
- インデックス構築は約 30 秒で完了します
- **ゲームパッチ適用後は必ずインデックスを再構築してください** — パッチでデータが更新されるため、古いインデックスでは最新のミッション・船・アイテム情報が反映されません
- ミッション・契約の検索にはインデックス構築が必須です。未構築の場合、チャットで「設定からインデックス再構成を行ってください」と案内されます
- チャットで質問すると、該当するエンティティの詳細をオンデマンドで Data.p4k から直接取得します（1〜2 秒）
- 取得済みデータは SQLite にキャッシュされるため、同じ質問は即応答します
- **ディスク消費は最小限** — 全データ抽出は行わず、必要な情報だけを都度取得します

#### DCB クエリ対応レコードタイプ

| レコードタイプ | 取得できる情報 |
|---|---|
| `EntityClassDefinition` | 船・車両・武器・全エンティティの基本定義 |
| `SCItemShieldGeneratorParams` | シールドの最大HP・リジェネ速度 |
| `SCItemQuantumDriveParams` | QD の燃料消費・ジャンプ距離・スプールアップ |
| `SCItemWeaponComponentParams` | 武器の発射速度 |
| `SAmmoContainerComponentParams` | 弾薬数・ダメージ種別（物理/エネルギー/ディストーション） |
| `SCItemPowerPlantParams` | パワープラントのパラメータ |
| `SCItemCoolerParams` | クーラーのパラメータ |
| `MissionBrokerEntry` | ミッション定義（タイトル・難易度・報酬・依頼者） |
| `ContractManager` | 契約管理画面の定義 |
| `CommoditySubtype` | 商品・資源（名前・シンボル・揮発性） |

### StarCitizen API について

[starcitizen-api.com](https://starcitizen-api.com/) は非公式の個人運営サービスです。2023年4月頃を最後に更新が停止しており、**API エンドポイントが 404 を返す状態が確認されています**。Discord Bot (`/api register`) も応答しません。

API キーを設定タブに入力しておけば接続を試みますが、現時点では UEX Corp・SC Trade Tools・Wiki・ローカルゲームデータで十分な情報が得られます。

### Claude / Gemini のクレジット不足通知

Claude または Gemini の API クレジットが不足した場合、チャット内にその旨が通知されます。

- **Claude**: HTTP 429/402/529 エラー時 → Anthropic Console で残高確認を案内
- **Gemini**: HTTP 429/403 エラー時 → Google AI Studio で使用量確認を案内

---

## 翻訳タブ

### Step 1: 抽出

- Data.p4k から `english/global.ini` と `japanese_(japan)/global.ini` を抽出
- 抽出したテキストを SQLite データベースに登録
- 完了後、翻訳エディタに自動反映

### Step 2: 翻訳

- DB 内の未翻訳エントリを AI に送信
- 複数バックエンドで**並列処理** (ラウンドロビン分配)
- 翻訳結果はリアルタイムでエディタに反映
- 失敗したエントリは翻訳完了後に自動リトライ
- **[中止]** ボタンで安全に停止可能

### Step 3: 反映

- 英語テキスト + 公式日本語 + AI 翻訳 + 手動編集を統合
- 統合した `global.ini` をゲームディレクトリに自動配置
- Star Citizen を起動すれば日本語テキストが適用されます

---

## 翻訳エディタ

- **Japanese 列をダブルクリック**して翻訳を手動編集
- **Source フィルタ**: official / ai / manual / untranslated で絞り込み
- **Translator フィルタ**: Claude/Gemini/Ollama 等のバックエンド別表示
- **チェックボックス + 削除**: 不満な翻訳を削除し、次回翻訳で再実行
- **CSV Export/Import**: 外部エディタでの一括編集に対応

## 用語集

- 英語→日本語の訳語ルールを登録
- 登録した用語は AI 翻訳時のプロンプトに自動挿入
- **[一括置換]**: 翻訳済みテキスト内の用語を一括で正しい訳語に置換

## プロファイル管理

キャラクターデザイン (`.chf`) とキーバインド設定をバックアップ・リストアできます。

- 保存先: Working Directory 内の `saves/characters/` および `saves/controls/`
- 外部ファイルの取り込み (`ImportChar` / `ImportCtrl`) にも対応
- バックアップのコピー・削除も可能

---

## データ保存先

全てのデータは Working Directory (設定タブで変更可能) に保存されます。

| ファイル | 内容 |
|---|---|
| `translations.db` | 翻訳データベース (SQLite) — 翻訳テキスト・用語集 |
| `gamedata_cache.db` | ゲームデータキャッシュ (SQLite) — Data.p4k からの取得結果 |
| `saves/characters/` | キャラクターデザインのバックアップ |
| `saves/controls/` | キーバインド設定のバックアップ |
| `output/global.ini` | 統合済み翻訳ファイル |
| `chat_debug.log` | チャット機能のデバッグログ |

### データベースのバックアップと共有

設定タブの **「エクスポート (バックアップ)」** で、データベースの内容を ZIP 形式で保存できます。

#### エクスポート

全データ（翻訳・用語集・インデックス）を 1 つの `.zip` ファイルに圧縮保存します。

#### インポート

**「インポート (復元)」** ボタンで `.zip` ファイルを読み込みます。インポート時にダイアログで取り込むデータを選択できます:

| カテゴリ | 対象テーブル | 説明 |
|---|---|---|
| 翻訳データ | `translations` | AI/手動の翻訳結果 |
| 用語集 | `glossary` | 英→日の訳語ルール |
| インデックス | `ships`, `items`, `missions`, `commodities` 等 | Data.p4k から抽出したゲームデータ |

#### チームでの共有

リポジトリの `db_backup/` フォルダにバックアップファイルを保存して共有できます。

```
db_backup/
├── translations/   翻訳データ
├── glossary/       用語集
└── index/          ゲームデータインデックス
```

1. アプリの **エクスポート** で `.zip` を `db_backup/` に保存
2. `git commit` & `git push`
3. チームメイトが `git pull` → アプリの **インポート** で取り込み

コマンドラインでのエクスポートも可能です:
```powershell
.\export_backup.ps1
```

---

## 翻訳ルール

AI は以下のルールに基づいて翻訳します:

| カテゴリ | ルール |
|---|---|
| 地名 (惑星・星系・ステーション) | **英語のまま** |
| 船の名前・メーカー名 | **英語のまま** |
| 人物名・企業名 | **英語のまま** |
| UI ラベル・説明文・ミッション | 自然な日本語に翻訳 |
| タグ (`%ls` `~action()` `<EM4>` 等) | そのまま保持 |

---

## セットアップガイド

### API キー取得

| バックエンド | ガイド | 概要 |
|---|---|---|
| **Claude** (Anthropic) | [API_Key_取得_Claude.md](docs/API_Key_取得_Claude.md) | Anthropic Console でのアカウント作成・課金設定・APIキー発行 |
| **Gemini** (Google) | [API_Key_取得_Gemini.md](docs/API_Key_取得_Gemini.md) | Google AI Studio でのAPIキー取得（無料枠あり） |

### ローカル LLM (Ollama)

API キー不要・無料・オフライン対応のローカル翻訳環境を構築できます。

| ドキュメント | 内容 |
|---|---|
| [Ollama_Install.md](docs/Ollama_Install.md) | Ollama 本体のインストールと基本操作 |
| [Gemma4_e4b_Install.md](docs/Gemma4_e4b_Install.md) | 高品質モデル (4B) — ミッション説明・長文向け |
| [Gemma4_e2b_Install.md](docs/Gemma4_e2b_Install.md) | 軽量モデル (2B) — UIラベル・短文・高速応答向け |

---

## ビルド方法

```bash
git clone https://github.com/batake321/StarCitizenJapaneseTextCreater.git
cd StarCitizenJapaneseTextCreater
dotnet build -c Release
```

出力先: `bin/Release/net8.0-windows/StarCitizenJapaneseTextCreater.exe`

---

## 利用している外部サービス・ツール

本アプリは以下の外部サービス・ツールを利用しています。各サービスの利用規約に従ってご使用ください。

### ゲームデータ取得

| ツール / サービス | 用途 | URL |
|---|---|---|
| **StarBreaker** | Data.p4k の DCB データベースからエンティティ情報をクエリ | [github.com/diogotr7/StarBreaker](https://github.com/diogotr7/StarBreaker) |
| [unp4k_rs](https://github.com/StarCitizenToolBox/unp4k_rs) | Data.p4k 解析ツール（MCP サーバー対応）。将来のデータソース拡張候補 | [github.com/StarCitizenToolBox/unp4k_rs](https://github.com/StarCitizenToolBox/unp4k_rs) |
| **UEX Corp API** | 機体・商品・価格・取引場所のリアルタイムデータ | [uexcorp.space](https://uexcorp.space/) |
| **SC Trade Tools API** | アイテム詳細 (7,500+ 件)・商品取引ショップ一覧 | [sc-trade.tools](https://sc-trade.tools/) |
| **starcitizen.tools** | 船の概要・スペック・ハードポイント (MediaWiki API) | [starcitizen.tools](https://starcitizen.tools/) |
| **StarCitizen API** | 機体・組織・スターマップ (現在停止中の可能性) | [starcitizen-api.com](https://starcitizen-api.com/) |

### AI 翻訳・チャット

| サービス | 用途 | URL |
|---|---|---|
| **Anthropic Claude API** | AI 翻訳・チャット | [anthropic.com](https://www.anthropic.com/) |
| **Google Gemini API** | AI 翻訳・チャット | [ai.google.dev](https://ai.google.dev/) |
| **Ollama** | ローカル LLM 翻訳・チャット | [ollama.com](https://ollama.com/) |

### 注意事項

- **Star Citizen** は Cloud Imperium Games (CIG) の著作物です
- ゲームデータの抽出・解析はファンによるローカライズ目的での利用を想定しています
- CIG はファンによるローカライズやデータマイニングをある程度容認していますが、抽出したアセットの商用利用やゲームバランスを崩す悪用は禁止されています
- 各外部 API は非公式のコミュニティ運営サービスであり、サービスの可用性は保証されません

## ライセンス

MIT License
