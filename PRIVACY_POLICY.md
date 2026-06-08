# プライバシーポリシー / Privacy Policy

最終更新日: 2026年6月8日

## 日本語

### はじめに

Star Citizen Japanese Text Creator（以下「本アプリ」）は、Star Citizen のゲームテキストを日本語に翻訳し、装備・トレード情報を管理するためのデスクトップアプリケーションです。本アプリは個人開発者 batake321 が提供しています。

### 収集する情報

本アプリは、ユーザーの個人情報を収集・送信しません。

すべてのデータはユーザーのローカルコンピュータ上に保存され、外部サーバーへの自動送信は行いません。ローカルに保存されるデータには以下が含まれます：

- 翻訳データ（英語テキストと日本語翻訳のペア）
- ゲームデータのキャッシュ（船舶、装備、ミッション情報）
- ユーザー設定（ゲームパス、作業ディレクトリ等）
- 所持船舶の管理データ
- 装備価格のキャッシュデータ

### 外部サービスとの通信

本アプリは以下の外部サービスと通信を行います。いずれもユーザーの操作に応じて実行され、自動的にバックグラウンドで送信されることはありません：

- **UEX Corp API** (api.uexcorp.space): 商品価格、装備価格、船舶データの取得
- **SC Trade Tools API** (sc-trade.tools): アイテム情報の取得
- **Star Citizen Wiki API**: ミッション情報の補完
- **AI翻訳サービス**（ユーザーが設定した場合のみ）: Claude (Anthropic)、OpenAI、Google Gemini、またはローカルの Ollama に翻訳リクエストを送信します。送信されるデータはゲームテキスト（英語）のみで、個人情報は含まれません。

### UEX 価格データの送信

価格キャプチャ機能を使用した場合、ユーザーの明示的な操作により、ゲーム内ターミナルの価格データを UEX Corp API に送信します。送信されるのはゲーム内の商品価格データのみです。

### Webサーバー機能

本アプリにはローカルWebサーバー機能があります。これはユーザーのローカルネットワーク内でのみ動作し、外部のインターネットには公開されません。

### データの保存場所

すべてのデータは以下のローカルディレクトリに保存されます：

- 設定ファイル: `%LOCALAPPDATA%\StarCitizenJapaneseTextCreater\`
- 作業データ: ユーザーが指定した作業ディレクトリ

### 第三者への提供

本アプリはユーザーのデータを第三者に販売、共有、または提供しません。

### 子どものプライバシー

本アプリは13歳未満の子どもを対象としておらず、意図的に子どもの個人情報を収集することはありません。

### 変更について

本プライバシーポリシーは必要に応じて更新される場合があります。重要な変更がある場合は、アプリのリリースノートでお知らせします。

### お問い合わせ

本プライバシーポリシーに関するご質問は、以下までご連絡ください：

- GitHub: https://github.com/batake321/StarCitizenJapaneseTextCreater/issues

---

## English

### Introduction

Star Citizen Japanese Text Creator ("the App") is a desktop application for translating Star Citizen game text into Japanese and managing equipment and trade information. The App is provided by individual developer batake321.

### Information We Collect

The App does not collect or transmit any personal information.

All data is stored locally on the user's computer and is never automatically sent to external servers. Locally stored data includes:

- Translation data (English text and Japanese translation pairs)
- Game data cache (ships, equipment, mission information)
- User settings (game path, working directory, etc.)
- Owned ship management data
- Equipment price cache data

### Communication with External Services

The App communicates with the following external services. All communications are initiated by user action and never occur automatically in the background:

- **UEX Corp API** (api.uexcorp.space): Retrieval of commodity prices, equipment prices, and ship data
- **SC Trade Tools API** (sc-trade.tools): Retrieval of item information
- **Star Citizen Wiki API**: Supplemental mission information
- **AI Translation Services** (only when configured by the user): Sends translation requests to Claude (Anthropic), OpenAI, Google Gemini, or local Ollama. Only game text (English) is sent; no personal information is included.

### UEX Price Data Submission

When using the price capture feature, game terminal price data may be submitted to the UEX Corp API through explicit user action. Only in-game commodity price data is submitted.

### Web Server Feature

The App includes a local web server feature. This operates only within the user's local network and is not exposed to the external internet.

### Data Storage Location

All data is stored in the following local directories:

- Settings: `%LOCALAPPDATA%\StarCitizenJapaneseTextCreater\`
- Working data: User-specified working directory

### Third-Party Sharing

The App does not sell, share, or provide user data to any third parties.

### Children's Privacy

The App is not directed at children under the age of 13 and does not intentionally collect personal information from children.

### Changes

This privacy policy may be updated as needed. Significant changes will be communicated through app release notes.

### Contact

For questions regarding this privacy policy, please contact:

- GitHub: https://github.com/batake321/StarCitizenJapaneseTextCreater/issues
