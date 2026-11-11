# Gemma 4 (e4b) インストール手順

Ollama 上で **`gemma4:e4b`** をセットアップする手順です。Star Citizen 日本語訳ソフトの「高品質ローカル翻訳バックエンド」として利用します。

> `gemma4:e4b` は `gemma4:latest` と同一エイリアスです。

---

## 0. Star Citizen 翻訳ソフト「AI 設定」での使い方

本ソフトの **「AI 設定」ダイアログ** で、デフォルトで用意されている **`LocalLLM`** 枠（Type: Ollama）に下記を入力します。

| 項目 | 入力値 |
|------|--------|
| Type | **Ollama** |
| Model | **`gemma4:e4b`** |
| Base URL | **`http://127.0.0.1:11434`**（同一 PC で動かす場合） |

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
| **同じ PC** で Ollama を動かす | `http://127.0.0.1:11434` | Ollama の標準ポート |
| **別 PC**（LAN 内）で Ollama を動かす | `http://192.168.x.x:11434` | サーバー PC の IP アドレスに置換 |
| **別 PC**（VPN / 社内ネット） | `http://server.local:11434` | mDNS / 名前解決が効く環境 |
| **クラウド VPS** など | `https://your-domain.example.com` | リバースプロキシで HTTPS 化推奨 |

#### 同じ PC で使う（最も簡単）

何もしなくて OK。Ollama インストール直後の状態で `http://127.0.0.1:11434` でアクセス可能です。

#### 別 PC（LAN 内の別マシン）の Ollama を使う

GPU が強いデスクトップに Ollama を立て、ノート PC からゲームを動かしながら翻訳だけ別 PC に投げる、という構成です。

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
- 空きストレージ **10GB 以上**
- RAM **16GB 以上**推奨
- GPU: VRAM **12GB 以上**推奨（無くても CPU 動作可、ただし応答は遅い）

---

## 2. e4b モデルの特徴

| 項目 | 内容 |
|------|------|
| タグ | `gemma4:e4b`（= `gemma4:latest`） |
| 実効サイズ | 約 4B パラメータ |
| ファイルサイズ | **9.6GB** |
| コンテキスト長 | **128K トークン** |
| モダリティ | **Text + Image**（マルチモーダル） |
| 得意分野 | 多言語翻訳、長文の文脈保持、画像中の文字理解 |
| 利点 | e2b より翻訳品質が高い、画像入力も扱える |
| 欠点 | e2b より VRAM 消費・推論時間が増える |

---

## 3. ダウンロード

```bash
ollama pull gemma4:e4b
```

完了確認：

```bash
ollama list
```

`gemma4:e4b` が表示されていれば成功です。

---

## 4. 動作確認

### 4-1. 対話で確認

```bash
ollama run gemma4:e4b
```

```
>>> 次の英文を自然な日本語に翻訳してください: Welcome, Citizen. Stand by for departure.
```

`Ctrl + D` または `/bye` で終了。

### 4-2. テキスト API で確認

```bash
curl http://localhost:11434/api/chat -d '{
  "model": "gemma4:e4b",
  "messages": [
    {"role": "user", "content": "Welcome, Citizen. を自然な日本語に翻訳してください。"}
  ],
  "stream": false
}'
```

### 4-3. 画像入力 API で確認（マルチモーダル）

スクリーンショットを base64 で渡せます。

```python
import base64, requests

with open("screenshot.png", "rb") as f:
    img_b64 = base64.b64encode(f.read()).decode()

r = requests.post("http://localhost:11434/api/chat", json={
    "model": "gemma4:e4b",
    "messages": [{
        "role": "user",
        "content": "この画像に映っている英語の UI 文字列を抽出し、日本語訳と対応表を返してください。",
        "images": [img_b64],
    }],
    "stream": False,
})
print(r.json()["message"]["content"])
```

---

## 5. 翻訳用プロンプト例（Star Citizen 用）

```python
SYSTEM_PROMPT = """あなたは Star Citizen の英日翻訳エンジンです。以下のルールに従って翻訳してください。

# ルール
- 出力は日本語訳のみ。前置き・解説・コードブロックは禁止。
- 改行とタグ（{0}, %s, <color="#fff"> 等）は原文どおり保持。
- ゲーム内固有名詞は原則カタカナまたは英語そのまま：
  - Stanton → スタントン
  - quantum drive → クォンタムドライブ
  - mobiGlas → モビグラス
  - UEC → UEC（そのまま）
- 命令口調・パイロット用語は自然な日本語に。
"""

USER_TEMPLATE = "次の英文を上記ルールで翻訳してください:\n\n{src}"
```

Python 実装：

```python
from openai import OpenAI

client = OpenAI(base_url="http://localhost:11434/v1", api_key="ollama")

def translate(text: str) -> str:
    resp = client.chat.completions.create(
        model="gemma4:e4b",
        messages=[
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": USER_TEMPLATE.format(src=text)},
        ],
        temperature=0.2,
    )
    return resp.choices[0].message.content.strip()

print(translate("Welcome, Citizen. Stand by for departure."))
```

---

## 6. Ollama 側のパラメータ（参考）

### 6-1. GPU 利用状況の確認

```bash
ollama ps
```

`PROCESSOR` 列が `100% GPU` ならフル GPU。`xx% CPU/yy% GPU` は VRAM 不足で一部 CPU にオフロードされている状態。

### 6-2. カスタムモデルでのパラメータ変更

Ollama 側でモデルパラメータを変更したい場合は、`Modelfile` でカスタムモデルを作成できます。

```Dockerfile
# Modelfile
FROM gemma4:e4b
PARAMETER num_ctx 16384
PARAMETER temperature 0.2
PARAMETER top_p 0.9
```

```bash
ollama create my-gemma4-e4b -f Modelfile
```

> 注意: `num_ctx` を大きく取るほど VRAM/RAM 消費が増えます。各パラメータの意味は Ollama 公式ドキュメントを参照してください。

---

## 7. e2b との比較（参考）

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
| `out of memory` | `gemma4:e2b` に切替、または `num_ctx` を下げる |
| 翻訳結果に英語の解説が混じる | システムプロンプトを強化、`temperature` を下げる |
| タグ・改行が壊れる | プロンプトで「タグ保持」を明示、Few-shot 例を追加 |
| 応答が極端に遅い | GPU が使われているか `ollama ps` で確認 |
| 画像入力でエラー | base64 文字列に `data:image/png;base64,` プレフィックスを**付けない**こと |

---

## 9. アンインストール

```bash
ollama rm gemma4:e4b
```

---

## 10. その他の Gemma 4 タグ（参考）

Ollama ライブラリには e2b / e4b 以外にも以下のタグが存在します。VRAM や用途に応じて選択肢になります。

| タグ | サイズ | コンテキスト |
|------|--------|--------------|
| `gemma4:26b` | 18GB | 256K |
| `gemma4:31b` | 20GB | 256K |
| `gemma4:31b-cloud` | — | 256K |

`gemma4:31b-cloud` は Ollama Cloud で実行するタグです（ローカル GPU 不要、要 Ollama Cloud 設定）。
