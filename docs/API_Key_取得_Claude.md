# Claude API キー取得手順

Star Citizen 日本語訳ソフトで Anthropic Claude を翻訳エンジンとして利用するための API キー取得手順です。

---

## 1\. 前提条件

- メールアドレス（Google / GitHub ログインも可）  
- クレジットカード（API 利用には課金設定が必須。無料試用クレジットが配布される場合あり）  
- ブラウザ

**注意**: Claude.ai（チャット版）の有料プランと、API 利用の課金は**別物**です。API を使うには Anthropic Console での課金設定が必要です。

---

## 2\. アカウント作成

ブラウザで以下にアクセスします。

https://console.anthropic.com/

メール / Google / GitHub のいずれかでサインアップします。電話番号認証を求められる場合があります。

---

## 3\. 課金設定（Billing）

1. Console 左メニューの **「Settings」→「Billing」** へ  
2. **「Add payment method」** からクレジットカードを登録  
3. **「Add credits」** で初期チャージ（最低 $5 〜）

従量課金式（プリペイド）です。クレジットを使い切るとリクエストが拒否されるので、低残高アラートを設定しておくのを推奨します。

---

## 4\. API キーの発行

1. Console 左メニューの **「API Keys」** をクリック  
2. **「Create Key」** を押下  
3. キーに名前を付ける（例: `starcitizen-translator-dev`）  
4. 表示された `sk-ant-api03-...` で始まる文字列をコピー

**キーはこの画面でしか全文表示されません。** 閉じてしまうと再表示不可なので、必ず安全な場所に保存してください。紛失時は削除して再発行します。

---

## 5\. モデルの選定（翻訳用途）

| モデル | 特徴 | 翻訳用途の目安 |
| :---- | :---- | :---- |
| `claude-haiku-4-5-20251001` | 高速・低価格 | UI ラベル、字幕、リアルタイム翻訳 |
| `claude-sonnet-4-6` | バランス型 | ミッションテキスト、メニュー全般 |
| `claude-opus-4-6` | 最高品質 | ロア・専門用語が密な長文 |

Star Citizen 用途であれば、まずは **Haiku** で全体を試し、品質が足りない箇所だけ Sonnet にフォールバックする二段構成がコスト効率良好です。

---

## 6\. プロジェクトへの組み込み

`.env` に記載：

ANTHROPIC\_API\_KEY=sk-ant-api03-...

ANTHROPIC\_MODEL=claude-haiku-4-5-20251001

Python 最小サンプル：

import os

from anthropic import Anthropic

client \= Anthropic(api\_key=os.environ\["ANTHROPIC\_API\_KEY"\])

msg \= client.messages.create(

    model=os.environ\["ANTHROPIC\_MODEL"\],

    max\_tokens=1024,

    messages=\[

        {"role": "user", "content": "次の英文を自然な日本語に訳してください: Welcome, Citizen."}

    \],

)

print(msg.content\[0\].text)

依存パッケージ：

pip install anthropic

---

## 7\. レート制限と Usage Tier

Anthropic API は使用実績に応じて Tier が自動昇格し、レート上限（RPM / TPM）が拡張されます。

- **Tier 1**: 初期、$5 課金で開放  
- **Tier 2 以降**: 累計支払額と経過日数で昇格

大量翻訳バッチを走らせる場合は、Console の **「Limits」** から現在の上限を確認してください。

---

## 8\. トラブルシューティング

| 症状 | 原因 / 対処 |
| :---- | :---- |
| `401 authentication_error` | キー誤り / 失効 |
| `429 rate_limit_error` | RPM/TPM 超過、指数バックオフでリトライ |
| `400 invalid_request_error` | モデル名・パラメータを確認 |
| `529 overloaded_error` | 一時的過負荷、数秒後リトライ |

---

## 9\. セキュリティ上の注意

- API キーをソースコードに直書きしない  
- 配布バイナリにキーを埋め込まない（ユーザー各自に取得してもらう設計が安全）  
- 漏洩時は Console から **即時 Revoke** → 新規発行  
- 用途別にキーを分け（dev / prod）、ログに出力しない

