# Gemini API キー取得手順

Star Citizen 日本語訳ソフトで Google Gemini を翻訳エンジンとして利用するための API キー取得手順です。

---

## 1\. 前提条件

- Google アカウント（Gmail）が必要  
- クレジットカード（無料枠だけで運用する場合は不要）  
- ブラウザ（Chrome / Edge 推奨）

---

## 2\. API キーの取得手順

### 2-1. Google AI Studio にアクセス

ブラウザで以下にアクセスします。

https://aistudio.google.com/

Google アカウントでログインしてください。初回はサービス利用規約への同意画面が出ます。

### 2-2. API キーの発行

1. 画面左メニューの **「Get API key」** をクリック  
2. **「Create API key」** ボタンを押下  
3. 「Create API key in new project」を選択（既存プロジェクトを使う場合はそちらを選択）  
4. 数秒で `AIzaSy...` で始まる文字列が表示される

このキーをコピーして安全な場所に保管してください。**再表示は可能ですが、漏洩した場合は即削除して再発行**します。

---

## 3\. 料金プラン

| プラン | 内容 |
| :---- | :---- |
| Free Tier | レート制限あり、データは学習に使われる可能性あり |
| Pay-as-you-go | 課金設定後、レート制限緩和、データは学習に使われない |

Star Citizen の UI 翻訳程度であれば、まずは **Free Tier** で動作確認をするのがおすすめです。料金詳細：

https://ai.google.dev/pricing

---

## 4\. 利用モデルの選定（翻訳用途）

| モデル名 | 特徴 | 用途目安 |
| :---- | :---- | :---- |
| `gemini-2.5-flash` | 高速・低コスト | リアルタイム字幕・大量テキスト |
| `gemini-2.5-flash-lite` | 最速・最安 | UI ラベル等の短文 |
| `gemini-2.5-pro` | 高品質 | 長文・専門用語が多い箇所 |

Star Citizen は宇宙船・パイロット用語が多いため、UI 系は Flash、ミッションテキストは Pro、と使い分けると品質とコストのバランスが良いです。

---

## 5\. プロジェクトへの組み込み

`.env` ファイルに以下を記述します。

GEMINI\_API\_KEY=AIzaSy...（取得したキー）

GEMINI\_MODEL=gemini-2.5-flash

`.env` は **必ず `.gitignore` に追加** して、リポジトリにコミットしないでください。

Python 側の最小サンプル：

import os

import google.generativeai as genai

genai.configure(api\_key=os.environ\["GEMINI\_API\_KEY"\])

model \= genai.GenerativeModel(os.environ\["GEMINI\_MODEL"\])

resp \= model.generate\_content("次の英文を日本語に翻訳してください: Welcome, Citizen.")

print(resp.text)

---

## 6\. トラブルシューティング

| 症状 | 原因 / 対処 |
| :---- | :---- |
| `403 PERMISSION_DENIED` | API キー誤り、または該当 API 未有効化 |
| `429 RESOURCE_EXHAUSTED` | レート制限超過、リトライ間隔を空ける |
| `400 INVALID_ARGUMENT` | モデル名のスペルミス、入力長超過を確認 |

---

## 7\. セキュリティ上の注意

- API キーをソースコードに直書きしない  
- 公開リポジトリにキーを含む `.env` をコミットしない  
- 漏洩時は Google AI Studio の管理画面から **即時 Revoke**  
- 配布バイナリにキーを埋め込まない（ユーザー各自に取得してもらう設計が安全）

