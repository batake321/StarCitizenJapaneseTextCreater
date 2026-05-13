# Gemma 4 (e2b) インストール手順

Ollama 上で **`gemma4:e2b`** をセットアップする手順です。Star Citizen 日本語訳ソフトの「軽量・高速ローカル翻訳バックエンド」として利用します。

---

## 0. Star Citizen 翻訳ソフト「AI 設定」での使い方

本ソフトの **「AI 設定」ダイアログ** で、デフォルトで用意されている **`LocalLLM`** 枠（Type: Ollama）に下記を入力します。

| 項目 | 入力値 |
|------|--------|
| Type | **Ollama** |
| Model | **`gemma4:e2b`** |
| Base URL | **`http://127.0.0.1:11435`**（同一 PC で動かす場合） |

チェックボックスを ON にして「保存」で適用されます。`Batch` は本ソフトのデフォルト値のままで問題ありません。

### バックエンド追加ボタンを使う場合

「バックエンド追加」は、**基本の `LocalLLM` 枠とは別に Ollama 接続先や AI サービスを追加したい時**に使います。主な用途は次の 2 つです。

- **追加の Ollama 接続を増やしたい**
  - 同じ PC に複数 GPU を載せていて、別ポートで Ollama を立てて使い分けたい
  - 別 PC（LAN 内の別マシン）に立てた Ollama にも接続したい
- **Ollama 以外の AI サービスを追加したい**
  - ChatGPT (OpenAI) などの API バックエンドを追加したい

追加した枠でも入力項目は同じです。Type を選び、Model（Ollama 系）または API Key（クラウド系）と Base URL を入れます。

### Base URL の設定パターン

| 構成 | Base URL の値 | 補足 |
|------|---------------|------|
| **同じ PC** で Ollama を動かす | `http://127.0.0.1:11435` | デフォルトはポート `11434`。本ソフトの初期値が `11435` の場合は Ollama 側のポート設定を合わせる（後述） |
| **別 PC**（LAN 内）で Ollama を動かす | `http://192.168.x.x:11434` | サーバー PC の IP アドレスに置換 |
| **別 PC**（VPN / 社内ネット） | `http://server.local:11434` | mDNS / 名前解決が効く環境 |
| **クラウド VPS** など | `https://your-domain.example.com` | リバースプロキシで HTTPS 化推奨 |

#### 同じ PC で使う（最も簡単）

何もしなくて OK。Ollama インストール直後の状態で `http://127.0.0.1:11434` でアクセス可能です。

> **ポート番号注意**: 本ソフトの画面では `11435` がデフォルトになっていますが、Ollama の標準ポートは `11434` です。どちらを使うか合わせてください。
> - 本ソフト側を `11434` に書き換える、もしくは
> - Ollama 側のポートを `11435` に変える（環境変数 `OLLAMA_HOST=127.0.0.1:11435` を設定して再起動）

#### 別 PC（LAN 内の別マシン）の Ollama を使う

ゲーミング PC で Star Citizen を動かしながら、サブ機やゲーミングノートに Ollama だけ動かして翻訳をオフロードする、という構成です。e2b は軽いので、CPU だけのサブ機でもそれなりに動きます。

**サーバー側 PC（Ollama を動かす方）の設定**

```bash
# 全インターフェースで待ち受けるよう環境変数を設定
# Windows (PowerShell, システム環境変数として設定推奨)
setx OLLAMA_HOST "0.0.0.0:11434"

# Linux / macOS
export OLLAMA_HOST=0.0.0.0:11434
```

設定後、Ollama を再起動。Windows ファイアウォールで **TCP 11434 番ポートの受信許可** を追加します。

サーバー PC の IP アドレスを確認：

```powershell
# Windows
ipconfig
```

```bash
# Linux / macOS
ip a   # または ifconfig
```

例: `192.168.1.50` だったとします。

**クライアント側（ゲームと本ソフトを動かす PC）の設定**

「AI 設定」の Base URL に以下を入力：

```
http://192.168.1.50:11434
```

接続テストはブラウザで `http://192.168.1.50:11434` を開いて `Ollama is running` と表示されれば OK です。

#### セキュリティ上の注意（別 PC 構成時）

- LAN 外（インターネット）に Ollama を直接公開しない（認証機構が無いため）
- 公開したい場合は **Nginx / Caddy 等のリバースプロキシ + Basic 認証 + HTTPS** を必ず噛ませる
- 家庭内 LAN 限定で使う場合でも、ルーターの **ポート開放（NAT/UPnP）はしない**

---

## 1. 前提条件

- `Ollama_Install.md` の手順で Ollama がインストール・起動済みであること
- 空きストレージ **8GB 以上**
- RAM **12GB 以上**推奨
- GPU: VRAM **8GB 以上**推奨（無くても CPU で動作可、e4b より軽快）

---

## 2. e2b モデルの特徴

| 項目 | 内容 |
|------|------|
| タグ | `gemma4:e2b` |
| 実効サイズ | 約 2B パラメータ |
| ファイルサイズ | **7.2GB** |
| コンテキスト長 | **128K トークン** |
| モダリティ | **Text + Image**（マルチモーダル） |
| 得意分野 | 短文翻訳、高速応答、低リソース動作 |
| 利点 | e4b より軽量、応答が速い、画像入力にも対応 |
| 欠点 | 長文や複雑な文脈での品質は e4b に劣る |

> e2b でも 7.2GB あるため、Gemma 3n の e2b（2〜3GB）と比べると重めです。VRAM 4GB クラスではフル GPU 動作は厳しく、一部 CPU へオフロードされます。

---

## 3. ダウンロード

```bash
ollama pull gemma4:e2b
```

完了確認：

```bash
ollama list
```

`gemma4:e2b` が表示されていれば成功です。

---

## 4. 動作確認

### 4-1. 対話で確認

```bash
ollama run gemma4:e2b
```

```
>>> 次の英文を日本語に翻訳: Welcome, Citizen.
```

`Ctrl + D` または `/bye` で終了。

### 4-2. テキスト API で確認

```bash
curl http://localhost:11434/api/chat -d '{
  "model": "gemma4:e2b",
  "messages": [
    {"role": "user", "content": "Welcome, Citizen. を日本語に翻訳してください。"}
  ],
  "stream": false
}'
```

### 4-3. 画像入力（マルチモーダル）

軽量モデルでも画像入力に対応しているため、簡易 OCR + 翻訳に利用可能です。

```python
import base64, requests

with open("hud.png", "rb") as f:
    img_b64 = base64.b64encode(f.read()).decode()

r = requests.post("http://localhost:11434/api/chat", json={
    "model": "gemma4:e2b",
    "messages": [{
        "role": "user",
        "content": "画像の英語UI文字列を抽出し、日本語訳を対応表で出力してください。",
        "images": [img_b64],
    }],
    "stream": False,
})
print(r.json()["message"]["content"])
```

---

## 5. 翻訳用プロンプト例（参考）

e2b は長い指示文への追従性が e4b より低めなので、**短く明確に**書きます。Few-shot を入れて出力形式を固定するのが効果的です。

```python
SYSTEM_PROMPT = """英語を自然な日本語に翻訳します。日本語訳のみを出力。タグ・改行は保持。

固有名詞:
- Stanton→スタントン / quantum drive→クォンタムドライブ / mobiGlas→モビグラス / UEC→UEC
"""

def build_messages(src: str):
    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        # Few-shot で出力フォーマットを固定
        {"role": "user", "content": "Welcome, Citizen."},
        {"role": "assistant", "content": "ようこそ、シチズン。"},
        {"role": "user", "content": "Quantum drive online."},
        {"role": "assistant", "content": "クォンタムドライブ オンライン。"},
        {"role": "user", "content": src},
    ]
```

Python 実装：

```python
from openai import OpenAI

client = OpenAI(base_url="http://localhost:11434/v1", api_key="ollama")

def translate_fast(text: str) -> str:
    resp = client.chat.completions.create(
        model="gemma4:e2b",
        messages=build_messages(text),
        temperature=0.1,
    )
    return resp.choices[0].message.content.strip()

print(translate_fast("Stand by for departure."))
```

---

## 6. Ollama 側のパラメータ（参考）

Ollama 側でモデルパラメータを変更したい場合は、`Modelfile` でカスタムモデルを作成できます。

```Dockerfile
# Modelfile
FROM gemma4:e2b
PARAMETER num_ctx 2048
PARAMETER temperature 0.1
PARAMETER top_p 0.9
```

```bash
ollama create my-gemma4-e2b -f Modelfile
```

各パラメータの意味は Ollama 公式ドキュメントを参照してください。

---

## 7. e4b との比較（参考）

| 項目 | `gemma4:e2b` | `gemma4:e4b` |
|------|--------------|--------------|
| ファイルサイズ | 7.2GB | 9.6GB |
| 実効パラメータ | 約 2B | 約 4B |
| 翻訳品質 | 軽量・速い | より高品質 |
| VRAM 要件 | 軽め | 重め |

どちらを `LocalLLM` の Model に指定するかは、ユーザーが GPU 性能と求める品質に応じて選択してください。

---

## 8. トラブルシューティング

| 症状 | 対処 |
|------|------|
| 訳が直訳調で固い | Few-shot 例を増やす、または e4b に切替 |
| 出力に英語の説明文が混じる | システムプロンプトに「日本語のみ出力」を強調 |
| 固有名詞が訳されてしまう | プロンプトの用語表を増強、温度を下げる |
| タグ（`{0}` 等）が消える | プロンプトに「タグはそのまま保持」を明記＋ Few-shot 提示 |
| CPU で遅い | `num_ctx` を下げる |
| 画像入力でエラー | base64 文字列に `data:image/png;base64,` プレフィックスを**付けない**こと |

---

## 9. アンインストール

```bash
ollama rm gemma4:e2b
```

---
