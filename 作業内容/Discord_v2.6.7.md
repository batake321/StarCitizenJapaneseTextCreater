**StarCitizenJapaneseTextCreater v2.6.7 を公開しました（2026-11-06 00:15）**
https://github.com/batake321/StarCitizenJapaneseTextCreater/releases/tag/v2.6.7
（アプリの自動更新からも入れられます）

**📍 現在位置の /showlocation を送り直さなくなりました**
座標が返らなかったときに、もう一度送るのをやめました（今までは、ボタンとキーで 1 回、自動の取得で 2 回まで送り直していました）。

**📦 更新のダウンロードが大きくなる不具合を直しました**
GitHub が混んでいるとき（504 など）に、小さな差分の代わりに 150 MB を超えるデータベースを丸ごと取りに行っていました。差分を取り直すようにしました。
