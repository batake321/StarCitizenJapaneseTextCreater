# Star Citizen Japanese Text Creator

Star Citizen のゲームテキストを日本語化するツールです。

## 機能

### 翻訳
- Data.p4k から英語・日本語の global.ini を自動抽出
- 公式日本語翻訳をベースに、未翻訳部分を AI で翻訳
- **船名・地名は英語のまま保持**
- 複数の翻訳バックエンドを並列利用可能

### 翻訳データベース
- SQLite で英語・日本語の対訳を管理
- CSV エクスポート/インポートで自由に編集可能
- 手動修正は AI 翻訳より優先される

### プロファイル管理
- キャラクターデザイン（.chf）の保存・読込
- キーバインド/マウス/ジョイスティック設定の保存・読込
- バージョンアップ時のバックアップに

## 対応翻訳バックエンド

| バックエンド | 設定 |
|---|---|
| Claude API | APIキーを設定 |
| Gemini API | APIキーを設定 |
| Local LLM (Ollama) | サーバーURLとモデル名を設定 |

複数のバックエンドを同時に有効化すると、並列で翻訳を実行します。

## 使い方

### 1. 設定

`appsettings.json` を編集してください。

```json
{
  "GamePath": "C:\\Program Files\\Roberts Space Industries\\StarCitizen\\LIVE",
  "Translation": {
    "Backends": [
      {
        "Name": "Claude",
        "Type": "Claude",
        "ApiKey": "sk-ant-...",
        "Model": "claude-sonnet-4-6",
        "BatchSize": 50,
        "Enabled": true
      },
      {
        "Name": "Gemini",
        "Type": "Gemini",
        "ApiKey": "AIza...",
        "Model": "gemini-2.5-flash",
        "BatchSize": 40,
        "Enabled": true
      },
      {
        "Name": "LocalLLM",
        "Type": "Ollama",
        "BaseUrl": "http://localhost:11434",
        "Model": "gemma4:27b",
        "BatchSize": 15,
        "Enabled": true
      }
    ]
  }
}
```

- `GamePath`: Star Citizen のインストールディレクトリ（LIVE or PTU）
- `Backends`: 使用する翻訳バックエンドを設定。`Enabled: true` で有効化
- 複数バックエンドを有効にすると並列翻訳

### 2. 実行

```
StarCitizenJapaneseTextCreater.exe
```

メニューが表示されます:

```
=== Star Citizen Japanese Text Creator ===

  --- 翻訳 ---
  1. Extract   - Data.p4k から global.ini を抽出
  2. Translate - 未翻訳テキストを翻訳
  3. Merge     - 翻訳を統合して global.ini を生成
  4. Deploy    - ゲームディレクトリに配置
  5. All       - 翻訳の全工程を実行

  --- 翻訳DB ---
  6. DB Stats    - 翻訳データベースの統計
  7. CSV Export  - 翻訳をCSVにエクスポート
  8. CSV Import  - CSVから翻訳をインポート

  --- プロファイル ---
  A. Save Character   - キャラクターデザインを保存
  B. Load Character   - キャラクターデザインを読込
  C. Save Controls    - キーバインド設定を保存
  D. Load Controls    - キーバインド設定を読込
```

コマンドライン引数でも指定可能:
```
StarCitizenJapaneseTextCreater.exe all
StarCitizenJapaneseTextCreater.exe translate
```

### 3. 翻訳の編集（CSV ワークフロー）

1. `7. CSV Export` で翻訳をCSVにエクスポート
2. Excel やテキストエディタで `translations.csv` を編集
3. `8. CSV Import` で編集済みCSVをインポート
4. `3. Merge` + `4. Deploy` でゲームに反映

CSV の形式:
```csv
key,english,japanese,source,modified_at
ui_PurchaseConfirm,Confirm Purchase,購入を確定,ai,2026-05-13 14:30:00
```

### 4. 翻訳の再開

翻訳はプログレスファイル（`work/progress.json`）で進捗を管理しています。
中断しても再実行すれば途中から再開します。

### 5. バージョンアップ時のバックアップ

ゲームのバージョンアップ前に:
1. `C. Save Controls` でキーバインドを保存
2. `A. Save Character` でキャラクターデザインを保存

バージョンアップ後に:
1. `D. Load Controls` でキーバインドを復元
2. `B. Load Character` でキャラクターデザインを復元

## ビルド

```
dotnet build
```

## 必要環境

- .NET 8.0 Runtime
- Star Citizen がインストール済み
- 翻訳バックエンドのいずれか:
  - Claude API キー
  - Gemini API キー
  - Ollama サーバー

## 翻訳ルール

- 地名（惑星名、星系名、都市名、ステーション名）は英語のまま
- 船の名前・メーカー名は英語のまま
- 人物名・企業名の固有名詞は英語のまま
- UI ラベル、説明文、ミッションテキストは日本語に翻訳
- プレースホルダー（%ls, ~mission() 等）はそのまま保持

## License

MIT
