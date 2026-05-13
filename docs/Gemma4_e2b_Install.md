# Gemma 4 (e2b) インストール手順

Ollama 上で **`gemma4:e2b`** をセットアップする手順です。Star Citizen 日本語訳ソフトの「軽量・高速ローカル翻訳バックエンド」として利用します。

---

## 0. Star Citizen 翻訳ソフト「AI 設定」での使い方

本ソフトの **「AI 設定」ダイアログ** に、以下のように入力します。

### 0-1. 新規バックエンドとして追加する

1. 「AI 設定」を開く
2. 右下の **「バックエンド追加」** ボタンを押す → 空の枠が増える
3. 追加された枠に下記を入力

| 項目 | 入力値 |
|------|--------|
| 名前（左上のテキスト） | `LocalLLM_e2b`（任意。複数追加するなら識別できる名前にする） |
| チェックボックス | ✅ ON（有効化） |
| Type | **Ollama** |
| Model | **`gemma4:e2b`** |
| Base URL | **`http://127.0.0.1:11435`**（同一 PC で動かす場合） |
| Batch | 40 程度（e2b は軽いので並列を増やせる） |

> 名前の付け方の例: `Gemma4_e2b_Local` / `Gemma4_e2b_Remote` のように、モデルと場所が一目で分かる名前にしておくと、後で複数バックエンドを使い分ける際に便利です。

### 0-2. 複数バックエンドの併用（推奨: e2b と e4b を両方登録）

e2b は短文向け、e4b は長文向けと役割を分けるのが本ソフトの推奨構成です。**「バックエンド追加」** で 2 つ登録しておきます。

| バックエンド名 | Model | Base URL | Batch | 用途 |
|----------------|-------|----------|-------|------|
| Gemma4_e2b | **`gemma4:e2b`** | `http://127.0.0.1:11435` | 40 | UI ラベル・HUD・短文（高速） |
| Gemma4_e4b | `gemma4:e4b` | `http://127.0.0.1:11435` | 15 | ミッション・NPC 会話・長文（高品質） |

Base URL が同じでも Model 名が違えば Ollama 側で別モデルとしてロード／使い分けされます。本ソフトの翻訳ルーティング機能（短文/長文振り分け）と組み合わせると、品質と速度のバランスが最適化されます。

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

Star Citizen の **UI ラベル、ボタン、HUD、短いシステムメッセージ** をリアルタイム翻訳する用途に最適です。

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

> ただし精度は e4b の方が高いので、**精度重視は e4b・速度重視は e2b** と使い分けます。

---

## 5. 翻訳用プロンプト例（短文向け）

e2b は長い指示文への追従性が e4b より低めなので、**短く明確に**書きます。Few-shot を入れて出力形式を固定するのが効果的。

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

## 6. パフォーマンスチューニング

### 6-1. レイテンシ重視の設定

UI 翻訳のような短文用途では、コンテキスト長を絞った方がレスポンスが速くなります。

```Dockerfile
# Modelfile
FROM gemma4:e2b
PARAMETER num_ctx 2048
PARAMETER temperature 0.1
PARAMETER top_p 0.9
PARAMETER repeat_penalty 1.1
PARAMETER num_predict 256
```

```bash
ollama create starcitizen-gemma4-e2b -f Modelfile
```

### 6-2. 並列処理

e4b より軽いので並列を増やせます。

```bash
OLLAMA_NUM_PARALLEL=4 ollama serve
```

UI 文字列の一括翻訳バッチで効率が上がります。

### 6-3. キャッシュ戦略

Star Citizen のローカライゼーション文字列は同じ ID が繰り返し参照されるので、

```
ID → 英文ハッシュ → 日本語訳
```

の 2 段キャッシュを SQLite 等に持たせると、実行時の API/LLM 呼び出しを 1 桁減らせます。

---

## 7. e4b との使い分け（推奨運用）

| シナリオ | 推奨モデル |
|----------|------------|
| メニュー・ボタン・HUD（短文・即応） | **`gemma4:e2b`** |
| ミッション説明・NPC 会話（中文） | `gemma4:e4b` |
| 画面キャプチャ OCR + 翻訳 | `gemma4:e4b` |
| ロア・専門用語密度の高い長文 | Claude / Gemini |
| ハイエンド GPU 環境 | `gemma4:26b` / `gemma4:31b` |

設定 UI 側で「翻訳エンジン: Auto / e2b / e4b / 26b / 31b / Claude / Gemini」と選べる設計にしておくと、ユーザー側のマシンスペックに応じて柔軟に最適化できます。

---

## 8. トラブルシューティング

| 症状 | 対処 |
|------|------|
| 訳が直訳調で固い | Few-shot 例を増やす、または e4b に切替 |
| 出力に英語の説明文が混じる | システムプロンプトに「日本語のみ出力」を強調 |
| 固有名詞が訳されてしまう | プロンプトの用語表を増強、温度を下げる |
| タグ（`{0}` 等）が消える | プロンプトに「タグはそのまま保持」を明記＋ Few-shot 提示 |
| CPU で遅い | `num_ctx` を下げる、それでも遅ければ Gemma 3 系の小型モデルへ |
| 画像入力でエラー | base64 文字列に `data:image/png;base64,` プレフィックスを**付けない**こと |

---

## 9. アンインストール

```bash
ollama rm gemma4:e2b
```

---

## 10. 推奨デプロイ構成（まとめ）

Star Citizen 日本語訳ソフトの典型的なルーティング：

```
入力英文
  │
  ├─ 短文 (≤30 chars) ──→ gemma4:e2b      (ローカル・高速・7.2GB)
  │
  ├─ 中文 (≤500 chars) ─→ gemma4:e4b      (ローカル・品質・9.6GB)
  │
  ├─ 画面キャプチャ ─────→ gemma4:e4b      (マルチモーダル OCR + 翻訳)
  │
  └─ 長文 / 重要文 ─────→ Claude or Gemini (クラウド・最高品質)
```

ユーザーがオフライン環境でもプレイできるよう、**ローカル LLM をデフォルト**にし、品質が必要な箇所だけクラウド API にフォールバックする設計を推奨します。
