// ステーションの地図の新しい方式のビューア (view3d.html)。試験の頁 (scratchpad の orison_perf\view\test8.html) の移植。
// データ (2026-11-04、利用者の決定「Data.p4k から見る所の区域だけを、開いたときにその場で取り出して描く（前もってデータを作らない）」):
//   ?data=<一時の置き場の URL (最後に /)> の station.json (案内するもの・乗り場・前の地図の階の名前。StationView3D.Session.StationJson)。
//   地点は、アプリ (StationView3DWindow) に {type:'region', id, c, r} を送り、まわり ±r m の区画をその場で取り出して作ってもらう。
//   出来たら ({type:'regionReady', base}) base の region.json と g/<番号>.bin を読み、読み終えたら {type:'regionLoaded'} を送る (アプリが消す)。
//   形は、地点を丸ごとではなく「升のかたまり」ごとに出し入れする (2026-11-06 の作り替え。下の「当たり」「升のかたまりの出し入れ」)
//   アプリの外で試すときは ?region=<地点の置き場の URL> (前もって作った地点。その地点だけ)
import * as THREE from 'three';
const report = (where, e) => { try { window.chrome?.webview?.postMessage({ type: 'error', source: 'station3d', message: `[新しい方式] ${where}: ${e && e.stack || e}` }); } catch (_) { } console.error(where, e); };
addEventListener('error', e => report('window', e.error || e.message));
addEventListener('unhandledrejection', e => report('promise', e.reason));
const qs = new URLSearchParams(location.search);
const BASE = qs.get('data') || '';
const APP = !!(window.chrome && window.chrome.webview);
// ?debug=1: アプリの設定「マップのデバッグ表示」(App.Config.Station3DDebugView。既定オフ) が入っているとき (StationView3DWindow が付ける)。
// 入っていなければ、下部の枠 (#pick。押した部品の名前・判定の面・押した所の高さ) を出さない (2026-11-05、利用者の指示「この3D の下部の細かいテキストも設定でデフォルトでオフのマップのデバッグ表示という設定を付けてほしい」)
const DEBUG = qs.get('debug') === '1';
document.body.classList.toggle('dbg', DEBUG);
// アプリからの知らせ (地点の進み具合・出来た・失敗)
const regionWait = new Map(); let regionSeq = 0;
if (APP) window.chrome.webview.addEventListener('message', e => {
  const d = e.data || {};
  // マイハンガーの用意ができた・できなかった ({type:'hangarReady', site, data, title, how}・{type:'hangarError', message})、販売品 ({type:'sales', title, shops})
  if (d.type === 'hangarReady' || d.type === 'hangarError') { const hw = hangarWait; hangarWait = null; if (hw) { if (d.type === 'hangarReady') hw.ok(d); else hw.ng(new Error(d.message)); } return; }
  if (d.type === 'sales') { showSales(d); return; }
  if (d.type === 'prewarm') { prewarmStart(d); return; }
  if (d.type === 'prewarmCancel') { prewarmStop(); return; }
  if (d.type === 'here') { onHere(d); return; }
  if (d.type === 'hereMark') { onHereMark(d); return; }   // 現在位置の印 (下の「現在位置の印」)
  if (d.type === 'walkFill') { onWalkFill(d); return; }
  if (d.type === 'notes') { onNotes(d); return; }
  if (d.type === 'noteAdded') { onNoteAdded(d); return; }
  if (d.type === 'exterior') { onExterior(d); return; }   // 外から見たときの印 (着陸パッド・ハンガー・ドッキングポート。下の「外から見たときの印と見出し」)
  if (d.type === 'exteriorView') { onExteriorView(); return; }   // 外から全体が見える形にする (下の「外から見る形」。2026-11-06)
  // マイハンガーの大きさのうち、ハンガーの区画が決まるもの (アプリの StationHangarCatalog.Resolve)。本体の station.json に覚え、行き先の窓はこの大きさだけを出す (hangarSizesOf)
  if (d.type === 'hangarSizes') { const sv = stationSaved ? stationSaved.VIEW : VIEW; if (Array.isArray(d.sizes)) { if (sv) sv.hangarSizesOk = d.sizes; else pendingSizes = d.sizes; } return; }   // 場所のデータ (VIEW) が入る前なら覚えて、入った後に当てる
  if (d.type === 'bigReady' || d.type === 'bigError') { onBig(d); return; }   // 場所の全部の大きな物体 (下の「場所の全部の大きな物体」)
  if (d.type === 'noteError') { document.getElementById('msg').textContent = 'コメントの印を保存できませんでした: ' + (d.message || ''); return; }
  const w = regionWait.get(d.id); if (!w) return;
  if (d.type === 'regionProgress') showProgress(d.done, d.total);
  else if (d.type === 'regionReady') { regionWait.delete(d.id); w.ok(d); }
  else if (d.type === 'regionError') { regionWait.delete(d.id); w.ng(new Error(d.message)); }
});
// 取り出し中の表示 (2026-11-04、利用者の指示「取り出し中はもっと大きく」): 画面の中央に大きな文字と、進み具合のバー
function showProgress(done, total, what = '取り出し中') {
  const box = document.getElementById('prog'); box.style.display = 'block';
  if (box.classList.contains('small')) what = '残りの' + what;
  document.getElementById('progText').textContent = total ? `${what}（${total} 個中 ${done} 個）` : `${what}…`;
  document.getElementById('progBar').style.width = total ? (100 * done / total).toFixed(1) + '%' : '0%';
}
function hideProgress() { document.getElementById('prog').style.display = 'none'; }
// 地点 (中心 c・±r m) の区画を取り出してもらい、置き場の URL を返す
function requestRegion(c, r) {
  // アプリの外で試すとき: ?api=<試しのサーバー> があれば、そのサーバーに地点を作ってもらう (scratchpad の試しのサーバー)。無ければ ?region= の前もって作った地点
  if (!APP && qs.get('api')) {
    const api = qs.get('api'); let on = true;
    (async () => { while (on) { try { const p = await (await fetch(api + 'progress')).json(); if (on && p.total) showProgress(p.done, p.total); } catch (_) { } await new Promise(r => setTimeout(r, 500)); } })();
    return (regionPalReq = palOn(), fetch(api + 'region?c=' + c.map(v => v.toFixed(3)).join(',') + '&r=' + r + '&site=' + encodeURIComponent(SITE) + '&pal=' + (regionPalReq ? '1' : '0'))).then(x => x.json()).then(d => { if (d.error) throw new Error(d.error); return d; }).finally(() => { on = false; });
  }
  if (!APP) return Promise.resolve({ base: qs.get('region') || '' });
  const id = ++regionSeq;
  for (const [k, w] of regionWait) { regionWait.delete(k); w.ng(new Error('取りやめ')); }
  return new Promise((ok, ng) => { regionWait.set(id, { ok, ng }); regionPalReq = palOn(); window.chrome.webview.postMessage({ type: 'region', id, c, r, site: SITE, pal: regionPalReq }); }).then(d => ({ ...d, id }));
}
const el = document.getElementById('view');
const R = new THREE.WebGLRenderer({ antialias: true }); R.setPixelRatio(devicePixelRatio); el.appendChild(R.domElement);
const scene = new THREE.Scene(); scene.background = new THREE.Color(0x111111);
const cam = new THREE.PerspectiveCamera(55, 1, 0.02, 2000000); cam.up.set(0, 0, 1);
scene.add(new THREE.HemisphereLight(0xffffff, 0x444444, 1.2));
const dl = new THREE.DirectionalLight(0xffffff, 1.2); dl.position.set(3, 5, 8); scene.add(dl);
// 部品の色 (試験の頁と同じ。platesrc・wall・door・noraw はこの方式のデータでは使わない (空))
const COL = { platesrc: 0x7a9a7a, wall: 0xee3333, door: 0xeeee33, other: 0x888888, noraw: 0xaa66cc, excluded: 0x444444, counterpart: 0x6b4423 };
// 判定 2 の色
const OCOL = { floor2: 0x33cc33, slope2: 0x3399ff, tube2: 0xff9900, counter2: 0x996633 };
const LABK = [null, 'floor2', 'slope2', 'tube2', 'counter2'];
const NAME = { platesrc: '板を作った部品', wall: '壁', door: 'ドア', other: '部品', noraw: '書き出しに無い区画', excluded: '除外', counterpart: 'カウンター', floor2: '床 (形ごと)', slope2: '坂・階段 (形ごと)', tube2: '筒の底面 (形ごと)', counter2: 'カウンター (形ごと)' };
const clip = new THREE.Plane(new THREE.Vector3(0, 0, -1), 25); const clipLo = new THREE.Plane(new THREE.Vector3(0, 0, 1), 1e9); R.localClippingEnabled = true;
let ghostG = null, selFloor = null;
// 切る面 (clip: この高さより上を描かない・clipLo: フロアを選んだときの床より下を描かない) の間か (2026-11-04、利用者の指摘「2m とかできる場合、全体を消してるのは間違い」:
// 部品は丸ごと消さず、three.js の clippingPlanes で面で切って描く。押したとき・歩くモードの当たりも、切った後の見える所だけにする)
const visZ = z => z <= clip.constant + 1e-6 && z >= -clipLo.constant - 1e-6;
let root = new THREE.Group(); scene.add(root); const groups = {}; let shpG = null, hiM = null;
let cur = null;
const tgt = new THREE.Vector3(); let dist = 40, az = 0.8, el2 = 0.6;
function upd() { cam.position.set(tgt.x + dist * Math.cos(el2) * Math.cos(az), tgt.y + dist * Math.cos(el2) * Math.sin(az), tgt.z + dist * Math.sin(el2)); cam.lookAt(tgt); }
let drag = null, moved = 0; const cv = R.domElement;
// 3D の表示へフォーカスを戻せるようにする (Tab の順には入れない)。左の欄の ▼ の見出し・ボタン・チェック・ドロップダウンを押した後は、ここへ戻す
// (2026-11-05、親の決定: 押した後にフォーカスが残ると、歩くモードのスペース・W・A・S・D がその欄の開閉・ボタン・選択に取られるため)。キーボードで Tab で移って操作するのはそのままできる
cv.tabIndex = -1;
const focusView = () => setTimeout(() => cv.focus({ preventScroll: true }), 0);
document.getElementById('side').addEventListener('click', e => { if (e.target.closest('summary, button, input[type=checkbox]')) focusView(); });
document.getElementById('side').addEventListener('change', e => { if (e.target.tagName === 'SELECT') focusView(); });
cv.addEventListener('contextmenu', e => e.preventDefault());
cv.addEventListener('pointerdown', e => { drag = { b: e.button, x: e.clientX, y: e.clientY }; moved = 0; cv.setPointerCapture(e.pointerId); });
cv.addEventListener('pointerup', e => { if (drag && drag.b === 0 && moved < 4) pick(e); drag = null; });
cv.addEventListener('pointermove', e => {
  if (!drag) return; const dx = e.clientX - drag.x, dy = e.clientY - drag.y; drag.x = e.clientX; drag.y = e.clientY; moved += Math.abs(dx) + Math.abs(dy);
  // (2026-11-05、利用者の指示「RMB 押しながら上下が逆がいい」「ステーションの 3Dは逆にする」) 右ボタンのドラッグだけ、上下の向きを前と逆にする (左ドラッグは今のまま)
  const vy = drag.b === 2 ? -1 : 1;
  if (walk) { wyaw -= dx * 0.005; wpitch = Math.max(-1.4, Math.min(1.4, wpitch - vy * dy * 0.005)); walkCam(); return; }
  if (drag.b === 0 && !e.shiftKey) { az -= dx * 0.006; el2 = Math.max(-1.5, Math.min(1.5, el2 + dy * 0.006)); }
  else {
    const f = dist * 0.0015; const right = new THREE.Vector3(-Math.sin(az), Math.cos(az), 0);
    const fwd = cam.getWorldDirection(new THREE.Vector3()); const up = new THREE.Vector3().crossVectors(right, fwd).normalize().negate();
    // 見るモードの動かす (右ドラッグ・Shift+左ドラッグ): 上下は前と逆 (前はマウスを上へ動かすと見ている所 (自分) が上へ動いた。今は下へ)。Shift+左ドラッグも右ドラッグと同じ向き
    tgt.addScaledVector(right, -dx * f).addScaledVector(up, -dy * f);
  }
  upd();
});
const LEAVE_DIST = 10000;   // ［マップ］タブの中で、中心からこれ (m) より遠ざかると星系図に戻る
// 星系図に戻る距離 (初めは LEAVE_DIST)。外から見る形で開いた・切り替えたとき (runExteriorView。2026-11-06) は、そのときの距離の 2 倍との大きいほうにする
// (全部の印が入る距離が LEAVE_DIST を超える場所で、開いた直後の 1 回の引きで星系図に戻ってしまわないように)
let leaveDist = LEAVE_DIST;
cv.addEventListener('wheel', e => {
  e.preventDefault();
  const nd = dist * Math.exp(e.deltaY * 0.001);
  // ［マップ］タブの中 (?tab=1) では、遠ざかり切ったら星系図に戻る (アプリへ知らせる)
  if (APP && qs.get('tab') === '1' && !walk && e.deltaY > 0 && nd > leaveDist) { window.chrome.webview.postMessage({ type: 'leave' }); return; }
  if (e.deltaY < 0 && nd < 2) {
    // 中心の 2 m より近くへは、中心を見ている向きへ進める (カメラも同じだけ進む)
    const step = Math.max(0.2, dist) * (1 - Math.exp(e.deltaY * 0.001));
    tgt.addScaledVector(cam.getWorldDirection(new THREE.Vector3()), step);
  } else dist = nd;
  if (walk) return;
  upd();
}, { passive: false });
function size() { R.setSize(el.clientWidth, el.clientHeight); cam.aspect = el.clientWidth / el.clientHeight; cam.updateProjectionMatrix(); }
addEventListener('resize', size); size();
const keys = new Set();
addEventListener('keydown', e => { if (document.getElementById('cbox').style.display === 'block') return; if (e.target.tagName === 'TEXTAREA' || e.target.tagName === 'SELECT' || (e.target.tagName === 'INPUT' && /^(text|search|number)$/.test(e.target.type))) return; keys.add(e.key.toLowerCase()); if ('wasdqe'.includes(e.key.toLowerCase())) e.preventDefault(); });
addEventListener('keyup', e => keys.delete(e.key.toLowerCase()));
addEventListener('keydown', e => {
  if (document.getElementById('cbox').style.display === 'block') { if (e.key === 'Enter') { e.preventDefault(); closeComment(true); } else if (e.key === 'Escape') closeComment(false); return; }
  if (e.target.id === 'salesQ') return;   // 販売品の枠の検索の欄に打っている間は、Enter (コメント)・歩く操作のキーを取らない
  if (e.target.tagName === 'SELECT') return;   // ドロップダウン (文字の道案内) にフォーカスがある間は、歩く操作を取らない
  if (walk && e.key === 'Enter') { e.preventDefault(); keys.clear(); openComment(); return; }   // 録画中でなくても (2026-11-05、利用者「enter で文字入れて印付けるのは、いつでもできて入力したら即反映してほしい」)
  if (!walk || e.repeat) return;
  if (e.target.tagName === 'INPUT' && /^(text|search|number)$/.test(e.target.type)) return;
  if (e.key === 'Shift') { run = !run; }
  else if (e.key === ' ') {
    e.preventDefault(); if (air || onLadder) return;
    const fx = wpos.x + Math.cos(wyaw) * 0.7, fy = wpos.y + Math.sin(wyaw) * 0.7, g = groundAt(fx, fy, wpos.z + CLIMB);
    if (g !== null && g > wpos.z + STEP && g <= wpos.z + CLIMB && fits(fx, fy, g)) { wpos.set(fx, fy, g); }
    else { air = true; vz = JUMP_V; }
  }
  else if (e.key.toLowerCase() === 'n') { noclip = !noclip; }   // すり抜けモード: 床の判定だけを残し、体の高さの判定を無くす
  else if (e.key.toLowerCase() === 'c') { const n = stance === 'crouch' ? 'stand' : 'crouch'; const keep = stance; stance = n; if (!fits(wpos.x, wpos.y, wpos.z)) stance = keep; }
  else if (e.key === 'Control') { e.preventDefault(); const n = stance === 'prone' ? 'stand' : 'prone'; const keep = stance; stance = n; if (!fits(wpos.x, wpos.y, wpos.z)) stance = keep; }
  updWalkUi();
});
let lastT = performance.now();
// ---- 歩くモード (試験の頁と同じ)
// 歩く・走る速さ (2026-11-04、利用者の指示「走る速度も歩く速度も二倍で」: 1.4 → 2.8・5 → 10 m/秒)
const STEP = 0.6, WALK = 2.8, RUN = 10.0, TURN = 1.6;
const STEP_FLOOR_MIN = 0.35;   // これより高い段を上がるときだけ「手すりの高さ」の検査 (tallObjectAt) に掛ける。階段の段 (0.2〜0.3 m) は検査なしで通る (2026-11-05、利用者「高さの基準が低すぎたんじゃない？」で 0.25 → 0.35)
// 姿勢: 体の高さと目の高さ (立つ・しゃがむ・寝そべる)。ゲームのデータでは確かめていない値
const STANCE = { stand: { h: 1.8, eye: 1.7, sp: 1 }, crouch: { h: 1.2, eye: 1.05, sp: 0.5 }, prone: { h: 0.5, eye: 0.35, sp: 0.25 } };
let stance = 'stand';
// ジャンプ: 上向きの速さ 4.43 m/秒 (重力 9.81 m/秒² で高さ 1.0 m)。よじ登り: 正方形のコンテナの高さ 1 個分 (container_dry_1_025x0375x025_a・storage_container_1_025x05x025_a の高さ 2.52 m) まで
const JUMP_V = 4.43, GRAV = 9.81, CLIMB = 2.52, LADDER_V = 1.5;
let vz = 0, air = false, onLadder = null;
let rec = null, play = null, noclip = false;
let walk = false, run = false, wyaw = 0, wpitch = 0, wpos = new THREE.Vector3();
// ===== 当たり (升のかたまりと一緒に受け取る) =====
// (2026-11-06 の作り替え。利用者の指摘 (升を踏んだその場で作る形について)「馬鹿なやり方だと思うのは俺だけか？」「止まるのは当たり前だよな？」
//  「細かく書く範囲戸当たり判定は一致しないとだめではないのか？ 読み込みで止まるんだからどっちかが短ければ意味ないよな？」。親の決定)
// 前は、地点の全部の三角形を 1 つの ArrayBuffer (curBuf) で持ち、当たりの升 (1 m。頭の上は 2 m) は、歩いて初めて引いたその場で、荒い区切り (coarse) から選んで作っていた (三角形の多い所で止まった)。
// 今は、形を「升のかたまり」(下の「升のかたまりの出し入れ」。view3d-worker.js が作る) ごとに受け取り、かたまりには描く形と当たりが一緒に入っている:
//   ch.F = 三角形の位置 (原点 cur.center からの相対。float32 x9)、ch.FL = 三角形ごとの当たりの印、ch.col = xy の升目の索引 (g2 = 2 m の升ごと、g16 = 16 m の升ごと、big = 大きな三角形)。
// かたまりを出すとき (colAdd) に索引を G2・G16・GBIG へつなぎ、捨てるとき (colDrop) に外す。歩いている最中に踏んだその場で作るものは無い。
// 升を引く (cellTris) ときは、その升の入る 2 m・16 m の升の一覧と大きな三角形から、印と升の条件で選ぶ。条件は前の升目 (cellGet) と同じ:
//   f (足元): 上向き (cz > 0)・1 m の升・升の数 ≤ 4096。ドアも入れる
//   c (頭の上): 下向き (cz < 0)・2 m の升 (floor(x/2))・升の数 ≤ 1024。ドア (名前に door・doors があり elevator の無い部品) は入れない
//   w (横): ドア以外の全部・1 m の升・升の数 ≤ 4096
//   fl (判定の床): 判定の色が floor2・tube2 の三角形・1 m の升・升の数 ≤ 4096
//   a (物体): 全部 (ドアも)・1 m の升・升の数 ≤ 4096 (歩くモードの「粗い形に物体が無い升」を探す用。利用者の指示「床でなく物体で」)
// ----- CELLS_BEGIN (この印の間は試しの道具が切り出して使う。ほかの変数を使わない) -----
const UP = 1, DOWN = 2, DOOR = 4, FLR = 8, OK1 = 16, OK2 = 32;
const G2 = new Map(), G16 = new Map(), GBIG = [];
function colAdd(ch) {
  const put = (G, g) => { const K = g.keys, S = g.starts; for (let q = 0; q < K.length; q++) { let a = G.get(K[q]); if (!a) G.set(K[q], a = []); a.push({ ch, it: g.items, s: S[q], e: S[q + 1] }); } };
  put(G2, ch.col.g2); put(G16, ch.col.g16);
  if (ch.col.big.length) GBIG.push({ ch, it: ch.col.big, s: 0, e: ch.col.big.length });
}
function colDrop(ch) {
  const cut = (G, g) => { for (const k of g.keys) { const a = G.get(k); if (!a) continue; const b = a.filter(s => s.ch !== ch); if (b.length) G.set(k, b); else G.delete(k); } };
  cut(G2, ch.col.g2); cut(G16, ch.col.g16);
  for (let i = GBIG.length - 1; i >= 0; i--) if (GBIG[i].ch === ch) GBIG.splice(i, 1);
}
function colReset() { G2.clear(); G16.clear(); GBIG.length = 0; }
// 升 (kind, i, j。c は 2 m の升の番号、ほかは 1 m) に入る三角形を CT.C (かたまり)・CT.B (かたまりの F の中の位置) に並べ、数を返す (次に引くまで使える)
const CT = { C: [], B: [] };
function cellTris(kind, i, j) {
  const two = kind === 'c', i2 = two ? i : Math.floor(i / 2), j2 = two ? j : Math.floor(j / 2);
  const need = kind === 'f' ? UP | OK1 : two ? DOWN | OK2 : kind === 'fl' ? FLR | OK1 : OK1, mask = two || kind === 'w' ? need | DOOR : need;
  let n = 0;
  for (let li = 0; li < 3; li++) {
    const a = li === 0 ? G2.get(i2 * 100003 + j2) : li === 1 ? G16.get(Math.floor(i2 / 8) * 100003 + Math.floor(j2 / 8)) : GBIG; if (!a) continue;
    for (let s = 0; s < a.length; s++) {
      const S = a[s], ch = S.ch, F = ch.F, FL = ch.FL, it = S.it;
      for (let p = S.s; p < S.e; p++) {
        const b = it[p]; if ((FL[b / 9] & mask) !== need) continue;
        const x0 = Math.min(F[b], F[b + 3], F[b + 6]), x1 = Math.max(F[b], F[b + 3], F[b + 6]), y0 = Math.min(F[b + 1], F[b + 4], F[b + 7]), y1 = Math.max(F[b + 1], F[b + 4], F[b + 7]);
        if (two ? Math.floor(x0 / 2) > i || Math.floor(x1 / 2) < i || Math.floor(y0 / 2) > j || Math.floor(y1 / 2) < j : Math.floor(x0) > i || Math.floor(x1) < i || Math.floor(y0) > j || Math.floor(y1) < j) continue;
        CT.C[n] = ch; CT.B[n] = b; n++;
      }
    }
  }
  return n;
}
// ----- CELLS_END -----
let stampSeq = 0;   // 同じ三角形を 2 度見ないための印 (segHit。かたまりの ch.ST に書く)
// ===== 読み込みの調整 (左の欄の「読み込みの調整」。下の「升のかたまりの出し入れ」・外判定が使う) =====
// (2026-11-06、利用者の指示「今言った距離で作って、調整可能なようにページに設定を足すのは不可能なの？」。親の決定)
//   d1・d2・d3: 距離の境 (m)。d1 以内は小・中・大、d2 以内は中・大、d3 以内は大。s1・s2・s3: 部品の大きさ (箱のいちばん長い辺) の境 (m)。小と中・中と大・大きな物体
//   step: 見直す間隔 (m)、speed: 細かい物を読む速さの上限 (m/秒。初めの値は歩きの速さ)、outDist・outFloor: 外判定の距離と床の距離 (m)
// 値は localStorage (view3d.load) に覚える (ほかのスイッチと同じ)
const LOAD_DEF = { d1: 50, d2: 100, d3: 150, s1: 1, s2: 5, s3: 20, step: 10, speed: 2.8, outDist: 200, outFloor: 0.5 };
const LOADCFG = { ...LOAD_DEF };
try { const s = JSON.parse(localStorage.getItem('view3d.load') || '{}'); if (s && typeof s === 'object') for (const k of Object.keys(LOAD_DEF)) if (typeof s[k] === 'number' && isFinite(s[k]) && s[k] >= 0) LOADCFG[k] = s[k]; } catch (_) { }
function buildGrid() { }
function buildCeil() { }
function ceilAt(x, y, zmin) {
  // (x, y) の真上で、zmin より高い下向きの面のいちばん低い所
  const n = cellTris('c', Math.floor(x / 2), Math.floor(y / 2)); let best = null;
  for (let q = 0; q < n; q++) { const all = CT.C[q].F, b = CT.B[q];
    const ax = all[b], ay = all[b + 1], az_ = all[b + 2], bx = all[b + 3], by = all[b + 4], bz = all[b + 5], cx = all[b + 6], cy = all[b + 7], cz = all[b + 8];
    const v0x = bx - ax, v0y = by - ay, v1x = cx - ax, v1y = cy - ay, den = v0x * v1y - v1x * v0y; if (Math.abs(den) < 1e-12) continue;
    const wx = x - ax, wy = y - ay, s_ = (wx * v1y - v1x * wy) / den, t_ = (v0x * wy - wx * v0y) / den;
    if (s_ < 0 || t_ < 0 || s_ + t_ > 1) continue;
    const z = az_ + s_ * (bz - az_) + t_ * (cz - az_); if (!visZ(z)) continue; if (z > zmin && (best === null || z < best)) best = z;
  }
  // 細かい形で補った面 (extra) の下向きの面も見る (選び方は同じ)
  const xl = extraCells(extra.c, x, y);
  if (xl) for (let q = 0; q < xl.length; q += 2) { const z = triZ(extra.list[xl[q]].tris, xl[q + 1], x, y); if (z === null || !visZ(z)) continue; if (z > zmin && (best === null || z < best)) best = z; }
  return best;
}
// 横の当たり (利用者の「本来物質があれば当たり判定してほしい」): ドア (名前に door・doors) を除く全部の三角形を 1 m の升目に入れ、
// 動く向きへ、足から STEP より上・体の高さまでの 3 つの高さで、動く距離 + 体の半径 0.25 m の線を引き、三角形に当たったら動かない
const BODY_R = 0.25;
function buildWall() { }
function segHit(ax, ay, az, bx, by, bz) {
  const dx = bx - ax, dy = by - ay, dz = bz - az; const seen = new Set(), st = ++stampSeq;
  for (const [cx, cy] of [[ax, ay], [bx, by]]) {
    const n = cellTris('w', Math.floor(cx), Math.floor(cy));
    for (let q = 0; q < n; q++) { const ch = CT.C[q], all = ch.F, b = CT.B[q];
      if (ch.ST[b / 9] === st) continue; ch.ST[b / 9] = st;   // 両端の升の両方に入る三角形は 1 回だけ見る
      const e1x = all[b + 3] - all[b], e1y = all[b + 4] - all[b + 1], e1z = all[b + 5] - all[b + 2], e2x = all[b + 6] - all[b], e2y = all[b + 7] - all[b + 1], e2z = all[b + 8] - all[b + 2];
      const px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x, det = e1x * px + e1y * py + e1z * pz; if (Math.abs(det) < 1e-12) continue;
      const inv = 1 / det, tx = ax - all[b], ty = ay - all[b + 1], tz = az - all[b + 2]; const u = (tx * px + ty * py + tz * pz) * inv; if (u < 0 || u > 1) continue;
      const qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x; const v = (dx * qx + dy * qy + dz * qz) * inv; if (v < 0 || u + v > 1) continue;
      const t = (e2x * qx + e2y * qy + e2z * qz) * inv; if (t > 0 && t < 1 && visZ(az + dz * t)) return true;
    }
    // 細かい形で補った面 (extra) の三角形 (全部) にも当たるか
    const xl = extraCells(extra.w, cx, cy);
    if (xl) for (let q = 0; q < xl.length; q += 2) {
      const xk = -((xl[q] + 1) * 1e9 + xl[q + 1]); if (seen.has(xk)) continue; seen.add(xk);   // 升目の b (正) と重ならない鍵 (負)
      const t = triSeg(extra.list[xl[q]].tris, xl[q + 1], ax, ay, az, dx, dy, dz); if (t !== null && visZ(az + dz * t)) return true;
    }
  }
  return false;
}
function blocked(x, y, z, nx, ny) {
  if (noclip) return false;
  const dx = nx - x, dy = ny - y, d = Math.hypot(dx, dy); if (d < 1e-9) return false;
  const ex = nx + dx / d * BODY_R, ey = ny + dy / d * BODY_R, H = STANCE[stance].h;
  for (const h of [STEP + 0.05, (STEP + H) / 2, H - 0.1]) if (h > STEP && segHit(x, y, z + h, ex, ey, z + h)) return true;
  // 進む先の、足元 (段の高さより上) から頭までの縦の線。横の線 3 本の間をすり抜ける細いバー (階段の手すりなど) に当てる
  // (2026-11-05、利用者の報告「階段の手すりをすり抜けて落ちる」。親の決定)
  if (H - 0.1 > STEP + 0.05 && segHit(ex, ey, z + STEP + 0.05, ex, ey, z + H - 0.1)) return true;
  return false;
}
function fits(x, y, z) { if (noclip) return true; const c = ceilAt(x, y, z + 0.05); return c === null || c - z >= STANCE[stance].h; }
function groundAt(x, y, zmax) {
  // (x, y) の真下で、zmax より低い歩く面のいちばん高い所
  const n = cellTris('f', Math.floor(x), Math.floor(y)); let best = null;
  for (let q = 0; q < n; q++) { const all = CT.C[q].F, b = CT.B[q];
    const ax = all[b], ay = all[b + 1], az_ = all[b + 2], bx = all[b + 3], by = all[b + 4], bz = all[b + 5], cx = all[b + 6], cy = all[b + 7], cz = all[b + 8];
    const v0x = bx - ax, v0y = by - ay, v1x = cx - ax, v1y = cy - ay, den = v0x * v1y - v1x * v0y; if (Math.abs(den) < 1e-12) continue;
    const wx = x - ax, wy = y - ay, s_ = (wx * v1y - v1x * wy) / den, t_ = (v0x * wy - wx * v0y) / den;
    if (s_ < 0 || t_ < 0 || s_ + t_ > 1) continue;
    const z = az_ + s_ * (bz - az_) + t_ * (cz - az_); if (!visZ(z)) continue; if (z <= zmax && (best === null || z > best)) best = z;
  }
  // 細かい形で補った面 (extra) の上向きの面も見る (選び方は同じ: いちばん高い)
  const xl = extraCells(extra.f, x, y);
  if (xl) for (let q = 0; q < xl.length; q += 2) { const z = triZ(extra.list[xl[q]].tris, xl[q + 1], x, y); if (z === null || !visZ(z)) continue; if (z <= zmax && (best === null || z > best)) best = z; }
  return best;
}
// 落ちる下限 (2026-11-04、利用者の指示「一番低い床より下には下がらないように落下する」「周囲1mとかでいいんじゃない」):
// 今の位置の真下と、まわり 0.5 m・1 m の各 8 点 (計 17 点) で、その点を含む歩く面のうち、いちばん低いものの高さ。どの点にも面が無ければ null
const FALL_R = 1.0;
function lowAt(x, y) {
  const n = cellTris('f', Math.floor(x), Math.floor(y)); let best = null;
  for (let q = 0; q < n; q++) { const all = CT.C[q].F, b = CT.B[q];
    const ax = all[b], ay = all[b + 1], az_ = all[b + 2], bx = all[b + 3], by = all[b + 4], bz = all[b + 5], cx = all[b + 6], cy = all[b + 7], cz = all[b + 8];
    const v0x = bx - ax, v0y = by - ay, v1x = cx - ax, v1y = cy - ay, den = v0x * v1y - v1x * v0y; if (Math.abs(den) < 1e-12) continue;
    const wx = x - ax, wy = y - ay, s_ = (wx * v1y - v1x * wy) / den, t_ = (v0x * wy - wx * v0y) / den;
    if (s_ < 0 || t_ < 0 || s_ + t_ > 1) continue;
    const z = az_ + s_ * (bz - az_) + t_ * (cz - az_); if (!visZ(z)) continue; if (best === null || z < best) best = z;
  }
  // 細かい形で補った面 (extra) の上向きの面も見る (選び方は同じ: いちばん低い)
  const xl = extraCells(extra.f, x, y);
  if (xl) for (let q = 0; q < xl.length; q += 2) { const z = triZ(extra.list[xl[q]].tris, xl[q + 1], x, y); if (z === null || !visZ(z)) continue; if (best === null || z < best) best = z; }
  return best;
}
function lowestNear(x, y) {
  let best = lowAt(x, y);
  for (const r of [FALL_R / 2, FALL_R]) for (let k = 0; k < 8; k++) { const a = k * Math.PI / 4; const g = lowAt(x + r * Math.cos(a), y + r * Math.sin(a)); if (g !== null && (best === null || g < best)) best = g; }
  return best;
}
// ===== 細かい形で補った面 (extra) と、粗い形に物体の無い升の知らせ (walkCheck) =====
// (2026-11-05、利用者の指示「歩くモードで近く 5 m 以内の部品を裏で先に細かい形で調べる 見つけたら DB に入れる」「z +/- 50 cm とか」「荒いのと細かいのの差だけをチェックすれば 10m でも実用的にできないかな？」
// 「そのエリアに荒いので部品がない場合、細かいのでチェック」「床でなく物体で」「床代わりの何かに対応できる。照明とかパネルとかチューブとか」)
// 歩くモード中 1 秒ごと (nearCheck と同じ間引き) に、現在位置 wpos のまわり xy 半径 WALK_R m の 1 m の升 (当たり判定の升目と同じ升。鍵 i × 100003 + j) のうち、
// 粗い形の三角形 (升のかたまりの三角形の全部。cellTris('a', …)) の z の範囲が足元 ±WALK_ZTOL m に 1 つも掛からない「物体の無い升」を、アプリに知らせる
// ({type:'walkCheck', pos (足元。場所の座標), zTol, cells: [[升の中心 x, y], …] (場所の座標), site})。同じ升は 1 回の 3D の中で二度送らない (送った升を持つ。親の決定)。1 回に最大 WALK_MAX_CELLS 升 (親の決定。近い順)。
// アプリが細かい形 (LOD0) で見つけた三角形 ({type:'walkFill', center, items:[{inst, name, tris}]}。tris は center からの相対) を extra に入れ、描き (部品の色の Mesh を root に足す。groups には入れない)、当たり判定に足す。
// 升 (cellTris の f・c・w・fl) は升のかたまりの三角形から引くものなので、extra は別の表 (三角形の並び Float32Array と、1 m の升ごとの添字の Map) に持ち、
// ceilAt・segHit・groundAt・lowAt・onFloor で升目の結果に加えて見る (選び方は各関数の今の決まりのまま)。
//   f (足元): 上向き (cz > 0)、c (頭の上): 下向き、w (横): 全部、fl (判定の床): 傾きが 46° 以内の上向き (親の決定。名前の除外と 1 m 未満の除外は掛けない)
const WALK_R = 10, WALK_ZTOL = 0.5, WALK_MAX_CELLS = 400, FL_COS = Math.cos(46 * Math.PI / 180);
const extra = { list: [], f: new Map(), c: new Map(), w: new Map(), fl: new Map(), sent: [], sentKeys: new Set() };
window.__extra = extra;
const ekey = (i, j) => i * 100003 + j;
// 送った升の鍵: (升の i, j, 足元の z を 1 m で切った数)。別の階 (高さ) に移ったら同じ xy の升も送り直す (親の決定)
const sentKey = (cx, cy, foot) => `${Math.floor(cx)},${Math.floor(cy)},${Math.floor(foot)}`;
function extraCells(m, x, y) { return m.size ? (m.get(ekey(Math.floor(x), Math.floor(y))) || null) : null; }
function extraPush(m, key, li, b) { let l = m.get(key); if (!l) m.set(key, l = []); l.push(li, b); }
// (x, y) の真上・真下で三角形 (F の b から 9 つ) に当たる高さ (groundAt と同じ計算)。当たらなければ null
function triZ(F, b, x, y) {
  const ax = F[b], ay = F[b + 1], az_ = F[b + 2], v0x = F[b + 3] - ax, v0y = F[b + 4] - ay, v1x = F[b + 6] - ax, v1y = F[b + 7] - ay, den = v0x * v1y - v1x * v0y; if (Math.abs(den) < 1e-12) return null;
  const wx = x - ax, wy = y - ay, s_ = (wx * v1y - v1x * wy) / den, t_ = (v0x * wy - wx * v0y) / den; if (s_ < 0 || t_ < 0 || s_ + t_ > 1) return null;
  return az_ + s_ * (F[b + 5] - az_) + t_ * (F[b + 8] - az_);
}
// 線 (a から d だけ進む) が三角形に当たる所の t (0 < t < 1)。当たらなければ null (segHit と同じ計算)
function triSeg(F, b, ax, ay, az, dx, dy, dz) {
  const e1x = F[b + 3] - F[b], e1y = F[b + 4] - F[b + 1], e1z = F[b + 5] - F[b + 2], e2x = F[b + 6] - F[b], e2y = F[b + 7] - F[b + 1], e2z = F[b + 8] - F[b + 2];
  const px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x, det = e1x * px + e1y * py + e1z * pz; if (Math.abs(det) < 1e-12) return null;
  const inv_ = 1 / det, tx = ax - F[b], ty = ay - F[b + 1], tz = az - F[b + 2]; const u = (tx * px + ty * py + tz * pz) * inv_; if (u < 0 || u > 1) return null;
  const qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x; const v = (dx * qx + dy * qy + dz * qz) * inv_; if (v < 0 || u + v > 1) return null;
  const t = (e2x * qx + e2y * qy + e2z * qz) * inv_; return t > 0 && t < 1 ? t : null;
}
// extra の li 番目の三角形を升ごとの表に入れる (升の数の上限 4096 は worker の sweep と同じ)
function extraIndex(li) {
  const F = extra.list[li].tris;
  for (let b = 0; b + 9 <= F.length; b += 9) {
    const x0 = Math.min(F[b], F[b + 3], F[b + 6]), x1 = Math.max(F[b], F[b + 3], F[b + 6]), y0 = Math.min(F[b + 1], F[b + 4], F[b + 7]), y1 = Math.max(F[b + 1], F[b + 4], F[b + 7]);
    const ux = F[b + 3] - F[b], uy = F[b + 4] - F[b + 1], uz = F[b + 5] - F[b + 2], vx = F[b + 6] - F[b], vy = F[b + 7] - F[b + 1], vz = F[b + 8] - F[b + 2];
    const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, cz = ux * vy - uy * vx, ln = Math.hypot(nx, ny, cz);
    const i0 = Math.floor(x0), i1 = Math.floor(x1), j0 = Math.floor(y0), j1 = Math.floor(y1);
    if ((i1 - i0 + 1) * (j1 - j0 + 1) > 4096) continue;
    const fl = cz > 0 && ln > 0 && cz / ln >= FL_COS;
    for (let i = i0; i <= i1; i++) for (let j = j0; j <= j1; j++) {
      const k = ekey(i, j);
      if (cz > 0) extraPush(extra.f, k, li, b);
      if (cz < 0) extraPush(extra.c, k, li, b);
      extraPush(extra.w, k, li, b);
      if (fl) extraPush(extra.fl, k, li, b);
    }
  }
}
// 描く: 部品の色 (COL.other) の Mesh (升のかたまりの形 (addChunk) と同じ作り: flatShading・両面・切る面)。root に足す。groups には入れない (pick はかたまりの部品の番号で引くため)
function extraDraw(li) {
  const e = extra.list[li];
  const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(e.tris, 3));
  const me = new THREE.Mesh(g, new THREE.MeshLambertMaterial({ color: COL.other, side: THREE.DoubleSide, flatShading: true, clippingPlanes: [clip, clipLo] }));
  me.renderOrder = 0; me.userData = { k: 'extra', li }; e.mesh = me; root.add(me);
}
function extraAdd(tris) { const li = extra.list.length; extra.list.push({ tris, mesh: null }); extraIndex(li); extraDraw(li); }
// 読み込み直し (applyRegion の入れ替えの後。今は keep のとき原点が変わらないので off は 0): keep なら、中心の差 off (新しい中心 − 前の中心) で座標を直し (新しい相対 = 前の相対 − off)、新しい root に描き直し、送った升も直す。keep でなければ捨てる
function extraRelocate(off, keep) {
  for (const m of [extra.f, extra.c, extra.w, extra.fl]) m.clear();
  if (!keep) { extra.list = []; extra.sent = []; extra.sentKeys.clear(); return; }
  for (const e of extra.list) { const F = e.tris; for (let i = 0; i + 3 <= F.length; i += 3) { F[i] -= off[0]; F[i + 1] -= off[1]; F[i + 2] -= off[2]; } e.mesh = null; }
  for (let li = 0; li < extra.list.length; li++) { extraIndex(li); extraDraw(li); }
  extra.sentKeys.clear();
  for (const s of extra.sent) { s[0] -= off[0]; s[1] -= off[1]; s[2] -= off[2]; extra.sentKeys.add(sentKey(s[0], s[1], s[2])); }
}
// アプリからの補いの面を受ける。tris は d.center からの相対なので、今の中心との差で直す
function onWalkFill(d) {
  if (!cur || !Array.isArray(d.items) || !d.items.length) return;
  const c = cur.center, dc = Array.isArray(d.center) ? [d.center[0] - c[0], d.center[1] - c[1], d.center[2] - c[2]] : [0, 0, 0];
  let n = 0; for (const it of d.items) n += (it.tris || []).length;
  n -= n % 9; if (!n) return;
  const F = new Float32Array(n); let o = 0;
  for (const it of d.items) { const T = it.tris || []; for (let i = 0; i + 3 <= T.length && o < n; i += 3) { F[o++] = T[i] + dc[0]; F[o++] = T[i + 1] + dc[1]; F[o++] = T[i + 2] + dc[2]; } }
  extraAdd(F);
  window.__walkFill = { tris: (window.__walkFill?.tris || 0) + n / 9, items: (window.__walkFill?.items || 0) + d.items.length, last: d.items.map(it => [it.inst, it.name, (it.tris || []).length / 9]) };
}
// 1 秒ごとに、物体の無い升を探してアプリに送る。
// (2026-11-06、利用者の報告「実用的でない 2 秒おきくらいで引っかかり」。親の決定) 前は 1 コマの中で、まわり WALK_R m の升 (約 300 個) を全部引いていた (初めての升はその場で作るので、動いていると 1 秒ごとに
// 新しい升をまとめて作って止まった)。今は、探し始めたときの位置 (px, py, foot) で同じ升を同じ順に見るのを、毎コマ WALK_SLICE_MS ms までに区切って続ける (walkSweep。送る中身の決まりは前と同じ)
const WALK_SLICE_MS = 2;
let lastWalkCheck = 0, walkJob = null;
function* walkSweep(px, py, foot) {
  const z0 = foot - WALK_ZTOL, z1 = foot + WALK_ZTOL;
  const out = [];
  for (let i = Math.floor(px - WALK_R); i <= Math.floor(px + WALK_R); i++) for (let j = Math.floor(py - WALK_R); j <= Math.floor(py + WALK_R); j++) {
    const cx = i + 0.5, cy = j + 0.5, d = Math.hypot(cx - px, cy - py); if (d > WALK_R) continue;
    if (extra.sentKeys.has(sentKey(cx, cy, foot))) continue;
    let empty = true;
    const n = cellTris('a', i, j);   // ドアも物体なので含めて見る (w はドアを除く)
    for (let q = 0; q < n; q++) { const F = CT.C[q].F, b = CT.B[q]; if (Math.max(F[b + 2], F[b + 5], F[b + 8]) >= z0 && Math.min(F[b + 2], F[b + 5], F[b + 8]) <= z1) { empty = false; break; } }
    if (empty) out.push([cx, cy, d]);
    yield;
  }
  if (!out.length) return;
  out.sort((a, b) => a[2] - b[2]);
  const send = out.slice(0, WALK_MAX_CELLS), c = cur.center;
  for (const s of send) { extra.sent.push([s[0], s[1], foot]); extra.sentKeys.add(sentKey(s[0], s[1], foot)); }
  window.__walkCheck = { sent: (window.__walkCheck?.sent || 0) + send.length, last: send.length, pending: out.length - send.length };
  window.chrome.webview.postMessage({ type: 'walkCheck', pos: [px + c[0], py + c[1], foot + c[2]], zTol: WALK_ZTOL, cells: send.map(s => [s[0] + c[0], s[1] + c[1]]), site: SITE });
}
function walkCheckTick() {
  if (!APP || !walk || !cur || loading || !cur.index) { walkJob = null; return; }
  const now = performance.now();
  if (walkJob && walkJob.cur !== cur) walkJob = null;   // 地点が替わった (座標がずれる): 途中の分はやめる
  if (!walkJob) { if (now - lastWalkCheck < 1000) return; lastWalkCheck = now; walkJob = { cur, it: walkSweep(wpos.x, wpos.y, wpos.z) }; }
  for (;;) { if (walkJob.it.next().done) { walkJob = null; break; } if (performance.now() - now >= WALK_SLICE_MS) break; }
}
function updWalkUi() {
  const sn = { stand: '立つ', crouch: 'しゃがむ', prone: '寝そべる' }[stance];
  document.getElementById('runBtn').textContent = '走る: ' + (run ? 'ON' : 'OFF'); document.getElementById('runBtn').classList.toggle('on', run);
  document.getElementById('stBtn').textContent = '姿勢: ' + sn; document.getElementById('stBtn').classList.toggle('on', stance !== 'stand');
  document.getElementById('ncBtn').textContent = 'すり抜け: ' + (noclip ? 'ON' : 'OFF'); document.getElementById('ncBtn').classList.toggle('on', noclip);
  document.getElementById('walkInfo').textContent = walk ? `${run ? '走る' : '歩く'}・${sn}・すり抜け ${noclip ? 'ON' : 'OFF'}` : '';
}
// 足元のすき間の決まり (利用者の「0.5m 以上間がないと落ちなくすればいい」): 足の真下と、まわり 0.25 m の 8 点の、いちばん高い上向きの面を足元にする
// (幅 0.5 m より狭いすき間の上では、まわりの面に立ったままになる)。落ちたかを見るための記録: 真下に面が無いか低く、まわりの面で支えた回数と、そのすき間の点
const GAP_R = 0.25; let gapLog = [];
function groundGap(x, y, zmax) {
  let best = groundAt(x, y, zmax), c0 = best;
  for (let k = 0; k < 8; k++) { const a = k * Math.PI / 4; const g = groundAt(x + GAP_R * Math.cos(a), y + GAP_R * Math.sin(a), zmax); if (g !== null && (best === null || g > best)) best = g; }
  if (best !== null && (c0 === null || c0 < best - 0.05) && gapLog.length < 200) gapLog.push([+x.toFixed(2), +y.toFixed(2), c0 === null ? null : +c0.toFixed(2), +best.toFixed(2)]);
  return best;
}
function walkCam() { cam.position.copy(wpos); cam.position.z += inv ? -STANCE[stance].eye : STANCE[stance].eye; cam.up.set(0, 0, inv ? -1 : 1); const d = new THREE.Vector3(Math.cos(wpitch) * Math.cos(wyaw), Math.cos(wpitch) * Math.sin(wyaw), Math.sin(wpitch)); cam.lookAt(cam.position.clone().add(d)); }
function walkStep(dt) {
  const f = new THREE.Vector3(Math.cos(wyaw), Math.sin(wyaw), 0), r = new THREE.Vector3(Math.sin(wyaw), -Math.cos(wyaw), 0);
  if (keys.has('q')) wyaw += TURN * dt; if (keys.has('e')) wyaw -= TURN * dt;
  const mv = new THREE.Vector3();
  if (keys.has('w')) mv.add(f); if (keys.has('s')) mv.sub(f); if (keys.has('d')) mv.add(r); if (keys.has('a')) mv.sub(r);
  if (play) { playStep(dt); return; }
  // 重力の範囲 (grav.js の説明)
  const gm = gravMode(); window.__grav = gm;
  if (gm === 'zero') { inv = false; onLadder = null; floatStep(dt); walkCam(); showElev(); recStep(dt); return; }
  if (gm === 'up') { if (!inv) { inv = true; air = true; vz = 0; onLadder = null; } invStep(dt); walkCam(); showElev(); recStep(dt); return; }
  if (inv) { inv = false; air = true; vz = 0; }
  // はしご: はしごの部品の箱から xy で 0.6 m 以内で W を押すと登る (S で下りる)。上の端に着いたら前へ 0.6 m 出て、そこの床に立つ
  if (!onLadder && keys.has('w')) onLadder = (cur.ladders || []).find(l => Math.max(l[2][0] - wpos.x, wpos.x - l[3][0], 0) ** 2 + Math.max(l[2][1] - wpos.y, wpos.y - l[3][1], 0) ** 2 <= 0.36 && wpos.z >= l[2][2] - 0.5 && wpos.z < l[3][2] - 0.1) || null;
  if (onLadder) {
    if (keys.has('w')) wpos.z += LADDER_V * dt; if (keys.has('s')) wpos.z -= LADDER_V * dt;
    if (wpos.z >= onLadder[3][2]) { wpos.z = onLadder[3][2]; wpos.x += Math.cos(wyaw) * 0.6; wpos.y += Math.sin(wyaw) * 0.6; const g = groundAt(wpos.x, wpos.y, wpos.z + STEP); if (g !== null) wpos.z = g; onLadder = null; outRequest(); review(true); }   // はしごを登り終えた・下り終えた (次の行) ので、外判定を見直す (outTick)・出す升のかたまりを見直す (review)
    else if (wpos.z <= onLadder[2][2] - 0.5 || !keys.size) { if (!keys.has('w') && !keys.has('s')) {} else if (wpos.z <= onLadder[2][2]) { onLadder = null; outRequest(); review(true); } }
    walkCam(); showElev(); recStep(dt); return;
  }
  const spd = (run ? RUN : WALK) * STANCE[stance].sp * dt;
  if (mv.lengthSq() > 0) {
    mv.normalize().multiplyScalar(spd); const nx = wpos.x + mv.x, ny = wpos.y + mv.y;
    const g = groundGap(nx, ny, wpos.z + STEP);
    const nz_ = air ? wpos.z : (g !== null && g >= wpos.z - STEP ? g : wpos.z);
    let tx = nx, ty = ny;
    // 段を上がる (STEP_FLOOR_MIN より高い) ときは、上がる先が「手すりの高さ」の物 (tallObjectAt) でないときだけ
    // (2026-11-05、利用者の報告「階段の手すりをすり抜けて落ちる。超えてるのかも」: 下り階段で手すりの上面が足元から 0.6 m 以内に入り、段として乗っていた。親の決定)
    const stepUpBad = (x_, y_, g_) => !air && g_ !== null && g_ > wpos.z + STEP_FLOOR_MIN && tallObjectAt(x_, y_, wpos.z);
    if (stepUpBad(nx, ny, g)) { tx = wpos.x; ty = wpos.y; }
    else if (blocked(wpos.x, wpos.y, wpos.z, tx, ty)) { if (!blocked(wpos.x, wpos.y, wpos.z, nx, wpos.y)) ty = wpos.y; else if (!blocked(wpos.x, wpos.y, wpos.z, wpos.x, ny)) tx = wpos.x; else { tx = wpos.x; ty = wpos.y; } }
    if ((tx !== wpos.x || ty !== wpos.y) && fits(tx, ty, nz_)) { const g3 = groundGap(tx, ty, wpos.z + STEP); if (!stepUpBad(tx, ty, g3)) { wpos.x = tx; wpos.y = ty; if (!air && g3 !== null) { if (g3 < wpos.z - 0.05) air = true; else wpos.z = g3; } } }
  }
  // 落ちる・跳ぶ
  const gnd = groundGap(wpos.x, wpos.y, wpos.z + 0.05);
  // 足の下に歩く面がまったく無い所 (床のデータが無い所) では落ちない。下に歩く面がある所でだけ落ちる
  if (!air && gnd !== null && gnd < wpos.z - 0.05) { air = true; vz = 0; }
  for (let left = dt; air && left > 1e-6; left -= 0.02) {
    const h_ = Math.min(0.02, left); const z0 = wpos.z;
    wpos.z += vz * h_; vz -= GRAV * h_;
    const g2 = groundGap(wpos.x, wpos.y, z0 + 0.01);
    if (vz <= 0 && g2 !== null && wpos.z <= g2) { wpos.z = g2; air = false; vz = 0; }
    if (vz <= 0 && g2 === null && groundAt(wpos.x, wpos.y, 1e9) === null) { air = false; vz = 0; }
    // 周囲 1 m のいちばん低い床より下には落ちない (落ちる前にその床より上にいたときだけ。下にいたのを持ち上げはしない)
    if (air && vz <= 0) { const lo = lowestNear(wpos.x, wpos.y); if (lo !== null && z0 >= lo - 0.01 && wpos.z < lo) { wpos.z = lo; air = false; vz = 0; } }
    if (wpos.z < -1000) { air = false; vz = 0; }
  }
  walkCam(); showElev(); recStep(dt);
}
// ---- エレベーター・トラムの行き先の窓 (2026-11-04、利用者の指示「エレベーターはパネル押したときに四角のスクロールできるリストボックスがいい」
// 「エレベーター移動したら、おなじ x y の向きを逆にしてほしい」、トラムで駅へ移る)
// 出す時: 歩いていて乗り場 (地図のデータの transit) から 2 m 以内に来たとき、または乗り場から 5 m 以内のパネルを押したとき (いちばん近い乗り場)。
// 行は行き先ごとに 1 つ (名前は行き先の訳)。今いる所の行は「（今の階・閉じる）」「（今の駅・閉じる）」で、押すと閉じる。Esc でも閉じる。
// 閉じた乗り場・移った先の乗り場では、そこから 3 m 離れるまで、近づいただけでは出さない。パネルで出したものは 5 m 離れるまで出したまま
// ---- 重力の範囲 (2026-11-04、利用者の決定「床から 身長程度以内は重力ありで案 1 でいいんじゃない？」「但し床の上だけ」) ----
// region.json の gravity [種類 (zero・up・down), 行列 (範囲の枠 → 場所の座標。3×4), 箱の大きさ | null, 柱の底の多角形 | null, 柱の高さ, 名前] を、地点の中心からの座標にして持つ。
// 範囲が重なったら、体積のいちばん小さい範囲の種類にする (担当が決めたもの: 大きな無重力の箱の中の、重力のある通路・上下が逆の通路を先にする)
// 歩くモード:
//   zero (無重力): 落ちない・浮く。W・S は見ている向き (上下の傾きも) に進み、Space で上・C で下へ。ただし床の真上で、床から GRAV_FLOOR m 以内は、ふつうの重力
//   up (上向きの重力): 上下を逆にする。下向きの面 (頭の上の判定の面) を床にして立ち、上へ落ちる。カメラも上下を逆にする
let grav = [];
const GRAV_FLOOR = 1.8;
function setGravity(list, c) {
  grav = (list || []).map(([kind, W, size, poly, h, name]) => {
    const M = [W[0], W[1], W[2], W[3] - c[0], W[4], W[5], W[6], W[7] - c[1], W[8], W[9], W[10], W[11] - c[2]];
    // 逆行列 (3×3 の部分の逆と、平行移動)
    const a = M[0], b = M[1], d = M[2], e = M[4], f = M[5], g = M[6], hh = M[8], i = M[9], j = M[10];
    const det = a * (f * j - g * i) - b * (e * j - g * hh) + d * (e * i - f * hh);
    if (!(Math.abs(det) > 1e-12)) return null;
    const R = [(f * j - g * i) / det, (d * i - b * j) / det, (b * g - d * f) / det, (g * hh - e * j) / det, (a * j - d * hh) / det, (d * e - a * g) / det, (e * i - f * hh) / det, (b * hh - a * i) / det, (a * f - b * e) / det];
    let vol;
    if (size) vol = size[0] * size[1] * size[2];
    else { let A2 = 0; for (let k = 0; k < poly.length; k++) { const p = poly[k], q = poly[(k + 1) % poly.length]; A2 += p[0] * q[1] - q[0] * p[1]; } vol = Math.abs(A2) / 2 * h; }
    return { kind, M, R, size, poly, h, name, vol };
  }).filter(v => v).sort((x, y) => x.vol - y.vol);
}
function gravLocal(v, x, y, z) {
  const px = x - v.M[3], py = y - v.M[7], pz = z - v.M[11], R = v.R;
  return [R[0] * px + R[1] * py + R[2] * pz, R[3] * px + R[4] * py + R[5] * pz, R[6] * px + R[7] * py + R[8] * pz];
}
function gravIn(v, x, y, z) {
  const [lx, ly, lz] = gravLocal(v, x, y, z);
  if (v.size) return Math.abs(lx) <= v.size[0] / 2 && Math.abs(ly) <= v.size[1] / 2 && Math.abs(lz) <= v.size[2] / 2;
  if (lz < 0 || lz > v.h) return false;
  let inside = false; const P = v.poly;
  for (let k = 0, m = P.length - 1; k < P.length; m = k++) if ((P[k][1] > ly) !== (P[m][1] > ly) && lx < (P[m][0] - P[k][0]) * (ly - P[k][1]) / (P[m][1] - P[k][1]) + P[k][0]) inside = !inside;
  return inside;
}
// その点の重力の種類 (範囲の外は 'down')。体積の小さい順に並べてあるので、最初に当たった範囲
function gravAt(x, y, z) { for (const v of grav) if (gravIn(v, x, y, z)) return v.kind; return 'down'; }
// 今の歩くモードの重力: 'down' (ふつう)・'zero' (浮く)・'up' (上下が逆)
let inv = false;
function gravMode() {
  const k = gravAt(wpos.x, wpos.y, wpos.z + (inv ? -0.9 : 0.9));
  if (k === 'zero') { const g = inv ? null : groundAt(wpos.x, wpos.y, wpos.z + 0.05); return g !== null && wpos.z - g <= GRAV_FLOOR ? 'down' : 'zero'; }
  return k;
}
// 範囲を色で出す (zero 水色・up 赤紫。半分透ける)
let gravG = null;
function drawGravity() {
  if (gravG) { root.remove(gravG); gravG.traverse(o => { o.geometry?.dispose(); o.material?.dispose?.(); }); gravG = null; }
  if (!grav.length) return;
  gravG = new THREE.Group();
  for (const v of grav) {
    if (v.kind === 'down') continue;
    let geo;
    if (v.size) geo = new THREE.BoxGeometry(v.size[0], v.size[1], v.size[2]);
    else { const sh = new THREE.Shape(v.poly.map(p => new THREE.Vector2(p[0], p[1]))); geo = new THREE.ExtrudeGeometry(sh, { depth: v.h, bevelEnabled: false }); }
    const m = new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ color: v.kind === 'zero' ? 0x33ccff : 0xff33cc, transparent: true, opacity: 0.12, depthWrite: false, side: THREE.DoubleSide, clippingPlanes: [clip, clipLo] }));
    const M = v.M; m.matrixAutoUpdate = false; m.matrix.set(M[0], M[1], M[2], M[3], M[4], M[5], M[6], M[7], M[8], M[9], M[10], M[11], 0, 0, 0, 1);
    m.renderOrder = 5; m.userData = { grav: v.name }; gravG.add(m);
  }
  gravG.visible = !extView;   // 外部視点の間は重力の範囲の箱も出さない (updExtView)
  root.add(gravG);
}
// 無重力で浮く: 見ている向きに進む (Space 上・C 下)。床の判定・壁は見ない (すり抜けと同じ)
function floatStep(dt) {
  const spd = (run ? RUN : WALK) * dt;
  const f = new THREE.Vector3(Math.cos(wpitch) * Math.cos(wyaw), Math.cos(wpitch) * Math.sin(wyaw), Math.sin(wpitch)), r = new THREE.Vector3(Math.sin(wyaw), -Math.cos(wyaw), 0);
  const mv = new THREE.Vector3();
  if (keys.has('w')) mv.add(f); if (keys.has('s')) mv.sub(f); if (keys.has('d')) mv.add(r); if (keys.has('a')) mv.sub(r);
  if (keys.has(' ')) mv.z += 1; if (keys.has('c')) mv.z -= 1;
  if (mv.lengthSq() > 0) wpos.add(mv.normalize().multiplyScalar(spd));
  air = false; vz = 0;
}
// 上下が逆: 下向きの面 (ceilAt) を床にする。上へ落ちる
function invStep(dt) {
  const f = new THREE.Vector3(Math.cos(wyaw), Math.sin(wyaw), 0), r = new THREE.Vector3(-Math.sin(wyaw), Math.cos(wyaw), 0);
  const mv = new THREE.Vector3();
  if (keys.has('w')) mv.add(f); if (keys.has('s')) mv.sub(f); if (keys.has('d')) mv.add(r); if (keys.has('a')) mv.sub(r);
  const H = STANCE[stance].h;
  if (mv.lengthSq() > 0) {
    mv.normalize().multiplyScalar((run ? RUN : WALK) * STANCE[stance].sp * dt);
    const tx = wpos.x + mv.x, ty = wpos.y + mv.y;
    if (noclip || !blocked(wpos.x, wpos.y, wpos.z - H, tx, ty)) {
      const g = ceilAt(tx, ty, wpos.z - STEP);
      wpos.x = tx; wpos.y = ty;
      if (!air && g !== null && g <= wpos.z + STEP) wpos.z = g; else if (!air) air = true;
    }
  }
  const g0 = ceilAt(wpos.x, wpos.y, wpos.z - 0.05);
  if (!air && (g0 === null || g0 > wpos.z + 0.05)) { air = true; vz = 0; }
  for (let left = dt; air && left > 1e-6; left -= 0.02) {
    const h_ = Math.min(0.02, left), z0 = wpos.z;
    wpos.z -= vz * h_; vz -= GRAV * h_;   // vz は「床へ向かう」速さ (上へ)
    const g2 = ceilAt(wpos.x, wpos.y, z0 - 0.01);
    if (g2 !== null && wpos.z >= g2) { wpos.z = g2; air = false; vz = 0; }
    if (wpos.z > 1e4) { air = false; vz = 0; }
  }
}

let elevPinned = null, tClosed = null;
// 乗り場との距離: xy で測り、高さの差が STOP_DZ m を超えるものは遠い扱い (2026-11-05、利用者の報告「シャトルの行き先が選べない」: Orison のスペースポートのシャトルの印が
// 床より 1.9 m 下にあり、3 次元の距離では 2 m 以内に入れなかった。親の決定)
const STOP_DZ = 3, TRAM_NEAR = 10;
const stopDist = (t, q) => { const c = cur.center; if (Math.abs(t[1][2] - c[2] - q.z) > STOP_DZ) return Infinity; return Math.hypot(t[1][0] - c[0] - q.x, t[1][1] - c[1] - q.y); };
function hideTbox(closeAt) { const b = document.getElementById('tbox'); if (closeAt) tClosed = closeAt; elevPinned = null; b.style.display = 'none'; b.dataset.key = ''; }
function tboxRow(list, text, cls, fn) { const b = document.createElement('button'); b.textContent = text; if (cls) b.className = cls; b.onclick = fn; list.appendChild(b); }
function showElev(at, pinned) {
  if (!cur) return;
  const box = document.getElementById('tbox'), head = document.getElementById('tboxHead'), list = document.getElementById('tboxList');
  const q = at ? at : wpos, lim = at ? 5 : 2;
  const all = (cur.transit || []).filter(t => t[0].startsWith('elevator') || t[0] === 'tram');
  // トラムの乗り場は、印が線路の側 (壁の向こう) にあることが多いので、エレベーターより広い範囲 (TRAM_NEAR m) で出す
  // (2026-11-05、利用者「シャトルはエレベータと違い、判定の範囲を広げないと、壁の向こうに黄色の丸がある」。値は親の決定)
  const limOf = t => t[0] === 'tram' ? TRAM_NEAR : lim;
  if (!pinned && elevPinned) { if (stopDist(elevPinned, wpos) < (elevPinned[0] === 'tram' ? TRAM_NEAR + 1 : 5)) return; hideTbox(); }
  if (!pinned && tClosed) { if (stopDist(tClosed, wpos) < (tClosed[0] === 'tram' ? TRAM_NEAR + 1 : 3)) return; tClosed = null; }
  let near = null, nd = Infinity;
  for (const t of all) { const d = stopDist(t, q); if (d < limOf(t) && d < nd) { nd = d; near = t; } }
  if (pinned) { elevPinned = near; tClosed = null; }
  if (!near) {
    if (!pinned) { if (box.style.display === 'block') hideTbox(); return; }
    box.dataset.key = 'none'; list.innerHTML = '';
    // マイハンガーの中で、地図のデータに乗り場が無いとき (試しのサーバーはハンガーの地図を取り込まない) は、パネルからステーションへ戻れるようにする
    if (hangarBack) { head.textContent = 'ハンガーのエレベーター'; tboxHead(list, 'ステーションへ戻る'); tboxRow(list, hangarBack.label, '', () => leaveHangar()); tboxRow(list, '（閉じる）', 'cur', () => hideTbox()); box.style.display = 'block'; return; }
    head.textContent = '乗り場が見つかりません';
    tboxRow(list, 'このパネルの 5 m 以内に、地図のデータのエレベーター・トラムの乗り場が無い（閉じる）', 'cur', () => hideTbox());
    box.style.display = 'block'; return;
  }
  const key = near[0] + near[3] + near[1].join();
  if (box.dataset.key === key && box.style.display === 'block') return;
  box.dataset.key = key; list.innerHTML = '';
  if (near[0].startsWith('elevator') && near[6] && hangarBack && stationSaved) { hangarLiftWindow(near, head, list); box.style.display = 'block'; return; }
  if (near[0].startsWith('elevator')) {
    head.textContent = `エレベーター（今: ${near[4]}）`;
    // 同じ路線の乗り場を高さ (1 m 単位) でまとめる。高さごとに、今の乗り場に xy でいちばん近い乗り場 (同じエレベーター) を使う
    const byZ = new Map();
    for (const t of all.filter(t => t[0].startsWith('elevator') && t[3] === near[3])) {
      const k2 = Math.abs(t[1][2] - near[1][2]) <= 1 && t[5] === near[5] ? 'cur' : t[5] + '|' + t[1][2].toFixed(0); const d = Math.hypot(t[1][0] - near[1][0], t[1][1] - near[1][1]);
      const prev = byZ.get(k2); if (!prev || d < prev[1]) byZ.set(k2, [k2 === 'cur' ? near : t, d]);
    }
    const rows = [...byZ.values()].map(v => v[0]);
    const dup = new Map(); for (const t of rows) dup.set(t[4], (dup.get(t[4]) || 0) + 1);
    const row = t => {
      const nm = t[4] + (dup.get(t[4]) > 1 ? `（高さ ${t[1][2].toFixed(1)} m）` : '');
      if (t === near) tboxRow(list, `${nm}（今の階・閉じる）`, 'cur', () => hideTbox(near));
      else tboxRow(list, nm, '', () => elevMove(near, t));
    };
    const o = elevOrder(rows, near);
    for (const t of o.normal) row(t);
    if (o.docks.length) { tboxHead(list, '外部ポート'); for (const t of o.docks) row(t); }
    if (o.pads.length) { tboxHead(list, '着陸パッド'); for (const t of o.pads) row(t); }
    if (o.sizes.length) { tboxHead(list, 'マイハンガー'); for (const z of o.sizes) tboxRow(list, `マイハンガー（${z}）`, '', () => openHangar(z, near)); }
    if (o.back.length) { tboxHead(list, 'ステーションへ戻る'); for (const b of o.back) tboxRow(list, b.label, '', () => leaveHangar()); }
    if (rows.length <= 1 && !o.sizes.length && !o.back.length) { const n = document.createElement('div'); n.style.padding = '6px'; n.textContent = '（同じ路線のほかの高さの乗り場が、地図のデータに無い）'; list.appendChild(n); }
  } else {
    head.textContent = `トラム（今: ${near[4]}）`;
    // 同じ路線 (Line) の駅 (行き先の鍵ごと)。駅の乗り場は、地図のデータの並びで最初のもの
    const st = new Map();
    for (const t of VIEW.transit.filter(t => t[0] === 'tram' && t[3] === near[3])) if (!st.has(t[5])) st.set(t[5], t);
    const rows = [...st.values()].sort((a, b) => a[4].localeCompare(b[4], 'ja'));
    for (const t of rows) {
      if (t[5] === near[5]) tboxRow(list, `${t[4]}（今の駅・閉じる）`, 'cur', () => hideTbox(near));
      else tboxRow(list, t[4], '', () => tramMove(t));
    }
  }
  box.style.display = 'block';
}
// エレベーターの行の並び (旧ビューア viewer.js の openElevator と同じ決まり、利用者の指示「ゲーム内では 外部ポート 着陸パッド マイハンガーの順」):
//   ハンガーのエレベーター (乗り場の印 t[6] か、行き先の鍵に dockingport_NN・pad_NN のある乗り場がある) なら、ほかの階 (高い順) の後ろに、
//   外部ポート (dockingport_NN の番号の順) → 着陸パッド (pad_NN の番号の順) → マイハンガー (この地図のハンガーの入口の大きさ。S・M・L・XL の順)。
//   マイハンガーの中のハンガーのエレベーターなら、マイハンガーの代わりに「ステーションへ戻る」
const HANGAR_SIZES = ['S', 'M', 'L', 'XL'];
// (2026-11-05、利用者の指示「3D でのハンガーのエレベーターで移動先がないのは困る」) ハンガー行きの路線 (station.json の hangarLineTags = 路線の管理のタグ
// Spaceport_Hangar・TransportManager_Generic_Hangar) の乗り場も、ハンガーのエレベーターとする (Lorville・New Babbage・Area18・GrimHEX は鍵に pad・dockingport が無いため)。
// ハンガーの入口の無い地図 (hangarSizes が空) では、この見分けは使わない (マイハンガーの行が出ないので、今までの並びのまま)
// 行き先に出すマイハンガーの大きさ: 入口の大きさ (hangarSizes) のうち、アプリがハンガーの区画が決まると返したもの (hangarSizesOk)。返事が無ければ入口の大きさのまま
// (2026-11-05、利用者の指示「ないものはエレベーターの行き先に出さない」)
const hangarSizesOf = sv => !sv ? [] : Array.isArray(sv.hangarSizesOk) ? (sv.hangarSizes || []).filter(z => sv.hangarSizesOk.includes(z)) : (sv.hangarSizes || []);
const hangarLineOf = (t, sv) => hangarSizesOf(sv).length > 0 && (sv.hangarLineTags || []).includes(t[3]);
function elevOrder(rows, near, sv = VIEW) {
  const num = (re, t) => { const m = re.exec(t[5] || ''); return m ? +m[1] : null; };
  const dk = t => num(/dockingport_(\d+)$/i, t), pd = t => num(/pad_(\d+)$/i, t);
  const lift = !!near[6] || hangarLineOf(near, sv) || rows.some(t => dk(t) !== null || pd(t) !== null);
  const normal = rows.filter(t => !(lift && (dk(t) !== null || pd(t) !== null))).sort((a, b) => b[1][2] - a[1][2]);
  const docks = lift ? rows.filter(t => dk(t) !== null).sort((a, b) => dk(a) - dk(b)) : [];
  const pads = lift ? rows.filter(t => dk(t) === null && pd(t) !== null).sort((a, b) => pd(a) - pd(b)) : [];
  const sizes = lift && (!hangarBack || sv !== VIEW) ? HANGAR_SIZES.filter(z => hangarSizesOf(sv).includes(z)) : [];
  const back = hangarBack && near[6] ? [hangarBack] : [];
  return { normal, docks, pads, sizes, back };
}
// マイハンガーの中のエレベーターの窓 (2026-11-05、利用者の指示「3D でのハンガーのエレベーターで移動先がないのは困る」。親の決定):
//   入るときに乗った本体の乗り場 (hangarBack.stop) の路線の行き先を、本体のその乗り場で窓を開いたときと同じ一覧 (同じ高さのまとめ方・elevOrder の並び) で出す。
//   本体の行き先を選ぶと、本体へ戻ってその乗り場に立つ (leaveHangar と同じ移り方。向きは入るときに決めた向き)。
//   入るときに乗った乗り場は「ステーションへ戻る」の段に今のまま出す (本体の一覧からは除く)。マイハンガーの段は本体と同じ大きさ (今のハンガーは「（今のハンガー・閉じる）」)
function hangarLiftWindow(near, head, list) {
  const from = hangarBack.stop, sv = stationSaved.VIEW;
  const size = window.__hangar && window.__hangar.size;
  head.textContent = `ハンガーのエレベーター（今: マイハンガー${size ? `（${size}）` : ''}）`;
  tboxRow(list, '（今の階・閉じる）', 'cur', () => hideTbox(near));
  const body = (sv.transit || []).filter(t => t[0].startsWith('elevator') && t[3] === from[3]);
  const byZ = new Map();
  for (const t of body) {
    const k2 = Math.abs(t[1][2] - from[1][2]) <= 1 && t[5] === from[5] ? 'cur' : t[5] + '|' + t[1][2].toFixed(0); const d = Math.hypot(t[1][0] - from[1][0], t[1][1] - from[1][1]);
    const prev = byZ.get(k2); if (!prev || d < prev[1]) byZ.set(k2, [k2 === 'cur' ? from : t, d]);
  }
  const rows = [...byZ.values()].map(v => v[0]);
  const dup = new Map(); for (const t of rows) dup.set(t[4], (dup.get(t[4]) || 0) + 1);
  const row = t => { if (t === from) return; tboxRow(list, t[4] + (dup.get(t[4]) > 1 ? `（高さ ${t[1][2].toFixed(1)} m）` : ''), '', () => leaveHangar(t)); };
  const o = elevOrder(rows, from, sv);
  for (const t of o.normal) row(t);
  if (o.docks.length) { tboxHead(list, '外部ポート'); for (const t of o.docks) row(t); }
  if (o.pads.length) { tboxHead(list, '着陸パッド'); for (const t of o.pads) row(t); }
  if (o.sizes.length) {
    tboxHead(list, 'マイハンガー');
    for (const z of o.sizes) { if (z === size) tboxRow(list, `マイハンガー（${z}）（今のハンガー・閉じる）`, 'cur', () => hideTbox(near)); else tboxRow(list, `マイハンガー（${z}）`, '', () => openHangar(z, from)); }
  }
  tboxHead(list, 'ステーションへ戻る'); tboxRow(list, hangarBack.label, '', () => leaveHangar());
}
function tboxHead(list, text) { const d = document.createElement('div'); d.textContent = text; d.style.cssText = 'padding:4px 8px;background:#2a3038;color:#9cf;font-size:12px'; list.appendChild(d); }
// 録画に「ここで移った」を入れる (再生で前の点からなめらかにつながず、その時刻に飛ぶ。点の 9 番目が 1)
function recJump() { if (!rec) return; const ro = recOff(); rec.points.push([+rec.t.toFixed(2), +(wpos.x + ro[0]).toFixed(3), +(wpos.y + ro[1]).toFixed(3), +(wpos.z + ro[2]).toFixed(3), +wyaw.toFixed(4), +wpitch.toFixed(4), stance, noclip ? 1 : 0, 1]); recAcc = 0; }
// エレベーター: 行き先の扉の向きで立つ所を決める (2026-11-04、利用者の指示「扉の向きで決められないの？ 元々ずっとそう言ってる」。
// 同じ x・y の床はかごの床になるので探さない)。利用者の決定「2 行先の扉の床がある側で扉に背を向ける」、
// (2026-11-05、利用者の報告「カーゴセンターに出たところ、エレベータードアの裏に出現」・指示「指示はドアの向きを見て」) 降りる側は扉の部品の向きで決める:
//   行き先の乗り場に近いエレベーターの扉 (cur.elevDoors。[名前, 箱の低い角, 高い角, 前の向き [fx, fy]]。前の向きは扉の部品の局所の +Y を場所の座標に回したもの。糸が作る) のうち、
//   立つ所 = 扉の箱の中心 + 前の向き × (前の向きでの箱の厚みの半分 + DOOR_STEP m)。そこに行き先の高さの床があればそこに立ち、扉に背を向ける。
//   前の向きの側に床が無ければ、反対側に床があればそこ (原文「床がある側」)。どちらにも無ければ次の扉へ。前の向きの無い扉 (xy の成分が無い) は見ない
// 扉は、乗り場から xy で DOOR_NEAR m 以内・高さが乗り場の高さ −ELEV_BAND〜+3 m に掛かるものを、近い順に見る。どれにも床が無ければ、動かずに窓を閉じる (window.__elevHow = 'none')
const ELEV_BAND = 1.5, DOOR_STEP = 0.6, DOOR_NEAR = 6;
function elevMove(from, t) {
  // 行き先の乗り場が読み込んだ範囲 (中心から ±r/2) の外なら (Orison のスペースポート → シャトル・ステーションは横に約 200 m・下に 212 m)、
  // 行き先の案内と同じく、乗り場のまわり ±FIRST_R m を先に読み込んでから立つ所を決め、続けて ±r m を裏で読み込む
  const c = cur.cc, h = cur.r / 2, xyAbs = [wpos.x + cur.center[0], wpos.y + cur.center[1]], yaw0 = wyaw;
  hideTbox();
  // 範囲の中なら、行き先の乗り場のまわりの升のかたまり (形と当たり) が出そろってから立つ所を決める (ensureNear。2026-11-06)
  if (Math.abs(t[1][0] - c[0]) <= h && Math.abs(t[1][1] - c[1]) <= h && Math.abs(t[1][2] - c[2]) <= h) { const og = originGen; ensureNear(t[1].slice()).then(() => { if (og === originGen) elevPlace(t, xyAbs, yaw0); }); return; }
  const gen = ++goGen, r = regionR(), first = Math.min(FIRST_R, r), t0 = performance.now(); window.__elevLoad = {};
  loadRegion(t[1].slice(), first).then(() => {
    if (!cur || gen !== goGen) return; elevPlace(t, xyAbs, yaw0); window.__elevLoad.first = (performance.now() - t0) / 1000;
    if (r > first) return loadRegion(t[1].slice(), r, true, true);
  }).then(() => { if (gen === goGen) window.__elevLoad.all = (performance.now() - t0) / 1000; });
}
// 立つ所を決める (行き先のまわりは読み込んである)。xyAbs: 乗る前の x・y (絶対)、yaw0: 乗る前の向き
function elevPlace(t, xyAbs, yaw0) {
  const c = cur.center, tz = t[1][2] - c[2], sx = t[1][0] - c[0], sy = t[1][1] - c[1];
  const band = g => g !== null && g >= tz - ELEV_BAND ? g : null;
  // (2026-11-04、利用者の指示「扉の向きで決められないの？ 元々ずっとそう言ってる」) 同じ x・y の床は探さない (かごの床に立ってしまう)。最初から扉の向きで決める
  {
    const doors = (cur.elevDoors || []).map(d => { const mx = (d[1][0] + d[2][0]) / 2, my = (d[1][1] + d[2][1]) / 2; return { d, mx, my, dist: Math.hypot(mx - sx, my - sy) }; })
      .filter(o => o.dist <= DOOR_NEAR && o.d[2][2] >= tz - ELEV_BAND && o.d[1][2] <= tz + 3).sort((a, b) => a.dist - b.dist);
    let best = null;
    for (const o of doors) {
      const f = o.d[3]; if (!f) continue;
      // 前の向きでの箱の厚みの半分 (箱の半分の幅を前の向きに投影したもの。軸に沿った扉では薄い向きの幅の半分)
      const half = (Math.abs(f[0]) * (o.d[2][0] - o.d[1][0]) + Math.abs(f[1]) * (o.d[2][1] - o.d[1][1])) / 2;
      let cand = null;
      for (const sgn of [1, -1]) {   // 前の向きの側 → 反対側
        const px = o.mx + sgn * f[0] * (half + DOOR_STEP), py = o.my + sgn * f[1] * (half + DOOR_STEP);
        const gg = band(groundAt(px, py, tz + STEP)); if (gg === null) continue;
        cand = { o, px, py, gg, yaw: Math.atan2(py - o.my, px - o.mx) }; break;
      }
      if (cand) { best = cand; break; }
    }
    if (best) { wpos.set(best.px, best.py, best.gg); wyaw = best.yaw; window.__elevHow = 'door'; window.__elevDoor = best.o.d[0]; }
    // 扉の床も見つからないとき: 乗り場の位置・高さに立つ (担当が決めたもの)
    else { wpos.set(sx, sy, tz); wyaw = yaw0 + Math.PI; window.__elevHow = 'none'; }
  }
  air = false; vz = 0; onLadder = null;
  hideTbox(t); walkCam(); recJump(); outRequest(); review(true);   // エレベーターで移り終えたので、外判定を見直す (outTick)・出す升のかたまりを見直す (review)
}
// トラム: 行き先の駅の乗り場の床へ、向きは反対。読み込んでいない所なら、行き先の案内と同じく ±FIRST_R m を先に、続けて ±r m を裏で (window.__tram に時間)
function tramMove(t) { window.__tram = { to: t[4] }; travelTo(t[1], wyaw + Math.PI, t, window.__tram); }
// 行き先 abs (絶対の位置) の床へ、向き yaw で移る。closeAt: 着いた乗り場 (そこから 3 m 離れるまで窓を出さない)。tm: かかった時間を書く所 (first・all 秒)
// 読み込んでいない所なら、行き先の案内と同じく ±FIRST_R m を先に、続けて ±r m を裏で
function travelTo(abs, yaw, closeAt, tm, force) {
  const r = regionR();
  const place = () => { const c = cur.center; const x = abs[0] - c[0], y = abs[1] - c[1], z = abs[2] - c[2]; const g = groundAt(x, y, z + STEP); wpos.set(x, y, g !== null ? g : z); wyaw = yaw; air = false; vz = 0; onLadder = null; hideTbox(closeAt); walkCam(); recJump(); outRequest(); review(true); };   // 移り終えたので、外判定を見直す (outTick。トラム・マイハンガーのリフト・現在位置)・出す升のかたまりを見直す (review)
  const c = cur.cc, h = cur.r / 2;
  // 範囲の中なら、行き先のまわりの升のかたまり (形と当たり) が出そろってから立つ (ensureNear。2026-11-06)
  if (!force && Math.abs(abs[0] - c[0]) <= h && Math.abs(abs[1] - c[1]) <= h && Math.abs(abs[2] - c[2]) <= h) { const og = originGen; return ensureNear(abs.slice()).then(() => { if (og === originGen) place(); }); }
  hideTbox(); const gen = ++goGen; const t0 = performance.now(); tm = tm || {};
  const first = Math.min(FIRST_R, r);
  return loadRegion(abs.slice(), first).then(() => {
    if (!cur || gen !== goGen) return; place(); tm.first = (performance.now() - t0) / 1000;
    if (r > first) return loadRegion(abs.slice(), r, true, true);
  }).then(() => { if (gen === goGen) tm.all = (performance.now() - t0) / 1000; });
}
// ---- マイハンガー (2026-11-04、利用者の指示「マイハンガーの S M L XL がない」。旧アプリ (StationMapPanel.ShowHangarAsync) と同じく、
// 場所の既定のハンガーか、入口の class からたどったハンガーを、アプリ (試しのサーバー) が選ぶ)。
// 選ぶと: ステーションの VIEW・置き場・乗った乗り場を覚え、ハンガーの station.json に替えて、ハンガーの中を描く (SITE をアプリに渡して、ハンガーの区画を取り出す)。
// 始める所: ハンガーの地図のハンガーのエレベーターの乗り場 (印 t[6]) の床。無ければ (地図の無いハンガー) 区画の真ん中の真下の床。
// ステーションへ戻る: 乗った乗り場の床へ、向きは反対
let SITE = '', hangarBack = null, stationSaved = null;
async function openHangar(size, from) {
  hideTbox(); const msg = document.getElementById('msg'); msg.textContent = `マイハンガー（${size}）を用意しています...`;
  let info;
  try {
    if (APP) info = await new Promise((ok, ng) => { hangarWait = { ok, ng }; window.chrome.webview.postMessage({ type: 'hangar', size }); });
    else { info = await (await fetch(qs.get('api') + 'hangar?size=' + size)).json(); if (info.error) throw new Error(info.error); }
  } catch (e) { msg.textContent = `マイハンガー（${size}）: ${e.message}`; return; }
  msg.textContent = '';
  if (!stationSaved) stationSaved = { VIEW, BASE, title: document.getElementById('title').textContent };
  // ハンガーの中から別の大きさのハンガーへ移るとき (from が入るときに乗った乗り場) は、戻る向きは入るときに決めた向きのまま
  const backYaw = hangarBack && hangarBack.stop === from ? hangarBack.yaw : wyaw + Math.PI;
  hangarBack = { label: `${from[4]}（${stationSaved.VIEW.station}）`, pos: from[1].slice(), yaw: backYaw, stop: from };
  SITE = info.site; BASE = info.data; VIEW = await (await fetch(BASE + 'station.json')).json();
  document.getElementById('title').textContent = `マイハンガー（${size}）: ${info.title || ''}`; document.getElementById('hangarHow').textContent = info.how || ''; document.getElementById('hangarBar').style.display = 'block';
  renderTargets();
  const lift = (VIEW.transit || []).find(t => t[6]);
  const abs = lift ? lift[1] : VIEW.center;
  window.__hangar = { size, site: SITE, start: lift ? 'lift' : 'center' };
  await travelTo(abs, wyaw + Math.PI, lift || null, window.__hangar, true);
}
// to: 本体の乗り場 (VIEW.transit の行)。無ければ入るときに乗った乗り場 (今までの「ステーションへ戻る」)。移り方・向きは同じで、立つ乗り場だけが違う
async function leaveHangar(to) {
  if (!stationSaved || !hangarBack) return;
  const b = to ? { ...hangarBack, pos: to[1].slice(), stop: to } : hangarBack; SITE = ''; VIEW = stationSaved.VIEW; BASE = stationSaved.BASE; document.getElementById('title').textContent = stationSaved.title;
  stationSaved = null; hangarBack = null; document.getElementById('hangarBar').style.display = 'none';
  renderTargets();
  await travelTo(b.pos, b.yaw, b.stop, window.__hangarBack = {}, true);
}
let hangarWait = null;
addEventListener('keydown', e => {
  if (e.key !== 'Escape' || document.getElementById('tbox').style.display !== 'block' || document.getElementById('cbox').style.display === 'block') return;
  const k = elevPinned; hideTbox(k || undefined);
  if (!k && cur) { let n = null, nd = 3; for (const t of cur.transit || []) { const d = stopDist(t, wpos); if (d < nd) { nd = d; n = t; } } if (n) tClosed = n; }
});
// ---- 道の録画と再生: 0.1 秒ごとに [時刻 秒, x, y, z, 向き, 上下, 姿勢, すり抜け] を記録。保存は JSON ({ format: 'scjt-walk-path-1', station, label, center, points: [...], comments: [...] })
let recAcc = 0, cPos = null, cMarks = [];
function openComment() { cPos = null; document.getElementById('cpos').textContent = '(位置: 今の目線の高さ)'; const b = document.getElementById('cbox'); b.style.display = 'block'; const i = document.getElementById('ctext'); i.value = ''; i.focus(); }
function closeComment(ok) {
  const b = document.getElementById('cbox'); const t = document.getElementById('ctext').value.trim(); b.style.display = 'none';
  if (ok && t && cur) {
    const q = cPos || cam.position;
    if (rec) {
      // 録画中は録画 (rec.comments) にも入れる (再生のときにその時刻で出す)。印は下の DB の印 1 つだけ出す (2 つ重ねない)
      const ro = recOff(); const cm = { t: +rec.t.toFixed(2), pos: [+(q.x + ro[0]).toFixed(3), +(q.y + ro[1]).toFixed(3), +(q.z + ro[2]).toFixed(3)], text: t };
      rec.comments.push(cm); document.getElementById('recInfo').textContent = `録画中 ${rec.points.length} 点・コメント ${rec.comments.length}`;
    }
    // 録画中でもなくても DB に入れ (親の決定)、入れた所にすぐ印を出す (2026-11-05、利用者「enter で文字入れて印付けるのは、いつでもできて入力したら即反映してほしい」「DB格納でいつでも」)
    const c = cur.center; addNote([+(q.x + c[0]).toFixed(3), +(q.y + c[1]).toFixed(3), +(q.z + c[2]).toFixed(3)], t);
  }
  cPos = null; cv.focus?.();
}
// ---- DB のコメントの印 (StationNotes。view3d_notes.db): 場所の座標 (絶対) と文。3D を開いたとき・地点を読み込み直したとき (show) に {type:'notesGet'} で全部もらって出し、
//   入れたら {type:'noteAdd', pos, text, tmp} → {type:'noteAdded', id, tmp} で id が付く (返るまで待たずに印を出す)。札を押したら確認して {type:'noteDelete', id}。
//   印は noteMarks (再生の cMarks とは別。showComments で消えない)。userData.note = { id, tmp, pos (絶対), text }
let noteMarks = [], noteSeq = 0, notesReq = 0;
function noteDraw(note) {
  const c = cur.center;
  const g = commentMark({ t: 0, pos: [note.pos[0] - c[0], note.pos[1] - c[1], note.pos[2] - c[2]], text: note.text }, [0, 0, 0], true, noteMarks);   // 保存したコメントはいつも「出すべき」(shown)。出すかはスイッチ (commentShow・applyMarkVis) で決める (コメントのスイッチが OFF の間に読み込んだ印も、ON に戻せば出す)
  g.userData.note = note; return g;
}
function addNote(pos, text) {
  const note = { id: null, tmp: ++noteSeq, pos, text, deleted: false };
  noteDraw(note);
  if (APP) window.chrome.webview.postMessage({ type: 'noteAdd', pos, text, tmp: note.tmp });
}
const noteDispose = g => { root.remove(g); g.traverse(o => { o.geometry?.dispose(); o.material?.map?.dispose?.(); disposeMats(o); }); };
function requestNotes() {
  for (const g of noteMarks) noteDispose(g);
  noteMarks = [];
  if (APP) window.chrome.webview.postMessage({ type: 'notesGet', req: ++notesReq });
}
function onNotes(d) {
  if (d.req !== notesReq || !cur || !Array.isArray(d.list)) return;   // 古い頼み (その後に読み込み直した) の返事は捨てる
  for (const g of noteMarks) noteDispose(g);
  noteMarks = [];
  for (const n of d.list) if (Array.isArray(n.pos) && n.pos.length === 3) noteDraw({ id: n.id, tmp: 0, pos: n.pos, text: n.text || '', deleted: false });
}
function onNoteAdded(d) {
  const g = noteMarks.find(m => m.userData.note && m.userData.note.tmp === d.tmp && d.tmp);
  if (!g) return;
  const note = g.userData.note; note.id = d.id;
  if (note.deleted && APP) window.chrome.webview.postMessage({ type: 'noteDelete', id: note.id });   // id が付く前に消していた
}
function deleteNote(g) {
  const note = g.userData.note; note.deleted = true;
  noteDispose(g); noteMarks = noteMarks.filter(m => m !== g);
  if (note.id != null && APP) window.chrome.webview.postMessage({ type: 'noteDelete', id: note.id });
}
// 札に距離を足す (2026-11-05、利用者「ショップ x2 / 260m みたいだと分かりやすい」): 端末・乗り口の札に、自分 (歩くモードなら立っている所、見るモードなら回転の中心) からの距離 (m) を最後の行に足し、
// 0.5 秒ごとに描き直す。札は地点を作り直すたびに作り直す (show) ので、一覧は show で空にする
const distLabels = []; let lastDistLabel = 0;
function refreshDistLabels(force) {
  const now = performance.now(); if (!force && now - lastDistLabel < 500) return; lastDistLabel = now;
  const q = walk ? wpos : tgt;
  for (const e of distLabels) {
    const sp = e.sp; if (!sp.parent) continue;
    const d = Math.hypot(sp.position.x - q.x, sp.position.y - q.y, sp.position.z - q.z); const dm = Math.round(d) + 'm';
    if (e.last === dm) continue; e.last = dm;
    const lines = e.lines.concat([dm]); const nsp = textSprite(lines);
    sp.material.map?.dispose(); sp.material.map = nsp.material.map; sp.material.needsUpdate = true; nsp.material.dispose();
    const asp = nsp.scale.x / nsp.scale.y, h = 0.04 * lines.length; sp.scale.set(h * asp, h, 1);
  }
}
// 文字の札 (1 行の文字か、行の配列)
function textSprite(text, frame = '#ffee66', ink = '#ffffff') {
  const lines = Array.isArray(text) ? text : [text];
  const c = document.createElement('canvas'); const g = c.getContext('2d'); g.font = '28px sans-serif';
  const w = Math.ceil(Math.max(...lines.map(l => g.measureText(l).width))) + 24, hh = 44 * lines.length; c.width = w; c.height = hh; g.font = '28px sans-serif';
  g.fillStyle = 'rgba(0,0,0,0.75)'; g.fillRect(0, 0, w, hh); g.strokeStyle = frame; g.strokeRect(1, 1, w - 2, hh - 2); g.fillStyle = ink;
  lines.forEach((l, i) => g.fillText(l, 12, 31 + 44 * i));
  const sp = new THREE.Sprite(new THREE.SpriteMaterial({ map: new THREE.CanvasTexture(c), depthTest: false })); sp.scale.set(w / 44 * 0.5, 0.5 * lines.length, 1); sp.renderOrder = 20; return sp;
}
// コメントの印 (地点に玉、そこから上へ 1.5 m の引き出し線、その先に文)。co: 記録の中心 − 今の中心。list: 入れる一覧 (再生・録画は cMarks、DB の印は noteMarks)。
// userData.shown = 出すべきか (再生の時刻で変わる)。実際の visible はそれと「表示」のチェック (comment) の両方 (applyMarkVis)
function commentMark(cm, co, visible, list = cMarks) {
  const g = new THREE.Group(); const p0 = new THREE.Vector3(cm.pos[0] + co[0], cm.pos[1] + co[1], cm.pos[2] + co[2]), p1 = p0.clone(); p1.z += 1.5;
  const ball = new THREE.Mesh(new THREE.SphereGeometry(0.12), new THREE.MeshBasicMaterial({ color: 0xffee66, depthTest: false })); ball.position.copy(p0); ball.renderOrder = 20;
  const lg = new THREE.BufferGeometry().setFromPoints([p0, p1]); const line = new THREE.Line(lg, new THREE.LineBasicMaterial({ color: 0xffee66, depthTest: false })); line.renderOrder = 20;
  const sp = textSprite(cm.text); sp.position.copy(p1); sp.position.z += 0.3;
  g.add(ball, line, sp); g.userData.shown = visible; g.visible = visible && commentShow(); g.userData.t = cm.t; g.userData.cat = 'comment'; g.userData.sp = sp; root.add(g); list.push(g);
  return g;
}
function showComments(list) {
  // 再生: コメントの印は、その時刻になったら出す (playStep)
  for (const m of cMarks) root.remove(m); cMarks = [];
  const co = play && play.center ? [play.center[0] - cur.center[0], play.center[1] - cur.center[1], play.center[2] - cur.center[2]] : [0, 0, 0];
  for (const cm of list) commentMark(cm, co, false);
}
const recOff = () => rec ? [cur.center[0] - rec.center[0], cur.center[1] - rec.center[1], cur.center[2] - rec.center[2]] : [0, 0, 0];
function recStep(dt) { if (!rec) return; recAcc += dt; rec.t += dt; const ro = recOff(); if (recAcc >= 0.1) { recAcc = 0; rec.points.push([+rec.t.toFixed(2), +(wpos.x + ro[0]).toFixed(3), +(wpos.y + ro[1]).toFixed(3), +(wpos.z + ro[2]).toFixed(3), +wyaw.toFixed(4), +wpitch.toFixed(4), stance, noclip ? 1 : 0]); document.getElementById('recInfo').textContent = `録画中 ${rec.points.length} 点`; } }
function playStep(dt) {
  if (!play.points.length) { play = null; return; }
  const P = play.points; let a, b, f;
  if (play.cum) {
    // 一定速度: 道のり play.d を進め、その道のりの所の 2 点の間を割る。コメントを出す時刻は、その所の録画の時刻
    const C = play.cum; play.d += play.speed * dt; while (play.i < P.length - 1 && C[play.i + 1] <= play.d) play.i++;
    a = P[play.i]; b = P[Math.min(play.i + 1, P.length - 1)]; const L = C[Math.min(play.i + 1, P.length - 1)] - C[play.i];
    f = L > 0 ? Math.min(1, (play.d - C[play.i]) / L) : 0; play.t = a[0] + (b[0] - a[0]) * f;
  } else {
    play.t += dt; while (play.i < P.length - 1 && P[play.i + 1][0] <= play.t) play.i++;
    a = P[play.i]; b = P[Math.min(play.i + 1, P.length - 1)]; f = b[0] > a[0] ? Math.min(1, (play.t - a[0]) / (b[0] - a[0])) : 0;
  }
  const po = play.center ? [play.center[0] - cur.center[0], play.center[1] - cur.center[1], play.center[2] - cur.center[2]] : [0, 0, 0];
  wpos.set(a[1] + (b[1] - a[1]) * f + po[0], a[2] + (b[2] - a[2]) * f + po[1], a[3] + (b[3] - a[3]) * f + po[2]); wyaw = a[4] + (b[4] - a[4]) * f; wpitch = a[5]; stance = a[6]; noclip = a[7] === 1; updWalkUi();
  { const on = commentShow(); for (const m of cMarks) { const s = play.t >= m.userData.t; m.userData.shown = s; m.visible = s && on; } }
  walkCam(); document.getElementById('recInfo').textContent = `再生中 ${play.i + 1} / ${P.length}`;
  if (play.i >= P.length - 1) { play = null; document.getElementById('recInfo').textContent = '再生おわり'; }
}
// 確かめ用 (Edge headless の試しから呼ぶ)
window.__probe = () => { cam.updateMatrixWorld(); ray.setFromCamera(new THREE.Vector2(0, 0), cam); const hs = ray.intersectObjects(Object.values(groups).filter(g => g).flat().filter(m => m.visible), false); if (!hs.length) return ['当たり無し', cam.position.toArray(), cam.getWorldDirection(new THREE.Vector3()).toArray()]; const h0 = hs[0]; const k = h0.object.userData.k; const pt = h0.object.userData.ch ? partAt(h0.object.userData.ch, k, h0.object.userData.t0 + h0.faceIndex) : null; return [k, +h0.distance.toFixed(2), pt ? pt[1] : '', h0.point.toArray().map(v => +v.toFixed(2))]; };
window.__cur = () => cur; window.__buf = () => chunks; window.__tbox = (x, y, z) => showElev(new THREE.Vector3(x, y, z), true); window.__gravList = () => grav; window.__gravAt = (x, y, z) => gravAt(x, y, z); window.__loadAt = (c, r) => loadRegion(c, r);
window.__walk = { low: (x, y) => lowestNear(x, y), setPitch: v => { wpitch = v; walkCam(); }, place: (x, y, z, yaw) => { wpos.set(x, y, z); wyaw = yaw; air = false; vz = 0; walkCam(); }, ground: (x, y, z) => groundAt(x, y, z), gaps: () => gapLog, seg: (...a) => segHit(...a), on: () => walk, pos: () => [wpos.x, wpos.y, wpos.z, wyaw, run, stance, air, noclip], cam, rec: () => rec, setRec: r => { play = { t: 0, i: 0, points: r.points, center: r.center }; showComments(r.comments || []); }, marks: () => cMarks.map(m => [m.visible, m.userData.t]), openComment, closeComment, keys };

const WALK_DT_MAX = 0.1;
function keyMove() {
  const now = performance.now(), raw = (now - lastT) / 1000, dt = Math.min(0.5, raw); lastT = now;
  if (!cur) return;
  // 歩くモードの 1 コマの動きに使う時間は WALK_DT_MAX 秒まで (2026-11-06、利用者の報告「止まってた間のキー入力を処理するから飛ぶ」。親の決定。前は上の 0.5 秒までで、走りだと 1 コマで 5 m 進んだ)
  if (walk) walkStep(play ? dt : Math.min(WALK_DT_MAX, dt));   // 再生 (play) の時間は縮めない
  else if (keys.size) {
    // 速さは時間で決める (描くのが遅くても進む): 毎秒 max(3 m, 中心までの距離) (Shift で 4 倍)
    const sp = Math.max(3, dist) * dt * (keys.has('shift') ? 4 : 1);
    const fwd = cam.getWorldDirection(new THREE.Vector3()), right = new THREE.Vector3(-Math.sin(az), Math.cos(az), 0);
    if (keys.has('w')) tgt.addScaledVector(fwd, sp); if (keys.has('s')) tgt.addScaledVector(fwd, -sp);
    if (keys.has('d')) tgt.addScaledVector(right, sp); if (keys.has('a')) tgt.addScaledVector(right, -sp);
    if (keys.has('e')) tgt.z += sp; if (keys.has('q')) tgt.z -= sp;
    upd();
  }
  speedTick(raw);   // 速さ (細かい物を読むかの決まり。下の「升のかたまりの出し入れ」)
}
const ray = new THREE.Raycaster();
// 部品の判定の面 (判定の色ごとの三角形の数と高さ)。F = 三角形の位置、LB = 三角形ごとの判定の色 (LABK の番号)、ID・pi = かたまりの中の部品の番号 (ID が無ければ F の全部)
function partFloors(F, LB, ID, pi) {
  const res = [];
  for (const k of Object.keys(OCOL)) {
    const li = LABK.indexOf(k), hs = new Map(); let n = 0;
    for (let q = 0; q < LB.length; q++) { if (LB[q] !== li || (ID && ID[q] !== pi)) continue; const b = q * 9;
      n++; const z = (F[b + 2] + F[b + 5] + F[b + 8]) / 3 + cur.center[2];
      const key = z.toFixed(1); hs.set(key, (hs.get(key) || 0) + 1);
    }
    if (n) res.push(`${NAME[k]} ${n} 三角形・高さ (絶対 m: 三角形の数) ` + [...hs.entries()].sort((a, b) => b[1] - a[1]).slice(0, 12).map(([z, c]) => `${z}: ${c}`).join(', ') + (hs.size > 12 ? ` ほか ${hs.size - 12} の高さ` : ''));
  }
  return res.length ? res.join('\n') : '床・階段・坂・筒の底・カウンターの三角形は無い';
}
function setCenter(pt) {
  const cp = cam.position.clone(); tgt.copy(pt);
  const v = cp.clone().sub(tgt); dist = Math.max(0.02, v.length());
  el2 = Math.asin(Math.max(-1, Math.min(1, v.z / dist))); az = Math.atan2(v.y, v.x); upd();
}
window.__view = { hi: () => hiM ? hiM.type : null, cam, tgt: () => tgt, setCenter, pick: (x, y) => pick({ clientX: x, clientY: y }), state: () => ({ dist, az, el2 }), load: (c, r) => loadRegion(c, r), targets: () => VIEW?.targets };
const SALES_R = 3;   // 端末の印からこの距離 (m) 以内を押したら販売品 (2026-11-05、利用者の報告「ショップのターミナル押すと売ってるものと値段が出るのは実装しなかったの？」: 1.5 m では端末の上の方を押すと外れた。親の決定)
function requestSales(t) {
  window.__salesAsked = { guid: t.shopGuid, entity: t.shop };
  if (APP) window.chrome.webview.postMessage({ type: 'sales', guid: t.shopGuid, entity: t.shop });
  else if (qs.get('api')) fetch(qs.get('api') + 'sales?guid=' + encodeURIComponent(t.shopGuid) + '&entity=' + encodeURIComponent(t.shop)).then(x => x.json()).then(showSales).catch(e => showSales({ title: '販売品', shops: [{ name: t.shop, note: e.message }] }));
}
// 販売品の枠 (アプリの地図の［MAP］の販売品の枠と同じ中身: {title, shops: [{name, note} | {name, tables: [{cols, rows, left}]}]})。✕ か Esc で閉じる
// 枠の上でホイールを回したら枠の中がスクロールし、画面の拡大縮小 (canvas の wheel) には取られないようにする (2026-11-05、利用者の指示「ホイールスクロール」)。枠は overflow:auto と高さの上限 (view3d.html の #sbox)
document.getElementById('sbox').addEventListener('wheel', e => { e.stopPropagation(); }, { passive: true });
// 販売品の枠の検索・表示する種類・並び替え (2026-11-05、利用者の指示「販売品は出る。検索・表示する種類の設定・並び替えができると完璧」)。
//   検索: 品名の一部 (大文字小文字を区別しない。打つたびに絞る。覚えない)。品名はアプリの返事 (StationView3DShops.Sales) の「品名」の列 (ゲームの英語の名前) だけで、日本語名は返事に無い。
//   表示する種類: 品目の表の「種類」の列 (［詳細］と同じ分類の言葉。アプリの DetailTypeLabel) ごとのチェックと数。外した種類は localStorage (view3d.salesHidden = 種類の名前の配列) に覚え、
//     別の店・次に開いたときも外れたまま (その店に無い種類の覚えは消さない)。［全部］はこの店の種類を全部出す形に戻す。種類が SALES_FOLD_TYPES より多いときは ▼ を閉じて始める。
//     コモディティの表 (種類の列が無い) は種類で絞らない。
//   並び替え: 元の順 (アプリの返事の順 = 品名の順)・品名・価格・種類と、昇順・降順。localStorage (view3d.salesSort = {key, desc}) に覚える。覚えが無ければ元の順。
//     価格は、表の左寄せでない最初の列 (価格・購入・買値。「-」なら次の列) の金額。金額の無い品は、昇順でも降順でも後ろ。同じ値の品は元の順
const SALES_FOLD_TYPES = 8;
const salesLoad = (k, def) => { try { const v = JSON.parse(localStorage.getItem(k) || 'null'); return v ?? def; } catch (_) { return def; } };
const salesSave = (k, v) => { try { if (v == null) localStorage.removeItem(k); else localStorage.setItem(k, JSON.stringify(v)); } catch (_) { } };
// 価格の列の金額 (「1,234 aUEC」・レンタルの「1日 1,234 aUEC / …」の最初の金額)。無ければ null
function salesPrice(tb, r) {
  for (let i = tb.left; i < r.length; i++) {
    const v = String(r[i] ?? '');
    if (v === '-' || v === '') continue;
    const m = /([\d,]+(?:\.\d+)?)\s*aUEC/.exec(v);
    if (m) return parseFloat(m[1].replace(/,/g, ''));
  }
  return null;
}
function showSales(d) {
  const box = document.getElementById('sbox'); box.innerHTML = ''; window.__sales = d;
  // 見出し (題と ✕、検索・並び替え・表示する種類) は、中をスクロールしても上に残す (2026-11-05、利用者「スクロールすると閉じるのバッテンが押せなくなるのは使いにくい」)
  const head = document.createElement('div'); head.style.cssText = 'position:sticky;top:0;background:rgba(0,0,0,.92);padding:4px 0;z-index:1;margin-bottom:6px';
  const h = document.createElement('div'); h.style.cssText = 'display:flex;justify-content:space-between;font-weight:bold';
  const ti = document.createElement('span'); ti.textContent = d.title || '販売品'; const x = document.createElement('button'); x.textContent = '✕'; x.style.cssText = 'width:auto;display:inline;border:1px solid #666'; x.onclick = () => { box.style.display = 'none'; };
  h.appendChild(ti); h.appendChild(x); head.appendChild(h);
  box.appendChild(head);
  const body = document.createElement('div'); box.appendChild(body);
  const tables = (d.shops || []).flatMap(sh => sh.tables || []);
  const typeCol = tb => tb.cols.indexOf('種類');
  // この店の種類と数 (品目の表の「種類」の列。出てくる順)
  const typeCount = new Map();
  for (const tb of tables) { const ci = typeCol(tb); if (ci < 0) continue; for (const r of tb.rows) typeCount.set(r[ci], (typeCount.get(r[ci]) || 0) + 1); }
  // 覚えが配列でなければ (形の違う値) 空とし、文字列でない要素は捨てる
  const hv = salesLoad('view3d.salesHidden', []);
  const hidden = new Set((Array.isArray(hv) ? hv : []).filter(t => typeof t === 'string'));
  const ss = salesLoad('view3d.salesSort', null);
  const sort = { key: ss && ['name', 'price', 'type'].includes(ss.key) ? ss.key : '', desc: !!(ss && ss.desc) };
  let q = null, typeBox = null, typeSum = null;
  if (tables.length) {
    const ctl = document.createElement('div'); ctl.style.cssText = 'display:flex;flex-wrap:wrap;align-items:center;gap:6px;margin-top:6px;font-weight:normal';
    q = document.createElement('input'); q.type = 'search'; q.id = 'salesQ'; q.placeholder = '品名の一部で絞り込み'; q.style.cssText = 'flex:1;min-width:160px;background:#222;color:#ddd;border:1px solid #444;padding:3px';
    const lab = document.createElement('span'); lab.textContent = '並び:';
    const sortSel = document.createElement('select'); sortSel.style.cssText = 'background:#222;color:#ddd;border:1px solid #444';
    for (const [v, t] of [['', '元の順'], ['name', '品名'], ['price', '価格'], ['type', '種類']]) { const o = document.createElement('option'); o.value = v; o.textContent = t; sortSel.appendChild(o); }
    const dirB = document.createElement('button'); dirB.style.cssText = 'width:auto;display:inline;border:1px solid #666;padding:2px 8px';
    const updDir = () => { dirB.textContent = sort.desc ? '降順 ▼' : '昇順 ▲'; dirB.disabled = !sort.key; };
    sortSel.value = sort.key; updDir();
    sortSel.onchange = () => { sort.key = sortSel.value; updDir(); salesSave('view3d.salesSort', sort.key ? { key: sort.key, desc: sort.desc } : null); render(); };
    dirB.onclick = () => { sort.desc = !sort.desc; updDir(); salesSave('view3d.salesSort', { key: sort.key, desc: sort.desc }); render(); };
    q.oninput = () => render();
    ctl.append(q, lab, sortSel, dirB); head.appendChild(ctl);
    if (typeCount.size) {
      typeBox = document.createElement('details'); typeBox.open = typeCount.size <= SALES_FOLD_TYPES; typeBox.style.cssText = 'margin-top:4px;font-weight:normal';
      typeSum = document.createElement('summary'); typeSum.style.cssText = 'cursor:pointer;color:#9cf'; typeBox.appendChild(typeSum);
      const wrap = document.createElement('div'); wrap.style.cssText = 'display:flex;flex-wrap:wrap;gap:2px 12px;margin-top:4px;align-items:center';
      const allB = document.createElement('button'); allB.textContent = '全部'; allB.style.cssText = 'width:auto;display:inline;border:1px solid #666;padding:1px 8px';
      wrap.appendChild(allB);
      const cbs = [];
      for (const [t, n] of typeCount) {
        const l = document.createElement('label'); l.style.cssText = 'display:inline;white-space:nowrap;cursor:pointer';
        const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = !hidden.has(t);
        cb.onchange = () => { if (cb.checked) hidden.delete(t); else hidden.add(t); salesSave('view3d.salesHidden', [...hidden]); render(); };
        l.append(cb, `${t || '（種類なし）'} (${n})`); wrap.appendChild(l); cbs.push([t, cb]);
      }
      allB.onclick = () => { for (const [t, cb] of cbs) { hidden.delete(t); cb.checked = true; } salesSave('view3d.salesHidden', [...hidden]); render(); };
      typeBox.appendChild(wrap); head.appendChild(typeBox);
    }
  }
  const cmpS = (a, b) => String(a).localeCompare(String(b), 'ja', { sensitivity: 'base' });
  function render() {
    body.innerHTML = '';
    const word = q ? q.value.trim().toLowerCase() : '';
    let shown = 0, total = 0;
    for (const sh of d.shops || []) {
      const n = document.createElement('div'); n.textContent = sh.name; n.style.cssText = 'margin:6px 0 2px;color:#9cf'; body.appendChild(n);
      if (sh.note) { const m = document.createElement('div'); m.textContent = sh.note; body.appendChild(m); continue; }
      for (const tb of sh.tables || []) {
        const ci = typeCol(tb);
        total += tb.rows.length;
        const rows = tb.rows.map((r, i) => ({ r, i })).filter(({ r }) => (!word || String(r[0]).toLowerCase().includes(word)) && (ci < 0 || !hidden.has(r[ci])));
        if (sort.key) {
          const sgn = sort.desc ? -1 : 1;
          rows.sort((A, B) => {
            let c = 0;
            if (sort.key === 'price') {
              const pa = salesPrice(tb, A.r), pb = salesPrice(tb, B.r);
              if (pa == null || pb == null) c = pa == null && pb == null ? 0 : pa == null ? 1 : -1;   // 金額の無い品は後ろ
              else c = sgn * (pa - pb);
            }
            else if (sort.key === 'type') c = ci >= 0 ? sgn * cmpS(A.r[ci], B.r[ci]) : 0;
            else c = sgn * cmpS(A.r[0], B.r[0]);
            return c || A.i - B.i;
          });
        }
        shown += rows.length;
        const cnt = document.createElement('div'); cnt.style.cssText = 'font-size:12px;color:#aaa'; cnt.textContent = rows.length === tb.rows.length ? `${tb.rows.length} 品` : `${rows.length} / ${tb.rows.length} 品`; body.appendChild(cnt);
        if (!rows.length) { const m = document.createElement('div'); m.textContent = '条件に合う品がありません。'; m.style.margin = '2px 0 10px'; body.appendChild(m); continue; }
        const table = document.createElement('table'); const tr = document.createElement('tr');
        for (const c of tb.cols) { const th = document.createElement('th'); th.textContent = c; tr.appendChild(th); } table.appendChild(tr);
        for (const { r } of rows) { const tr2 = document.createElement('tr'); r.forEach((v, i) => { const td = document.createElement('td'); td.textContent = v; if (i >= tb.left) td.style.textAlign = 'right'; tr2.appendChild(td); }); table.appendChild(tr2); }
        body.appendChild(table);
      }
    }
    if (typeSum) { const on = [...typeCount.keys()].filter(t => !hidden.has(t)).length; typeSum.textContent = `表示する種類 (${on} / ${typeCount.size})・表示 ${shown} / ${total} 品`; }
  }
  render();
  box.style.display = 'block';
}
addEventListener('keydown', e => { if (e.key === 'Escape') document.getElementById('sbox').style.display = 'none'; });
function pick(e) {
  if (!cur) return;
  cam.updateMatrixWorld();
  const r = cv.getBoundingClientRect();
  ray.setFromCamera(new THREE.Vector2((e.clientX - r.left) / r.width * 2 - 1, -(e.clientY - r.top) / r.height * 2 + 1), cam);
  const ms = Object.values(groups).filter(g => g).flat().filter(m => m.visible);
  const hit = ray.intersectObjects(ms, false).find(h => visZ(h.point.z));
  if (hit) { hit.kind = hit.object.userData.k; hit.tri = hit.object.userData.t0 + hit.faceIndex; hit.ch = hit.object.userData.ch; }
  const box = document.getElementById('pick');
  // DB のコメントの印の札 (文) を押したら、確認して消す (親の決定: 消せないと増える一方のため)。札は形より手前 (depthTest なし) に描いているので、形に当たっていても札を先に見る。
  // コメントの欄を開いている間は見ない (押した所に付ける方を優先)。形に当たらなくても (空中の札) 効く
  if (document.getElementById('cbox').style.display !== 'block') {
    const sps = noteMarks.filter(g => g.visible).map(g => g.userData.sp); const nh = sps.length ? ray.intersectObjects(sps, false)[0] : null;
    if (nh && (!hit || nh.distance <= hit.distance + 1.5)) { const g = nh.object.parent; const note = g.userData.note; if (window.confirm(`このコメントを消しますか？\n「${note.text}」`)) deleteNote(g); return; }
  }
  // 歩くモードで乗り場の印 (エレベーター・トラムの黄色の玉) を押したら、その印のまわりの乗り場の行き先の窓を開く (操作盤を押したときと同じ。着いた直後の 3 m の決まりにも掛からない)
  // (2026-11-05、親の決定)。印は形より手前に描いているので、コメントの札と同じく、形に当たっていても印を先に見る
  if (walk && shpG && shpG.visible && document.getElementById('cbox').style.display !== 'block') {
    const balls = []; shpG.traverse(o => { if (o.isMesh && o.userData.cat === 'transit' && o.visible) balls.push(o); });
    const bh = balls.length ? ray.intersectObjects(balls, false)[0] : null;
    if (bh && (!hit || bh.distance <= hit.distance + 1.5)) { showElev(bh.object.position.clone(), true); return; }
  }
  if (!hit) { box.style.display = 'none'; return; }
  if (document.getElementById('cbox').style.display === 'block') { cPos = hit.point.clone(); document.getElementById('cpos').textContent = `(位置: 押した所 ${cPos.x.toFixed(1)}, ${cPos.y.toFixed(1)}, ${cPos.z.toFixed(1)})`; document.getElementById('ctext').focus(); return; }
  // 店の端末 (station.json の端末のうち、店 (shopGuid) の結び付いたもの) から 1.5 m 以内を押したら、その店の販売品を出す
  // (2026-11-04、利用者の指示「押したら売ってるリストと値段を出してほしい」。端末と店の結び付きは地図のデータ (端末の shop・shopEntity) のまま)
  { const c = cur.center; let tn = null, td = SALES_R, tAny = null, tdAny = SALES_R;
    for (const t of cur.targets || []) { if (!t.kind.startsWith('terminal_')) continue; const d = Math.hypot(t.pos[0] - c[0] - hit.point.x, t.pos[1] - c[1] - hit.point.y, t.pos[2] - c[2] - hit.point.z); if (d < tdAny) { tdAny = d; tAny = t; } if (t.shopGuid && d < td) { td = d; tn = t; } }
    if (tn) { requestSales(tn); return; }
    // 店の結び付いていない端末を押したときは、そう出す (何も出ないと押せていないのか分からない)
    if (tAny) { showSales({ title: '販売品', shops: [{ name: tAny.label || tAny.kind, note: 'この端末に結び付いた店が地図のデータにありません' }] }); return; } }
  if (!walk) setCenter(hit.point);
  else { if (hit.ch) { const w_ = partAt(hit.ch, hit.kind, hit.tri)[1].split('_');
      // エレベーターの操作盤: 名前の語に elevator と panel (hightech 型 console_elevator_1_panel_a など)、または elevator・console・freestanding
      // (util 型のハンガー・ステーションの console_elevator_1_freestanding_a。2026-11-05、親の決定。語の組は Data.p4k の形の名前で確かめた)
      if (w_.includes('elevator') && (w_.includes('panel') || (w_.includes('console') && w_.includes('freestanding')))) showElev(hit.point, true); }
    // トラム: 乗り場 (地図のデータの tram) から 5 m 以内のどの面を押しても、その駅の一覧を出す (トラムのパネルの部品の名前は確かめていないので、名前では決めない)
    if ((cur.transit || []).some(t => t[0] === 'tram' && stopDist(t, hit.point) < 5)) showElev(hit.point, true); }
  const k = hit.kind; if (!hit.ch) return;
  const ch = hit.ch, pid = ch.ID[ch.offs[k][0] / 9 + hit.tri];
  const part = ch.parts[pid];
  if (!DEBUG) return;   // 下部の枠 (#pick) は設定「マップのデバッグ表示」のときだけ (上の先頭の DEBUG)
  box.style.display = 'block';
  box.textContent = `${part[1]}  (inst ${part[0]}・${part[4]})\n押した面: ${NAME[k]}\n部品の種類: ${NAME[part[2]]}\n部品の中の判定の面:\n${partFloors(ch.F, ch.LB, ch.ID, pid)}\n押した所の高さ: 中心から ${hit.point.z.toFixed(2)} m (絶対 ${(hit.point.z + cur.center[2]).toFixed(2)} m)
押した所 (絶対): ${(hit.point.x + cur.center[0]).toFixed(2)}, ${(hit.point.y + cur.center[1]).toFixed(2)}, ${(hit.point.z + cur.center[2]).toFixed(2)}`;
}

// ===== データ (StationView3D.cs) =====
let VIEW = null, REG = null, REGBASE = ''; const geoCache = new Map();
async function loadGeo(gi) {
  if (geoCache.has(gi)) return geoCache.get(gi);
  const url = REGBASE + REG.geos[gi];
  const resp = await fetch(url);
  const buf = await resp.arrayBuffer();
  // 読めなかったら、どのファイルか・応答・大きさを出す (形の頭 24 バイトより短いと配列を作れない)
  if (!resp.ok || buf.byteLength < 24) throw new Error(`区画のファイルを読めませんでした: ${url} (応答 ${resp.status}・${buf.byteLength} バイト)`);
  const dv = new DataView(buf);
  // 形 (StationView3D.BuildSocpak): 頭 SCJT3DG2・表の位置 (int64)・表の長さ (int32)・空き 4 バイト、中身 (24 バイト目から)、最後に表 (JSON)
  const magic = new TextDecoder().decode(new Uint8Array(buf, 0, 8)); if (magic !== 'SCJT3DG2') throw new Error('形の違うファイル ' + url);
  const hp = Number(dv.getBigInt64(8, true)), jl = dv.getInt32(16, true); const head = JSON.parse(new TextDecoder().decode(new Uint8Array(buf, hp, jl)));
  const base = 24;
  const g = { buf, base, parts: head.parts, inst: new Map(head.inst.map(x => [x[0], x])), triStart: [] };
  let s = 0; for (const p of g.parts) { g.triStart.push(s); s += p[5]; } g.tris = s;
  geoCache.set(gi, g); return g;
}
// 地点: 中心 c (場所の座標) のまわり ±r m の立方体 (アプリが区画を取り出す範囲)。形は、その中から升のかたまりごとに出す (下の「升のかたまりの出し入れ」)
let loading = false;
// keep: 近づいて読み込み直すとき (2026-11-04、利用者の指示「近づいたら書かないと」)。見ている所・歩いている所・選んだフロアをそのままにする
// アプリのログに 1 行残す (2026-11-05、利用者の報告「何度も作画中を繰り返してる」の原因を追うため。呼び元の関数名も付ける)
function logApp(message) { if (APP) try { window.chrome.webview.postMessage({ type: 'log', message }); } catch (_) { } }
async function loadRegion(c, r, keep, small) {
  if (loading) return; loading = true;
  logApp(`loadRegion c=${c.map(v => v.toFixed(1)).join(',')} r=${r} keep=${!!keep} small=${!!small} cur=${cur ? cur.cc.map(v => v.toFixed(1)).join(',') + ' r=' + cur.r : '-'} wpos=${walk ? [wpos.x, wpos.y, wpos.z].map(v => v.toFixed(1)).join(',') : '-'} from=${((new Error().stack || '').split('\n')[2] || '').trim()}`);
  document.getElementById('prog').classList.toggle('small', !!small);
  const msg = document.getElementById('msg'); msg.textContent = ''; showProgress(0, 0);
  try {
    const t0 = performance.now();
    const rq = await requestRegion(c, r);
    const tReady = performance.now();
    // 取り出しの後は、同じ大きさで「作画中」(2026-11-04、利用者の指示)。バーは解析の糸が用意した区画の数、続けて (別の所へ移ったときは) 中心のまわりの升のかたまりの数
    msg.textContent = ''; showProgress(0, 0, '作画中');
    REGBASE = rq.base; REG = await (await fetch(REGBASE + 'region.json')).json();
    // アプリの外で試すとき (?region=) は、前もって作った地点の中心と範囲を使う
    if (!APP && !qs.get('api')) { c = REG.center; r = REG.r; }
    window.__lastRegion = { app: rq, wait: (tReady - t0) / 1000 };
    const ph = window.__phase = { region: (tReady - t0) / 1000 }; let tp = performance.now();
    window.__stage = 'worker';
    // 座標の原点 (cur.center): 読み込み直し (keep) は今の原点のまま (出ている升のかたまりをそのまま使う)。別の所へ移る (keep でない) ときは、その地点の中心を新しい原点にして、かたまりを全部捨てる
    const kp = !!(keep && cur), origin = kp ? cur.center : c.slice(), og = kp ? originGen : originGen + 1;
    const res = await workerCall({ type: 'build', og, base: new URL(REGBASE, location.href).href, region: REG, o: origin, c, r, keys: { col: Object.keys(COL), ocol: Object.keys(OCOL), labk: LABK }, names: NAME, pal: regionPalReq, site: SITE, sizes: [LOADCFG.s1, LOADCFG.s2, LOADCFG.s3] }, (d, n) => showProgress(d, n, '作画中'));
    // 索引の数をアプリのログに 1 行出す (事実を数で確かめるため。画面には出さない): 大きさの組ごと (小・中・大・大きな物体) の 部品/三角形。外装、続けて内装
    if (res.stat) {
      const st = res.stat, pt = k => `${st.parts[k]}/${st.tris[k]}`;
      logApp(`索引 c=${c.map(v => v.toFixed(1)).join(',')} r=${r} 区画 ${st.geos} (使い回し ${st.reused})・置かれた所 ${st.insts}・部品 ${st.partsAll}・かたまり ${res.index.keys.length} (部品/三角形) 外装 小 ${pt(0)} 中 ${pt(1)} 大 ${pt(2)} 大きな物体 ${pt(3)} 内装 小 ${pt(4)} 中 ${pt(5)} 大 ${pt(6)} 大きな物体 ${pt(7)} (うち KEEP ${st.keep[0]}/${st.keep[1]}・場所の全部の大きな物体から ${st.big[0]}/${st.big[1]})`);
    }
    ph.worker = res.times; ph.workerAll = (performance.now() - tp) / 1000; tp = performance.now();
    // 区画のファイルはもう要らない (解析の糸が読んで持っている)。アプリに置き場を消してもらう
    if (APP && rq.id) window.chrome.webview.postMessage({ type: 'regionLoaded', id: rq.id });
    else if (!APP && qs.get('api') && rq.base) fetch(qs.get('api') + 'loaded?base=' + encodeURIComponent(rq.base)).catch(() => { });
    window.__stage = 'makeCur'; const nc = makeCur(origin, c, r, res);
    ph.makeCur = (performance.now() - tp) / 1000; tp = performance.now();
    applyRegion(nc, kp, t0);
    // 別の所へ移ったとき (初めに開いたとき・行き先へ飛んだとき・エレベーターなどで遠くへ移ったとき) は、中心のまわりの升のかたまり (形と当たり) が出そろうまで待つ (呼んだ側が、その後に立つ所を決める)。
    // 読み込み直し (keep) は待たない (今出ている物をそのまま出しておき、足りないかたまりは裏で足す)
    bigCheck();   // 場所の全部の大きな物体 (まだ頼んでいない・場所・色付け・境が替わったときだけ頼む)
    if (!kp) { window.__stage = 'chunks'; await ensureNear(c); }
    ph.scene = (performance.now() - tp) / 1000;
    hideProgress();
    window.__lastRegion.total = (performance.now() - t0) / 1000;
  } catch (e) { hideProgress(); msg.textContent = '読み込めませんでした: ' + e.message; report('loadRegion', e); lastFail = { c: c.slice(), r, t: performance.now() }; }   // keep の読み込み直しでも隠さず出す (2026-11-05、利用者の報告「何度も作画中を繰り返してる」: 失敗が見えず原因が追えなかった)
  finally {
    loading = false;
    if (pendingGo) { const [pt, pb] = pendingGo; pendingGo = null; setTimeout(() => goTarget(pt, pb), 0); }
  }
}
// 解析の糸 (view3d-worker.js) に地点の区画の読み取り・索引・升のかたまり・外判定を頼む
const worker = new Worker(new URL('./view3d-worker.js', import.meta.url));
let workerSeq = 0; const workerWait = new Map();
worker.onmessage = e => {
  const d = e.data;
  if (d.type === 'wstat') { if (APP && typeof d.used === 'number') window.chrome.webview.postMessage({ type: 'wcacheUsed', bytes: d.used }); return; }
  if (d.type === 'chunk') { onChunk(d); return; }
  if (d.type === 'out') { onOut(d); return; }
  if (d.type === 'error' && d.kind === 'chunk') { onChunkError(d); return; }
  if (d.type === 'error' && d.kind === 'out') { report('out', d.message); onOut({ id: d.id, error: true }); return; }
  const w = workerWait.get(d.id); if (!w) return;
  if (d.type === 'progress') { if (w.progress) w.progress(d.done, d.total); }
  else if (d.type === 'error') { workerWait.delete(d.id); w.ng(new Error(d.message)); }
  else { workerWait.delete(d.id); w.ok(d); }   // done (地点の索引)・index (分け直した索引)・part (部品の強調)
};
// 解析の糸の中で拾われない失敗 (メモリ不足など) があったら、待っているものを失敗にする (「作画中」のまま止まらないように)。頼んでいた升のかたまりも、待つのをやめる
worker.onerror = e => {
  report('worker', e.message);
  for (const w of workerWait.values()) w.ng(new Error('解析の途中で止まりました' + (e.message ? ': ' + e.message : '')));
  workerWait.clear();
  pend.clear(); wantQ = []; outBusy = null; ensureCheck();
};
// 糸に頼んで返事を待つ (build・sizes・part)。progress: 途中の知らせ ({ type:'progress' })
function workerCall(msg, progress) {
  const id = ++workerSeq;
  return new Promise((ok, ng) => { workerWait.set(id, { ok, ng, progress }); worker.postMessage({ ...msg, id }); });
}
// 前の形の保存 (解析の結果を丸ごとブラウザの私的なファイル領域 (OPFS) に置いていたもの) は使わなくなったので、開いたときに消し、合計 (0) をアプリに知らせる
// (2026-11-06 の作り替え: 升のかたまりは、頼まれるたびに糸が作る。保存はしない)
worker.postMessage({ type: 'wpurge', id: 0 });
// 先読み (アプリが地点を保存した後に頼んでくる {type:'prewarm'}): 解析の結果を保存しなくなったので、何もせずに済んだと返す
function prewarmStop() { }
function prewarmStart(d) { if (APP) window.chrome.webview.postMessage({ type: 'prewarmDone', pid: d.pid, used: null }); }
// 外形 (部品の箱の水色の線) を細かい形の前に先に出すのは、やめた (2026-11-06、利用者の指示「読み込みが済む前の水色の線は消した方がいい」。2026-11-04 に入れた物)
// 解析の結果 (地点の索引) から、地点の表 (cur) を作る。O = 座標の原点 (cur.center。相対の座標は全部これから)、c・r = 地点の立方体 (アプリが取り出した範囲) の中心と半径 (cur.cc・cur.r)
function makeCur(O, c, r, res) {
  const { ladders, elevDoors, stairs, fz, fa } = res;
  window.__lightParts = new Set(res.lightParts);
  const near = q => Math.abs(q[0] - c[0]) <= r && Math.abs(q[1] - c[1]) <= r && Math.abs(q[2] - c[2]) <= r;
  // 乗り場: 立方体の中のものと、それと同じ路線のもの (試験の頁の transit と同じ)
  const nearLines = new Set(VIEW.transit.filter(t => near(t[1])).map(t => t[3]));
  const transit = VIEW.transit.filter(t => near(t[1]) || nearLines.has(t[3]));
  const shopNames = new Set(VIEW.targets.filter(t => ['shop', 'admin', 'pharmacy'].includes(t.kind) && near(t.pos)).map(t => t.name));
  const targets = VIEW.targets.filter(t => t.kind === 'stair' ? (near(t.pos) || near(t.top)) : near(t.pos));
  const floorsOld = VIEW.floors.filter(f => Math.abs(f[2] - c[2]) <= r);
  // 店の入口 (部品は 'インスタンス:部品の番号' で持つ。地点の索引に無ければ -1。押したときに糸からその部品の三角形をもらう: focusPart)
  const entOk = new Set(res.entParts || []);
  // 地点の立方体の中の店のもの (同じ名前の店がほかの所にもあるので位置で選ぶ。位置の無い前のデータは名前で)
  const entrances = REG.entrances.filter(e => e.pos ? near(e.pos) : shopNames.has(e.shop)).map(e => ({ shop: e.shop, pos: e.pos, floor: e.floor, h: e.h, list: e.list.map(o => [entOk.has(o[0] + ':' + o[1]) ? o[0] + ':' + o[1] : -1, o[2], o[0], o[3], o[4], o[5], o[6], o[7], o[8], o[9].map((v, a) => v - O[a]), o[10].map((v, a) => v - O[a]), o[11]]) }));
  // 店のカウンターの箱 (region.json の counters: 店の名前 → [[下, 上], …]。原点からの相対に直す。前のデータに無ければ空)
  const counters = {};
  for (const [nm, lst] of Object.entries(REG.counters || {})) counters[nm] = lst.map(b => [b[0].map((v, a) => v - O[a]), b[1].map((v, a) => v - O[a])]);
  const flist = floorList(fz, fa, transit, floorsOld);
  // 階段の行き先 (2026-11-04、利用者の決定「作り直す」。2D の地図の階段の一覧の代わり): 読み込んだ区画の階段の部品ごとに、下の端 (箱の低い所) を行き先にし、
  // 名前は「下の端の階 → 上の端の階」。行き先の一覧 (VIEW.targets) に足す (同じ位置 (0.1 m) のものは足さない。区画を読み込むたびに増える)
  const flAt = z => { for (const fl of flist) if (fl.lo - 0.5 <= z + O[2] && z + O[2] <= fl.hi + 2.0) return fl.name; return `高さ ${(z + O[2]).toFixed(1)} m`; };
  const seen = new Set(VIEW.targets.filter(t => t.kind === 'stair').map(t => t.pos.map(v => v.toFixed(1)).join()));
  for (const [nm, lo, hi] of stairs || []) {
    const pos = [(lo[0] + hi[0]) / 2 + O[0], (lo[1] + hi[1]) / 2 + O[1], lo[2] + O[2]], key = pos.map(v => v.toFixed(1)).join();
    if (seen.has(key)) continue; seen.add(key);
    VIEW.targets.push({ kind: 'stair', label: '階段', name: nm, ja: `${flAt(lo[2])} → ${flAt(hi[2])}`, pos, top: [pos[0], pos[1], hi[2] + O[2]] });
  }
  if (stairs && stairs.length) setTimeout(renderTargets, 0);
  return { center: O, cc: c, r, index: makeIndex(res.index), textures: res.textures || [], texGen: res.texGen, ladders, elevDoors: elevDoors || [], transit, targets, entrances, counters, flist, guide: guideText(targets, entrances, flist), shops: VIEW.targets.filter(t => ['shop', 'admin', 'pharmacy'].includes(t.kind) && near(t.pos)), station: VIEW.station, label: `${VIEW.station} (${c.map(v => v.toFixed(0)).join(', ')})` };
}
// フロア (利用者の「フロアについては 2M 程度で切ればいい」「床の高さをリスト化してエレベーターのボタンに合わせて名前を付ける」):
// 判定 2 の床の三角形の高さを低い順に並べ、フロアのいちばん低い高さから 2.0 m を超えたら次のフロアにする。
// 名前: フロアの高さの範囲 (± 0.5 m) にあるエレベーターの乗り場の行き先の名前 (訳)。無ければ、範囲にある前の地図の階の名前。無ければ「高さ ○ m」
const FLOOR_CUT = 2.0;
function floorList(fz, fa, transit, floorsOld) {
  const flist = []; const order = fz.map((_, i) => i).sort((a, b) => fz[a] - fz[b]); let cr = null;
  for (const i of order) { const z_ = fz[i], a_ = fa[i]; if (cr === null || z_ - cr.lo > FLOOR_CUT) { cr = { lo: z_, hi: z_, area: 0 }; flist.push(cr); } cr.hi = z_; cr.area += a_; }
  for (const fl of flist) {
    const stKeys = [...new Set(transit.filter(t => t[0] === 'elevator' && fl.lo - 0.5 <= t[1][2] && t[1][2] <= fl.hi + 0.5).map(t => t[5]))].sort();
    const locOf = k => (transit.find(t => t[5] === k) || [])[4] || k;
    // 前の地図の階の名前は、元の名前の順に並べて最初の 3 つ (訳は C# で付けたもの)
    const tfRaw = [...new Set(floorsOld.filter(x => fl.lo - 0.5 <= x[2] && x[2] <= fl.hi + 0.5).map(x => x[3]))].sort();
    const tf = tfRaw.map(r => (floorsOld.find(x => x[3] === r) || [])[1] ?? r);
    fl.name = stKeys.length ? stKeys.map(locOf).join(' / ') : (tf.length ? tf.slice(0, 3).join(' / ') : `高さ ${fl.lo.toFixed(1)} m`);
    fl.src = stKeys.length ? 'エレベーターの行き先' : (tf.length ? '前の地図の階の名前' : '名前なし');
    fl.lo = +fl.lo.toFixed(2); fl.hi = +fl.hi.toFixed(2); fl.area = +fl.area.toFixed(1);
  }
  return flist;
}
// 文字の道案内 (試験の頁の guide): 案内するものごとに、フロア・店の入口・いちばん近い乗り口
// 行き先の一覧と案内の見出しに出す名前 (2026-11-04、利用者の指示「行先は、艦隊マネージャーとかも日本語になる？」): station.json の ja (店はアプリの地図の店の名前、端末は端末の名前と階)。
// 無いもの (エレベーター・トラムは name がもう訳) は name。端末は名前に種類が入っているので種類の札を付けない
const dispName = t => t.ja || t.name;
const dispRow = t => t.kind.startsWith('terminal_') ? dispName(t) : `${t.label}: ${dispName(t)}`;
function guideText(targets, entrances, flist) {
  const floorOf = z => { for (const fl of flist) if (fl.lo - 0.5 <= z && z <= fl.hi + 2.0) return fl.name; return `高さ ${z.toFixed(1)} m`; };
  const els = targets.filter(t => ['elevator', 'tram', 'hangar_lobby'].includes(t.kind));
  const res = [];
  for (const tg of targets) {
    if (['elevator', 'tram', 'hangar_lobby'].includes(tg.kind)) continue;
    // text: 人が読む案内 (フロア・入口の有無と店の中心からの距離・乗り場につながるか・いちばん近い乗り口)。
    // dbg: 前と同じ全文 (部品の名前・床の部品・直線の高さ・道の点・直線が当たった部品の内訳を含む。設定「マップのデバッグ表示」のときに出す)
    const p_ = tg.pos; let txt = `フロア ${floorOf(p_[2])}。`, dbg = txt;
    if (['shop', 'admin', 'pharmacy'].includes(tg.kind)) {
      const en = entrances.find(e => e.shop === tg.name); const ok_ = (en ? en.list : []).filter(o => o[5]);
      if (en && en.floor) dbg += `店の中心の床 ${en.floor[0].toFixed(2)} m (${en.floor[1]})、直線の高さ ${en.h.toFixed(2)} m。`;
      if (ok_.length) {
        const o = ok_[0];
        dbg += `入口 ${o[1]} (店の中心から ${o[3].toFixed(1)} m)。入口の道の点 ${o[6]}・${o[8] ? '乗り場につながる' : '乗り場につながらない'}。`;
        txt += `入口は店の中心から ${o[3].toFixed(1)} m。${o[8] ? '乗り場につながる' : '乗り場につながらない'}。`;
      }
      else {
        dbg += '入口になる開口部が無い (' + (en ? en.list : []).map(o => `${o[1]} ${o[3].toFixed(1)} m: 直線が ${o[11].join(', ')} に当たる`).join('; ') + ')。';
        txt += '入口になる開口部が無い。';
      }
    }
    if (els.length) {
      const d3 = t => Math.hypot(t.pos[0] - p_[0], t.pos[1] - p_[1], t.pos[2] - p_[2]);
      const e = els.reduce((a, b) => d3(b) < d3(a) ? b : a);
      const near_ = `いちばん近い乗り口: ${e.label} (行き先 ${e.name}・フロア ${floorOf(e.pos[2])})、直線で ${d3(e).toFixed(0)} m。`;
      txt += near_; dbg += near_;
    }
    res.push({ shop: dispRow(tg), text: txt, dbg, t: tg });
  }
  return res;
}
// ===== ゲームの画像 (テクスチャ) (2026-11-05、利用者の指示「引いたときは色はビットマップ貼り付けにしてほしい」「端末は元々も色を貼ってほしい」) =====
// region.json の textures ([{url, w, h, repeat:[u, v]}]) の画像を TextureLoader で読み、同じ url の画像は 1 つの Texture を共有 (繰り返しの違う材質は別の項目 = 別の url+repeat の鍵)。
// UV は glTF と同じ向き (上下を返さない。前のステーションの 3D の viewer.html と同じ flipY = false)。別の場所に替わったとき (keep でない) に全部捨てる
const GRAY = 0x888888;   // 画像の無い面の色 (2026-11-05、利用者の指示「デバッグモードとして階段や床で色分けしたりするのはもうやめていい」: 種類ごとの色分けはしない)
const texLoader = new THREE.TextureLoader(); const texCache = new Map();
function texOf(t) {
  const key = t.url + '|' + (t.repeat || [1, 1]).join(',');
  let tx = texCache.get(key);
  if (!tx) {
    tx = texLoader.load(t.url); tx.colorSpace = THREE.SRGBColorSpace; tx.wrapS = tx.wrapT = THREE.RepeatWrapping; tx.anisotropy = 4; tx.flipY = false;
    const rp = t.repeat || [1, 1]; tx.repeat.set(rp[0], rp[1]);
    texCache.set(key, tx);
  }
  return tx;
}
function disposeTextures() { for (const tx of texCache.values()) tx.dispose(); texCache.clear(); }
const disposeMats = o => { const m = o.material; if (!m) return; (Array.isArray(m) ? m : [m]).forEach(x => x?.dispose?.()); for (const x of o.userData?.mats || []) x.dispose?.(); o.userData?.plain?.dispose?.(); };
// ===== 升のかたまりの出し入れ (2026-11-06 の作り替え) =====
// (利用者の指示「広いところならいいが、ステーション内なら 150m もみても仕方なくないか？ ほんらい遠いところも小さいパーツは省くべきで、近くなったら疲労、遠ざかったら捨てるが 50m 単位3段階くらいじゃないか？
//  開けたとこでも150m 以上先は逆に建物など大まかなものが取れた方がいいし」「50 / 100 / 150 で区切り、10m 移動ごと、エレベータやリフトや階段はしごリフト移動ごとに見直し」
//  「外判定時にはある程度以上小さなものは読み込まない方がいいと思う。内部まで読み込んでいる恐れがある。」「ターミナルは固定で全部品描いた方がいい 見た目が悪くなった」「距離に関係なく大きなびったいは書いた方がいいのかも」
//  「建物内部で初期や行先やエレベーターで移動したときなどや移動速度が一定以下の時は細かいものを書くのが本当はいいんじゃない？」「50m以内とか」
//  「細かく書く範囲戸当たり判定は一致しないとだめではないのか？ 読み込みで止まるんだからどっちかが短ければ意味ないよな？」。親の決定)
// 前は、地点 (中心・半径の立方体) の形を丸ごと 1 回で読んで入れ替えていた (描く範囲は地点の中心に固定)。今は、解析の糸 (view3d-worker.js) が地点の部品を
// 「升 (場所の座標で固定の 1 辺 CELL m の立方体) × 部品の大きさの組 (小・中・大・大きな物体) × 外装・内装」のかたまりに分けた索引 (cur.index) を返し、
// ここが、歩いている位置・見ている所 (見るモードでは回転の中心。前の読み直しの判定と同じ) からの距離で、要るかたまりだけを糸に頼んで出し、要らなくなったかたまりを捨てる。
// かたまりには描く形と当たりが一緒に入っている (上の「当たり」) ので、描く範囲と当たりの範囲は必ず同じになる。
//   どのかたまりを出すか (距離 = 位置から升までのいちばん近い距離。境は「読み込みの調整」LOADCFG):
//     d1 以内: 小・中・大。d1〜d2: 中・大。d2〜d3: 大。大きな物体は距離に関係なく全部 (索引にある分 = アプリが取り出した地点の立方体に掛かる区画の分)。
//     「外」(外判定 outNow) のとき: 外装の区画のかたまりは d3 以内は小・中・大とも、大きな物体は全部。内装の区画のかたまりは出さない。
//   いつ見直すか (review): 前に見直した位置から step m 動くごと (reviewTick)、エレベーター・トラム・リフト・はしごで移った後、行き先へ飛んだとき、地点を読み込んだとき (初め)、外判定が変わったとき、「読み込みの調整」を変えたとき。
//     見直す = 要るかたまりを近い順に頼み (同時に MAX_PEND 個まで)、要らないかたまりを捨てる。届いたかたまりは 1 コマに FRAME_BUDGET ms まで足す (chunkTick)。
//     地点を読み直して索引が替わったときは、かたまりの中身の印 (hash) が同じものはそのまま出しておき、違うものは新しいのが届いたときに入れ替える (届くまでは今出ている物のまま)。
//   細かい物 (小の組): 建物の中 (「外」でない) では、速さが speed を超えている間は、新しく読まない (もう出ている物は、範囲の中なら出したまま)。
//     速さ = 1 コマに動いた距離 ÷ 時間 (歩くモードは水平の距離。階段の上り下りは数えない。見るモードは回転の中心の動き)。速さが speed 以下に戻ったら見直す。
//     地点を読み込んだとき・行き先へ飛んだとき・エレベーターなどで移った後の見直しは、速さに関係なく読む (review(true))。
//   立つ所を決める前 (行き先へ飛ぶ・エレベーター・トラム・現在位置): その所のまわり d1 以内のかたまりが出そろうまで待つ (ensureNear)。待つ間は、今いる所と移る先の両方のまわりを出しておく
const CELL = 10, MAX_PEND = 4;
// 1 コマに足すかたまりの上限 (ms。担当が決めた値)
const FRAME_BUDGET = 8;
const chunks = new Map();              // 出ているかたまり: 鍵 'ix,iy,iz,cls' → かたまり
let chunkG = new THREE.Group();        // かたまりの形の置き場 (root の子。root を作り直すときは付け替える)
let originGen = 0;                     // 座標の原点の代 (別の所へ移るたびに増える。前の原点で作ったかたまりを使わないため)
const pend = new Map();                // 糸に頼んでいるかたまり: 鍵 → { id, hash }
let wantQ = [], arrived = [];          // これから頼むかたまり (近い順)・届いてまだ足していないかたまり
const failed = new Set();              // 作れなかったかたまり (鍵|hash。同じ中身を頼み直さない)
let reviewAt = null, ensureAt = null, ensureWait = [];   // 前に見直した位置・移る先 (場所の座標)・出そろうのを待っているもの
let spLast = null, spWalk = false, fastNow = false, spSlowed = false;   // 速さ (speedTick)
const focusAbs = () => { const c = cur.center, q = walk ? wpos : tgt; return [q.x + c[0], q.y + c[1], q.z + c[2]]; };
// 位置 P (場所の座標) から升 i までのいちばん近い距離
function cellDist(P, cells, i) {
  const x0 = cells[i * 3] * CELL, y0 = cells[i * 3 + 1] * CELL, z0 = cells[i * 3 + 2] * CELL;
  return Math.hypot(Math.max(x0 - P[0], 0, P[0] - x0 - CELL), Math.max(y0 - P[1], 0, P[1] - y0 - CELL), Math.max(z0 - P[2], 0, P[2] - z0 - CELL));
}
function makeIndex(ix) { const at = new Map(); for (let i = 0; i < ix.keys.length; i++) at.set(ix.keys[i], i); return { n: ix.keys.length, keys: ix.keys, cells: ix.cells, cls: ix.cls, tris: ix.tris, parts: ix.parts, hash: ix.hash, at }; }
// 索引の i 番目のかたまりが要るか。要るなら位置からの距離 (移る先があれば近い方)、要らなければ -1。F・E = 見直した位置・移る先 (場所の座標)
function chunkRange(ix, i, F, E, out) {
  const cls = ix.cls[i], sz = cls & 3, L = LOADCFG;
  if (out && (cls & 4)) return -1;
  let d = cellDist(F, ix.cells, i); if (E) d = Math.min(d, cellDist(E, ix.cells, i));
  if (sz !== 3 && d > (out ? L.d3 : sz === 0 ? L.d1 : sz === 1 ? L.d2 : L.d3)) return -1;
  return d;
}
// 見直す。small: 細かい物を、速さに関係なく読む
function review(small) {
  if (!cur || !cur.index) return;
  const ix = cur.index, F = focusAbs(), E = ensureAt, out = outNow && !SITE;
  reviewAt = F; spSlowed = false;
  if (small) { spLast = null; fastNow = false; }   // 位置を移した直後の 1 コマを「速い」と数えない
  const gate = !out && fastNow && !small;
  const keep = new Set(), q = [];
  for (let i = 0; i < ix.n; i++) {
    const d = chunkRange(ix, i, F, E, out); if (d < 0) continue;
    const key = ix.keys[i], ch = chunks.get(key); keep.add(key);
    if (ch && ch.hash === ix.hash[i]) continue;
    if (failed.has(key + '|' + ix.hash[i])) continue;
    // 小の組でも KEEP (cls の 8。前の keepAlways の部品: エレベーターの扉・はしご・階段・判定の色のある部品・上下が逆の重力の範囲に掛かる部品) は、速さに関係なく読む
    // (2026-11-06、親の決定: 前の版の動きを戻す。走っている間に 1 m 未満の床の部品の所で床が無くなるため。描く物と当たりは同じかたまりなので、範囲は一致したまま)
    if (gate && !ch && (ix.cls[i] & 3) === 0 && !(ix.cls[i] & 8)) continue;
    q.push({ key, i, d: E ? cellDist(E, ix.cells, i) : d });
  }
  for (const [key, ch] of [...chunks]) if (!keep.has(key)) dropChunk(ch);
  q.sort((a, b) => a.d - b.d); wantQ = q;
  pump(); ensureCheck();
}
function pump() {
  if (!cur || !cur.index) return;
  const ix = cur.index;
  while (pend.size < MAX_PEND && wantQ.length) {
    const w = wantQ.shift(), hash = ix.hash[w.i], p = pend.get(w.key);
    if (p && p.hash === hash) continue;
    const id = ++workerSeq; pend.set(w.key, { id, hash });
    worker.postMessage({ type: 'chunk', id, og: originGen, key: w.key });
  }
}
function onChunk(d) {
  const p = pend.get(d.key); if (p && p.id === d.id) pend.delete(d.key);
  if (d.og === originGen && !d.missing) arrived.push(d);
}
function onChunkError(d) {
  const p = pend.get(d.key);
  if (p && p.id === d.id) { pend.delete(d.key); failed.add(d.key + '|' + p.hash); }
  report('chunk ' + d.key, d.message);
}
// 届いたかたまりを足す (1 コマに FRAME_BUDGET ms まで)。続けて次のかたまりを頼む
function chunkTick() {
  if (!cur || !cur.index) return;
  const ix = cur.index, t0 = performance.now(); let n = 0;
  while (arrived.length && (n === 0 || performance.now() - t0 < FRAME_BUDGET)) {
    const d = arrived.shift(); n++;
    const i = ix.at.get(d.key);
    // 索引が替わった後に届いた前の中身・もう要らなくなったかたまりは足さない
    if (d.og !== originGen || i === undefined || ix.hash[i] !== d.hash || chunkRange(ix, i, reviewAt || focusAbs(), ensureAt, outNow && !SITE) < 0) continue;
    // 画像の番号の並びが今の地点と違う代のかたまり (読み直しの前に糸が作ったもの) は足さず、頼み直す
    if (d.texGen !== cur.texGen) { reviewAt = null; continue; }
    addChunk(d);
  }
  pump();
  if (n) { ensureCheck(); if (!arrived.length && !pend.size && !wantQ.length) showInfo(true); }
}
// 前に見直した位置から step m 動いた・速さが speed 以下に戻ったら、見直す
function reviewTick() {
  if (!cur || !cur.index || ensureAt) return;
  const F = focusAbs();
  if (!reviewAt || spSlowed || Math.hypot(F[0] - reviewAt[0], F[1] - reviewAt[1], F[2] - reviewAt[2]) >= Math.max(1, LOADCFG.step)) review(false);
}
// 速さ (keyMove が毎コマ呼ぶ。raw = 前のコマからの時間 (秒))
function speedTick(raw) {
  const q = walk ? wpos : tgt; let fast = false;
  if (spLast && spWalk === walk && raw > 0) fast = Math.hypot(q.x - spLast.x, q.y - spLast.y, walk ? 0 : q.z - spLast.z) / raw > LOADCFG.speed * (1 + 1e-9) + 1e-9;
  if (!spLast) spLast = new THREE.Vector3(); spLast.copy(q); spWalk = walk;
  if (fastNow && !fast) spSlowed = true;
  fastNow = fast;
}
// abs (場所の座標) のまわり d1 以内のかたまりが出そろうまで待つ。待つ間は右下に「作画中」と数を出す
function ensureNear(abs) {
  if (!cur || !cur.index) return Promise.resolve();
  ensureAt = abs.slice(); review(true);
  return new Promise(ok => { ensureWait.push({ abs: ensureAt, ok, total: 0 }); ensureCheck(); });
}
function ensureLeft(abs) {
  const ix = cur.index, d1 = LOADCFG.d1; let n = 0;
  const near = key => { const i = ix.at.get(key); return i !== undefined && cellDist(abs, ix.cells, i) <= d1; };
  for (const k of pend.keys()) if (near(k)) n++;
  for (const w of wantQ) if (cellDist(abs, ix.cells, w.i) <= d1) n++;
  for (const d of arrived) if (near(d.key)) n++;
  return n;
}
function ensureCheck() {
  if (!ensureWait.length) return;
  const rest = []; let last = null, left = 0;
  for (const w of ensureWait) {
    const n = cur && cur.index ? ensureLeft(w.abs) : 0; if (n > w.total) w.total = n;
    if (n) { rest.push(w); last = w; left = n; } else w.ok();
  }
  ensureWait = rest;
  if (!rest.length) { ensureAt = null; if (!loading) hideProgress(); }
  else showProgress(last.total - left, last.total, '作画中');
}
// 画像・灰色の材質は、かたまりの間で共用する (同じ画像・色合いなら 1 つ)。別の所へ移ったとき (keep でない) に全部捨てる (disposeShared)
const matCache = new Map();
const matOptsOf = wall => ({ side: THREE.DoubleSide, flatShading: true, transparent: wall, opacity: wall ? 0.25 : 1, depthWrite: !wall, clippingPlanes: [clip, clipLo] });
function matPlain(wall) { const key = wall ? 'plain|w' : 'plain'; let m = matCache.get(key); if (!m) matCache.set(key, m = new THREE.MeshLambertMaterial({ color: GRAY, ...matOptsOf(wall) })); return m; }
// 色合い (textures の tint = 材質の層 1 の TintColor。無ければ白): hardsurface/layerblend 系は灰色の下地の画像に層の色を掛けて色を出すので map × color (2026-11-05、利用者の報告「かなり灰色になった」)
function matTex(t, wall) {
  const tn = t.tint || [1, 1, 1], key = (wall ? 'w|' : 'o|') + (t.url || '') + '|' + (t.repeat || [1, 1]).join(',') + '|' + tn.join(',');
  let m = matCache.get(key); if (!m) matCache.set(key, m = new THREE.MeshLambertMaterial({ map: t.url ? texOf(t) : null, color: new THREE.Color(tn[0], tn[1], tn[2]), ...matOptsOf(wall) }));
  return m;
}
function disposeShared() { for (const m of matCache.values()) m.dispose(); matCache.clear(); }
const kindOn = k => { const cb = document.querySelector(`[data-k="${k}"]`); return !cb || cb.checked; };
// 届いたかたまり d を出す (同じ鍵の前のかたまりがあれば入れ替える)。形は種類ごとに 1 つの Mesh (面は flatShading。前と同じ見え方)。
// 画像: 種類の中は画像の番号の順 (糸が並べる) なので、同じ番号の続く区間を geometry.addGroup にし、材質の配列 (0 = 灰色、1〜 = 画像ごと) で描く。
// 「テクスチャ」のチェックが OFF なら灰色の 1 つの材質 (userData.plain) で描く (applyVis)。判定の色の半透明の重ねと輪郭の線は描かない (2026-11-05、利用者の指示「色分けはもうやめていい」)
function addChunk(d) {
  const old = chunks.get(d.key); if (old) dropChunk(old);
  const ch = { key: d.key, cls: d.cls, hash: d.hash, F: d.F, ID: d.ID, LB: d.LB, FL: d.FL, UV: d.UV, TX: d.TX, offs: d.offs, parts: d.parts, labN: d.labN, col: d.col, ST: new Uint32Array(d.ID.length), meshes: [], bytes: d.F.byteLength };
  // 場所の全部の大きな物体のかたまり (d.big) の画像の番号は、大きな物体の textures (bigTex) の番号
  const texOn = document.getElementById('texOn').checked, TEXS = d.big ? bigTex : (cur.textures || []);
  for (const k of Object.keys(COL)) {
    const [o, n] = ch.offs[k] || [0, 0]; if (!n) continue;
    const a = o / 9, cnt = n / 9, wall = k === 'wall';
    const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(ch.F.subarray(o, o + n), 3));
    // 箱と球 (描くかどうか・押したときの当たりの前の見分けに three.js が使う。糸が求めた箱から作り、ここで点を全部は見ない)
    const bx = d.box && d.box[k];
    if (bx) { const lo = new THREE.Vector3(bx[0][0] - 0.001, bx[0][1] - 0.001, bx[0][2] - 0.001), hi = new THREE.Vector3(bx[1][0] + 0.001, bx[1][1] + 0.001, bx[1][2] + 0.001); g.boundingBox = new THREE.Box3(lo, hi); g.boundingSphere = g.boundingBox.getBoundingSphere(new THREE.Sphere()); }
    const plain = matPlain(wall);
    let mats = null;
    if (ch.UV && ch.TX && TEXS.length) {
      const T = ch.TX.subarray(a, a + cnt);
      // 画像の番号の続く区間。画像のある区間が 1 つでもあれば uv とグループを付ける
      const runs = []; let s = 0;
      while (s < cnt) { let e = s + 1; const id = T[s]; while (e < cnt && T[e] === id) e++; runs.push([s, e, id]); s = e; }
      // (2026-11-05) textures の color: true は画像なしの色だけの項目 (場所のパレットの色を指す、画像の無い面)。tint の色で描く (map なし)
      const hasTex = id => id >= 0 && TEXS[id] && (TEXS[id].url || TEXS[id].color);
      if (runs.some(([, , id]) => hasTex(id))) {
        if (runs.some(([, , id]) => id >= 0 && TEXS[id] && TEXS[id].url)) g.setAttribute('uv', new THREE.BufferAttribute(ch.UV.subarray(a * 6, (a + cnt) * 6), 2));
        mats = [plain]; const mi = new Map();
        for (const [s0, e0, id] of runs) {
          let m = 0;
          if (hasTex(id)) { m = mi.get(id); if (m === undefined) { m = mats.length; mats.push(matTex(TEXS[id], wall)); mi.set(id, m); } }
          g.addGroup(s0 * 3, (e0 - s0) * 3, m);
        }
      }
    }
    const me = new THREE.Mesh(g, texOn && mats ? mats : plain);
    // userData: k = 種類、t0 = その種類の中の最初の三角形の番号 (押したときに部品を引くのに使う。1 つの Mesh なので 0)、ch = かたまり
    me.renderOrder = wall ? 1 : 0; me.userData = { k, t0: 0, plain, mats, ch }; me.visible = kindOn(k);
    ch.meshes.push(me); chunkG.add(me); (groups[k] ??= []).push(me);
    if (ghostG && me.visible) addGhost(me);
  }
  colAdd(ch); chunks.set(ch.key, ch);
}
function dropChunk(ch) {
  for (const me of ch.meshes) {
    chunkG.remove(me);
    if (me.userData.ghost) { ghostG?.remove(me.userData.ghost); me.userData.ghost = null; }
    const a = groups[me.userData.k]; if (a) { const i = a.indexOf(me); if (i >= 0) { a[i] = a[a.length - 1]; a.pop(); } }
    me.geometry.dispose();
  }
  colDrop(ch); chunks.delete(ch.key);
}
function dropAllChunks() {
  for (const ch of [...chunks.values()]) dropChunk(ch);
  colReset(); pend.clear(); wantQ = []; arrived = []; failed.clear(); reviewAt = null;
}
// かたまりの中の三角形 (種類 k の tri 番目) の部品
const partAt = (ch, k, tri) => ch.parts[ch.ID[ch.offs[k][0] / 9 + tri]];
// 読み込んだ地点 p を今の地点にする。keep (読み込み直し): 座標の原点はそのまま。出ているかたまりは捨てず、新しい索引で見直す (中身の違うものだけ入れ替わる)。
// keep でない (別の所へ移った): 前の形・画像・材質を全部捨て、新しい原点で始める。印・札・一覧は show が作り直す
function applyRegion(p, keep, t0) {
  window.__stage = 'swap';
  const nroot = new THREE.Group();
  // フロアの薄い写しはかたまりの形 (geometry) を使っているので、形ごと捨てないよう先に外す (show の applyFloor が作り直す)
  if (ghostG) { root.remove(ghostG); ghostG.userData.mat?.dispose(); ghostG = null; }
  if (keep) nroot.add(chunkG);
  else { dropAllChunks(); originGen++; for (const w of ensureWait) w.ok(); ensureWait = []; ensureAt = null; }
  scene.remove(root); root.traverse(o => { o.geometry?.dispose(); disposeMats(o); });
  if (!keep) { disposeTextures(); disposeShared(); chunkG = new THREE.Group(); nroot.add(chunkG); for (const k of Object.keys(groups)) groups[k] = null; }
  scene.add(nroot); root = nroot;
  cur = p; curPal = regionPalReq;   // この地点を頼んだときの色付け (reloadForPalette)
  p.t0 = t0;
  // 細かい形で補った面 (extra): keep なら新しい root に描き直す (原点は同じ)。別の所へ移った (keep でない) なら捨てる
  extraRelocate([0, 0, 0], !!keep);
  // コメントの印は DB から show で出し直す (requestNotes。前の root と一緒に消えている)
  window.__stage = 'show'; show(t0, keep);
  review(true);
}
// 地点の印・札・一覧などを作る (形は、升のかたまりが別に出す)
function show(t0, keep) {
  const p = cur;
  hiM = null; ghostG = null; cMarks = []; noteMarks = []; if (!keep) selFloor = null; distLabels.length = 0;
  requestNotes();   // DB のコメントの印をもらって出す (返事 onNotes で描く)
  document.querySelectorAll('#shopforms button').forEach(b => b.classList.remove('on')); document.getElementById('pick').style.display = 'none';
  const c = p.center;
  shpG = new THREE.Group(); root.add(shpG);
  setGravity(REG && REG.gravity, p.center); drawGravity();
  for (const s of p.shops) {
    const q = s.pos, ok = s.ok;
    const x = q[0] - c[0], y = q[1] - c[1], z = q[2] - c[2];
    // 白い柱は出さない (2026-11-05、利用者「白い縦線は要らない」)。店の玉の色は、入口の判定 (ok) による緑・赤の出し分けをやめて 1 色 (利用者「デバッグ的なのはもう表示しなくていい」)
    const b = new THREE.Mesh(new THREE.SphereGeometry(0.4), new THREE.MeshBasicMaterial({ color: 0xff3333 }));
    b.position.set(x, y, z); b.renderOrder = 7; b.userData.cat = 'shop'; shpG.add(b);
  }
  // 店の名前の札 (2026-11-05、利用者「こういう端末がないところもショップとしてキャプションは欲しい」): 端末の札と同じく、水平 6 m・高さ 3 m 以内の店は 1 枚にまとめ、店の名前を 1 行ずつ。玉の 1.5 m 上
  {
    const shops = p.shops || []; const usedS = new Set();
    for (let i = 0; i < shops.length; i++) {
      if (usedS.has(i)) continue; const grp = [i]; usedS.add(i);
      for (let g = 0; g < grp.length; g++) for (let j = 0; j < shops.length; j++) {
        if (usedS.has(j)) continue; const a = shops[grp[g]].pos, b = shops[j].pos;
        if (Math.hypot(a[0] - b[0], a[1] - b[1]) <= 6 && Math.abs(a[2] - b[2]) <= 3) { usedS.add(j); grp.push(j); } }
      const names = new Map(); for (const k of grp) { const nm = shops[k].ja || shops[k].name || 'ショップ'; names.set(nm, (names.get(nm) || 0) + 1); }   // 店名 (アプリの名前、無ければ Entity 名。利用者「店名でもいい」)
      const lines = [...names.entries()].map(([nm, n]) => nm + (n > 1 ? ` ×${n}` : ''));
      const q = [0, 1].map(a => grp.reduce((s, k) => s + shops[k].pos[a], 0) / grp.length).concat([Math.max(...grp.map(k => shops[k].pos[2]))]);
      const sp = textSprite(lines); sp.material.sizeAttenuation = false; const asp = sp.scale.x / sp.scale.y; const h = 0.04 * lines.length; sp.scale.set(h * asp, h, 1);
      sp.center.set(0.5, 0); sp.position.set(q[0] - c[0], q[1] - c[1], q[2] - c[2] + 1.5); sp.userData.cat = 'shop'; shpG.add(sp);
      distLabels.push({ sp, lines });
    }
  }
  // 開いたときは切らない (2026-11-04、利用者の指摘「crusader の本社の建物がない」: 前は「この高さより上を消す」の初めの値 25 m で、中心から 25 m より上が消えていた)
  if (keep) { applyFloor(); applyVis(); if (walk) walkCam(); else upd(); }
  else { document.getElementById('cut').value = 25; applyFloor(); applyVis(); tgt.set(0, 0, 0); dist = p.r * 1.6; upd(); }
  showInfo(false);
  renderGuide();
  drawHereMark();
  if (pendingHere && !pendingHereSince) setTimeout(runPendingHere, 0);   // 待つのをやめていた here (runPendingHere) を、地点を読み込めたので出す   // 現在位置の印 (地点の中心が変わったので置き直す)
  if (pendingExtView && !pendingExtSince && !pendingExtMarksSince) setTimeout(runExteriorView, 0);   // 待つのをやめていた「外から見る形」(runExteriorView。2026-11-06) を、地点を読み込めたので行う
  const fl2 = document.getElementById('flist2'); fl2.innerHTML = '';
  const allB = document.createElement('button'); allB.textContent = '全部 (切らない)'; allB.onclick = () => { selFloor = null; applyFloor(); }; fl2.appendChild(allB);
  (p.flist || []).slice().reverse().forEach(fl => {
    const b = document.createElement('button');
    b.textContent = `${fl.name} (${fl.lo.toFixed(1)}〜${fl.hi.toFixed(1)} m・床 ${fl.area.toFixed(0)} m²・名前: ${fl.src})`;
    b.onclick = () => { selFloor = fl; applyFloor(); tgt.z = fl.lo - p.center[2]; upd(); };
    fl2.appendChild(b);
  });
  // 案内するもの: 店 (赤)・admin (桃)・薬局 (緑)・端末 (橙)・エレベーター・トラム・hangar_lobby (黄)・階段 (青)
  const TCOL = { shop: 0xff3333, admin: 0xff66cc, pharmacy: 0x33ff99, elevator: 0xffdd00, tram: 0xffdd00, hangar_lobby: 0xffdd00, stair: 0x3399ff };
  for (const tg of (p.targets || [])) {
    if (tg.kind === 'shop') continue;   // 店は白い柱と玉
    if (tg.kind === 'stair') continue;  // 階段の玉は描かない (2026-11-05、利用者の報告「青の丸が意味不明」: 札が無く、壁越しに大きな丸だけが見えていた。行き先の一覧には残る。親の決定)
    const col = TCOL[tg.kind] ?? (tg.kind.startsWith('terminal_') ? 0xff9933 : 0xffffff); const q = tg.pos;
    const b = new THREE.Mesh(new THREE.SphereGeometry(0.3), new THREE.MeshBasicMaterial({ color: col, depthTest: false }));
    b.position.set(q[0] - p.center[0], q[1] - p.center[1], q[2] - p.center[2]); b.renderOrder = 7; b.userData.cat = markCatOf(tg.kind); shpG.add(b);
  }
  // 端末の名前 (2026-11-04、利用者の指示「ターミナルには何のターミナルか画面に書くといい」): 端末の玉の上に、画面での大きさが変わらない文字の札で出す。
  // 重なりを減らすため、水平に 6 m 以内・高さの差 3 m 以内でつながる端末は、名前が違っても 1 つの札にまとめ、1 行に 1 つの名前と数 (「名前 ×数」) を書く
  // (値はアプリの地図の端末のまとめ方 TerminalJoin・TerminalJoinZ と同じ)。札は、まとめた端末のいちばん高い玉の 0.8 m 上
  const terms = (p.targets || []).filter(t => t.kind.startsWith('terminal_'));
  const used = new Set(); window.__termLabels = [];
  for (let i = 0; i < terms.length; i++) {
    if (used.has(i)) continue; const grp = [i]; used.add(i);
    for (let g = 0; g < grp.length; g++) for (let j = 0; j < terms.length; j++) {
      if (used.has(j)) continue; const a = terms[grp[g]].pos, b = terms[j].pos;
      if (Math.hypot(a[0] - b[0], a[1] - b[1]) <= 6 && Math.abs(a[2] - b[2]) <= 3) { used.add(j); grp.push(j); } }
    // 店の端末は「店名（種類）」(行き先の一覧と同じ表記。2026-11-05、利用者「他も店名で括弧つけて個人装備とか書いてあると使いやすい」)。店の無い端末は端末の種類
    const shopNameOf = t => { if (!t.shop) return null; const s = (p.shops || []).find(x => x.name === t.shop); return s ? (s.ja || s.name) : null; };
    // 長い文 (クエストの扉・パネルの Entity 名など) は、3D の札では「: 」の前の種類だけにする (2026-11-05、利用者「文字が長すぎて見にくい」。名前は行き先の一覧と押したときの枠に残る)
    const shortText = s => s.length > 16 && s.includes(': ') ? s.slice(0, s.indexOf(': ')) : s;
    const names = new Map(); for (const k of grp) { const nm = shopNameOf(terms[k]) || shortText(terms[k].text || terms[k].label); names.set(nm, (names.get(nm) || 0) + 1); }
    const lines = [...names.entries()].map(([nm, c]) => nm + (c > 1 ? ` ×${c}` : ''));
    const q = [0, 1].map(a => grp.reduce((s, k) => s + terms[k].pos[a], 0) / grp.length).concat([Math.max(...grp.map(k => terms[k].pos[2]))]);
    const sp = textSprite(lines);
    sp.material.sizeAttenuation = false; const asp = sp.scale.x / sp.scale.y; const h = 0.04 * lines.length; sp.scale.set(h * asp, h, 1);
    sp.center.set(0.5, 0); sp.position.set(q[0] - p.center[0], q[1] - p.center[1], q[2] - p.center[2] + 0.8);
    sp.userData.cat = grp.every(k => markCatOf(terms[k].kind) === 'quest') ? 'quest' : 'terminal';   // まとめた札: 全部クエストのものなら「クエストの扉・パネル」、1 つでも端末があれば「端末」
    shpG.add(sp);
    distLabels.push({ sp, lines });
    window.__termLabels.push(lines.join(' / '));
  }
  // エレベーターの乗り口の名前 (2026-11-04、利用者の指摘「エレベーターがたくさん並んでる。何のエレベーターかわからない」):
  // 乗り口 (地点の立方体の中のエレベーター・トラム) を、同じ路線・同じ行き先の名前 (訳) で、水平に 10 m・高さ 3 m 以内につながるものごとに 1 つの札にまとめ、
  // 「行き先 ×数」(数が 1 なら行き先だけ) を、いちばん高い乗り口の 2.4 m 上に出す (端末の札 (0.8 m 上) と重ならないように高くした)
  window.__elevLabels = [];
  {
    const cc = p.center, kc = p.cc, near = q => Math.abs(q[0] - kc[0]) <= p.r && Math.abs(q[1] - kc[1]) <= p.r && Math.abs(q[2] - kc[2]) <= p.r;
    const st = (p.transit || []).filter(t => (t[0] === 'elevator' || t[0] === 'tram') && near(t[1]));
    const usedE = new Set();
    for (let i = 0; i < st.length; i++) {
      if (usedE.has(i)) continue; const grp = [i]; usedE.add(i);
      for (let g = 0; g < grp.length; g++) for (let j = 0; j < st.length; j++) {
        if (usedE.has(j) || st[j][3] !== st[i][3] || st[j][4] !== st[i][4]) continue; const a = st[grp[g]][1], b = st[j][1];
        if (Math.hypot(a[0] - b[0], a[1] - b[1]) <= 10 && Math.abs(a[2] - b[2]) <= 3) { usedE.add(j); grp.push(j); } }
      const text = (st[i][0] === 'tram' ? 'トラム: ' : 'エレベーター: ') + st[i][4] + (grp.length > 1 ? ` ×${grp.length}` : '');
      const q = [0, 1].map(a => grp.reduce((s, k) => s + st[k][1][a], 0) / grp.length).concat([Math.max(...grp.map(k => st[k][1][2]))]);
      const sp = textSprite(text); sp.material.sizeAttenuation = false; const asp = sp.scale.x / sp.scale.y; sp.scale.set(0.04 * asp, 0.04, 1);
      sp.center.set(0.5, 0); sp.position.set(q[0] - cc[0], q[1] - cc[1], q[2] - cc[2] + 2.4); sp.userData.cat = 'transit'; shpG.add(sp);
      distLabels.push({ sp, lines: [text] });
      window.__elevLabels.push(text);
    }
  }
  refreshDistLabels(true);
  applyMarkVis();   // 「表示」のチェック (店・端末・エレベーター・トラム・クエスト・コメント) を、作り直した印と札に効かせる
  // 店の入口を探した直線 (緑・赤) は出さない (2026-11-05、利用者「デバッグモードとして…色分けしたりするのはもうやめていい」。データは cur.entrances に残す)
  const sf = document.getElementById('shopforms'); sf.innerHTML = '';
  const addH = t => { const h = document.createElement('div'); h.style.marginTop = '4px'; h.textContent = t; sf.appendChild(h); };
  const addB = (pi, t) => { const b = document.createElement('button'); b.textContent = t; b.onclick = () => { if (pi !== -1) focusPart(p, pi, b); }; sf.appendChild(b); };
  addH('店の入口 (名前の語に door・doors・doorway・window・opening のある部品で、店の中心から 10 m 以内。店の中心の真下の床 + 1 m の高さで、店の中心から開口部へ水平に引いた直線が、どの三角形にも当たらないもの)');
  for (const en of (p.entrances || [])) {
    addH(`■ ${en.shop}: 店の中心の床 ${en.floor ? en.floor[0] + ' m (' + en.floor[1] + ')' : 'なし'}・直線の高さ ${en.h ?? '-'} m`);
    en.list.forEach(o => addB(o[0], `${o[5] ? '入口' : '×'} ${o[1]} (inst ${o[2]}) 中心から ${o[3]} m・当たり ${o[4]}${o[11] && o[11].length ? ' (' + o[11].join(', ') + ')' : ''}・道の点 ${o[6]}・${o[8] ? '乗り場につながる' : 'つながらない'}`));
  }
}
// 左下の数字 (地点・出ているかたまりの部品と三角形の数・読み込みの時間)。かたまりが出そろったとき (final) にも書き直す
function showInfo(final) {
  const p = cur; if (!p) return;
  const c = p.cc, nk = {}, nl = new Array(LABK.length).fill(0); let parts = 0, bytes = 0;
  for (const ch of chunks.values()) {
    parts += ch.parts.length; bytes += ch.bytes;
    for (const k of Object.keys(COL)) { const o = ch.offs[k]; if (o && o[1]) nk[k] = (nk[k] || 0) + o[1] / 9; }
    for (let i = 1; i < LABK.length; i++) nl[i] += ch.labN[i] || 0;
  }
  const elapsed = (performance.now() - (p.t0 || performance.now())) / 1000;
  if (final && p.sec == null) p.sec = elapsed;
  const sec = p.sec ?? elapsed;
  document.getElementById('info').textContent = `${p.station}\n中心 (${c.map(v => v.toFixed(1)).join(', ')})、中心から ±${p.r} m の立方体\n部品 ${parts}\n三角形: ` +
    Object.keys(COL).filter(k => nk[k]).map(k => `${NAME[k]} ${nk[k]}`).join(' / ') +
    `\n重ねた面の三角形: ` + Object.keys(OCOL).map(k => `${NAME[k]} ${nl[LABK.indexOf(k)]}`).join(' / ') +
    `\n読み込み ${sec.toFixed(1)} 秒・${(bytes / 1048576).toFixed(0)} MB` +
    (REG.missing.length ? `\n形の出なかった socpak (この地点): ${REG.missing.length}` : '') +
    (window.__lastRegion?.app?.extractSeconds != null ? `\n時間: 選んでから描き終わるまで ${sec.toFixed(1)} 秒 = 区画の用意 ${window.__lastRegion.wait.toFixed(1)} 秒 (取り出し ${window.__lastRegion.app.extractSeconds.toFixed(1)} 秒・計算 ${window.__lastRegion.app.computeSeconds.toFixed(1)} 秒。どちらも同時に動かした分の足し合わせ) + ビューアの読み込みと描く用意 ${(sec - window.__lastRegion.wait).toFixed(1)} 秒` : '');
}
// 店の入口の部品を強調する。key = 'インスタンス:部品の番号'。部品の三角形は、出ているかたまりに無くても出せるよう、解析の糸からもらう
async function focusPart(p, key, btn) {
  document.querySelectorAll('#shopforms button').forEach(b => b.classList.remove('on')); btn.classList.add('on');
  const og = originGen; let res;
  try { res = await workerCall({ type: 'part', og, key }); } catch (e) { report('focusPart', e); return; }
  if (res.missing || og !== originGen || !cur) return;
  if (hiM) { root.remove(hiM); hiM.geometry.dispose(); }
  const a = res.tris;
  const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(a, 3)); g.computeVertexNormals(); g.computeBoundingBox();
  hiM = new THREE.LineSegments(new THREE.EdgesGeometry(g, 20), new THREE.LineBasicMaterial({ color: 0x33ffff, depthTest: false, transparent: true, opacity: 0.9, clippingPlanes: [clip, clipLo] }));
  hiM.renderOrder = 10; root.add(hiM);
  if (!walk) { const bb = g.boundingBox; bb.getCenter(tgt); const s = bb.getSize(new THREE.Vector3()); dist = Math.max(8, Math.min(120, Math.max(s.x, s.y, s.z) * 1.3)); upd(); }
  const part = res.part;
  const box = document.getElementById('pick'); box.style.display = 'block';
  box.textContent = `${part[1]}  (inst ${part[0]}・${part[4]})\n種類: ${NAME[part[2]]}\n強調した三角形 ${a.length / 9} (部品まるごと。水色の縁取り)\n部品の中の判定の面:\n${partFloors(a, res.lab, null, 0)}`;
}
// 選んだフロアの床 (いちばん低い高さ − 0.1 m) から H m までを描き、床より下のほかの階は薄い写し (濃さ ghostA %) で描く。
// 切る高さより上は、薄い写しも描かない (2026-11-04、利用者の指示「切った方が作画が早くて良くないか？」・親の指示「薄く出す形にはしません」)
function applyFloor() {
  if (ghostG) { root.remove(ghostG); ghostG.userData.mat?.dispose(); ghostG = null; }
  if (!selFloor || !cur) { clip.constant = 1e9; clipLo.constant = 1e9; document.getElementById('cutv').textContent = '切らない'; return; }
  const H = +document.getElementById('cutH').value || 2, A = Math.max(0, Math.min(100, +document.getElementById('ghostA').value)) / 100;
  const z0 = selFloor.lo - cur.center[2] - 0.1;
  clip.constant = z0 + 0.1 + H; clipLo.constant = -z0; document.getElementById('cutv').textContent = (z0 + 0.1 + H).toFixed(1);
  ghostG = new THREE.Group(); ghostG.userData.mat = new THREE.MeshBasicMaterial({ color: GRAY, transparent: true, opacity: A, depthWrite: false, side: THREE.DoubleSide, clippingPlanes: [clip] });
  // 薄い写しは灰色 (材質が画像ごとの配列のこともあるので、材質の色は見ない。判定の色の重ね (OCOL) の groups は描いていないので無い)
  for (const k of [...Object.keys(COL), ...Object.keys(OCOL)]) for (const g of groups[k] || []) {
    if (!g.visible) continue;
    addGhost(g);
  }
  root.add(ghostG);
}
// 形 g の薄い写しを足す (applyFloor・フロアを選んでいる間に足した升のかたまり)
function addGhost(g) { const m = new THREE.Mesh(g.geometry, ghostG.userData.mat); m.renderOrder = -1; g.userData.ghost = m; ghostG.add(m); }
function applyVis() {
  document.querySelectorAll('[data-k]').forEach(cb => { for (const m of groups[cb.dataset.k] || []) m.visible = cb.checked; });
  // 「テクスチャ」: ON なら画像の材質の配列、OFF なら灰色の 1 つの材質 (形のグループはそのまま。1 つの材質ならグループ全部に同じ材質で描かれる)
  const texOn = document.getElementById('texOn').checked;
  for (const k of Object.keys(COL)) for (const m of groups[k] || []) { const want = texOn && m.userData.mats ? m.userData.mats : m.userData.plain; if (want && m.material !== want) m.material = want; }
  // 外部視点 (extView) の間は、内部の玉と札 (shpG の中の全部) を出さない (updExtView)
  if (shpG) shpG.visible = document.getElementById('shp').checked && !extView;
  // 店の印 (柱と玉) は、部品の中・後ろにあっても見えるよう、いつも手前に描く
  shpG?.traverse(o => { if (o.material) { o.material.depthTest = false; o.material.needsUpdate = true; } });
}
document.querySelectorAll('.lg input').forEach(i => i.addEventListener('change', applyVis));
// 「テクスチャ」のスイッチ (右上の #swPanel): 切り替えたら localStorage (view3d.tex = "1"・"0") に覚え、次に開いたとき (別の場所・別ウィンドウ・［マップ］タブ・次の起動) もその状態で始める。
// OFF で開いたときは、addChunk が初めから灰色の材質で作る (2026-11-05、利用者の指示「テクスチャを貼るのは ON OFF できるように、切り替えたら覚えておいて以降はそれに従うようにしてほしい」)
try { const t = localStorage.getItem('view3d.tex'); if (t === '1' || t === '0') document.getElementById('texOn').checked = t === '1'; } catch (_) { }
document.getElementById('texOn').addEventListener('change', e => { try { localStorage.setItem('view3d.tex', e.target.checked ? '1' : '0'); } catch (_) { } applyVis(); refreshPalSwitch(); });
// 「色付け」のスイッチ (右上の #swPanel の「テクスチャ」の下。2026-11-05、利用者の指示「1 で。右のスイッチに入れて自動保存にして」「色付けは場所に寄らず、どこの場所でも全体で共通でいいです」):
//   場所のパレットの色を付けるか。地点を頼む知らせ ({type:'region', …, pal}) に入れてアプリへ渡し、アプリがその色付けで地点を作る (StationView3DWindow。先読みも最後に頼んだ色付け)。
//   切り替えたら localStorage (view3d.pal = "1"・"0"。場所によらず 1 つ) に覚え、今の地点を同じ中心・半径で読み直す (keep。見ている所・向き・歩くモードはそのまま)。
//   読み込み中なら終わってから、終わった地点の色付けが今のスイッチと違うときだけ読み直す。「テクスチャ」が OFF の間は押せない (つまみの位置・覚えは変えない)
const palOn = () => document.getElementById('palOn').checked;
let regionPalReq = true, curPal = null;   // 最後に頼んだ地点の色付け・今出している地点の色付け
try { const t = localStorage.getItem('view3d.pal'); if (t === '1' || t === '0') document.getElementById('palOn').checked = t === '1'; } catch (_) { }
function refreshPalSwitch() { document.getElementById('palOn').disabled = !document.getElementById('texOn').checked; }
function reloadForPalette() {
  // 読み込み中 (最初の地点を含む) は終わってから見る (最初の地点はまだ cur が無いので、先に loading を見る)
  if (loading) { setTimeout(reloadForPalette, 500); return; }
  if (!cur) return;
  if (curPal === palOn()) return;
  loadRegion(cur.cc.slice(), cur.r, true);
}
document.getElementById('palOn').addEventListener('change', e => { try { localStorage.setItem('view3d.pal', e.target.checked ? '1' : '0'); } catch (_) { } reloadForPalette(); });
refreshPalSwitch();
// エレベーター・トラムの行き先の窓 (#tbox) は、右上のスイッチの欄の左に置く (欄と重ならないように。欄の幅は数の文字で変わるので、変わるたびに置き直す)
function placeTbox() { const s = document.getElementById('swPanel'), b = document.getElementById('tbox'); if (!s || !b) return;
  // #view が狭くて欄の左に入り切らないときは、左の端 (8 px) で止める (画面の外に出さない。そのときは欄と重なる)
  const want = el.clientWidth - s.offsetLeft + 8, maxRight = Math.max(0, el.clientWidth - (b.offsetWidth || 340 + 20) - 8);
  b.style.right = Math.min(want, maxRight) + 'px'; }
addEventListener('resize', placeTbox); try { new ResizeObserver(placeTbox).observe(document.getElementById('swPanel')); } catch (_) { } placeTbox();
// 「表示」のチェック (2026-11-05、利用者「チェックボックスで表示するものを選べるように」): 店 (shop)・端末 (terminal)・エレベーター・トラム (transit)・クエストの扉・パネル (quest)・コメント (comment)。
// 印 (玉) と札に userData.cat を付けておき、作り直さずに visible だけ切り替える。状態は localStorage (view3d.marks) に覚える。既定は全部 ON。
// (2026-11-05) チェックは左の欄から右上のスイッチの欄 (#swPanel の「印」) へ移した (class mk・data-cat・保存の鍵は前のまま)
const MARK_CATS = ['shop', 'terminal', 'transit', 'quest', 'comment'];
function markCatOf(kind) {
  if (kind === 'terminal_questdoor' || kind === 'terminal_questitem' || kind === 'terminal_questpanel') return 'quest';   // StationView3D.cs の EntityKinds ("terminal_" + questdoor/questitem/questpanel)
  if (kind.startsWith('terminal_')) return 'terminal';
  if (kind === 'elevator' || kind === 'tram' || kind === 'hangar_lobby') return 'transit';
  if (kind === 'shop' || kind === 'admin' || kind === 'pharmacy') return 'shop';
  return undefined;   // そのほか (白い玉) はいつも出す
}
function markOn(cat) { const cb = document.querySelector(`.mk[data-cat="${cat}"]`); return !cb || cb.checked; }
function applyMarkVis() {
  const on = {}; for (const c of MARK_CATS) on[c] = markOn(c);
  shpG?.traverse(o => { const c = o.userData.cat; if (c && c in on) o.visible = on[c]; });
  for (const g of [...cMarks, ...noteMarks]) g.visible = on.comment && !extView && g.userData.shown !== false;
  try { localStorage.setItem('view3d.marks', JSON.stringify(on)); } catch (_) { }
}
// コメントの印を出すか: 「印」のコメントのスイッチが ON で、外部視点でない (スイッチの覚えている状態は変えない)
const commentShow = () => markOn('comment') && !extView;
// ===== 外部視点 (2026-11-05、利用者の指示「外になったら内部は全部消してほしい。邪魔。」「外部から見てる、内部で見てるでこのスイッチの有効無効は切り替えてほしい」) =====
// 外部視点 = 外から見たときの印と見出しを出す条件 (extTick) のうち、印の有無を除いたもの: 地点があり、歩くモードでなく、マイハンガーの中でなく、回転の中心からの距離が地点の半径以上。
// 毎コマ、描く (R.render) 前に判定し (updExtView)、外部視点の引き出し線・見出し (extTick) も同じ値で出す・消すので、片方だけ出ているコマは無い。
// 外部視点の間: 内部の印と札 (shpG = 店・端末・エレベーター・トラム・クエスト・そのほかの玉と札、店名の札、距離の付いた札・まとめた札。cMarks・noteMarks = 再生と保存のコメントの印) を出さない。
//   「印」の 5 つのスイッチは押せない形、「キャプション」のスイッチは押せる形 (数が 0 の種類は押せないまま)。内部で見ている間はその逆。「テクスチャ」はいつでも押せる。
//   押せない形にしてもスイッチの ON・OFF (覚えている状態) は変えない。部品の形は消さない
let extView = false;
// ----- 見ている位置の外判定 -----
// (2026-11-06、利用者の指示「外に出てるのに外判定がない。宇宙空間にあって xyz 各方向 +/-200m のどちらかに何もなければ外判定がいいかな？」「外判定は弱い」。親の決定)
// 見ている位置 (カメラの位置。歩くモードでは歩いている位置の目の高さ = カメラ) から x・y・z の ＋・− の 6 つの向きへ LOADCFG.outDist m の線分を引き、地点の部品の三角形に 1 つも当たらない向きが
// 1 つでもあれば「外」、6 つ全部が当たれば「中」(outNow)。上の決まり (回転の中心からの距離) に足して、どちらかが真なら外部視点にする (extViewNow)。マイハンガーの中は今までどおり外部視点にしない。
//   当てる三角形 (2026-11-06 の作り替え): 解析の糸 (view3d-worker.js の outScan) が、地点の全部の部品 (大きさ・距離・外装か内装かに関係なく) の三角形で見る。
//   前は、読み込んである三角形だけで見ていた。今は、出す升のかたまりを外判定で変える (「外」のときは内装の区画を出さない) ので、出しているかたまりに左右されないようにした。切る面 visZ・細かい形の補い extra は見ない (前と同じ)。
//   地点の立方体 (中心 cur.cc から ±cur.r m) の外へ出る線分は、立方体の端までで見る (端に届いたら「何も無い」)。見ている位置が立方体の外なら「外」。
//   見直すとき (2026-11-06、利用者の指示「外判定は、初期、エレベーターなどで移動したとき、ドア・壁を通り抜けたとき」。親の決定。ただ動いた・止まっただけでは見直さない):
//     (a) 初期: 地点を読み込んで表示が替わったとき (cur が替わる)。行き先へ飛んだとき (goTarget)・歩くモードを切り替えたとき (下の床の決まりの足の位置が替わるので) も頼む (outRequest)
//     (b) エレベーターなどで移動したとき: 位置を移す仕組みで移り終えた所で頼む (outRequest。elevPlace・travelTo の place・はしごを登り終えた/下り終えた所)
//     (c) ドア・壁を通り抜けたとき: 止まったときに、前に外判定した位置から今の位置までの線分が地点の部品の三角形に当たるか (糸の outCross) を 1 回見て、当たれば見直す
//   見直し・(c) の線分を見るのは、見ている位置が止まってから OUT.IDLE_MS ms たった後だけ。動いている間は何も見ず、前の結果のまま (途中で動いたら、その見直しはやめる = 糸の返事を捨てる)
//   (2026-11-06、利用者の報告「動きがかなりぎこちない。もうちょっとどうにかならないかなぁ？ 前よりかなり悪い」。親の決定)。
//   床の決まり (2026-11-06、利用者の指示「外判定に一個追加。外でも足の 0.5m 以内に床があれば中判定。 ORISON などの屋外想定」。親の決定):
//     足の下 LOADCFG.outFloor m 以内に立てる面があれば、6 つの向きを見ずに「中」。モードに関係なく当てる (利用者の指摘「モード関係なく、足位置の想定があるだろ？」)。
//     まず出ているかたまりの当たり (groundAt) で見て (outFloorNear)、無ければ糸が地点の全部の部品の三角形で同じ決まりを見る (内装の区画を出していない「外」の間でも、内装の床の上なら「中」に戻る)。
//     足の位置: 歩くモードでは歩いている位置 (wpos)。歩くモードでないときは、見ている位置 (カメラ) から重力の向きへ目の高さ (STANCE.stand.eye) だけ下がった点
//     (利用者の指示「建物(最後の位置の床)方向を重力方向としておけばいいだけじゃない？ 主には Z マイナス方向」。親の決定)。
//     重力の向き: 歩くモードの上下の印 inv (上下が逆の範囲に立っていたら true。歩くモードを OFF にすると false に戻る)。false なら Z マイナス (床 = groundAt)、true なら Z プラス (床 = 下向きの面 ceilAt。invStep と同じ)
//   見直しは糸が行うので、画面の糸は止まらない。見直しが済む (返事が届く) までは前の結果のまま。
// 外判定 (outNow) は、外部視点 (着陸パッドなどのキャプション) と、出す升のかたまりの決まり (上の「升のかたまりの出し入れ」) の両方に使う
const OUT = { IDLE_MS: 500 };
// outPos: 前に外判定した (済んだ) 位置。outBusy: 糸に頼んでいる見直し { id, kind ('scan' = 6 つの向き、'cross' = 通り抜けの線分), pos }。outTried: 前に見直し・線分を済ませた位置。
// outWant: 見直しを頼まれている。outLast・outMoveAt: 前のコマの見ている位置と、最後に動いた時刻
let outNow = false, outBusy = null, outPos = null, outTried = null, outWant = false, outCur = null, outLast = null, outMoveAt = 0;
// 足 (歩くモード: wpos。歩くモードでない: カメラから重力の向きへ目の高さだけ下がった点) の下 LOADCFG.outFloor m 以内に、立てる面があるか (立っている面も入る)。出ているかたまりの当たりで見る
const outFloorNear = () => {
  const FL = LOADCFG.outFloor;
  if (walk) { const g = groundAt(wpos.x, wpos.y, wpos.z + 0.05); return g !== null && wpos.z - g <= FL; }
  const p = cam.position, e = STANCE.stand.eye;
  if (inv) { const fz = p.z + e, g = ceilAt(p.x, p.y, fz - 0.05); return g !== null && g - fz <= FL; }
  const fz = p.z - e, g = groundAt(p.x, p.y, fz + 0.05); return g !== null && fz - g <= FL;
};
// 外判定の見直しを頼む (エレベーター・トラム・はしごなど、位置を移す仕組みで移り終えたとき・行き先へ飛んだとき)。見直すのは次に止まったとき (outTick)
function outRequest() { outWant = true; }
// 外判定が変わったら、出すかたまりを見直す (「外」のときは内装の区画を出さない。上の「升のかたまりの出し入れ」)
function outSet(v) { if (v === outNow) return; outNow = v; review(false); }
function outTick() {
  if (!cur || !cur.index || SITE) { outNow = false; outBusy = null; outPos = null; outCur = null; outLast = null; outTried = null; outWant = false; return; }
  const p = cam.position, now = performance.now();
  // 動いたか (前のコマの位置と比べる)。動いたら、途中の見直しはやめる (糸の返事は捨てる)
  if (!outLast) { outLast = p.clone(); outMoveAt = now; }
  else if (outLast.distanceToSquared(p) > 1e-8) { outLast.copy(p); outMoveAt = now; outBusy = null; }
  // (a) 地点を読み込んで表示が替わった
  if (outCur !== cur) { outCur = cur; outBusy = null; outPos = null; outTried = null; outWant = true; }
  if (now - outMoveAt < OUT.IDLE_MS || outBusy) return;
  // 頼まれていず、止まった位置が前に見直し・線分を済ませた位置のままなら、何もしない
  if (!outWant && outPos && outTried && outTried.distanceToSquared(p) <= 1e-8) return;
  const c = cur.center, cc = cur.cc, r = cur.r, abs = v => [v.x + c[0], v.y + c[1], v.z + c[2]], pa = abs(p);
  // 地点の立方体の外にいるときは、線分を見ずに見直す (糸がすぐ「外」を返す)
  if (outWant || !outPos || Math.abs(pa[0] - cc[0]) > r || Math.abs(pa[1] - cc[1]) > r || Math.abs(pa[2] - cc[2]) > r) {
    // 床の決まり: 足の下に床があれば、6 つの向きを見ずに「中」(出ているかたまりに床が無ければ、糸が地点の全部の部品でもう一度見る)
    if (outFloorNear()) { outPos = p.clone(); outTried = outPos; outWant = false; outSet(false); return; }
    const e = STANCE.stand.eye, foot = walk ? { p: abs(wpos), mode: 'g' } : inv ? { p: [pa[0], pa[1], pa[2] + e], mode: 'c' } : { p: [pa[0], pa[1], pa[2] - e], mode: 'g' };
    outBusy = { id: ++workerSeq, kind: 'scan', pos: p.clone() };
    worker.postMessage({ type: 'out', id: outBusy.id, og: originGen, kind: 'scan', p: pa, dist: LOADCFG.outDist, floor: LOADCFG.outFloor, foot });
  }
  // (c) 止まった位置が変わっていれば、前に外判定した位置からの線分を 1 回見る
  else {
    outBusy = { id: ++workerSeq, kind: 'cross', pos: p.clone() };
    worker.postMessage({ type: 'out', id: outBusy.id, og: originGen, kind: 'cross', a: abs(outPos), b: pa });
  }
}
// 糸の返事 (頼んだ後に動いていたら捨てる)
function onOut(d) {
  const b = outBusy; if (!b || b.id !== d.id) return;
  outBusy = null;
  // 糸が見られなかった (失敗): この位置は済んだことにして、繰り返し頼まない。地点が替わった直後の返事 (stale) は、次のコマで頼み直す
  if (d.error) { outTried = b.pos; outPos = outPos || b.pos; outWant = false; return; }
  if (d.stale) return;
  outTried = b.pos;
  if (b.kind === 'scan') { outPos = b.pos; outWant = false; outSet(!!d.outside); }
  else if (d.hit) outWant = true;   // 通り抜けた: 続けて (止まったままなら次のコマから) 見直す
}
const extViewNow = () => !!cur && !SITE && ((!walk && dist >= cur.r) || outNow);
function refreshSwitches() {
  document.querySelectorAll('.mk').forEach(cb => { cb.disabled = extView; });
  for (const id of ['chkExtPad', 'chkExtDock', 'chkExtFreight', 'chkExtHangar']) { const cb = document.getElementById(id); if (cb) cb.disabled = !extView || cb.dataset.zero === '1'; }
}
function updExtView() {
  outTick();
  const v = extViewNow(); if (v === extView) return;
  extView = v; applyVis(); applyMarkVis(); refreshSwitches();
  if (gravG) gravG.visible = !extView;   // 重力の範囲の箱 (drawGravity) も、内部の印と同じ値で出す・消す
}
refreshSwitches();
try { const s = JSON.parse(localStorage.getItem('view3d.marks') || '{}'); document.querySelectorAll('.mk').forEach(cb => { if (typeof s[cb.dataset.cat] === 'boolean') cb.checked = s[cb.dataset.cat]; }); } catch (_) { }
document.querySelectorAll('.mk').forEach(cb => cb.addEventListener('change', applyMarkVis));
['cutH', 'ghostA'].forEach(id => document.getElementById(id).addEventListener('input', applyFloor));
const ORBIT_D = 10;
document.getElementById('walkBtn').onclick = () => {
  if (!cur) return; walk = !walk; document.getElementById('walkBtn').textContent = '歩くモード: ' + (walk ? 'ON' : 'OFF');
  outRequest();   // 外判定を見直す (足の位置が、歩いている位置とカメラの下とで替わるので、結果が変わりうる。outTick)
  if (walk) { buildGrid(); buildCeil(); buildWall(); air = false; vz = 0; onLadder = null; stance = 'stand';
    // 今のカメラの xy のまま、カメラの真下の面に立つ (面が無ければカメラの高さ − 目の高さ)。向きもカメラのまま
    const st0 = cam.position.clone(); wpos.set(st0.x, st0.y, st0.z - STANCE.stand.eye);
    const g = groundAt(st0.x, st0.y, st0.z); if (g !== null) wpos.z = g;
    const dv = cam.getWorldDirection(new THREE.Vector3()); wyaw = Math.atan2(dv.y, dv.x); wpitch = Math.asin(Math.max(-1, Math.min(1, dv.z))); walkCam(); updWalkUi(); document.getElementById('walkInfo').textContent = run ? '走る' : '歩く'; }
  else {
    // 歩くモードを OFF にしても、カメラの位置と向きは変えない (2026-11-04、利用者の指摘「歩くモード OFF で位置が変わる」)。
    // 回す中心 (tgt) は、画面の真ん中の先で当たる面の点 (当たらなければ ORBIT_D m 先)。そこからの距離・向きを、今のカメラの位置になるように決める
    hideTbox(); document.getElementById('walkInfo').textContent = ''; inv = false; cam.up.set(0, 0, 1);
    cam.updateMatrixWorld(); const P = cam.position.clone(), d = cam.getWorldDirection(new THREE.Vector3());
    ray.setFromCamera(new THREE.Vector2(0, 0), cam);
    const hit = ray.intersectObjects(Object.values(groups).filter(g => g).flat().filter(m => m.visible), false).find(h => visZ(h.point.z) && h.distance > 0.3);
    dist = hit ? hit.distance : ORBIT_D; tgt.copy(P).addScaledVector(d, dist);
    az = Math.atan2(-d.y, -d.x); el2 = Math.asin(Math.max(-1, Math.min(1, -d.z))); upd();
  }
};
document.getElementById('runBtn').onclick = () => { run = !run; updWalkUi(); };
document.getElementById('stBtn').onclick = () => { const n = { stand: 'crouch', crouch: 'prone', prone: 'stand' }[stance]; const keep = stance; stance = n; if (walk && !fits(wpos.x, wpos.y, wpos.z)) stance = keep; updWalkUi(); };
document.getElementById('ncBtn').onclick = () => { noclip = !noclip; updWalkUi(); };
document.getElementById('recBtn').onclick = () => {
  if (!walk) return;
  if (rec) { document.getElementById('recBtn').textContent = '録画を始める'; document.getElementById('recInfo').textContent = `録画 ${rec.points.length} 点`; window.__lastRec = rec; rec = null; }
  else { rec = { format: 'scjt-walk-path-1', station: cur.station, label: cur.label, center: cur.center, t: 0, points: [], comments: [] }; document.getElementById('recBtn').textContent = '録画を止める'; }
};
// 再生 (2026-11-06、利用者の指摘「録画はいいけど再生がなくない？」): いちばん新しい録画 (録画中ならその途中まで、無ければ最後に読み込んだ録画) を、
// 保存・読み込みをせずにその場で再生する (読み込みの再生と同じ playStep)。再生中に押すと止める
function startPlay(r) {
  if (!r || !Array.isArray(r.points) || !r.points.length) { document.getElementById('recInfo').textContent = '再生する録画がありません'; return; }
  if (!walk) document.getElementById('walkBtn').click();
  play = { t: 0, i: 0, points: r.points.slice(), center: r.center }; showComments(r.comments || []);
  // 一定速度 (2026-11-06、利用者の指示「ついでに一定速度で再生も付けてほしい」): 録画の時刻ではなく、道のりを決めた速さ (m/秒) で進む (止まっていた所は飛ばす)
  if (document.getElementById('playConst').checked) {
    const P = play.points, cum = [0];
    for (let k = 1; k < P.length; k++) cum.push(cum[k - 1] + Math.hypot(P[k][1] - P[k - 1][1], P[k][2] - P[k - 1][2], P[k][3] - P[k - 1][3]));
    play.cum = cum; play.d = 0; play.speed = Math.max(0.1, +document.getElementById('playSpeed').value || 2.8);
  }
}
document.getElementById('playBtn').onclick = () => {
  if (play) { play = null; document.getElementById('recInfo').textContent = '再生を止めました'; return; }
  if (rec) document.getElementById('recBtn').click();   // 録画中なら止めてから再生する
  startPlay(window.__lastRec);
};
document.getElementById('saveBtn').onclick = () => {
  const r = rec || window.__lastRec; if (!r) return;
  const blob = new Blob([JSON.stringify({ format: r.format, station: r.station, label: r.label, center: r.center, points: r.points, comments: r.comments || [] })], { type: 'application/json' });
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `walk_${r.label.replace(/[^A-Za-z0-9_]/g, '_')}.json`; a.click();
};
document.getElementById('loadF').onchange = async e => {
  const f = e.target.files[0]; if (!f) return; const r = JSON.parse(await f.text());
  if (r.format !== 'scjt-walk-path-1' || !Array.isArray(r.points) || !r.points.length) { document.getElementById('recInfo').textContent = '読めない形'; return; }
  window.__lastRec = r; startPlay(r);
};
const cut = document.getElementById('cut'); cut.addEventListener('input', () => { clip.constant = +cut.value; document.getElementById('cutv').textContent = cut.value; });
// ===== 行き先の一覧 (名前は訳。押すと、まだ読み込んでいない所ならそのまわりを読み込み、そこを中心にする) =====
// 行き先を選んだとき (と開いたとき) の始める所 (2026-11-04、利用者の指示「開始地点が壁の向こうの事がある。基本端末前 端末ない場合は入り口 それもない場合は丸の地点の目の高さ」・
// 利用者の指摘「扉の裏開始」):
//   1. 端末の前: 行き先が端末ならその端末。店なら、その店の端末 (地図の JSON の端末の shopEntity が店の名前のもの) のうち、店の印にいちばん近いもの (30 m 以内)。
//      端末から水平に 1.2 m の 8 方向 (端末の向き yaw から順に) の床に立つ。
//   2. 入口の前: 店の入口 (店の入口の直線が障害物に当たらなかった開口部) の、店の中心から見て開口部の 1 m 先の床に立ち、入口の方を向く。
//   3. 丸の地点のそば: 丸の地点から水平に 2・3・4 m の 8 方向の床 (丸の地点の 2.5 m 下〜0.5 m 上) に立ち、丸の地点を見る。
//   4. どれも無ければ、丸の地点の真下の床 (無ければ丸の地点) の目の高さ。
//   1〜3 は、立つ所の目の高さ (床 + 1.7 m) から行き先 (端末・入口の点・丸の地点) へ直線を引き、壁・扉 (すり抜けるドアを含む全部の部品) に当たらないものだけを選ぶ
//   (行き先の手前 0.3 m まで。壁・扉の向こうから始めないため)。
//   店 (shop・admin・pharmacy) は上の 2・3 を使わず、端末の前 → カウンターの前 (standAtCounter) → 店の点そのもの (placeStart の中の説明。2026-11-05、利用者の指示)
// 決めたものは window.__startHow に書く (確かめ用)
function doorHit(ax, ay, az, bx, by, bz) {
  const x0 = Math.min(ax, bx), x1 = Math.max(ax, bx), y0 = Math.min(ay, by), y1 = Math.max(ay, by), z0 = Math.min(az, bz), z1 = Math.max(az, bz);
  const dx = bx - ax, dy = by - ay, dz = bz - az;
  // 出ているかたまりのドアの形 (種類 door) の三角形を全部見る
  for (const me of groups.door || []) {
    const ch = me.userData.ch, [o, n] = ch.offs.door, F = ch.F;
    for (let t = 0; t < n / 9; t++) {
      const b = o + t * 9;
      if (Math.max(F[b], F[b + 3], F[b + 6]) < x0 || Math.min(F[b], F[b + 3], F[b + 6]) > x1 || Math.max(F[b + 1], F[b + 4], F[b + 7]) < y0 || Math.min(F[b + 1], F[b + 4], F[b + 7]) > y1 || Math.max(F[b + 2], F[b + 5], F[b + 8]) < z0 || Math.min(F[b + 2], F[b + 5], F[b + 8]) > z1) continue;
      const e1x = F[b + 3] - F[b], e1y = F[b + 4] - F[b + 1], e1z = F[b + 5] - F[b + 2], e2x = F[b + 6] - F[b], e2y = F[b + 7] - F[b + 1], e2z = F[b + 8] - F[b + 2];
      const px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x, det = e1x * px + e1y * py + e1z * pz; if (Math.abs(det) < 1e-12) continue;
      const inv = 1 / det, tx = ax - F[b], ty = ay - F[b + 1], tz = az - F[b + 2]; const u = (tx * px + ty * py + tz * pz) * inv; if (u < 0 || u > 1) continue;
      const qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x; const v = (dx * qx + dy * qy + dz * qz) * inv; if (v < 0 || u + v > 1) continue;
      const tt = (e2x * qx + e2y * qy + e2z * qz) * inv; if (tt > 0 && tt < 1 && visZ(az + dz * tt)) return true;
    }
  }
  return false;
}
// 目の高さの見通し: (x, y, eye) から (tx, ty, tz) の手前 0.3 m まで、壁・扉に当たらないか
function clearSight(x, y, eye, tx, ty, tz) {
  const d = Math.hypot(tx - x, ty - y, tz - eye); if (d < 0.35) return true;
  const f = (d - 0.3) / d, ex = x + (tx - x) * f, ey = y + (ty - y) * f, ez = eye + (tz - eye) * f;
  return !segHitLong(x, y, eye, ex, ey, ez) && !doorHit(x, y, eye, ex, ey, ez);
}
// segHit は線の両端の升目しか見ないので、長い線は 0.5 m ごとに区切って見る
function segHitLong(ax, ay, az, bx, by, bz) {
  const d = Math.hypot(bx - ax, by - ay), k = Math.max(1, Math.ceil(d / 0.5));
  for (let i = 0; i < k; i++) { const f0 = i / k, f1 = (i + 1) / k; if (segHit(ax + (bx - ax) * f0, ay + (by - ay) * f0, az + (bz - az) * f0, ax + (bx - ax) * f1, ay + (by - ay) * f1, az + (bz - az) * f1)) return true; }
  return false;
}
// 判定の床 (床・筒の底の色の三角形) の上か (2026-11-04、利用者の指示「床のある側に」): (x, y) を含む床の色の三角形で、高さが g から 2 cm 以内のものがあるか
function buildFloorGrid() { }
function onFloor(x, y, g) {
  const n = cellTris('fl', Math.floor(x), Math.floor(y));
  for (let q = 0; q < n; q++) { const F = CT.C[q].F, b = CT.B[q];
    const v0x = F[b + 3] - F[b], v0y = F[b + 4] - F[b + 1], v1x = F[b + 6] - F[b], v1y = F[b + 7] - F[b + 1], den = v0x * v1y - v1x * v0y; if (Math.abs(den) < 1e-12) continue;
    const wx = x - F[b], wy = y - F[b + 1], s_ = (wx * v1y - v1x * wy) / den, t_ = (v0x * wy - wx * v0y) / den; if (s_ < 0 || t_ < 0 || s_ + t_ > 1) continue;
    const z = F[b + 2] + s_ * (F[b + 5] - F[b + 2]) + t_ * (F[b + 8] - F[b + 2]); if (Math.abs(z - g) <= 0.02 && visZ(z)) return true;
  }
  // 細かい形で補った面 (extra) の判定の床 (傾き 46° 以内の上向きの面) も見る
  const xl = extraCells(extra.fl, x, y);
  if (xl) for (let q = 0; q < xl.length; q += 2) { const z = triZ(extra.list[xl[q]].tris, xl[q + 1], x, y); if (z !== null && Math.abs(z - g) <= 0.02 && visZ(z)) return true; }
  return false;
}
// 段を上がる先が「手すりの高さ」の物か (2026-11-05、利用者「高さで区別が正しい」「手すりの高さは通れない。段差は通れる」。種類 (床・坂・階段) は見ない):
//   足元 (z) のまわり 9 点 (中心と GAP_R の 8 方向) のうち、上がる先の面 (z + STEP 以下でいちばん高い面 top) の真下に面 (below。足元から STEP 下まで) があり、
//   top − below が STEP を超えるものがあれば、その面は床に立つ背の高い物 (手すりの上面・壁の上) なので乗れない。
//   下り階段の脇の手すりは、上面が足元から 0.5 m でも、その下の段との差が 1 m なので乗れない。階段の段は真下が数 m 下の床 (探す範囲の外) なので上がれる。箱 (0.5 m) は差が STEP 以下なので乗れる
function tallObjectAt(x, y, z) {
  for (let k = -1; k < 8; k++) {
    const a = k * Math.PI / 4, px = k < 0 ? x : x + GAP_R * Math.cos(a), py = k < 0 ? y : y + GAP_R * Math.sin(a);
    const top = groundAt(px, py, z + STEP); if (top === null || top <= z + STEP_FLOOR_MIN) continue;
    const below = groundAt(px, py, top - 0.05);
    if (below !== null && below >= z - STEP && top - below > STEP) return true;
  }
  return false;
}
// 足元の面の部品の名前 (確かめ用)
function groundPart(x, y, zmax) {
  const g = groundAt(x, y, zmax); if (g === null) return null;
  // 足元の升の上向きの三角形のうち、(x, y) での高さが g のものの部品
  const n = cellTris('f', Math.floor(x), Math.floor(y));
  for (let q = 0; q < n; q++) { const ch = CT.C[q], F = ch.F, b = CT.B[q];
    const v0x = F[b + 3] - F[b], v0y = F[b + 4] - F[b + 1], v1x = F[b + 6] - F[b], v1y = F[b + 7] - F[b + 1], den = v0x * v1y - v1x * v0y; if (Math.abs(den) < 1e-12) continue;
    const wx = x - F[b], wy = y - F[b + 1], s_ = (wx * v1y - v1x * wy) / den, t_ = (v0x * wy - wx * v0y) / den; if (s_ < 0 || t_ < 0 || s_ + t_ > 1) continue;
    const z = F[b + 2] + s_ * (F[b + 5] - F[b + 2]) + t_ * (F[b + 8] - F[b + 2]); if (Math.abs(z - g) < 1e-4) { const pt = ch.parts[ch.ID[b / 9]]; return [pt[1], pt[0]]; } }
  return null;
}
function placeStart(t) {
  const c = cur.center, q = [t.pos[0] - c[0], t.pos[1] - c[1], t.pos[2] - c[2]];
  buildGrid(); buildWall(); buildCeil();
  const done = (how, x, y, g, extra) => { window.__startHow = Object.assign({ target: t.name, how, start: [x + c[0], y + c[1], g + c[2]].map(v => +v.toFixed(2)), floor: groundPart(x, y, g + 0.05) }, extra || {}); };
  let term = t.kind.startsWith('terminal_') ? t : null;
  const isShop = ['shop', 'admin', 'pharmacy'].includes(t.kind);
  // 店 (2026-11-05、利用者の指示「KEI-TO 端末前だとよかった。店の壁の裏。そもそも端末ない場所もカウンターあればカウンター なければ点の位置でいいのにわざわざずらしておかしい」):
  //   1. その店に結び付いた端末 (地図の JSON の端末の shop が店の名前、30 m 以内。近い順) があれば、端末の前 (standBefore)
  //   2. 端末が無ければ (端末の前が 1 つも成り立たないときも)、カウンターがあればカウンターの前 (standAtCounter。店と同じ階のものだけ)
  //   3. どちらも無ければ店の点 (SCShop の pos) そのもの (真下の床の目の高さ。xy はずらさない)
  //   入口の外側に立つ段と、店の中心から 2〜4 m の 8 方向にずらす段は店では使わない (コードは残す。店以外の行き先 (エレベーター・トラム・階段など) はこれまでのまま)
  if (!term && isShop) {
    const d3 = x => Math.hypot(x.pos[0] - t.pos[0], x.pos[1] - t.pos[1], x.pos[2] - t.pos[2]);
    const cands = VIEW.targets.filter(x => x.kind.startsWith('terminal_') && x.shop === t.name && d3(x) <= 30).sort((a, b) => d3(a) - d3(b));
    for (const x of cands) { const r = standBefore(x); if (r) { done('端末の前', r[0], r[1], r[2], { terminal: x.name }); return; } }
    const r = standAtCounter(t); if (r) { done('カウンターの前', r[0], r[1], r[2], { counter: r[3] }); return; }
    const g = groundAt(q[0], q[1], q[2] + 0.5), z = (g ?? q[2]) + 1.7;
    tgt.set(q[0], q[1], z); dist = 4; el2 = 0.3; upd();
    done('店の点', q[0], q[1], z - 1.7); return;
  }
  if (term) { const r = standBefore(term); if (r) { done('端末の前', r[0], r[1], r[2], { terminal: term.name }); return; } }
  if (isShop) {
    const en = (cur.entrances || []).filter(e => e.shop === t.name).sort((a, b) => Math.hypot(...a.pos.map((v, i) => v - t.pos[i])) - Math.hypot(...b.pos.map((v, i) => v - t.pos[i])))[0];
    for (const o of (en ? en.list : []).filter(o => o[5])) {
      const b = o[9], a = o[10];   // 入口の点 (開口部の箱の、店の中心に近い所) と、店の中心 (どちらも直線の高さ。中心からの相対)
      let dx = b[0] - a[0], dy = b[1] - a[1]; const dl = Math.hypot(dx, dy); if (dl < 1e-6) continue; dx /= dl; dy /= dl;
      const x = b[0] + dx * 1.0, y = b[1] + dy * 1.0, g = groundAt(x, y, b[2] + 0.5);
      if (g === null || !onFloor(x, y, g) || !clearSight(x, y, g + 1.7, b[0], b[1], b[2])) continue;
      look(x, y, g, b[0], b[1]); done('入口の前', x, y, g, { entrance: o[1] }); return;
    }
  }
  for (const r of [2, 3, 4]) for (let k = 0; k < 8; k++) {
    const a = k * Math.PI / 4, x = q[0] + Math.cos(a) * r, y = q[1] + Math.sin(a) * r, g = groundAt(x, y, q[2] + 0.5);
    if (g === null || g < q[2] - 2.5 || !onFloor(x, y, g) || !clearSight(x, y, g + 1.7, q[0], q[1], q[2])) continue;
    look(x, y, g, q[0], q[1]); done('丸の地点のそば', x, y, g, { distance: r }); return;
  }
  const g = groundAt(q[0], q[1], q[2] + 0.5), z = (g ?? q[2]) + 1.7;
  tgt.set(q[0], q[1], z); dist = 4; el2 = 0.3; upd();
  done('丸の地点の目の高さ', q[0], q[1], z - 1.7);
}
// カウンターの前に立つ所 (2026-11-05、利用者の指示「行先でショップ選んだ時に何か所か店の裏のドアの向こうに出現 本来カウンターの前にすべき」「床のある側に」):
// その店のカウンターの箱 (region.json の counters。店の中心から箱まで COUNTER_DIST m 以内で、店の印の COUNTER_BELOW m 下〜COUNTER_ABOVE m 上に掛かる = 店と同じ階のもの) を近い順に見て、
// 箱の xy の 4 辺それぞれの外側 COUNTER_OUT m の点を候補にする。高さは箱の上面から下に床を探す (groundAt)。
// 候補が成り立つ条件: 床がある・床が店の印の COUNTER_BELOW m 下〜COUNTER_ABOVE m 上 (丸の地点のそばに立つ決まりと同じ)・判定の床の上 (onFloor)・その店のどのカウンターの箱の中でもない・
//   目の高さから「カウンターの近い面の上の縁」(箱の中で立つ点にいちばん近い xy、高さは箱の上面 + 0.05 m) への見通し (clearSight)。
//   (2026-11-05、利用者の報告「行先で店の裏」の原因: 前は的が箱の中心だったので、見通しの線が必ずカウンター自身の面に当たり、どの辺も成り立たず、
//    端末・入口・丸の地点の順の決め方に落ちていた (Orison Whammers: 16 箱 × 4 辺が全部落ちた)。また 10 m の距離だけで集めた箱に上の階の別の店のカウンターが入り、
//    そこが成り立つと上の階に立っていた (Orison Kel-To → 真上の FTL の counter の部品、店の z + 4.1 m))
// 成り立つ辺が複数あれば、客側を選ぶ:
//   1. その辺の外側 (立つ点より先の半平面) に、その店のほかのカウンターの箱 (立つ点から xy で COUNTER_SIDE_R m 以内) が少ない辺 (counterBehind)。
//      厨房・事務側は調理台・バックカウンター・U 字の向かいのカウンターで囲まれ、客側にはその店のカウンターが無い
//      (Seraphim HotDogBar: 厨房側にバーカウンター 2、客側 0。Cargo Office: U 字の内側に向かいのカウンター、外側 0。Orison Whammers: L 字の内側に調理台 3 以上、外側 0)
//   2. 同じなら、辺の外向きに歩ける距離 (counterOpen。0.5 m ずつ最大 COUNTER_OPEN m。足元から体の高さの 3 本の線 (歩くモードの blocked と同じ高さ) が壁・物に当たらず、
//      床が続き、その店のカウンターの箱に入らない所まで) が長い辺 (Seraphim Armor: カウンター 1 つ。客側 8 m、ほかの 3 辺は壁で 0〜0.5 m)
//   3. それも同じなら店の印から遠い辺
//   (歩ける距離だけ・歩いて行ける床の広さでは客側が決まらなかった: HotDogBar の厨房は奥の廊下につながって 8 m 歩け、客側はフードコートのテーブルで 6.5 m)
// 向きはカウンターの中心を見る。どのカウンターでも成り立たなければ null。見つかれば [x, y, 床, 説明]
const COUNTER_DIST = 10, COUNTER_OUT = 0.8, COUNTER_OPEN = 8, COUNTER_BELOW = 2.5, COUNTER_ABOVE = 0.5, COUNTER_SIDE_R = 4;
// (x, y) の床 g が、その店のカウンターの箱 b の中か (xy が箱の中で、g が箱の下 0.5 m 〜 上)
const inCounterBox = (b, x, y, g) => b[0][0] <= x && x <= b[1][0] && b[0][1] <= y && y <= b[1][1] && b[0][2] - 0.5 <= g && g <= b[1][2];
// (x0, y0, 床 g) から (x1, y1) へ 1 歩歩けるか: 歩く先に床 (g の STEP 下〜STEP 上) があり、カウンターの箱に入らず、足元から体の高さの 3 本の線が壁・物に当たらない。歩けるなら歩く先の床、歩けなければ null
function counterStep(x0, y0, g, x1, y1, boxes) {
  const g1 = groundAt(x1, y1, g + STEP); if (g1 === null || g1 < g - STEP) return null;
  if (boxes.some(b => inCounterBox(b, x1, y1, g1))) return null;
  const H = STANCE.stand.h;
  for (const h of [STEP + 0.05, (STEP + H) / 2, H - 0.1]) if (segHit(x0, y0, g + h, x1, y1, g + h)) return null;
  return g1;
}
// 辺の外向き (nx, ny) に歩ける距離 (m。0.5 m ずつ、最大 COUNTER_OPEN)
function counterOpen(x, y, g, nx, ny, boxes) {
  let gz = g;
  for (let s = 0; s < COUNTER_OPEN; s += 0.5) {
    const g1 = counterStep(x + nx * s, y + ny * s, gz, x + nx * (s + 0.5), y + ny * (s + 0.5), boxes); if (g1 === null) return s; gz = g1;
  }
  return COUNTER_OPEN;
}
// 立つ点 (x, y) の辺の外側 (箱の一部が立つ点より先 (nx, ny) にはみ出す) にある、その店のほかのカウンターの箱 (bi 以外) のうち、立つ点から xy で COUNTER_SIDE_R m 以内の数
function counterBehind(x, y, nx, ny, boxes, bi) {
  let n = 0;
  for (let k = 0; k < boxes.length; k++) {
    if (k === bi) continue; const [lo, hi] = boxes[k];
    const beyond = nx > 0 ? hi[0] > x : nx < 0 ? lo[0] < x : ny > 0 ? hi[1] > y : lo[1] < y; if (!beyond) continue;
    if (Math.hypot(Math.max(lo[0] - x, x - hi[0], 0), Math.max(lo[1] - y, y - hi[1], 0)) <= COUNTER_SIDE_R) n++;
  }
  return n;
}
function standAtCounter(t) {
  const c = cur.center, q = [t.pos[0] - c[0], t.pos[1] - c[1], t.pos[2] - c[2]];
  const boxDist = ([lo, hi]) => Math.hypot(...[0, 1, 2].map(a => Math.max(lo[a] - q[a], q[a] - hi[a], 0)));
  const boxes = ((cur.counters || {})[t.name] || []).filter(b => boxDist(b) <= COUNTER_DIST && b[1][2] >= q[2] - COUNTER_BELOW && b[0][2] <= q[2] + COUNTER_ABOVE).sort((a, b) => boxDist(a) - boxDist(b));
  for (let bi = 0; bi < boxes.length; bi++) {
    const [lo, hi] = boxes[bi], cx = (lo[0] + hi[0]) / 2, cy = (lo[1] + hi[1]) / 2;
    const sides = [[hi[0] + COUNTER_OUT, cy, 1, 0], [lo[0] - COUNTER_OUT, cy, -1, 0], [cx, hi[1] + COUNTER_OUT, 0, 1], [cx, lo[1] - COUNTER_OUT, 0, -1]];
    const cands = [];
    for (const [x, y, nx, ny] of sides) {
      const g = groundAt(x, y, hi[2]);
      if (g === null || g < q[2] - COUNTER_BELOW || g > q[2] + COUNTER_ABOVE || !onFloor(x, y, g)) continue;
      if (boxes.some(b => inCounterBox(b, x, y, g))) continue;
      // 的: カウンターの近い面の上の縁 (箱の中で立つ点にいちばん近い xy、箱の上面 + 0.05 m)。箱の中心を的にすると線がカウンター自身に当たる
      const tx = Math.min(Math.max(x, lo[0]), hi[0]), ty = Math.min(Math.max(y, lo[1]), hi[1]);
      if (!clearSight(x, y, g + 1.7, tx, ty, hi[2] + 0.05)) continue;
      cands.push({ x, y, g, nx, ny, behind: counterBehind(x, y, nx, ny, boxes, bi), open: counterOpen(x, y, g, nx, ny, boxes), far: Math.hypot(x - q[0], y - q[1]) });
    }
    if (!cands.length) continue;
    cands.sort((a, b) => (a.behind - b.behind) || (b.open - a.open) || (b.far - a.far));
    const best = cands[0];
    look(best.x, best.y, best.g, cx, cy);
    return [best.x, best.y, best.g, { index: bi, box: [lo.map((v, a) => +(v + c[a]).toFixed(2)), hi.map((v, a) => +(v + c[a]).toFixed(2))], side: [best.nx, best.ny], behind: best.behind, open: best.open, sides: cands.map(k => [k.nx, k.ny, k.behind, k.open]) }];
  }
  return null;
}
// 立つ所 (x, y, 床 g) を中心に、(tx, ty) の方を向いて少し上から見下ろす (カメラは立つ所の後ろ)
function look(x, y, g, tx, ty) { tgt.set(x, y, g + 1.7); az = Math.atan2(y - ty, x - tx); dist = 4; el2 = 0.45; upd(); }
// 端末の前に立つ所 (端末から水平に 1.2 m の 8 方向。端末の向き yaw から順に)。目の高さから端末への見通しのある所。見つかれば [x, y, 床] (中心からの相対)
let startAt = null;
// 端末の前の向き: 地図の JSON の端末の yaw (Entity の回転で局所の +x を回した向き。度) に FRONT_DEG を足した向き。
// FRONT_DEG = 90 (局所の +y の向き): Orison の CousinCrows のまわりの端末 10 個で、端末から 1.2 m の 8 方向のうち判定の床があり見通しのある向きを数えると、
// yaw + 90° は 10 個とも当てはまり、ほかの向き (+0° は 2 個、−90° は 6 個) は当てはまらないものがあった (端末の部品の画面の向きそのものは確かめていない)
const FRONT_DEG = 90;
function standBefore(t, diag) {
  const c = cur.center, q = [t.pos[0] - c[0], t.pos[1] - c[1], t.pos[2] - c[2]];
  buildGrid(); buildWall();
  const y0 = (t.yaw != null ? t.yaw : 0) * Math.PI / 180 + FRONT_DEG * Math.PI / 180;
  const res = [];
  for (let k = 0; k < 8; k++) {
    const a = y0 + (k % 2 ? -1 : 1) * Math.ceil(k / 2) * Math.PI / 4;
    const x = q[0] + Math.cos(a) * 1.2, y = q[1] + Math.sin(a) * 1.2, g = groundAt(x, y, q[2] + 0.5);
    if (diag) { const ok = g !== null && g >= q[2] - 1.5 && onFloor(x, y, g) && clearSight(x, y, g + 1.7, q[0], q[1], Math.max(q[2], g + 1.0)); res.push([+((a - y0) * 180 / Math.PI).toFixed(0), ok]); continue; }
    if (g === null || g < q[2] - 1.5 || !onFloor(x, y, g)) continue;
    if (!clearSight(x, y, g + 1.7, q[0], q[1], Math.max(q[2], g + 1.0))) continue;
    look(x, y, g, q[0], q[1]);
    window.__start = { terminal: t.name, stand: [x + c[0], y + c[1], g + c[2]], dir: +(Math.atan2(q[1] - y, q[0] - x) * 180 / Math.PI).toFixed(1), front: k === 0 };
    return [x, y, g];
  }
  return diag ? res : null;
}
window.__standDiag = () => (cur.targets || []).filter(t => t.kind.startsWith('terminal_')).map(t => [t.kind, t.name, t.yaw, standBefore(t, true)]);
// 読み込む範囲 (既定 150 m: 試験の頁の Whammers の地点と同じ。60 m では、大きな区画は部品を全部描くのに、その中の小さな区画 (エレベーターの扉など) が範囲の外で読まれず、枠だけになった)
// 先に描く範囲 (行き先のまわり。担当が決めた値)
const FIRST_R = 30;
// (2026-11-06、親の決定: 距離の段が読み込んだ範囲の端で切れないように) 半径は、欄の値と「大を描く距離 (LOADCFG.d3) × 2」の大きいほう。
// 取り出し直しは立方体の中心から半径の半分 (nearCheck。決まりは変えない) なので、半径 ≥ d3 + 半径の半分 (= d3 × 2) なら、歩いている位置から d3 m までは、取り出し直しの前でも必ず立方体の中にある
function regionR() { return Math.max(10, +document.getElementById('regionR').value || 150, 2 * LOADCFG.d3); }
// 読み込み中に行き先を選んだら、今の読み込みが終わってから、その行き先を始める (前は押しても読み込まれなかった)。
// 行き先を選び直したら、前の行き先の残りの読み込み (裏の ±R) はしない (goGen)
let goGen = 0, pendingGo = null, focusTk = 0;
function goTarget(t, btn) {
  // 文字の道案内も、押した先の項目に合わせる (今の地点に無ければ、読み込んだ後の show で合わせる)。道案内の無い種類 (エレベーター・トラム・hangar_lobby) は合わせない
  if (!['elevator', 'tram', 'hangar_lobby'].includes(t.kind)) { guideWant = t; renderGuide(); }
  if (loading) { pendingGo = [t, btn]; document.querySelectorAll('#tlist button').forEach(b => b.classList.remove('on')); btn?.classList.add('on'); ++goGen; return; }
  document.querySelectorAll('#tlist button').forEach(b => b.classList.remove('on')); btn?.classList.add('on');
  const q = t.pos, r = regionR();
  const inside = cur && cur.r === r && Math.abs(q[0] - cur.cc[0]) <= r && Math.abs(q[1] - cur.cc[1]) <= r && Math.abs(q[2] - cur.cc[2]) <= r;
  // 最初の見え方: 行き先の印を中心に、印から 8 m・上へ約 3.5 m (傾き 0.45) から見下ろす (2026-11-04、利用者の指摘「初期位置の問題」。前は 25 m・約 14 m 上で、上の階・屋根の中から見ていた)
  const focus = () => { if (walk) return; startAt = null; placeStart(t); outRequest(); review(true); };   // 行き先へ飛んだので、外判定を見直す (outTick)・出す升のかたまりを見直す (review)
  // まだ読み込んでいない所なら、まず行き先のまわり ±FIRST_R m だけを取り出して描き、始める所に立つ (操作できる)。
  // 続けて ±r m 全部を裏で取り出し、できたら見ている所・歩いている所をそのままに入れ替える (先に作った区画は使い回されるので、取り出し直さない)
  // 読み込んである範囲の中なら、行き先のまわりの升のかたまり (形と当たり) が出そろってから始める所を決める (ensureNear。2026-11-06。歩くモードでは位置を移さないので待たない)
  if (inside) { if (walk) focus(); else { const og = originGen, tk = ++focusTk; ensureNear(q.slice()).then(() => { if (og === originGen && tk === focusTk) focus(); }); } }
  else if (r <= FIRST_R) loadRegion(q.slice(), r).then(() => { if (cur) focus(); });
  else {
    const gen = ++goGen;
    loadRegion(q.slice(), FIRST_R).then(() => {
      if (!cur || gen !== goGen) return; focus(); window.__firstDone = performance.now();
      return loadRegion(q.slice(), r, true, true);
    }).then(() => { if (gen === goGen) window.__allDone = performance.now(); });
  }
}
// ===== 文字の道案内 (2026-11-05、利用者の指示「文字の道案内にドロップダウンリストを出して選んで表示する形にして」) =====
// 項目は今の地点の道案内 (cur.guide。名前は飛ぶ先の一覧と同じ dispRow、並びも同じ)。いちばん上の「（選ぶ）」は何も出さない。
// 飛ぶ先の一覧で押した先が道案内にもあれば、その項目に合わせる (guideWant。読み込みが終わった後の show でも合わせる)。
// 文は、設定「マップのデバッグ表示」(DEBUG) のときは確かめ用の内訳を含む全文 (dbg)、そうでなければ案内の部分 (text)
let guideWant = null;
function renderGuide() {
  const sel = document.getElementById('guideSel'), box = document.getElementById('guide');
  const list = (cur && cur.guide) || [];
  const keep = guideWant || (sel.selectedIndex > 0 && sel._list ? sel._list[sel.selectedIndex - 1]?.t : null);
  sel.innerHTML = ''; const o0 = document.createElement('option'); o0.value = ''; o0.textContent = '（選ぶ）'; sel.appendChild(o0);
  list.forEach((g, i) => { const o = document.createElement('option'); o.value = String(i); o.textContent = g.shop; sel.appendChild(o); });
  sel._list = list;
  const k = keep ? list.findIndex(g => g.t === keep) : -1;
  sel.selectedIndex = k >= 0 ? k + 1 : 0;
  if (k >= 0) guideWant = null;
  showGuide();
}
function showGuide() {
  const sel = document.getElementById('guideSel'), box = document.getElementById('guide');
  const g = sel.selectedIndex > 0 && sel._list ? sel._list[sel.selectedIndex - 1] : null;
  box.textContent = g ? (DEBUG ? g.dbg : g.text) : '';
}
document.getElementById('guideSel').addEventListener('change', () => { guideWant = null; showGuide(); });
// 左の欄のまとまり (details.sec) の開いた・畳んだ状態を localStorage (view3d.side = {id: true|false}) に覚える。覚えが無ければ html の初めの状態
// (飛ぶ先・歩くモード・文字の道案内が開く)
try { const s = JSON.parse(localStorage.getItem('view3d.side') || '{}'); if (s && typeof s === 'object') document.querySelectorAll('details.sec').forEach(d => { if (typeof s[d.id] === 'boolean') d.open = s[d.id]; }); } catch (_) { }
document.querySelectorAll('details.sec').forEach(d => d.addEventListener('toggle', () => {
  const s = {}; document.querySelectorAll('details.sec').forEach(x => { s[x.id] = x.open; });
  try { localStorage.setItem('view3d.side', JSON.stringify(s)); } catch (_) { }
}));
function renderTargets() {
  const box = document.getElementById('tlist'); box.innerHTML = ''; const f = document.getElementById('tfilter').value.trim().toLowerCase();
  const order = { shop: 0, admin: 1, pharmacy: 2, hangar_lobby: 4, elevator: 5, tram: 6, stair: 7 };
  // 絞り込みは、出している名前 (日本語) と元の名前 (Entity 名など) と種類の札のどれでも
  const list = VIEW.targets.filter(t => !f || (t.label + ' ' + t.name + ' ' + dispName(t)).toLowerCase().includes(f))
    .sort((a, b) => ((order[a.kind] ?? 3) - (order[b.kind] ?? 3)) || dispName(a).localeCompare(dispName(b), 'ja'));
  for (const t of list.slice(0, 500)) { const b = document.createElement('button'); b.textContent = dispRow(t); b.title = t.name; b._t = t; b.onclick = () => goTarget(t, b); box.appendChild(b); }
  if (list.length > 500) { const d = document.createElement('div'); d.textContent = `ほか ${list.length - 500} 件 (絞り込んでください)`; box.appendChild(d); }
}
document.getElementById('tfilter').addEventListener('input', renderTargets);
document.getElementById('reloadHere').onclick = () => { if (!cur) return; const c = cur.center; const q = walk ? wpos : tgt; loadRegion([q.x + c[0], q.y + c[1], q.z + c[2]], regionR()); };
let lastNear = 0, lastFail = null; const FAIL_HOLD_MS = 30000;
function nearCheck() {
  const now = performance.now(); if (now - lastNear < 1000 || loading || !cur) return; lastNear = now;
  const q = walk ? wpos : tgt, r = cur.r;
  // 歩くモードでないときは、カメラが読み込んだ範囲より近く (距離が半径未満) にあるときだけ読み直す。引いて全体を見ている間に、回転の中心が動くたびに読み直して
  // ラベル・引き出し線が消え続けていた (2026-11-05、利用者の報告「何度も作画中を繰り返してる」「外部パッドなどの引き出し線とキャプションがなくなってる」。親の決定)
  if (!walk && dist >= r) return;
  // 読み直しが失敗した直後 (FAIL_HOLD_MS) は、同じ半径では読み直さない (失敗 → 1 秒後にまた読み直す、の繰り返しを止める。2026-11-05、親の決定)
  if (lastFail && lastFail.r === regionR() && performance.now() - lastFail.t < FAIL_HOLD_MS) return;
  // 地点の立方体の中心 (cur.cc) から半径の半分より離れたら、今の位置を中心に区画を取り出し直す (座標の原点・出ている升のかたまりはそのまま。足りないかたまりだけ裏で足す。2026-11-06)
  const a = [q.x + cur.center[0], q.y + cur.center[1], q.z + cur.center[2]];
  if (Math.abs(a[0] - cur.cc[0]) > r / 2 || Math.abs(a[1] - cur.cc[1]) > r / 2 || Math.abs(a[2] - cur.cc[2]) > r / 2) loadRegion(a, regionR(), true);
}
// ===== 外から見たときの印と見出し (着陸パッド・ハンガー・ドッキングポート) =====
// (2026-11-05、利用者の指示「Seraphim なんか引いて行って外部視点になったときパッドなんかは引き出し線とか前にさんざんやったのを表示するように」「一度作ってんだから、今回入れてよ。外部になったときだけ出して」)
// v2.5.3 の旧方式のステーションの 3D (wwwroot/station3d/viewer.html) の印・見出し・引き出し線の置き方を、そのまま移したもの (置き方の決まり・定数・色・丸の大きさ・線の太さは旧方式のまま)。
// 印の元: station.json を読んだ後にアプリへ {type:'exteriorGet'} で頼み、{type:'exterior', pads:[{pos,key,text}], hangars:[{pos,size}], docks:[{pos,key,text}], roofDoors:[{origin,center}]} で返る
// (StationView3D.ExteriorMarks。pos は場所の座標 (z 上)。text は鍵のゲームの訳。旧方式の scene.json の markers と URL の labels に当たる。
//  roofDoors は屋根の扉の候補 (StationView3D.RoofDoors。旧方式がビューアの中で全部の部品から集めていたものを、今の方式ではアプリが区画の置き方の一覧の全部の区画から集める))。
// 出すのは外部視点のときだけ: 歩くモードでなく、回転の中心からの距離 dist が読み込んだ地点の半径 cur.r 以上 (nearCheck の「引いて全体を見ている」判定 !walk && dist >= r と同じ)。
// 中に戻ったら (dist < cur.r)・歩くモードにしたら消す。マイハンガーの中 (SITE) はステーションではないので出さない。
// 旧方式の「初めの位置」(goHome) は無いので、丸の大きさの基準 (homeRadius) は「今の向きで全部の印が画面の真ん中 HOME_FILL に入るいちばん近い距離」(旧方式の fitRadius と同じ求め方) にする。
// 旧方式の「ハンガーの印を屋根の扉の中心へ動かす」(roofDoors) は、旧方式と同じ所 (onExterior の buildMarks の直後。ハンガーの集まりは動かす前の位置、寄せる箱は動かした後の位置) で行う。
// 外部貨物エレベーター (freight) は {type:'exterior'} の freight:[{pos,text}] (text は旧方式と同じ「外部貨物EV」)。切り替えはキャプションのスイッチ chkExtFreight (旧方式の chkFreight)。
// extReady・extFit: 外から見る形 (下の「外から見る形」。2026-11-06) が使う。extReady = 印の知らせ ({type:'exterior'}) が届いたか。
// extFit = 回す中心をステーションの中心 (寄せる箱の中心。印の知らせがまだなら station.json の center、それも無ければ今の地点の中心) にし、今の向きのまま、
// 全部の印とステーションの中心が画面の真ん中 HOME_FILL に入るいちばん近い距離 (homeRadius。入らなければその探し方の上限) まで引く。
// 外部視点になる距離 (読み込んだ地点の半径 cur.r) より近くにはしない
let extTick = () => { }, onExterior = () => { }, requestExterior = () => { }, extReady = () => false, extFit = () => { };
{
  const svg = document.getElementById('extLines');
  const capsEl = document.getElementById('extCaps');
  const size = () => ({ w: Math.max(1, el.clientWidth), h: Math.max(1, el.clientHeight) });
  const KINDS = {
    pad: { color: '#ffcc33', chk: 'chkExtPad', count: 'nExtPad' },
    freight: { color: '#6fdc8c', chk: 'chkExtFreight', count: 'nExtFreight' },
    hangar: { color: '#6cb6ff', chk: 'chkExtHangar', count: 'nExtHangar' },
    dock: { color: '#ff8ad8', chk: 'chkExtDock', count: 'nExtDock' },
  };
  let marks = [];   // { kind, pos (場所の座標の Vector3), label ("" なら見出しなし) }
  let center = new THREE.Vector3();   // ステーションの中心 (見出しを左右上下に振り分ける基準。寄せる箱 (station.json の center と印の位置を囲む箱) の中心)
  let stationRadius = 1000;
  let dirty = true;   // 見出しを置き直すか (カメラ・大きさ・表示する印が変わったとき)
  const SIZE_WORD = { S: 'S', M: 'M', L: 'L', XL: 'XL' };
  const toV = p => new THREE.Vector3(p[0], p[1], p[2]);
  // 印 (idx) を、間が join (m) 以内でつながるもの (つながりをたどる。same が true の組どうしだけ) の集まりに分け、cluster に集まりの番号を入れる
  function joinClusters(list, idx, join, same) {
    let nextCluster = 0;
    for (const i of idx) {
      if (list[i].cluster >= 0) continue;
      const id = nextCluster++;
      const stack = [i];
      list[i].cluster = id;
      while (stack.length > 0) {
        const a = list[stack.pop()];
        for (const j of idx)
          if (list[j].cluster < 0 && same(a, list[j]) && list[j].pos.distanceTo(a.pos) <= join) { list[j].cluster = id; stack.push(j); }
      }
    }
  }
  function buildMarks(m) {
    const list = [];
    // (2026-11-02、利用者の指示「着陸できるなら、エレベーターで底に戻るためにその番号が大事。Pad01 などのように短くする方がいい」「padであることと、その数値が重要」)
    // パッドの見出しは「Pad01」の形 (番号は鍵 @ui_interactor_pad_NN・@landing_pad_NN の NN)。番号の取れない鍵はゲームの訳のまま。
    for (const p of m?.pads ?? []) {
      const num = /pad_(\d+)$/i.exec(p.key ?? '')?.[1];
      const label = num ? `Pad${num}` : p.key ? (p.text || p.key.replace(/^@/, '')) : '';
      list.push({ kind: 'pad', pos: toV(p.pos), label, padNo: num ?? null });
    }
    // (2026-11-02、利用者の指示「ハンガーの入り口がどこにあるかが重要で、XL L M S などの大きさが必要」「番号は必要ない」)
    // ハンガーの見出しは「ハンガー L」の形 (大きさは size: S・M・L・XL)。近い同じ大きさのハンガーをまとめても「×N」は付けない (線はまとめた全部の印に引く)
    for (const h of m?.hangars ?? [])
      list.push({ kind: 'hangar', pos: toV(h.pos), label: SIZE_WORD[h.size] ? `ハンガー ${h.size}` : 'ハンガー', hangarSize: SIZE_WORD[h.size] ? h.size : null, cluster: -1 });
    // (2026-11-02、利用者の指示「ハンガーも大きさでまとめて近いものは一緒でいい」) 同じ大きさのハンガーで、間が HANGAR_JOIN_M 以内でつながるものを初めから 1 つの見出しにする
    joinClusters(list, list.map((x, i) => i).filter(i => list[i].kind === 'hangar'), HANGAR_JOIN_M, (a, b) => a.label === b.label);
    const freightIdx = [];
    for (const f of m?.freight ?? []) {
      freightIdx.push(list.length);
      list.push({ kind: 'freight', pos: toV(f.pos), label: f.text || 'Freight Elevator', cluster: -1 });
    }
    joinClusters(list, freightIdx, FREIGHT_JOIN_M, () => true);
    // (2026-11-02、利用者の指示「外部のドッキングポートは番号が大事。指定されるが、ハンガーエレベーターで移動して戻るとき、ドッキングポート01 のように番号で表示されるため」
    //  「保安ドックはまとめてでいい」「PAD 外部ポートは番号が大事。まとめ禁止」)
    // 外部のドッキングポートの見出しは「ドッキングポート01」の形 (番号は鍵 @dockingport_NN・@ui_interactor_dockingport_NN の NN)。1 つずつ出し、まとめない。
    // 保安ドックのポート (鍵 @ui_interactor_secdock_a_dockingport など。ゲームの訳「セカンダリ・ポート-A」) は、末尾の「-A」などを除いた 1 つの見出しにまとめる
    for (const d of m?.docks ?? []) {
      const num = /dockingport_(\d+)$/i.exec(d.key ?? '')?.[1];
      const sec = /secdock_[a-z]+_dockingport$/i.test(d.key ?? '');
      const text = d.key ? (d.text || d.key.replace(/^@/, '')) : '';
      const label = num ? `ドッキングポート${num}` : sec ? text.replace(/[-－‐]\s*[A-Za-z]$/, '') : text;
      list.push({ kind: 'dock', pos: toV(d.pos), label, dockNo: num ?? null, secdock: sec, cluster: sec ? 'sec' : -1 });
    }
    for (const [k, d] of Object.entries(KINDS)) {
      if (!d.chk) continue;
      const n = list.filter(x => x.kind === k).length;
      document.getElementById(d.count).textContent = `(${n})`;
      // 数が 0 の種類は押せない (dataset.zero)。外部視点でないときも押せない (refreshSwitches)
      document.getElementById(d.chk).dataset.zero = n === 0 ? '1' : '';
    }
    refreshSwitches();
    return list;
  }
  // キャプションのスイッチ (右上の #swPanel。着陸パッド・ハンガー・ドッキングポート) は、切り替えたら localStorage (view3d.ext = {pad, hangar, dock}) に覚え、次に開いたときもその状態で始める
  // (2026-11-05、利用者の指示「切り替えたら覚えておいて以降はそれに従うようにしてほしい」「キャプション(引き出し線を含む) も同様に縦に並べてほしい」)
  try { const s = JSON.parse(localStorage.getItem('view3d.ext') || '{}'); for (const [k, d] of Object.entries(KINDS)) if (d.chk && typeof s[k] === 'boolean') document.getElementById(d.chk).checked = s[k]; } catch (_) { }
  const saveExt = () => { const on = {}; for (const [k, d] of Object.entries(KINDS)) if (d.chk) on[k] = document.getElementById(d.chk).checked; try { localStorage.setItem('view3d.ext', JSON.stringify(on)); } catch (_) { } };
  for (const d of Object.values(KINDS)) if (d.chk) document.getElementById(d.chk).addEventListener('change', () => { dirty = true; saveExt(); });
  const kindOn = k => { const d = KINDS[k]; return !d.chk || document.getElementById(d.chk).checked; };

  const MERGE_PX = 28;     // 同じ見出しの印をまとめる画面上の距離
  const FREIGHT_JOIN_M = 800;
  // 同じ大きさのハンガーを初めから 1 つの見出しにする間 (m)。旧方式で写しで数えて決めた値 (Checkmate・HUR-L1・GrimHEX は大きさごとに 1 つになり、JP の離れたハンガーは別に出る)
  const HANGAR_JOIN_M = 600;
  // 屋根の扉の見分け (m): 写しの扉の部品は、屋根の扉 XL が 202 × 320 × 4 m の板 (GrimHEX)、正面の扉は高さ 42〜66 m・厚さ 4 m の縦の板
  // (旧方式の ROOF_DOOR_THIN = 10, ROOF_DOOR_WIDE = 30, ROOF_DOOR_JOIN = 2。板の見分け (THIN・WIDE) はアプリ (StationView3D.RoofDoors) が行うので、ここは結び付ける距離だけ)
  const ROOF_DOOR_JOIN = 2;
  const MARGIN = 8;       // 画面の端からの余白
  const LABEL_OFFSET = 14; // 印から見出しまでの初めの間
  const LABEL_GAP = 6;     // 見出しの枠をほかの見出しの枠・結ぶ線から離す距離 (利用者の目安 4〜8 px / 6 px)
  // 引き出し線の太さ (px)。利用者の指示「引き出し線がわかりにくい。太くしたら合格」「引き出し線は全てで同じく太く」で、どの種類も同じ 3
  const LINE_W = 3;
  const LABEL_STEP = 4;    // 重なるとき外へ逃がす 1 段
  const LABEL_STEPS = 75;  // 逃がす最大の段 (300 px)
  // 印の点の半径 (px)。利用者の指示「色関係なく丸を小さい方に統一しろ。拡大したときにそれなりに大きくすればいい」: どの印も 2.5 × DOT_SCALE = 6.25 にそろえ、
  // 基準の距離 (homeRadius) より近づいたときは、近づいた割合だけ大きくする (DOT_ZOOM_MAX 倍まで)
  const DOT_SCALE = 2.5;
  const DOT_ZOOM_MAX = 2;
  let dotZoom = 1;
  const dotRadius = m => 2.5 * DOT_SCALE * dotZoom;
  const DOT_R = 4;         // 見出しで隠さない印の点の半径 (見出しの置き方で見る。点の半径に、この値との大きいほうを使う)
  const HOME_FILL = 0.75;  // 基準の距離: 印が画面の真ん中 HOME_FILL の範囲に入るいちばん近い距離 (旧方式の初めの位置の fitRadius)
  const capPool = [];
  const widthCache = new Map();
  function capEl(i) {
    while (capPool.length <= i) { const d = document.createElement('div'); d.className = 'xcap'; capsEl.appendChild(d); capPool.push(d); }
    return capPool[i];
  }
  const escapeHtml = t => t.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  // パッドの見出しの中身: まとめたパッドの見出しを、番号の順に「・」でつなぐ (同じ見出しが k 個なら「 ×k」)
  function padPieces(g) {
    const cnt = new Map();
    for (const p of g.pts) cnt.set(p.m.label, (cnt.get(p.m.label) ?? 0) + 1);
    const no = new Map(g.pts.map(p => [p.m.label, p.m.padNo]));
    return [...cnt.keys()].sort((a, b) => (no.get(a) ?? '~' + a).localeCompare(no.get(b) ?? '~' + b)).map(l => ({ label: l, no: no.get(l), k: cnt.get(l) }));
  }
  function capHtml(g) {
    const more = g.pts.length > 1 && g.kind !== 'hangar' ? ` ×${g.pts.length}` : '';
    const size = g.pts[0].m.hangarSize;
    if (g.kind === 'pad')
      return padPieces(g).map(x => (x.no ? `<span class="padw">Pad</span><span class="padn">${escapeHtml(x.no)}</span>` : escapeHtml(x.label)) + (x.k > 1 ? ` ×${x.k}` : '')).join('・');
    if (size) return `<span class="hw">ハンガー</span><span class="hs">${escapeHtml(size)}</span>`;
    if (g.kind === 'dock' && g.pts[0].m.dockNo) return `<span class="dkw">ドッキングポート</span><span class="dkn">${escapeHtml(g.pts[0].m.dockNo)}</span>`;
    return escapeHtml(g.label + more);
  }
  function measure(text, html) {
    let w = widthCache.get(text);
    if (w === undefined) {
      const d = capEl(0);
      d.style.display = ''; d.style.left = '-9999px'; d.innerHTML = html;
      w = d.offsetWidth;
      widthCache.set(text, w);
    }
    return w;
  }
  // 見出しの枠の高さ (px): 行の高さ 32 + 上下の余白 1 × 2 + 枠の線 1 × 2
  const CAP_H = 36;
  // 外へ (o) と左右 (上下。l) を合わせて逃がす候補 (近い順。どの向きでも空かないときに使う)
  const SPREAD = [];
  for (let o = 0; o <= 240; o += 12) for (let l = -360; l <= 360; l += 12) SPREAD.push({ o, l, d: Math.hypot(o, l) });
  SPREAD.sort((a, b) => a.d - b.d);
  // 前に置いた見出しの所 (鍵 → 辺・印からのずれ)。回転・拡大縮小で置き直すとき、空いていれば先に使う (ちらつかないように)
  const prevPlace = new Map();

  // ===== 見出しの置き方 (旧方式のまま) =====
  // 必ず守る条件 (利用者の指摘「とにかく引き出し線がクロスしない、隠れないことが大事」): (1) 引き出し線どうしが交差しない、(2) 引き出し線が自分以外の見出しの枠を通らない、
  // (3) 見出しどうしが重ならない (隙間 LABEL_GAP 以上)。ほかの印の点も見出しで隠さない。
  // 置けない見出しは、ハンガーなら同じ大きさでまとめる距離を HANGAR_MERGE_PX の順に広げ、パッド・ドッキングポートはまとめない。それでも置けない見出しは画面の中を全部探し (GRID_STEP)、
  // 置けなかった見出しを先に置く置き直し (PLACE_RETRIES)、近くの見出しを動かす置き換え (REPAIR) をし、最後まで置けない見出しは出さない (点は出す)
  const HANGAR_MERGE_PX = [MERGE_PX, 80, 160, 320, Infinity];
  const GRID_STEP = 16, PLACE_RETRIES = 4, RETRY_BUDGET_MS = 400, REPAIR_NEAR = 6, REPAIR_BUDGET_MS = 600;

  function segCross(a, b) {
    const d = (p0, p1, q0, q1, r0, r1) => (q0 - p0) * (r1 - p1) - (q1 - p1) * (r0 - p0);
    const d1 = d(b[0], b[1], b[2], b[3], a[0], a[1]), d2 = d(b[0], b[1], b[2], b[3], a[2], a[3]);
    const d3 = d(a[0], a[1], a[2], a[3], b[0], b[1]), d4 = d(a[0], a[1], a[2], a[3], b[2], b[3]);
    return d1 * d2 < 0 && d3 * d4 < 0;
  }
  function segBox(s, r) {
    const [x1, y1, x2, y2] = s;
    if (Math.max(x1, x2) < r.x0 || Math.min(x1, x2) > r.x1 || Math.max(y1, y2) < r.y0 || Math.min(y1, y2) > r.y1) return false;
    const inside = (x, y) => x > r.x0 && x < r.x1 && y > r.y0 && y < r.y1;
    if (inside(x1, y1) || inside(x2, y2)) return true;
    return segCross(s, [r.x0, r.y0, r.x1, r.y0]) || segCross(s, [r.x1, r.y0, r.x1, r.y1]) || segCross(s, [r.x1, r.y1, r.x0, r.y1]) || segCross(s, [r.x0, r.y1, r.x0, r.y0]);
  }
  const grow = (r, k) => ({ x0: r.x0 - k, y0: r.y0 - k, x1: r.x1 + k, y1: r.y1 + k });
  const boxHit = (a, b) => a.x0 < b.x1 && a.x1 > b.x0 && a.y0 < b.y1 && a.y1 > b.y0;
  const nearOnBox = (r, x, y) => [THREE.MathUtils.clamp(x, r.x0, r.x1), THREE.MathUtils.clamp(y, r.y0, r.y1)];
  function shorten(s, k) {
    const dx = s[2] - s[0], dy = s[3] - s[1], l = Math.hypot(dx, dy);
    if (l <= k) return null;
    return [s[0], s[1], s[2] - dx / l * k, s[3] - dy / l * k];
  }
  // 見出しの線 (印から見出しへ)。mode 'direct' は印それぞれから見出しの枠のいちばん近い点へ、'trunk' は見出しから集まりの真ん中へ 1 本と、真ん中から印へ枝
  function labelSegs(g, box, mode) {
    if (mode === 'direct' || g.pts.length === 1) return g.pts.map(p => { const [lx, ly] = nearOnBox(box, p.x, p.y); return [p.x, p.y, lx, ly]; });
    const [lx, ly] = nearOnBox(box, g.ax, g.ay);
    return [[g.ax, g.ay, lx, ly, 'trunk'], ...g.pts.map(p => [p.x, p.y, g.ax, g.ay, 'branch'])];
  }
  // 見出しをまとめる (pad: まとめない、freight: 場所の集まり (cluster) ごと、hangar: 同じ大きさで HANGAR_JOIN_M 以内の集まり (split にある集まりは除く) と、画面上 hangarPx 以内のつながり)
  function makeGroups(shown, hangarPx, split) {
    const groups = [];
    for (const s of shown) {
      if (!s.m.label) continue;
      let g;
      if (s.m.kind === 'freight') g = groups.find(g => g.kind === 'freight' && g.cluster === s.m.cluster);
      else {
        const px = s.m.kind === 'hangar' ? hangarPx : MERGE_PX;
        // パッドはまとめない (利用者の指摘「PAD を纏めていいと誰が言った？ PAD は番号の位置が大事だと言ったよな？」)
        if (s.m.kind === 'pad') g = null;
        // 外部のドッキングポートもまとめない。保安ドックのポートだけ 1 つにまとめる (利用者の指示「保安ドックはまとめてでいい」)
        else if (s.m.kind === 'dock') g = s.m.secdock ? groups.find(g => g.kind === 'dock' && g.cluster === 'sec') : null;
        else g = groups.find(g => g.label === s.m.label && g.kind === s.m.kind && (s.m.kind === 'hangar' && !split.has(s.m.cluster) && g.pts.some(p => p.m.cluster === s.m.cluster) || g.pts.some(p => Math.hypot(p.x - s.x, p.y - s.y) <= px)));
      }
      if (g) g.pts.push(s); else groups.push({ label: s.m.label, kind: s.m.kind, cluster: s.m.cluster, pts: [s] });
    }
    for (let changed = true; changed;) {
      changed = false;
      for (let i = 0; i < groups.length && !changed; i++)
        for (let j = i + 1; j < groups.length && !changed; j++) {
          const a = groups[i], b = groups[j];
          const lim = a.kind === 'hangar' && b.kind === 'hangar' && a.label === b.label ? hangarPx : -1;
          if (lim < 0) continue;
          if (a.pts.some(p => b.pts.some(q => Math.hypot(p.x - q.x, p.y - q.y) <= lim))) { a.pts.push(...b.pts); groups.splice(j, 1); changed = true; }
        }
    }
    for (const g of groups) {
      g.ax = g.pts.reduce((a, p) => a + p.x, 0) / g.pts.length;
      g.ay = g.pts.reduce((a, p) => a + p.y, 0) / g.pts.length;
      g.text = g.kind === 'pad' ? padPieces(g).map(x => x.label + (x.k > 1 ? ` ×${x.k}` : '')).join('・') : g.pts.length > 1 && g.kind !== 'hangar' ? `${g.label} ×${g.pts.length}` : g.label;
      g.html = capHtml(g);
      g.w = measure(g.text, g.html);
    }
    return groups;
  }
  // 置く。戻り値は置けなかった見出し (g.hidden)。置いた見出しには g.box・g.segs・g.x・g.y が入る。
  function placeGroups(groups, shown, ctx, opt = {}) {
    const { W, cx, cy, TOP, BOTTOM, avoid } = ctx;
    const boxes = [];
    const segs = [];
    const dots = shown.map(s => { const r = Math.max(DOT_R, dotRadius(s.m) + 0.5); return { s, r: { x0: s.x - r, y0: s.y - r, x1: s.x + r, y1: s.y + r } }; });
    const clampX = (x, w) => THREE.MathUtils.clamp(x, MARGIN, W - MARGIN - w);
    const clampY = y => THREE.MathUtils.clamp(y, TOP, Math.max(TOP, BOTTOM - CAP_H));
    const strict = !!opt.strict;
    const tryAt = (g, x, y, own) => {
      const box = { x0: x, y0: y, x1: x + g.w, y1: y + CAP_H };
      const wide = grow(box, LABEL_GAP);
      // 右上のスイッチの欄 (#swPanel) の下には見出しを置かない (置ける範囲から欄の分を除く。線と点はこれまでどおり)
      if (avoid && boxHit(wide, avoid)) return null;
      for (const b of boxes) if (boxHit(wide, b)) return null;
      for (const d of dots) if (boxHit(box, d.r)) return null;
      for (const s of segs) if (segBox(s, wide)) return null;
      for (const mode of g.pts.length > 1 ? ['direct', 'trunk'] : ['direct']) {
        const mine = labelSegs(g, box, mode);
        let bad = false;
        for (const s of mine) {
          if (boxes.some(b => segBox(s, b))) { bad = true; break; }
          if (s[4] !== 'branch') { const sh = shorten(s, 1.5); if (sh && segBox(sh, box)) { bad = true; break; } }
          if (s[4] === 'branch' && segBox(s, box)) { bad = true; break; }
          if (segs.some(t => segCross(s, t))) { bad = true; break; }
          if (strict && dots.some(d => !own.has(d.s) && segBox(s, d.r))) { bad = true; break; }
        }
        if (!bad) for (let i = 0; i < mine.length && !bad; i++) for (let j = i + 1; j < mine.length; j++) if (segCross(mine[i], mine[j])) { bad = true; break; }
        if (!bad) return { box, mine };
      }
      return null;
    };
    const KIND_ORDER = { pad: 0, dock: 0, freight: 1, hangar: 2 };
    const base = groups.slice().sort((a, b) => (KIND_ORDER[a.kind] - KIND_ORDER[b.kind]) || (Math.hypot(a.ax - cx, a.ay - cy) - Math.hypot(b.ax - cx, b.ay - cy)));
    const seen = new Map();
    for (const g of base) {
      const n = (seen.get(g.kind + g.text) ?? 0) + 1;
      seen.set(g.kind + g.text, n);
      g.key = `${g.kind}|${g.text}|${n}`;
    }
    const first = opt.first ?? new Set();
    const order = [...base.filter(g => first.has(g.key)), ...base.filter(g => !first.has(g.key))];
    const findSpot = (g, wide) => {
      const dx = g.ax - cx, dy = g.ay - cy;
      const horiz = Math.abs(dx / W) >= Math.abs(dy / ctx.H);
      g.side = horiz ? (dx < 0 ? 'left' : 'right') : (dy < 0 ? 'top' : 'bottom');
      const len = Math.hypot(dx, dy);
      const sx = g.side === 'right' ? 1 : g.side === 'left' ? -1 : 0, sy = g.side === 'bottom' ? 1 : g.side === 'top' ? -1 : 0;
      const ux = len > 1 ? dx / len : sx, uy = len > 1 ? dy / len : sy;
      const bx = g.side === 'right' ? g.ax + LABEL_OFFSET : g.side === 'left' ? g.ax - LABEL_OFFSET - g.w : g.ax - g.w / 2;
      const by = g.side === 'bottom' ? g.ay + LABEL_OFFSET : g.side === 'top' ? g.ay - LABEL_OFFSET - CAP_H : g.ay - CAP_H / 2;
      const own = new Set(g.pts);
      const at = (ex, ey, d) => ({ x: clampX(bx + ex * d, g.w), y: clampY(by + ey * d) });
      const vertical = !horiz;
      const radial = [[ux, uy], [sx, sy]];
      const sideways = vertical ? [[1, 0], [-1, 0]] : [[0, 1], [0, -1]];
      const tiers = vertical ? [sideways, radial] : [radial, sideways];
      let got = null;
      const prev = prevPlace.get(g.key);
      if (prev && prev.side === g.side) {
        const x = clampX(g.ax + prev.ox, g.w), y = clampY(g.ay + prev.oy);
        if (Math.hypot(x - bx, y - by) <= LABEL_STEP * LABEL_STEPS) { const r = tryAt(g, x, y, own); if (r) got = { x, y, ...r }; }
      }
      if (!got) {
        for (const tier of tiers) {
          const found = [];
          for (const [ex, ey] of tier)
            for (let k = 0; k <= LABEL_STEPS; k++) {
              const p = at(ex, ey, k * LABEL_STEP);
              const r = tryAt(g, p.x, p.y, own);
              if (r) { found.push({ ...p, ...r }); break; }
            }
          if (found.length > 0) { got = found.reduce((a, b) => Math.hypot(a.x - bx, a.y - by) <= Math.hypot(b.x - bx, b.y - by) ? a : b); break; }
        }
      }
      if (!got)
        for (const c of SPREAD) {
          const p = { x: clampX(bx + sx * c.o + (vertical ? c.l : 0), g.w), y: clampY(by + sy * c.o + (vertical ? 0 : c.l)) };
          const r = tryAt(g, p.x, p.y, own);
          if (r) { got = { ...p, ...r }; break; }
        }
      if (!got && wide) {
        const cand = [];
        const x1 = W - MARGIN - g.w, y1 = Math.max(TOP, BOTTOM - CAP_H);
        for (let y = TOP; y <= y1; y += GRID_STEP) for (let x = MARGIN; x <= x1; x += GRID_STEP) cand.push({ x, y, d: Math.hypot(x - bx, y - by) });
        cand.sort((a, b) => a.d - b.d);
        for (const p of cand) { const r = tryAt(g, p.x, p.y, own); if (r) { got = { x: p.x, y: p.y, ...r }; break; } }
      }
      return got;
    };
    const commit = (g, got) => {
      g.hidden = false;
      g.x = got.x; g.y = got.y; g.box = got.box; g.segs = got.mine;
      got.box.owner = g; boxes.push(got.box);
      for (const s of got.mine) { s.owner = g; segs.push(s); }
    };
    const unplace = gs => {
      const set = new Set(gs);
      for (let i = boxes.length - 1; i >= 0; i--) if (set.has(boxes[i].owner)) boxes.splice(i, 1);
      for (let i = segs.length - 1; i >= 0; i--) if (set.has(segs[i].owner)) segs.splice(i, 1);
      for (const g of gs) g.hidden = true;
    };
    const failed = [];
    for (const g of order) {
      const got = findSpot(g, opt.wide);
      if (!got) { g.hidden = true; failed.push(g); continue; }
      commit(g, got);
    }
    // 置き換え: 置けなかった見出しごとに、近くに置いた見出しを REPAIR_NEAR 個まで見て、その 1 つ・2 つをいったん外し、置けなかった見出しを先に置いてから、外した見出しを置き直す
    if (opt.repair && failed.length > 0) {
      const t0 = performance.now();
      for (let pass = 0, again = true; again && failed.length > 0; pass++) {
        again = false;
        for (const h of failed.slice()) {
          if (performance.now() - t0 > REPAIR_BUDGET_MS) break;
          const near = order.filter(g => !g.hidden)
            .map(g => ({ g, d: Math.hypot((g.box.x0 + g.box.x1) / 2 - h.ax, (g.box.y0 + g.box.y1) / 2 - h.ay) }))
            .sort((a, b) => a.d - b.d).slice(0, REPAIR_NEAR).map(x => x.g);
          const sets = near.map(g => [g]);
          for (let a = 0; a < near.length; a++) for (let b = a + 1; b < near.length; b++) sets.push([near[a], near[b]], [near[b], near[a]]);
          let done = false;
          for (const rm of sets) {
            if (performance.now() - t0 > REPAIR_BUDGET_MS) break;
            const saved = rm.map(g => ({ g, got: { x: g.x, y: g.y, box: g.box, mine: g.segs } }));
            unplace(rm);
            const gh = findSpot(h, false);
            if (gh) {
              commit(h, gh);
              const back = [];
              for (const g of rm) { const r = findSpot(g, true); if (!r) break; commit(g, r); back.push(g); }
              if (back.length === rm.length) { done = true; break; }
              unplace([h, ...back]);
            }
            for (const { g, got } of saved) commit(g, got);
          }
          if (done) { failed.splice(failed.indexOf(h), 1); again = true; }
        }
      }
    }
    for (const g of order) if (!g.hidden) prevPlace.set(g.key, { side: g.side, ox: g.x - g.ax, oy: g.y - g.ay });
    return failed;
  }
  // 守る条件の違反を数える (確かめ用。document.body.dataset.extViolations に入れる)
  function countViolations(groups, shown, dotOrder) {
    const shownG = groups.filter(g => !g.hidden);
    let cross = 0, hidden = 0, overlap = 0;
    const all = [];
    shownG.forEach((g, gi) => g.segs.forEach(s => all.push({ s, gi })));
    for (let i = 0; i < all.length; i++)
      for (let j = i + 1; j < all.length; j++) if (segCross(all[i].s, all[j].s)) cross++;
    for (const { s, gi } of all)
      shownG.forEach((g, k) => {
        if (k !== gi) { if (segBox(s, g.box)) hidden++; }
        else if (s[4] === 'branch') { if (segBox(s, g.box)) hidden++; }
        else { const sh = shorten(s, 1.5); if (sh && segBox(sh, g.box)) hidden++; }
      });
    for (let i = 0; i < shownG.length; i++)
      for (let j = i + 1; j < shownG.length; j++) if (boxHit(grow(shownG[i].box, LABEL_GAP - 0.01), shownG[j].box)) overlap++;
    let dotHidden = 0;
    for (const s of shown) {
      const r = dotRadius(s.m);
      const d = { x0: s.x - r, y0: s.y - r, x1: s.x + r, y1: s.y + r };
      for (const g of shownG) if (boxHit(g.box, d)) dotHidden++;
    }
    let lineOverDot = 0;
    shownG.forEach(g => {
      const own = new Set(g.pts);
      for (const sg of g.segs) for (const s of shown) {
        if (own.has(s)) continue;
        const r = dotRadius(s.m);
        if (segBox(sg, { x0: s.x - r, y0: s.y - r, x1: s.x + r, y1: s.y + r })) lineOverDot++;
      }
    });
    let dotUnderDot = 0;
    for (let i = 0; i < dotOrder.length; i++) {
      const a = dotOrder[i], ra = dotRadius(a.m);
      for (let j = i + 1; j < dotOrder.length; j++) {
        const b = dotOrder[j], rb = dotRadius(b.m);
        if (Math.hypot(a.x - b.x, a.y - b.y) + ra <= rb) { dotUnderDot++; break; }
      }
    }
    return { cross, hidden, overlap, dotHidden, lineOverDot, dotUnderDot, notShown: groups.length - shownG.length };
  }
  // 基準の距離 (旧方式の初めの位置の fitRadius): 今の向きで、全部の印とステーションの中心が画面の真ん中 HOME_FILL の範囲に入るいちばん近い距離。入らなければ 0
  function homeRadius(pts) {
    const c2 = cam.clone();
    const dir = new THREE.Vector3().subVectors(cam.position, tgt);
    if (dir.lengthSq() < 1e-12) return 0;
    dir.normalize();
    const v = new THREE.Vector3();
    const fits = r => {
      c2.position.copy(tgt).addScaledVector(dir, r); c2.lookAt(tgt); c2.updateMatrixWorld(true);
      return pts.every(c => { v.copy(c).project(c2); return v.z < 1 && Math.abs(v.x) <= HOME_FILL && Math.abs(v.y) <= HOME_FILL; });
    };
    let lo = 1, hi = Math.max(stationRadius * 8, 100);
    if (!fits(hi)) return 0;
    for (let i = 0; i < 40; i++) { const mid = (lo + hi) / 2; if (fits(mid)) hi = mid; else lo = mid; }
    return hi;
  }
  // full: 全部の見出しを出すための仕上げの置き直し (GRID_STEP・PLACE_RETRIES) までするか。重いので、カメラが動いている間は false (止まって SETTLE_MS たったら true でもう 1 回)
  function layout(full = true) {
    const tLayout = performance.now();
    const { w: W, h: H } = size();
    svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
    const TOP = MARGIN, BOTTOM = H - MARGIN;
    const cc = toV(cur.center);
    // 画面上の位置 (カメラの後ろ・画面の外の印は出さない)。印の位置は場所の座標なので、地点の中心を引いて描く座標にする
    const shown = [];
    const v = new THREE.Vector3();
    const scenePts = [];
    for (const m of marks) scenePts.push(m.pos.clone().sub(cc));
    scenePts.push(center.clone().sub(cc));
    const home = homeRadius(scenePts);
    dotZoom = home > 0 ? THREE.MathUtils.clamp(home / dist, 1, DOT_ZOOM_MAX) : 1;
    marks.forEach((m, i) => {
      if (!kindOn(m.kind)) return;
      v.copy(scenePts[i]).project(cam);
      if (v.z > 1 || v.z < -1) return;
      const x = (v.x + 1) / 2 * W, y = (1 - v.y) / 2 * H;
      if (x < 0 || x > W || y < 0 || y > H) return;
      shown.push({ m, x, y });
    });
    v.copy(center).sub(cc).project(cam);
    const swp = document.getElementById('swPanel');
    const avoid = swp && swp.offsetWidth > 0 ? { x0: swp.offsetLeft, y0: swp.offsetTop, x1: swp.offsetLeft + swp.offsetWidth, y1: swp.offsetTop + swp.offsetHeight } : null;
    const ctx = { W, H, TOP, BOTTOM, avoid, cx: THREE.MathUtils.clamp((v.x + 1) / 2 * W, 0, W), cy: THREE.MathUtils.clamp((1 - v.y) / 2 * H, 0, H) };
    const split = new Set();
    let hangI = 0, retries = 0, groups = [];
    let lastOpt = {};
    const place = (opt = {}) => { lastOpt = opt; groups = makeGroups(shown, HANGAR_MERGE_PX[hangI], split); return placeGroups(groups, shown, ctx, opt); };
    for (;;) {
      const failed = place().filter(g => g.kind === 'hangar');
      const more = failed.flatMap(g => g.pts.map(p => p.m.cluster)).filter(c => !split.has(c));
      if (more.length === 0) break;
      for (const c of more) split.add(c);
    }
    const widen = f => {
      let changed = false;
      if (f.some(g => g.kind === 'hangar') && hangI < HANGAR_MERGE_PX.length - 1) { hangI++; changed = true; }
      return changed;
    };
    for (let f = place(); f.length > 0 && widen(f);) f = place();
    if (full) {
      const tFull = performance.now();
      for (;;) {
        const first = new Set();
        let best = null, f = [];
        for (let t = 0; t <= PLACE_RETRIES; t++) {
          if (t > 0 && performance.now() - tFull > RETRY_BUDGET_MS) break;
          f = place({ first, wide: true });
          retries = t;
          if (!best || f.length < best.f.length) best = { f, first: new Set(first) };
          if (f.length === 0) break;
          const before = first.size;
          for (const g of f) first.add(g.key);
          if (first.size === before) break;
        }
        if (best.f.length === 0) { if (f.length > 0) place({ first: best.first, wide: true }); break; }
        if (!widen(best.f)) { place({ first: best.first, wide: true }); break; }
      }
    }
    if (full) {
      const base = lastOpt;
      place({ ...base, repair: true });
      const keepGroups = groups, nHid = groups.filter(g => g.hidden).length;
      const keepPrev = new Map(prevPlace);
      place({ ...base, strict: true, repair: true });
      if (groups.filter(g => g.hidden).length > nHid) { groups = keepGroups; prevPlace.clear(); for (const [k, v] of keepPrev) prevPlace.set(k, v); }
    }
    const usedPx = HANGAR_MERGE_PX[hangI];
    for (const k of [...prevPlace.keys()]) if (!groups.some(g => g.key === k && !g.hidden)) prevPlace.delete(k);
    // 描く: 点 → 線 → 見出し (出さない見出しは点だけ)。線は点の上に描く
    let html = '';
    const dotR = s => dotRadius(s.m);
    const dotOrder = shown.slice();
    for (const s of dotOrder) html += `<circle cx="${s.x.toFixed(1)}" cy="${s.y.toFixed(1)}" r="${dotR(s)}" fill="${KINDS[s.m.kind].color}" stroke="#000" stroke-width="0.8"/>`;
    for (const g of groups) {
      if (g.hidden) continue;
      const col = KINDS[g.kind].color;
      for (const s of g.segs)
        html += `<line x1="${s[0].toFixed(1)}" y1="${s[1].toFixed(1)}" x2="${s[2].toFixed(1)}" y2="${s[3].toFixed(1)}" stroke="${col}" stroke-width="${LINE_W}"/>`;
    }
    svg.innerHTML = html;
    let ci = 0;
    for (const g of groups) {
      if (g.hidden) continue;
      const d = capEl(ci++);
      d.style.display = '';
      d.innerHTML = g.html;
      d.style.left = `${Math.round(g.x)}px`;
      d.style.top = `${Math.round(g.y)}px`;
      d.style.borderColor = KINDS[g.kind].color;
    }
    for (let i = ci; i < capPool.length; i++) capPool[i].style.display = 'none';
    const vio = countViolations(groups, shown, dotOrder);
    const ms = performance.now() - tLayout;
    lastLayoutMs = ms;
    document.body.dataset.extViolations = `交差 ${vio.cross}・線が枠に隠れる ${vio.hidden}・見出しの重なり ${vio.overlap}・点が隠れる ${vio.dotHidden}・線がほかの点の上を通る ${vio.lineOverDot}・点が点に隠れる ${vio.dotUnderDot}・出さない見出し ${vio.notShown}/${groups.length}・ハンガーをまとめた距離 ${usedPx === Infinity ? '全部' : usedPx + 'px'}・一緒にしなかったハンガーの集まり ${split.size}・置き直し ${retries} 回・${ms.toFixed(1)} ms`;
    document.body.dataset.extNotShown = groups.filter(g => g.hidden).map(g => g.text).join(', ');
  }
  function clear() {
    svg.innerHTML = '';
    for (const d of capPool) d.style.display = 'none';
  }
  // 置き直しの間引き (旧方式の frame と同じ): 動いている間は軽い置き方 (full=false)、止まって SETTLE_MS たったら仕上げの置き方 (full=true) をもう 1 回
  let lastLayoutAt = 0, lastLayoutMs = 0;
  const LAYOUT_SLOW_MS = 40, LAYOUT_THROTTLE_MS = 200, SETTLE_MS = 250;
  let lastDirtyAt = 0, needFull = false, wasOn = false, lastSig = '', lastMoveAt = 0;
  let got = false;   // 印の知らせ ({type:'exterior'}) が届いたか (extReady)
  requestExterior = () => { marks = []; got = false; clear(); if (APP) window.chrome.webview.postMessage({ type: 'exteriorGet' }); };
  extReady = () => got;
  extFit = () => {
    const cc = toV(cur.center);
    const ctr = got ? center : Array.isArray(VIEW?.center) && VIEW.center.length === 3 ? toV(VIEW.center) : cc;
    tgt.copy(ctr).sub(cc); upd();
    const pts = marks.map(m => m.pos.clone().sub(cc)); pts.push(tgt.clone());
    const home = homeRadius(pts);
    dist = Math.max(home > 0 ? home : Math.max(stationRadius * 8, 100), cur.r); upd();
  };
  onExterior = d => {
    got = true;
    marks = buildMarks(d);
    // (2026-11-02、利用者の指示「ハンガーの入り口がどこにあるかが重要」による親の決定。旧方式 viewer.html 1091〜1101 行の移植) 屋根に扉のあるハンガーの印は、扉の部品の真ん中へ動かす。
    // 結び付け方: ハンガーの印 (InstancedHangarGateway の Pos) と、扉の部品を置いた原点が ROOF_DOOR_JOIN 以内で同じ所にあり (写しの GrimHEX・Checkmate で、扉の部品の原点は
    // 印とぴったり同じだった)、その部品が水平の板 (屋根の扉) のときだけ。正面の扉 (縦の板) は、印が扉の下の真ん中 (入口) にあるので動かさない
    const roofDoors = (d?.roofDoors ?? []).map(r => ({ origin: toV(r.origin), center: toV(r.center) }));
    let movedDoors = 0;
    for (const mk of marks) {
      if (mk.kind !== 'hangar') continue;
      let best = null, bd = ROOF_DOOR_JOIN;
      for (const r of roofDoors) { const dd = r.origin.distanceTo(mk.pos); if (dd <= bd) { bd = dd; best = r; } }
      if (best) { mk.pos = best.center.clone(); movedDoors++; }
    }
    document.body.dataset.extRoofDoors = `屋根の扉へ動かしたハンガー ${movedDoors}`;   // 確かめ用 (旧方式は dataset.info に書いていた)
    // 寄せる箱: station.json の center (区画を置いた所の真ん中) と印の位置を囲む箱。その中心を振り分けの基準にし、半径を基準の距離の探し方の上限に使う
    const box = new THREE.Box3();
    if (Array.isArray(VIEW?.center) && VIEW.center.length === 3) box.expandByPoint(toV(VIEW.center));
    for (const m of marks) box.expandByPoint(m.pos);
    if (box.isEmpty()) box.set(new THREE.Vector3(-100, -100, -100), new THREE.Vector3(100, 100, 100));
    center = box.getCenter(new THREE.Vector3());
    stationRadius = Math.max(box.getBoundingSphere(new THREE.Sphere()).radius, 10);
    dirty = true;
  };
  extTick = now => {
    const on = extView && marks.length > 0;   // extView は描く前に updExtView が決めた外部視点 (内部の印を消すのと同じ値)
    if (!on) { if (wasOn) { clear(); wasOn = false; needFull = false; } return; }
    const { w, h } = size();
    const sig = [cam.position.x, cam.position.y, cam.position.z, cam.quaternion.x, cam.quaternion.y, cam.quaternion.z, cam.quaternion.w, w, h, cur.center.join()].map(x => typeof x === 'number' ? x.toFixed(4) : x).join(',');
    // lazy: 見ている位置の外判定だけで外部視点になっているとき (歩くモード・回転の中心からの距離が地点の半径未満。2026-11-06 に足した外部視点)。
    // カメラが動いている間は置き直さず (出ている見出しはそのまま)、止まって SETTLE_MS たったら仕上げの置き直しを 1 回だけする
    // (2026-11-06、利用者の報告「動きがかなりぎこちない。…前よりかなり悪い」。親の決定: 前は走っていなかった所なので、動いている間の置き直しを足さない)
    const lazy = !(!walk && dist >= cur.r);
    if (!wasOn || sig !== lastSig) { dirty = true; lastSig = sig; if (lazy) lastMoveAt = now; }
    wasOn = true;
    if (lazy) {
      if ((dirty || needFull) && now - lastMoveAt >= SETTLE_MS) { dirty = false; needFull = false; layout(true); lastLayoutAt = performance.now(); }
      return;
    }
    if (dirty && now - lastLayoutAt >= (lastLayoutMs > LAYOUT_SLOW_MS ? LAYOUT_THROTTLE_MS : 0)) { dirty = false; layout(false); lastLayoutAt = performance.now(); lastDirtyAt = now; needFull = true; }
    else if (!dirty && needFull && now - lastDirtyAt >= SETTLE_MS) { needFull = false; layout(true); lastLayoutAt = performance.now(); }
  };
}
// 確かめ用: いちばん長い 1 コマ (ms。window.__frameMax = 0 で計り直す) と、読み込みの段ごとの時間 (window.__phase)
// window.__frames: 長いコマの上位 8 件 [コマの間 ms, そのうち描画 (R.render) ms, その間の段 (window.__stage)]
let lastFrame = performance.now(), lastRender = 0; window.__frameMax = 0; window.__frames = []; window.__stage = '';
(function loop() { requestAnimationFrame(loop); { const nowF = performance.now(), gap = nowF - lastFrame; window.__frameMax = Math.max(window.__frameMax, gap); if (gap > 50) { const f = window.__frames; f.push([+gap.toFixed(0), +lastRender.toFixed(0), window.__stage]); f.sort((a, b) => b[0] - a[0]); f.length = Math.min(f.length, 8); } lastFrame = nowF; } try { nearCheck(); } catch (e) { window.__err = String(e && e.stack || e); } try { refreshDistLabels(); } catch (e) { window.__err = String(e && e.stack || e); } try { walkCheckTick(); } catch (e) { window.__err = String(e && e.stack || e); } try { keyMove(); } catch (e) { window.__err = String(e && e.stack || e); } try { chunkTick(); reviewTick(); } catch (e) { window.__err = String(e && e.stack || e); } try { updExtView(); } catch (e) { window.__err = String(e && e.stack || e); } const tr = performance.now(); R.render(scene, cam); lastRender = performance.now() - tr; try { extTick(performance.now()); } catch (e) { window.__err = String(e && e.stack || e); } window.__stage = loading ? window.__stage : ''; })();
// ===== 始め =====
(async () => {
  try {
    if (!BASE) { document.getElementById('msg').textContent = 'データの置き場が指定されていません'; return; }
    VIEW = await (await fetch(BASE + 'station.json')).json();
    if (VIEW.format !== 'scjt-view3d-1') throw new Error('形の違うデータ: ' + VIEW.format);
    if (pendingSizes) { VIEW.hangarSizesOk = pendingSizes; pendingSizes = null; }
    document.getElementById('title').textContent = qs.get('title') || VIEW.station;
    renderTargets();
    if (APP) window.chrome.webview.postMessage({ type: 'hangarSizesGet' });   // マイハンガーの大きさのうち区画が決まるもの (返事 {type:'hangarSizes'})
    if (APP) window.chrome.webview.postMessage({ type: 'hereMarkGet' });   // 今の現在位置がこの場所の中なら印を出す (返事 {type:'hereMark'})
    requestExterior();   // 外から見たときの印 (着陸パッド・ハンガー・ドッキングポート) をアプリに頼む (返事 {type:'exterior'} → onExterior)
    // 最初の地点: 店があればいちばん上の店、無ければ一覧の最初、何も無ければ最初のインスタンスの箱の中心
    // 最初の行き先: ?goto=<名前の一部> があればそれ。無ければハンガーロビーの艦隊マネージャーの端末の前 (2026-11-04、利用者の指示「初期位置は hanger lobby 船舶マネージャーターミナル前がいい」):
    //   艦隊マネージャーの端末 (種類 fleet) のうち、hangar_lobby の乗り口 (エレベーターの行き先に hangar) にいちばん近いもの。hangar_lobby が無ければ一覧のいちばん上の艦隊マネージャー。
    //   艦隊マネージャーの端末が無ければ、一覧のいちばん上の行き先
    const gt = qs.get('goto');
    // トラムのある場所 (この場所の乗り場にトラムがある。Orison・Lorville など) は、今までの始め方ですぐ始め、現在位置の知らせ (hereMark) がこの場所の中の点で届いたとき、
    // 利用者がまだ何も操作していなければ、現在位置のまわりを読み込んでそこを中心にする (onHereMark の hereStart。2026-11-05、利用者の指示「"Orison と Lorville" tram がある場所では現在いる区域だけ」・親の決定「待たない」)。
    // 行き先つきで開いたとき (?goto=) はそちらを先にする (切り替えない)
    hereStart = APP && !gt && (VIEW.transit || []).some(t => t[0] === 'tram') ? 'wait' : 'no';
    if (pendingHereMark) { const d = pendingHereMark; pendingHereMark = null; onHereMark(d); }   // 場所のデータが入る前に届いた現在位置の知らせ
    const btns = [...document.querySelectorAll('#tlist button')];
    let first = gt && (btns.find(b => b._t.name === gt) || btns.find(b => b.textContent.includes(gt)));
    if (!first) {
      const fleets = btns.filter(b => b._t.kind === 'terminal_fleet').map(b => b._t), lobbies = VIEW.targets.filter(t => t.kind === 'hangar_lobby');
      const dl = t => lobbies.length ? Math.min(...lobbies.map(l => Math.hypot(l.pos[0] - t.pos[0], l.pos[1] - t.pos[1], l.pos[2] - t.pos[2]))) : 0;
      const f = fleets.length ? fleets.reduce((a, b) => dl(b) < dl(a) ? b : a) : null;
      if (f) { startAt = f; first = btns.find(b => b._t === f); }
    }
    first = first || btns[0];
    if (first) first.click();
    // 行き先の無い場所 (端末・店・乗り場のデータが無い所): 区画を置いた所の真ん中のまわりを出す
    else if (VIEW.center) { document.getElementById('msg').textContent = ''; loadRegion(VIEW.center.slice(), regionR()); }
    else document.getElementById('msg').textContent = 'この場所には飛ぶ先がありません';
  } catch (e) { document.getElementById('msg').textContent = '読み込めませんでした: ' + e.message; report('start', e); }
})();
document.getElementById('hangarLeave').onclick = () => leaveHangar();
// ［現在位置］ (アプリが /showlocation で座標を取り、場所の座標に直して {type:'here', ok, pos, note} を返す)。着いたら歩くモードで、その位置の床に立つ
document.getElementById('hereBtn').onclick = () => {
  const msg = document.getElementById('msg');
  if (!APP) { msg.textContent = '［現在位置］はアプリの窓で使えます'; return; }
  msg.textContent = '現在位置を取っています...';
  window.chrome.webview.postMessage({ type: 'here' });
};
// ===== 現在位置の印 (2026-11-05、利用者の指示「3D でも現在位置が欲しい」。親の決定 3・4) =====
// アプリが {type:'hereMark', pos (この場所の座標), utc (取った時刻、Unix 秒), spin (自転する場所), note} で知らせる (開いたとき・現在位置を取ったとき・［現在位置］)。
// pos が null なら印を消す (この場所の外の点)。印は水色 (#00e5ff。admin の桃の玉と見分ける) の玉と、札「現在位置 HH:MM:SS」(画面での大きさが変わらない)。内部の印とは別に扱い、外から見ているときも出す。
// マイハンガーの中 (SITE) では出さない (ハンガーの中の位置はまだ決められない)。右上のスイッチ「現在位置」(view3d.here) で出す・出さない
let hereData = null, hereG = null;
const hereOn = () => document.getElementById('hereOn').checked;
// トラムのある場所の始め方 ('wait' = 最初の知らせを待っている、'no' = しない・済んだ)。userActed: 利用者が何か操作した (飛ぶ先を押す・ドラッグ・ホイール・キー・歩くモードの切り替え)
let hereStart = 'no', userActed = false;
const markActed = e => { if (e.isTrusted) userActed = true; };
cv.addEventListener('pointerdown', markActed); cv.addEventListener('wheel', markActed);
addEventListener('keydown', markActed);
document.getElementById('tlist').addEventListener('click', markActed); document.getElementById('walkBtn').addEventListener('click', markActed);
// 現在位置のまわりを読み込んで、見るモードでそこを中心にする (読み込み中なら終わってから。その間に操作されたらやめる)
function startAtHere(pos) {
  if (userActed || walk || pendingHere) return;
  if (loading) { setTimeout(() => startAtHere(pos), 300); return; }
  loadRegion(pos.slice(), regionR()).then(() => { if (cur && !userActed && !walk) { tgt.set(pos[0] - cur.center[0], pos[1] - cur.center[1], pos[2] - cur.center[2]); dist = 40; upd(); } });
}
function onHereMark(d) {
  if (!VIEW) { pendingHereMark = d; return; }   // 場所のデータが入る前: 覚えて、入った後に出す
  if (hereStart === 'wait') { hereStart = 'no'; if (d && Array.isArray(d.pos) && !userActed) startAtHere(d.pos); }
  hereData = d && Array.isArray(d.pos) ? d : null; drawHereMark();
  // ［マップ］タブで取ったとき (・開いたとき) は、ハンガーへ勝手に切り替えない。ハンガーの中なら知らせとハンガーを開くボタンだけ出す。
  // いまそのハンガー (同じ大きさ) の中を開いているときは、開いた後の形で出す
  const h = d && d.hangar && Array.isArray(d.at) ? { pos: d.at, hangar: d.hangar, utc: d.utc } : null;
  showHangarNote(h, !!(h && SITE && window.__hangar && window.__hangar.size === h.hangar.size));
}
// ===== 現在位置がハンガーの中のとき (2026-11-05、利用者の指示「ハンガーにいるかどうかは現在位置で分かってるから、マイハンガーか割り当て化はどっちでもいい。3D データは一緒」。親の決定) =====
// 判定はアプリ (StationView3DView.FixHere・HangarPointAt。v2.5.4 の 2D の地図の判定の移植)。here・hereMark の hangar = {size, index, dist}。
// 開くハンガーの大きさは判定された入口の大きさ。その大きさのハンガーのデータが決まらない (hangarSizesOf に無い) ときは開かない。
// 「入るときに乗った本体の乗り場」は、取った点にいちばん近い、ハンガーへ行けるエレベーターの乗り場 (elevOrder でマイハンガーの行が出る乗り場)
function bodyView() { return stationSaved ? stationSaved.VIEW : VIEW; }
// 場所のデータ (VIEW) が入る前に届いた知らせ (hangarSizes・hereMark)。入った後 (始めの処理) に当てる
let pendingSizes = null, pendingHereMark = null;
function hangarLiftNear(pos) {
  const sv = bodyView(); if (!sv) return null;
  const all = (sv.transit || []).filter(t => t[0].startsWith('elevator'));
  const dk = t => /dockingport_(\d+)$/i.test(t[5] || '') || /pad_(\d+)$/i.test(t[5] || '');
  const lines = new Set(); for (const t of all) if (dk(t) || hangarLineOf(t, sv) || t[6]) lines.add(t[3]);
  let best = null, bd = Infinity;
  for (const t of all) { if (!lines.has(t[3])) continue; const d = Math.hypot(t[1][0] - pos[0], t[1][1] - pos[1], t[1][2] - pos[2]); if (d < bd) { bd = d; best = t; } }
  return best;
}
let hangarNoteData = null;
// 知らせ (歩くモードの欄の #hereNote): 「ハンガー（S）の所にいます。…」と、［ハンガーを開く］(開いていないとき) ・［本体のこの位置を見る］
function showHangarNote(h, opened) {
  hangarNoteData = h;
  const box = document.getElementById('hereNote'); box.innerHTML = '';
  if (!h) { box.style.display = 'none'; return; }
  const z = h.hangar.size || '?', ok = hangarSizesOf(bodyView()).includes(h.hangar.size);
  const t = document.createElement('div');
  t.textContent = `ハンガー（${z}）の所にいます（ハンガーの入口 ${h.hangar.index} から水平に ${h.hangar.dist} m）。` + (opened ? 'ハンガーの中での位置は、まだ出せません' : ok ? '' : `この場所のハンガー（${z}）の形はデータにありません`);
  box.appendChild(t);
  if (!opened && ok) { const b = document.createElement('button'); b.textContent = `ハンガー（${z}）を開く`; b.onclick = () => openHangarAtHere(h); box.appendChild(b); }
  const v = document.createElement('button'); v.textContent = '本体のこの位置を見る'; v.onclick = () => standAtBody(h.pos, h.utc); box.appendChild(v);
  box.style.display = 'block';
}
// 本体のその点に立つ (今までの［現在位置］の動き)
function standAtBody(pos, utc) {
  if (SITE) leaveHangarQuiet();
  if (!walk) document.getElementById('walkBtn').click();
  showHangarNote(null);
  // 本体のその点に印を出す (ハンガーの中と判定した点は、アプリが本体の印を送らないため)
  hereData = { pos: pos.slice(), utc: utc || (hereData && hereData.utc) || Date.now() / 1000, spin: false }; drawHereMark();
  travelTo(pos, wyaw, null, window.__here = {}, false);
}
// ハンガーの 3D を開く (本体のハンガーのエレベーターから入るときと同じ openHangar)。開けなければ本体のその点に立つ
async function openHangarAtHere(h) {
  const from = hangarLiftNear(h.pos);
  if (!from || !hangarSizesOf(bodyView()).includes(h.hangar.size)) { standAtBody(h.pos, h.utc); return; }
  if (SITE) leaveHangarQuiet();
  if (!walk) document.getElementById('walkBtn').click();
  await openHangar(h.hangar.size, from);
  showHangarNote(h, true);
}
function drawHereMark() {
  if (hereG) { scene.remove(hereG); hereG.traverse(o => { o.geometry?.dispose(); o.material?.map?.dispose?.(); o.material?.dispose?.(); }); hereG = null; }
  if (!hereData || !cur || SITE) return;
  const c = cur.center, q = hereData.pos;
  // 読み込んだ地点の範囲 (中心から ±cur.r m の立方体) の外の点には出さない (札は画面での大きさが変わらないので、数 km 先の点の札が大きく見えてしまうため。
  // 2026-11-05、親の決定「見ている地点の読み込んだ範囲の外の印は出さない」)。その点の地点を読み込めば (［現在位置］など) 出る
  if (Math.abs(q[0] - cur.cc[0]) > cur.r || Math.abs(q[1] - cur.cc[1]) > cur.r || Math.abs(q[2] - cur.cc[2]) > cur.r) return;
  const t = new Date(hereData.utc * 1000); const hhmm = [t.getHours(), t.getMinutes(), t.getSeconds()].map(v => String(v).padStart(2, '0')).join(':');
  hereG = new THREE.Group(); hereG.position.set(q[0] - c[0], q[1] - c[1], q[2] - c[2]);
  const ball = new THREE.Mesh(new THREE.SphereGeometry(0.5), new THREE.MeshBasicMaterial({ color: 0x00e5ff, depthTest: false }));
  ball.renderOrder = 9; hereG.add(ball);
  const lines = [`現在位置 ${hhmm}`]; if (hereData.spin) lines.push('（自転する場所のため、ずれることがあります）');
  const sp = textSprite(lines, '#00e5ff', '#bff8ff'); sp.material.sizeAttenuation = false; sp.material.depthTest = false;   // 玉と同じ水色の枠・薄い水色の文字 (ほかの札の黄色の枠・白い文字と見分ける)
  const asp = sp.scale.x / sp.scale.y; const h = 0.045 * lines.length; sp.scale.set(h * asp, h, 1);
  sp.center.set(0.5, 0); sp.position.set(0, 0, 1.0); sp.renderOrder = 9; hereG.add(sp);
  hereG.visible = hereOn();
  scene.add(hereG);
}
try { const t = localStorage.getItem('view3d.here'); if (t === '1' || t === '0') document.getElementById('hereOn').checked = t === '1'; } catch (_) { }
document.getElementById('hereOn').addEventListener('change', e => { try { localStorage.setItem('view3d.here', e.target.checked ? '1' : '0'); } catch (_) { } if (hereG) hereG.visible = e.target.checked; });
// 地点を読み込む前・読み込み中に届いた here (［マップ］の現在位置を押して開いたときなど) は、読み込み終わってから出す
let pendingHere = null;
// 待つのは FAIL_HOLD_MS まで (最初の地点の読み込みが失敗したままでも止まる)。過ぎたら待つのをやめ、次に地点を読み込めた時点 (show の後) で出す
let pendingHereSince = 0;
function runPendingHere() {
  if (!pendingHere) return;
  if (!pendingHereSince) pendingHereSince = performance.now();
  if (!cur || loading || !VIEW) { if (performance.now() - pendingHereSince < FAIL_HOLD_MS) setTimeout(runPendingHere, 300); else pendingHereSince = 0; return; }
  pendingHereSince = 0; const d = pendingHere; pendingHere = null; onHere(d);
}
function onHere(d) {
  const msg = document.getElementById('msg');
  if (!d.ok) { msg.textContent = d.note || '現在位置を出せませんでした'; return; }
  hereStart = 'no';   // トラムのある場所の始め方 (startAtHere) より、こちらを先にする
  pendingExtView = false;   // 待っている「外から見る形」(runExteriorView) より、こちらを先にする
  if (!cur || loading || !VIEW) { pendingHere = d; runPendingHere(); return; }
  msg.textContent = d.note || '';
  // ハンガーの中なら、そのハンガーの 3D を開く (データが決まらない・乗り場が無いときは、今までどおり本体のその点に立つ。知らせは出す)
  if (d.hangar) { const h = { pos: d.pos, hangar: d.hangar, utc: d.utc }; if (hangarSizesOf(bodyView()).includes(d.hangar.size) && hangarLiftNear(d.pos)) { openHangarAtHere(h); return; } standAtBody(d.pos, d.utc); showHangarNote(h, false); return; }
  showHangarNote(null);
  if (SITE) leaveHangarQuiet();
  if (!walk) document.getElementById('walkBtn').click();
  travelTo(d.pos, wyaw, null, window.__here = {}, false);
}
// ===== 外から見る形 (2026-11-06、利用者の指示「あとあると便利なのは、現在位置を取ったときにステーションから 50Km 以内かつそのステーションから出た後でなければ、
//  そのステーションの外観の 3D を map 表示して pad の場所などを分かりやすくする機能」。親の決定) =====
// アプリが {type:'exteriorView'} で頼む (［マップ］の現在位置で、近くのステーションを外から見る形で出すとき。StationView3DView.RequestExterior)。
// 今ある外部視点 (extView。外から見たときの印と見出しが出る) になる所まで引くだけで、新しい描き方は無い: 見るモードにし、extFit で回す中心と距離を決める。中の行き先へは飛ばない。
// 読み込んだ地点は替えない (開いたときは初めの行き先のまわり、出していた 3D から切り替えたときは今の地点のまま)。
// 場所のデータ・地点の読み込みがまだ・読み込み中なら、済むまで待つ (here の待ち方 runPendingHere と同じ: FAIL_HOLD_MS まで。過ぎたら待つのをやめ、次に地点を読み込めた時点 (show の後) で行う)。
// 印の知らせ (extReady) は、地点が読み込めてから FAIL_HOLD_MS まで待ち、来ないままなら印なしで引く。
// マイハンガーの中を出しているときは、ステーションへ戻り、入るときに乗った乗り場のまわりを読み込んでから引く (読み込めなければやめる)。
// 待っている間に［現在位置］の知らせ (here) が来たら、そちらを先にしてやめる (onHere)。
// ［マップ］タブの中では、引いた距離の 2 倍が LEAVE_DIST より大きければ、星系図に戻る距離 (leaveDist) をそこまで広げる
let pendingExtView = false, pendingExtSince = 0, pendingExtMarksSince = 0;
function onExteriorView() { pendingExtView = true; pendingExtMarksSince = 0; hereStart = 'no'; pendingHere = null; runExteriorView(); }
function runExteriorView() {
  if (!pendingExtView) return;
  if (!pendingExtSince) pendingExtSince = performance.now();
  if (!VIEW || !cur || loading || pendingGo) { if (performance.now() - pendingExtSince < FAIL_HOLD_MS) setTimeout(runExteriorView, 300); else pendingExtSince = 0; return; }
  pendingExtSince = 0;
  if (SITE) {
    const back = hangarBack ? hangarBack.pos.slice() : null;
    if (!back) { pendingExtView = false; return; }
    if (walk) document.getElementById('walkBtn').click();
    leaveHangarQuiet();
    const before = cur;
    loadRegion(back, regionR()).then(() => { if (cur === before) pendingExtView = false; else runExteriorView(); });
    return;
  }
  if (!extReady()) {
    if (!pendingExtMarksSince) pendingExtMarksSince = performance.now();
    if (performance.now() - pendingExtMarksSince < FAIL_HOLD_MS) { setTimeout(runExteriorView, 300); return; }
  }
  pendingExtView = false; pendingExtMarksSince = 0;
  if (walk) document.getElementById('walkBtn').click();
  extFit();
  leaveDist = Math.max(LEAVE_DIST, dist * 2);
}
// マイハンガーの中から、ステーションへ戻る (［現在位置］で移るとき。移る先は呼ぶ側が決める)
function leaveHangarQuiet() { if (!stationSaved) return; SITE = ''; VIEW = stationSaved.VIEW; BASE = stationSaved.BASE; document.getElementById('title').textContent = stationSaved.title; stationSaved = null; hangarBack = null; document.getElementById('hangarBar').style.display = 'none'; renderTargets(); }
// ===== 読み込みの調整 (左の欄。値は LOADCFG。2026-11-06、利用者の指示「今言った距離で作って、調整可能なようにページに設定を足すのは不可能なの？」) =====
// 数字を変えたら覚えて (localStorage の view3d.load)、その場で効かせる: 部品の大きさの境は、解析の糸に索引を分け直してもらってから見直す (中身の変わったかたまりだけ入れ替わる)。
// 外判定の距離・床の距離は、外判定を見直す。ほかは、出すかたまりを見直す。地点を読み込んでいる最中は、終わってから効かせる
function loadCfgChanged(keys) {
  try { localStorage.setItem('view3d.load', JSON.stringify(LOADCFG)); } catch (_) { }
  if (loading) { setTimeout(() => loadCfgChanged(keys), 500); return; }
  if (!cur || !cur.index) return;
  if (keys.some(k => k === 's1' || k === 's2' || k === 's3')) {
    const og = originGen, p = cur;
    workerCall({ type: 'sizes', og, sizes: [LOADCFG.s1, LOADCFG.s2, LOADCFG.s3] }).then(d => { if (d.stale || og !== originGen || p !== cur) return; cur.index = makeIndex(d.index); failed.clear(); review(true); }).catch(e => report('sizes', e));
  }
  // 大きな物体の境を、持っている大きな物体の境より小さくしたら、アプリに作り直してもらう (bigCheck)
  if (keys.includes('s3')) bigCheck();
  // 大を描く距離を大きくして、読み込む範囲 (regionR) が今の地点の半径より大きくなったら、今の位置を中心に取り出し直す (原点・出ているかたまりはそのまま)
  if (keys.includes('d3') && cur.r < regionR()) loadRegion(focusAbs(), regionR(), true);
  if (keys.some(k => k === 'outDist' || k === 'outFloor')) outRequest();
  review(true);
}
document.querySelectorAll('input.ld').forEach(inp => {
  const k = inp.dataset.k; inp.value = LOADCFG[k];
  inp.addEventListener('change', () => { const v = parseFloat(inp.value); if (!isFinite(v) || v < 0) { inp.value = LOADCFG[k]; return; } LOADCFG[k] = v; loadCfgChanged([k]); });
});
document.getElementById('ldReset').onclick = () => { Object.assign(LOADCFG, LOAD_DEF); document.querySelectorAll('input.ld').forEach(inp => { inp.value = LOADCFG[inp.dataset.k]; }); loadCfgChanged(Object.keys(LOAD_DEF)); };
// ===== 場所の全部の大きな物体 (2026-11-06、利用者の指示「距離に関係なく大きなびったいは書いた方がいいのかも」。親の決定) =====
// アプリ (StationView3DWindow の BigGetAsync・StationView3D.Session.BuildBigAsync) が、場所の全部の区画から「大きな物体の境」以上の部品だけを取り出して、場所ごとに 1 回作って保存する。
// ここは {type:'bigGet', site, pal, s3} で頼み、{type:'bigReady', site, pal, s3 (作った境), base} で受け取って解析の糸に渡す (糸が今の地点の索引に足す。view3d-worker.js の「場所の全部の大きな物体」)。
// 作っている間・作れなかったときは、今までどおり地点の中の大きな物体だけを出す (待たない)。頼むのは、まだ頼んでいない・場所 (SITE)・色付けが替わった・境 (LOADCFG.s3) を持っている物より小さくしたとき
let bigTex = [], bigHave = null, bigAsk = null;
const bigWant = () => ({ site: SITE, pal: regionPalReq, s3: LOADCFG.s3 });
function bigCheck() {
  if (!APP || !cur) return;
  const w = bigWant(), ok = b => b && b.site === w.site && b.pal === w.pal && b.s3 <= w.s3;
  if (ok(bigHave) || (bigAsk && bigAsk.site === w.site && bigAsk.pal === w.pal && bigAsk.s3 <= w.s3)) return;
  bigAsk = w; window.chrome.webview.postMessage({ type: 'bigGet', site: w.site, pal: w.pal, s3: w.s3 });
}
async function onBig(d) {
  if (d.type === 'bigError') { bigAsk = null; logApp('場所の全部の大きな物体を用意できませんでした: ' + (d.message || '')); return; }
  const w = bigWant(); if (d.site !== w.site || d.pal !== w.pal) return;   // 頼んだ後に場所・色付けが替わった
  bigAsk = null;
  try {
    const region = await (await fetch(d.base + 'region.json')).json();
    const og = originGen, res = await workerCall({ type: 'big', og, base: new URL(d.base, location.href).href, region, thr: d.s3, site: d.site, pal: d.pal });
    bigTex = res.textures || []; bigHave = { site: d.site, pal: d.pal, s3: d.s3 };
    if (res.stale || og !== originGen || !cur) return;   // 地点が替わった: 次の地点の索引に糸が足す
    cur.index = makeIndex(res.index); review(false);
    if (res.stat) logApp(`場所の全部の大きな物体 境 ${d.s3} m: 部品 ${res.stat.bigAll}・今の地点に足した部品/三角形 ${res.stat.big[0]}/${res.stat.big[1]}・読み込み ${res.stat.bigLoad.toFixed(1)} 秒`);
  } catch (e) { report('big', e); }
}
