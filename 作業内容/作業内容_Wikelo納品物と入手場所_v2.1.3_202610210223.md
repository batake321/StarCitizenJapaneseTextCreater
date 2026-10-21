# 作業内容: Wikelo の契約の納品物と入手場所（v2.1.3）

2026-10-20〜21。利用者の要望「Wikelo の契約は、納品物と数、その納品物のクリックで詳細にどこで入手できるかが欲しい」。

## 公開済み
- v2.1.3 を release.ps1 で公開（commit d10c6e2、機能本体は 7f186f0）。添付 13 件（アプリ zip・sc_japanese_backup.zip・update-manifest.json・.gz 7・DB の .patch 3）。
- 添付の DB は利用者がインデックスを作り直した `DB\sc_japanese_backup_20261021_001236.zip`（gamedata_cache.db・translations.db の 2 つだけ）。
- Discord の文は利用者に渡した（1 投稿）。
- この PC は v2.1.2 のまま。利用者が自動更新で自分で入れる。**v2.1.2 → v2.1.3 は自動更新が初めて実際に動く版で、まだ確かめていない。**

## 公開していない（main に commit 済み、次の版に入る）→ v2.1.4 で公開済み
- 契約の詳細と納品物の欄のあいだに左右の仕切り（GridSplitter）。初めは半分ずつ。隠すときは列の幅を 0 にし、動かした幅を覚えて戻す（MainWindow.xaml.cs SetMissionDeliverablesVisible）。

## 作りの要点
- 画面: 契約タブの詳細の右に `pnlMissionDeliverables`（表 dgMissionDeliverables・入手場所 txtMissionDeliverableSource）。対象は分類 wikelo_* か raw_json に `_TemplateHauling_` がある契約（導入の Intro・Lunch を含む 89 件）。
- 納品物: 契約側 `_Contract_.paramOverrides.propertyOverrides[]` の HaulingOverride と、テンプレート側 ContractTemplate の objectiveTokens[].objectiveHandler.haulingOrders[]（構築で raw_json の `_TemplateHauling_` に保存。TheCollector のテンプレートだけ）。Property が無いのに契約側にもある PolarisParts はテンプレート側を使う（推定）。数は minAmount、資源は minSCU の SCU。
- 入手場所: 店（item_shop_prices。店の購入が無いときだけ UEX）、資源の購入場所（trade_prices）、BP、他の契約の報酬、ゲームデータ（deliverable_sources 表）。
- deliverable_sources: 構築の Step 6c（GameDataExtractor.DeliverableSources.cs）。DCB を `dcb extract --filter` で 19 個に小分けして「書き出す→読む→消す」、Data.p4k の socpak を一時ファイルなしで読む。採掘・収穫・生き物（.ale）・配置・ルート（タグの評価、推定）・出現（推定）。実測 76〜120 秒。バックアップの IndexTables に入れた。
- 場所の名前: 星図の記録の英語名。引けないものは socpak の名前を翻訳のキーで引く（nyxsystem → Nyx System）。`<星系>_<名前>` の星図の記録（akirocluster → Pyro_AkiroCluster = Akiro Cluster）。施設は親の Entity の starmapRecord との組（QV Breaker Station など）。`<= UNINITIALIZED =>` などの訳は名前に数えない。
- `*_planet_only` / `*_planetonly`: 名前の頭から星（stanton1 → Hurston、stanton_2c_yela → Yela）、「/」の前のフォルダを場所（lorville → Stanton1_Lorville = Lorville。a18 は対応表で area18）。引けないフォルダは名前のまま（motel）。**利用者の決定: 出さない扱いは禁止、最悪でも内部名で出す。**

## 残っていること
- 場所の名前で内部名のまま出るのは「motel」（Yela の planetonly のフォルダ）だけ。
- Yormandi の舌は場所が出ない（生き物の出現の仕組みが辿れない）。RCMBNT 9 品は参照が無く「入手場所のデータなし」。
- 「場所まで辿れないもの」の節は件数と内部名の連なりの最初の 5 件を出す（利用者: 出さないのはだめ）。
- 取り消したとき、starbreaker の終了を待たずに作業フォルダを消すので残ることがある（次の構築で消える）。
- VS Code の C# 拡張が同じ obj で裏のビルドをするため、dotnet build が中間ファイルの無いエラーで落ちることがある。`dotnet build StarCitizenJapaneseTextCreater.csproj -nodeReuse:false -m:1` を繰り返すと通る。
- 確かめ用のテスター（構築の取り込みの段だけを DB の写しで走らせる）: scratchpad の impl\tester（今回のセッションの作業場所。次のセッションには無い）。走らせる前に利用者の許可を取ること。

## 2026-10-21 v2.1.4 を公開（1a9351c）
- 契約の詳細と納品物の欄の左右の仕切り（初めは半分ずつ）。**利用者が画面で確認済み。**
- 「motel」→「Grim HEX」: *_planet_only の「/」の前のフォルダで星図の記録が引けないときは、同じ並びの starmap のフォルダ（starmap/pu/station/motel/）の StarMapObject の名前を使う（SmoInFolder）。
- 構築の取り消し: starbreaker を Kill したあと最大 5 秒終わるのを待ってから作業フォルダを消す（WaitStarBreakerExitAsync）。
- Yormandi の舌は利用者の決定で「不明」のまま。RCMBNT 9 品はゲームデータに参照が無い。
- **v2.1.2 → v2.1.4 の自動更新は、利用者が手動で確認済み。**
- 公開の DB は利用者がインデックスを作り直した `DB\sc_japanese_backup_20261021_121751.zip`。構築の前に、写し（バックアップ 001236 ＋ Data.p4k から取り出した global.ini）で取り込みの段を走らせ、結果と構築の結果が 14,120 行すべて同じことを確かめた。

## 利用者に叱られたこと（次のエージェントへ）
- 出る名前を見ないまま構築を何度も頼み、丸一日以上かかった。構築を頼む前に、写しで 1 回走らせて画面に出る文字を全部見る。写しの実行も毎回許可を取る。
- 「1 回だけ」「最後」と約束して破った。回数の約束をしない。
- 不具合を「〜だけです」と軽く言わない。残さず直す。
