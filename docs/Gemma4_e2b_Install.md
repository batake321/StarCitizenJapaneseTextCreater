# Gemma (e2b) インストール手順

Ollama 上で **Gemma の e2b モデル** をセットアップする手順です。Star Citizen 日本語訳ソフトの「軽量・高速ローカル翻訳バックエンド」として利用します。

**モデル名について**: Google が公開している「実効パラメータ 2B」のオンデバイス向けモデルは **Gemma 3n** の `e2b` バリアントです。Ollama 上のタグ名は `gemma3n:e2b` となります（ユーザーが「Gemma4:e2b」と呼ぶ場合の実体）。最新タグは `ollama search gemma` で確認してください。

---

## 1\. 前提条件

- `Ollama_Install.md` の手順で Ollama がインストール・起動済みであること  
- 空きストレージ 3GB 以上  
- RAM 8GB 以上  
- GPU: VRAM 4GB 以上推奨（無くても CPU で快適に動作）

---

## 2\. e2b モデルの特徴

| 項目 | 内容 |
| :---- | :---- |
| 系列 | Gemma 3n（オンデバイス特化） |
| 実効サイズ | 約 2B パラメータ |
| ファイルサイズ | 約 2〜3GB（量子化済み） |
| コンテキスト長 | 32K トークン |
| 得意分野 | 短文翻訳、高速応答、低リソース動作 |
| 利点 | 低 VRAM、応答が非常に速い |
| 欠点 | 長文や複雑な文脈での品質は e4b に劣る |

Star Citizen の **UI ラベル、ボタン、短いシステムメッセージ** をリアルタイム翻訳する用途に最適です。

---

## 3\. ダウンロード

ollama pull gemma3n:e2b

完了後の確認：

ollama list

`gemma3n:e2b` が表示されていれば成功です。

---

## 4\. 動作確認

### 4-1. 対話で確認

ollama run gemma3n:e2b

\>\>\> 次の英文を日本語に翻訳: Welcome, Citizen.

`Ctrl + D` または `/bye` で終了。

### 4-2. API で確認

curl http://localhost:11434/api/chat \-d '{

  "model": "gemma3n:e2b",

  "messages": \[

    {"role": "user", "content": "Welcome, Citizen. を日本語に翻訳してください。"}

  \],

  "stream": false

}'

---

## 5\. 翻訳用プロンプト例（短文向け）

e2b は長い指示文に対する追従性が e4b より低めなので、プロンプトは**短く明確に**します。

SYSTEM\_PROMPT \= """英語を自然な日本語に翻訳します。日本語訳のみを出力。タグ・改行は保持。

固有名詞:

\- Stanton→スタントン / quantum drive→クォンタムドライブ / mobiGlas→モビグラス / UEC→UEC

"""

def build\_messages(src: str):

    return \[

        {"role": "system", "content": SYSTEM\_PROMPT},

        \# Few-shot で出力フォーマットを固定

        {"role": "user", "content": "Welcome, Citizen."},

        {"role": "assistant", "content": "ようこそ、シチズン。"},

        {"role": "user", "content": "Quantum drive online."},

        {"role": "assistant", "content": "クォンタムドライブ オンライン。"},

        {"role": "user", "content": src},

    \]

Python 実装：

from openai import OpenAI

client \= OpenAI(base\_url="http://localhost:11434/v1", api\_key="ollama")

def translate\_fast(text: str) \-\> str:

    resp \= client.chat.completions.create(

        model="gemma3n:e2b",

        messages=build\_messages(text),

        temperature=0.1,

    )

    return resp.choices\[0\].message.content.strip()

print(translate\_fast("Stand by for departure."))

---

## 6\. パフォーマンスチューニング

### 6-1. レイテンシ重視の設定

`Modelfile` でストリーミング短文向けにチューニング：

FROM gemma3n:e2b

PARAMETER num\_ctx 2048

PARAMETER temperature 0.1

PARAMETER top\_p 0.9

PARAMETER repeat\_penalty 1.1

PARAMETER num\_predict 256

ollama create starcitizen-gemma-e2b \-f Modelfile

UI 翻訳のような短文は `num_ctx` を絞ったほうがレスポンスが速くなります。

### 6-2. 並列処理

UI 文字列を一括翻訳するなら：

OLLAMA\_NUM\_PARALLEL=4 ollama serve

e2b は軽いので、ミドルレンジ GPU でも 3〜4 並列が現実的です。

### 6-3. キャッシュ戦略

Star Citizen のローカライゼーション文字列は同じ ID が繰り返し参照されるので、

ID → 英文ハッシュ → 日本語訳

の 2 段キャッシュを SQLite 等に持つと、実行時の API 呼び出しを 1 桁減らせます。

---

## 7\. e4b との使い分け（推奨運用）

| シナリオ | 推奨モデル |
| :---- | :---- |
| メニュー・ボタン・HUD（短文・即応） | **`gemma3n:e2b`** |
| ミッション説明・NPC 会話（中文） | `gemma3n:e4b` |
| ロア・専門用語密度の高い長文 | Claude / Gemini |

設定 UI 側で「翻訳エンジン: Auto / e2b / e4b / Claude / Gemini」と選べる設計にしておくと、ユーザー側のマシンスペックに応じて最適化できます。

---

## 8\. トラブルシューティング

| 症状 | 対処 |
| :---- | :---- |
| 訳が直訳調で固い | Few-shot 例を増やす、または e4b に切替 |
| 出力に英語の説明文が混じる | システムプロンプトに「日本語のみ出力」を強調 |
| 固有名詞が訳されてしまう | プロンプトの用語表を増強、温度を下げる |
| タグ（`{0}` 等）が消える | プロンプトに「タグはそのまま保持」を明記＋ Few-shot 提示 |
| CPU で遅い | `num_ctx` を下げる、量子化版タグを利用 |

---

## 9\. アンインストール

ollama rm gemma3n:e2b

---

## 10\. 推奨デプロイ構成（まとめ）

Star Citizen 日本語訳ソフトの典型的なルーティング：

入力英文

  │

  ├─ 短文 (≤30 chars) ──→ gemma3n:e2b      (ローカル・高速)

  │

  ├─ 中文 (≤500 chars) ─→ gemma3n:e4b      (ローカル・品質)

  │

  └─ 長文 / 重要文 ─────→ Claude or Gemini (クラウド・最高品質)

ユーザーがオフライン環境でもプレイできるよう、**ローカル LLM をデフォルト**にし、品質が必要な箇所のみクラウド API にフォールバックする設計を推奨します。  
