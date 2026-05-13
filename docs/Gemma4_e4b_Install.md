# Gemma 4 (e4b) インストール手順

Ollama 上で **`gemma4:e4b`** をセットアップする手順です。Star Citizen 日本語訳ソフトの「高品質ローカル翻訳バックエンド」として利用します。

> `gemma4:e4b` は `gemma4:latest` と同一エイリアスです。

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
