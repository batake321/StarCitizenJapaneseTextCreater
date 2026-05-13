# Star Citizen Japanese Text Creator

Star Citizen のゲームテキストを日本語化するツールです。

- Data.p4k から英語・日本語の global.ini を自動抽出
- 公式日本語翻訳をベースに、未翻訳部分を AI で翻訳
- **船名・地名は英語のまま保持**
- 複数の翻訳バックエンドを並列利用可能

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

1. **Extract** - Data.p4k から global.ini を抽出
2. **Translate** - 未翻訳テキストを AI で翻訳
3. **Merge** - 翻訳を統合して global.ini を生成
4. **Deploy** - ゲームディレクトリに配置
5. **All** - 全工程を一括実行

コマンドライン引数でも指定可能:
```
StarCitizenJapaneseTextCreater.exe all
StarCitizenJapaneseTextCreater.exe translate
```

### 3. 翻訳の再開

翻訳はプログレスファイル（`work/progress.json`）で進捗を管理しています。
中断しても再実行すれば途中から再開します。

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
