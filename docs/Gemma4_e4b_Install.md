# Gemma 4 (e4b) インストール手順

Ollama 上で **`gemma4:e4b`** をセットアップする手順です。Star Citizen 日本語訳ソフトの「高品質ローカル翻訳バックエンド」として利用します。

> `gemma4:e4b` は `gemma4:latest` と同一エイリアスです。

---

## 0. Star Citizen 翻訳ソフト「AI 設定」での使い方

本ソフトの **「AI 設定」ダイアログ** に、以下のように入力します。

### 0-1. 新規バックエンドとして追加する

1. 「AI 設定」を開く
2. 右下の **「バックエンド追加」** ボタンを押す → 空の枠が増える
3. 追加された枠に下記を入力

| 項目 | 入力値 |
|------|--------|
| 名前（左上のテキスト） | `LocalLLM_e4b`（任意。複数追加するなら識別できる名前にする） |
| チェックボックス | ✅ ON（有効化） |
| Type | **Ollama** |
| Model | **`gemma4:e4b`** |
| Base URL | **`http://127.0.0.1:11435`**（同一 PC で動かす場合） |
| Batch | 15 程度（GPU に余裕があれば増やす） |

> 名前の付け方の例: `Gemma4_e4b_Local` / `Gemma4_e4b_Remote` のように、モデルと場所が一目で分かる名前にしておくと、後で複数バックエンドを使い分ける際に便利です。

### 0-2. 複数バックエンドの併用（例: e2b と e4b を両方登録）

「バックエンド追加」を再度押せば、**同じ Ollama サーバー上の別モデルを別バックエンドとして登録**できます。たとえば短文用に e2b、長文用に e4b を切り替えたい場合：

| バックエンド名 | Model | Base URL | Batch | 用途 |
|----------------|-------|----------|-------|------|
| Gemma4_e2b | `gemma4:e2b` | `http://127.0.0.1:11435` | 40 | UI・短文 |
| Gemma4_e4b | `gemma4:e4b` | `http://127.0.0.1:11435` | 15 | ミッション・長文 |

Base URL が同じでも Model 名が違えば Ollama 側で別モデルとしてロードされます。

### 0-3. Base URL の設定パターン

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

Star Citizen の **ミッションテキスト、ロア、長文 NPC ダイアログ** の翻訳に向きます。マルチモーダル対応のため、**ゲーム画面のスクリーンショットから直接 UI 翻訳**を行う実装も可能です。

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

> Star Citizen のように **テキスト抽出が困難な独自 UI** に対しては、画面キャプチャ → e4b で OCR + 翻訳という流れが有効です。

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

## 6. パフォーマンスチューニング

### 6-1. GPU 利用状況の確認

```bash
ollama ps
```

`PROCESSOR` 列が `100% GPU` ならフル GPU。`xx% CPU/yy% GPU` は VRAM 不足で一部 CPU にオフロードされている状態。

### 6-2. 128K コンテキストの活用

e4b は 128K のコンテキスト長を持ちますが、デフォルトでは Ollama 側で短く設定されている場合があるため、`Modelfile` で明示します。

```Dockerfile
# Modelfile
FROM gemma4:e4b
PARAMETER num_ctx 16384
PARAMETER temperature 0.2
PARAMETER top_p 0.9
```

```bash
ollama create starcitizen-gemma4-e4b -f Modelfile
ollama run starcitizen-gemma4-e4b
```

> 注意: `num_ctx` を大きく取るほど VRAM/RAM 消費が増えます。翻訳用途なら 8192〜16384 で十分な場合が多いです。

### 6-3. 並列リクエスト

```bash
OLLAMA_NUM_PARALLEL=2 ollama serve
```

e4b（9.6GB）は重めなので、並列数は VRAM の余裕分だけにします。RTX 4070 (12GB) クラスで 1〜2 並列が目安です。

---

## 7. e2b との使い分け（推奨運用）

| シナリオ | 推奨モデル |
|----------|------------|
| UI ラベル・ボタン文言（短文・大量） | `gemma4:e2b` |
| ミッション説明・チャットログ（中〜長文） | **`gemma4:e4b`** |
| 画面キャプチャからの OCR + 翻訳 | **`gemma4:e4b`** |
| ロアテキスト・固有名詞密度の高い長文 | Claude / Gemini にフォールバック |
| ハイエンド GPU で更に高品質を狙う | `gemma4:26b` / `gemma4:31b` |

翻訳キャッシュ（原文ハッシュ→訳文）を併用すれば、同一原文の再翻訳コストはゼロになり、ローカルでも実用速度が出ます。

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

## 10. 上位モデルへの拡張

VRAM 24GB 以上の環境なら、さらに高品質な以下のタグも選択肢になります。

| タグ | サイズ | コンテキスト | 用途 |
|------|--------|--------------|------|
| `gemma4:26b` | 18GB | 256K | 大規模文書翻訳・高品質一括処理 |
| `gemma4:31b` | 20GB | 256K | さらに高品質、要 VRAM 24GB+ |
| `gemma4:31b-cloud` | — | 256K | Ollama Cloud 実行（ローカル GPU 不要） |

`gemma4:31b-cloud` を使えば、ローカル GPU が貧弱でも Ollama アカウント経由で 31B モデルを叩けます（要 Ollama Cloud 設定）。
