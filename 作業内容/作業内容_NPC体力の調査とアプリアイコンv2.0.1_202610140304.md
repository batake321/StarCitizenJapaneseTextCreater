# 作業内容: NPC の体力の調査と、アプリのアイコン（v2.0.1）（2026-10-13〜14）

v2.0.1 として公開済み（コミット 611dd43）。

## 1. NPC の体力の調査（調査のみ。コードは変更していない）

### 目的
ゲームデータに、強い NPC の体力が入っているかを確かめる。

### アプリの DB に入っているもの
- `player_damage_params` に入っているのはプレイヤーの値だけ。
  - 取り込むコードは `GameDataExtractor.cs` の `ExtractPlayerDamageParamsAsync`。
  - プレイヤー用の record（`PlayerStatusPU`、`HealthConfig_Player`）だけを選んで取り込む作りになっている。
- アーマーのダメージ倍率は全件入っている。AI 専用の `HeavyArmor_ai_exclusive`（0.25）もある。

### Data.p4k から starbreaker の dcb query で確かめた値（ゲームデータ 2026-10-09）
NPC の体力は `ActorStatusComponent` の record ごとに違う（HealthPool の maximumStatValue）。

| record | 体力 | 主な NPC |
|---|---|---|
| PlayerStatusAI | 100 | 普通の敵（大半） |
| PlayerStatusAI_captain | 185 | ASD のエリート |
| PlayerStatusAI_Juggernaut | 235 | 各勢力の juggernaut |
| PlayerStatusAI_Elite | 250 | Nine Tails・Salamanders の _Elite |
| PlayerStatus_AIBoss | 280 | Nine Tails・Crusader などのボス |
| PlayerStatusAI_Juggernaut_Elite | 400 | Ninetails_juggernaut_Elite |
| PlayerStatus_AIBossSuper | 560 | Nine Tails の名前付きボス、Kareah のボス |
| PlayerStatusAI_Vanduul | 275 | Vanduul_Pilot_01 |
| PlayerStatusCreature | 75 | Kopion など |
| PlayerStatusAI_Vlk_* | 110 / 550 / 400000 | Valakkar の子ども・大人・Apex |
| PlayerStatusAI_Yormandi | 3355 | Yormandi |

- 部位倍率は NPC 用の `HealthConfig_AI` が胴 1.8・頭 2.4（プレイヤーは胴 1.0・頭 1.5）。Vanduul は胴 4.0・頭 12.0。
- 倍率が大きいほど受けるダメージが増えるのかは、計算式を確かめていない（未確認）。
- どの NPC がどのアーマーを着ているかは、装備の XML（`Scripts/Loadouts/...`）に書かれていて DCB に無いため、未確認。
  - 調べるには p4k から装備の XML を取り出す必要がある。利用者の判断待ちのまま。
- 調べた出力は、そのセッションの scratchpad にだけ置いた（リポジトリには入れていない）。

## 2. アプリのアイコン（v2.0.1）

### 決まった形
- 緑の盾の部隊章に、金の縁。
- ライフル 2 丁を、銃口を上・トリガーを内側にして交差。色は黒で、細い赤の縁取り。
- 中央に、白い斜めの輪を巻いた青い惑星（ライフルの奥）。
- 惑星の上に赤い「SCJTC」の文字（惑星とライフルの奥）。

### 決まるまで
1. 星とライフルの 6 案を出したあと、交差ライフルの案（C）に絞られた。
2. 以後の案は、すべて「交差・銃口上・トリガー内側・中央に輪のある惑星・星マーク無し」の条件で作ることになった。
   - 「C 案以外はやり直し」で条件の外の案を出してしまい、指摘を受けた。
3. 盾の形・黒いライフル・縁取りを赤・文字を赤の順に決まった。

### 作り方
- SVG で描き、Edge のヘッドレス（画面に出さない）で透明背景の PNG に書き出した。
- 16・24・32・48・64・128・256px の 7 サイズを PNG のまま 1 つの ICO にまとめた。
- 元の SVG と書き出しの手順はセッションの scratchpad にあり、リポジトリには入れていない。

### 変更したファイル
- `app.ico`: 新しいアイコンに置き換え。
- `StarCitizenJapaneseTextCreater.csproj`: `Version` を 2.0.1 に。`<ApplicationIcon>app.ico</ApplicationIcon>` を追加。
  - それまで exe にはアイコンが入っておらず、app.ico はインストーラー（`installer.iss` の SetupIconFile）でだけ使われていた。
- `README.md`: ダウンロードのリンクとインストーラー名を v2.0.1 に。「v2.0.1 更新内容」を追加し、v2.0.0 の内容は折りたたみに移した。
- `docs/index.html`: インストーラー名を v2.0.1 に。
- 翻訳データベースの zip は v2.0.0 から変えていないので、リンクは v2.0.0 のまま。v2.0.1 のリリースには添付していない。

### 確かめたこと
- ビルド: エラー 0。
- ビルドした exe からアイコンを取り出して、新しいアイコンになっていることを見た。製品バージョンは 2.0.1。
- `release.ps1` は終了コード 0 で最後まで通った（インストーラー、zip、commit 611dd43 の push、GitHub のリリースと zip の添付）。

### 確かめていないこと
- アプリを起動したときのタイトルバーとタスクバーのアイコン。
  - WPF の画面にアイコンを個別に指定していないので、exe のアイコンが使われるはず。ただし推測。
