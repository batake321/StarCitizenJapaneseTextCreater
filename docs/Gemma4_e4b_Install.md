# Gemma (e4b) インストール手順

Ollama 上で **Gemma の e4b モデル** をセットアップする手順です。Star Citizen 日本語訳ソフトの「高品質ローカル翻訳バックエンド」として利用します。

**モデル名について**: Google が公開している「実効パラメータ 4B」のオンデバイス向けモデルは **Gemma 3n** の `e4b` バリアントです。Ollama 上のタグ名は `gemma3n:e4b` となります（ユーザーが「Gemma4:e4b」と呼ぶ場合の実体）。最新タグは `ollama search gemma` で確認してください。

---

## 1\. 前提条件

- `Ollama_Install.md` の手順で Ollama がインストール・起動済みであること  
- 空きストレージ 7GB 以上  
- RAM 16GB 以上推奨  
- GPU: VRAM 8GB 以上推奨（無くても CPU で動作可、ただし応答は遅め）

---

## 2\. e4b モデルの特徴

| 項目 | 内容 |
| :---- | :---- |
| 系列 | Gemma 3n（オンデバイス特化） |
| 実効サイズ | 約 4B パラメータ |
| ファイルサイズ | 約 5〜7GB（量子化済み） |
| コンテキスト長 | 32K トークン |
| 得意分野 | 多言語翻訳、長文の文脈保持、一般会話 |
| 利点 | e2b より翻訳品質が高い |
| 欠点 | VRAM 消費・推論時間が増える |

Star Citizen の **ミッションテキストやロアの長文** を翻訳する用途に向いています。

---

## 3\. ダウンロード

ollama pull gemma3n:e4b

進捗バーが完了したら：

ollama list

`gemma3n:e4b` が表示されていれば成功です。

---

## 4\. 動作確認

### 4-1. 対話で確認

ollama run gemma3n:e4b

\>\>\> 次の英文を自然な日本語に翻訳してください: Welcome, Citizen. Stand by for departure.

`Ctrl + D` または `/bye` で終了。

### 4-2. API で確認

curl http://localhost:11434/api/chat \-d '{

  "model": "gemma3n:e4b",

  "messages": \[

    {"role": "user", "content": "Welcome, Citizen. を自然な日本語に翻訳してください。"}

  \],

  "stream": false

}'

---

## 5\. 翻訳用プロンプト例（Star Citizen 用）

ゲーム固有用語が多いため、システムプロンプトで用語を固定すると安定します。

SYSTEM\_PROMPT \= """あなたは Star Citizen の英日翻訳エンジンです。以下のルールに従って翻訳してください。

\# ルール

\- 出力は日本語訳のみ。前置き・解説・コードブロックは禁止。

\- 改行とタグ（{0}, %s, \<color="\#fff"\> 等）は原文どおり保持。

\- ゲーム内固有名詞は原則カタカナまたは英語そのまま：

  \- Stanton → スタントン

  \- quantum drive → クォンタムドライブ

  \- mobiGlas → モビグラス

  \- UEC → UEC（そのまま）

\- 命令口調・パイロット用語は自然な日本語に。

"""

USER\_TEMPLATE \= "次の英文を上記ルールで翻訳してください:\\n\\n{src}"

Python 実装：

from openai import OpenAI

client \= OpenAI(base\_url="http://localhost:11434/v1", api\_key="ollama")

def translate(text: str) \-\> str:

    resp \= client.chat.completions.create(

        model="gemma3n:e4b",

        messages=\[

            {"role": "system", "content": SYSTEM\_PROMPT},

            {"role": "user", "content": USER\_TEMPLATE.format(src=text)},

        \],

        temperature=0.2,

    )

    return resp.choices\[0\].message.content.strip()

print(translate("Welcome, Citizen. Stand by for departure."))

---

## 6\. パフォーマンスチューニング

### 6-1. GPU 利用状況の確認

ollama ps

`PROCESSOR` 列が `100% GPU` ならフル GPU、`30%/70% CPU/GPU` などは VRAM 不足で一部 CPU にオフロードされています。

### 6-2. コンテキスト長の調整

長い字幕ブロックを扱うなら、Modelfile で `num_ctx` を上げます。

\# Modelfile

FROM gemma3n:e4b

PARAMETER num\_ctx 8192

PARAMETER temperature 0.2

PARAMETER top\_p 0.9

ollama create starcitizen-gemma-e4b \-f Modelfile

ollama run starcitizen-gemma-e4b

以後 `starcitizen-gemma-e4b` という名前で呼べます。

### 6-3. 並列リクエスト

環境変数で同時実行数を制御：

OLLAMA\_NUM\_PARALLEL=2 ollama serve

VRAM に応じて 1〜4 程度が現実的です。

---

## 7\. e2b との使い分け（推奨運用）

| シナリオ | 推奨モデル |
| :---- | :---- |
| UI ラベル・ボタン文言（短文・大量） | `gemma3n:e2b` |
| ミッション説明・チャットログ（中〜長文） | `gemma3n:e4b` |
| ロアテキスト・固有名詞密度の高い長文 | Claude / Gemini にフォールバック |

翻訳キャッシュを噛ませて同じ原文には API を呼ばない設計にすると、ローカルでも実用速度が出ます。

---

## 8\. トラブルシューティング

| 症状 | 対処 |
| :---- | :---- |
| `out of memory` | `gemma3n:e2b` に切替、または量子化レベルを下げたタグを使用 |
| 翻訳結果に英語の解説が混じる | システムプロンプトを強化、`temperature` を下げる |
| タグ・改行が壊れる | プロンプトで「タグ保持」を明示、Few-shot 例を追加 |
| 応答が極端に遅い | GPU が使われているか `ollama ps` で確認 |

---

## 9\. アンインストール

ollama rm gemma3n:e4b

モデルファイル自体は `.ollama/models` 以下に保存されるため、上記コマンドで完全に削除されます。  
