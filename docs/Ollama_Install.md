# Ollama インストール手順

ローカル LLM 実行環境 **Ollama** のインストール手順です。Star Citizen 日本語訳ソフトのローカル翻訳バックエンドとして、Gemma 等のモデルをホストします。

---

## 1. Ollama とは

- ローカル PC で LLM を動かすためのランタイム
- OpenAI 互換 API (`http://localhost:11434`) を提供
- モデルは `ollama pull <name>` でダウンロード、`ollama run <name>` で対話、`ollama serve` でサーバー起動
- Windows / macOS / Linux 対応、GPU（NVIDIA / Apple Silicon / AMD）アクセラレーション対応

---

## 2. システム要件

| 項目 | 推奨 |
|------|------|
| OS | Windows 10/11 (64bit) / macOS 12+ / Ubuntu 22.04+ |
| RAM | 最低 8GB（2B モデル）、16GB 以上推奨（4B モデル） |
| VRAM | NVIDIA 6GB 以上推奨（CPU でも動作可、ただし低速） |
| ストレージ | モデル 1 個あたり 1〜10GB 程度 |
| ネットワーク | 初回モデル DL 時のみ必要 |

---

## 3. インストール

### 3-1. Windows

1. 公式サイトにアクセス

   ```
   https://ollama.com/download
   ```

2. **「Download for Windows」** をクリックして `OllamaSetup.exe` を取得
3. インストーラーを実行（管理者権限）
4. インストール完了後、自動でタスクトレイに常駐し、`http://localhost:11434` が起動

### 3-2. macOS

```bash
# Homebrew 利用
brew install ollama

# または公式 .dmg を https://ollama.com/download からダウンロード
```

サービス起動：

```bash
brew services start ollama
# または
ollama serve
```

### 3-3. Linux

```bash
curl -fsSL https://ollama.com/install.sh | sh
```

systemd サービスとして自動起動します。手動起動の場合は：

```bash
ollama serve
```

---

## 4. 動作確認

別のターミナルで以下を実行：

```bash
ollama --version
```

API エンドポイントの確認：

```bash
curl http://localhost:11434/api/tags
```

`{"models":[]}` などが返れば成功です。

---

## 5. 基本コマンド

| コマンド | 用途 |
|----------|------|
| `ollama pull <model>` | モデルをダウンロード |
| `ollama list` | インストール済みモデル一覧 |
| `ollama run <model>` | 対話モードで起動 |
| `ollama rm <model>` | モデル削除 |
| `ollama ps` | 実行中モデル確認 |
| `ollama serve` | API サーバー手動起動 |

---

## 6. プロジェクトからの呼び出し

Ollama は OpenAI 互換 API を備えているため、`openai` Python SDK でそのまま呼べます。

```python
from openai import OpenAI

client = OpenAI(
    base_url="http://localhost:11434/v1",
    api_key="ollama",  # ダミーで OK
)

resp = client.chat.completions.create(
    model="gemma4:e4b",
    messages=[
        {"role": "user", "content": "次の英文を日本語に: Welcome, Citizen."}
    ],
)
print(resp.choices[0].message.content)
```

ネイティブ API（`/api/generate`、`/api/chat`）を使う場合は：

```python
import requests

r = requests.post(
    "http://localhost:11434/api/chat",
    json={
        "model": "gemma4:e4b",
        "messages": [{"role": "user", "content": "Welcome, Citizen. を日本語に。"}],
        "stream": False,
    },
)
print(r.json()["message"]["content"])
```

---

## 7. GPU 利用の確認

Windows / Linux で NVIDIA GPU を使う場合、最新の NVIDIA ドライバーが入っていれば自動で GPU が選択されます。確認方法：

```bash
ollama ps
```

`PROCESSOR` 列に `100% GPU` と出ていれば GPU を使えています。`100% CPU` の場合はモデルサイズが VRAM を超えている可能性が高いので、より小さいモデルを使うか量子化版を選びます。

---

## 8. モデル保存場所

| OS | パス |
|----|------|
| Windows | `C:\Users\<user>\.ollama\models` |
| macOS | `~/.ollama/models` |
| Linux | `/usr/share/ollama/.ollama/models` または `~/.ollama/models` |

C ドライブ容量を圧迫する場合は環境変数 `OLLAMA_MODELS` で別ドライブに変更可能です。

```powershell
# Windows (PowerShell, システム環境変数として設定推奨)
setx OLLAMA_MODELS "D:\ollama\models"
```

設定後は Ollama を再起動してください。

---

## 9. トラブルシューティング

| 症状 | 対処 |
|------|------|
| `connection refused` | `ollama serve` で起動済みか、ポート 11434 が空いているか確認 |
| ダウンロードが遅い | プロキシ環境変数 `HTTPS_PROXY` を確認 |
| `out of memory` | より小型のモデル（例: `gemma4:e2b`）に切替 |
| 応答が遅い | GPU が使われているか `ollama ps` で確認 |

---

## 10. 次のステップ

実際に翻訳用モデルを取得します。

- 高品質モデル: `Gemma4_e4b_Install.md`
- 軽量モデル: `Gemma4_e2b_Install.md`
