// ステーションの地図の新しい方式のビューア (view3d.html)。試験の頁 (scratchpad の orison_perf\view\test8.html) の移植。
// データ (2026-11-04、利用者の決定「Data.p4k から見る所の区域だけを、開いたときにその場で取り出して描く（前もってデータを作らない）」):
//   ?data=<一時の置き場の URL (最後に /)> の station.json (案内するもの・乗り場・前の地図の階の名前。StationView3D.Session.StationJson)。
//   地点は、アプリ (StationView3DWindow) に {type:'region', id, c, r} を送り、まわり ±r m の区画をその場で取り出して作ってもらう。
//   出来たら ({type:'regionReady', base}) base の region.json と g/<番号>.bin を読み、読み終えたら {type:'regionLoaded'} を送る (アプリが消す)。
//   アプリの外で試すときは ?region=<地点の置き場の URL> (前もって作った地点。その地点だけ)
import * as THREE from 'three';
const report = (where, e) => { try { window.chrome?.webview?.postMessage({ type: 'error', source: 'station3d', message: `[新しい方式] ${where}: ${e && e.stack || e}` }); } catch (_) { } console.error(where, e); };
addEventListener('error', e => report('window', e.error || e.message));
addEventListener('unhandledrejection', e => report('promise', e.reason));
const qs = new URLSearchParams(location.search);
const BASE = qs.get('data') || '';
const APP = !!(window.chrome && window.chrome.webview);
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
  if (d.type === 'walkFill') { onWalkFill(d); return; }
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
    return fetch(api + 'region?c=' + c.map(v => v.toFixed(3)).join(',') + '&r=' + r + '&site=' + encodeURIComponent(SITE)).then(x => x.json()).then(d => { if (d.error) throw new Error(d.error); return d; }).finally(() => { on = false; });
  }
  if (!APP) return Promise.resolve({ base: qs.get('region') || '' });
  const id = ++regionSeq;
  for (const [k, w] of regionWait) { regionWait.delete(k); w.ng(new Error('取りやめ')); }
  return new Promise((ok, ng) => { regionWait.set(id, { ok, ng }); window.chrome.webview.postMessage({ type: 'region', id, c, r, site: SITE }); }).then(d => ({ ...d, id }));
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
let cur = null, curBuf = null;
const tgt = new THREE.Vector3(); let dist = 40, az = 0.8, el2 = 0.6;
function upd() { cam.position.set(tgt.x + dist * Math.cos(el2) * Math.cos(az), tgt.y + dist * Math.cos(el2) * Math.sin(az), tgt.z + dist * Math.sin(el2)); cam.lookAt(tgt); }
let drag = null, moved = 0; const cv = R.domElement;
cv.addEventListener('contextmenu', e => e.preventDefault());
cv.addEventListener('pointerdown', e => { drag = { b: e.button, x: e.clientX, y: e.clientY }; moved = 0; cv.setPointerCapture(e.pointerId); });
cv.addEventListener('pointerup', e => { if (drag && drag.b === 0 && moved < 4) pick(e); drag = null; });
cv.addEventListener('pointermove', e => {
  if (!drag) return; const dx = e.clientX - drag.x, dy = e.clientY - drag.y; drag.x = e.clientX; drag.y = e.clientY; moved += Math.abs(dx) + Math.abs(dy);
  if (walk) { wyaw -= dx * 0.005; wpitch = Math.max(-1.4, Math.min(1.4, wpitch - dy * 0.005)); walkCam(); return; }
  if (drag.b === 0 && !e.shiftKey) { az -= dx * 0.006; el2 = Math.max(-1.5, Math.min(1.5, el2 + dy * 0.006)); }
  else {
    const f = dist * 0.0015; const right = new THREE.Vector3(-Math.sin(az), Math.cos(az), 0);
    const fwd = cam.getWorldDirection(new THREE.Vector3()); const up = new THREE.Vector3().crossVectors(right, fwd).normalize().negate();
    tgt.addScaledVector(right, -dx * f).addScaledVector(up, dy * f);
  }
  upd();
});
const LEAVE_DIST = 10000;   // ［マップ］タブの中で、中心からこれ (m) より遠ざかると星系図に戻る
cv.addEventListener('wheel', e => {
  e.preventDefault();
  const nd = dist * Math.exp(e.deltaY * 0.001);
  // ［マップ］タブの中 (?tab=1) では、遠ざかり切ったら星系図に戻る (アプリへ知らせる)
  if (APP && qs.get('tab') === '1' && !walk && e.deltaY > 0 && nd > LEAVE_DIST) { window.chrome.webview.postMessage({ type: 'leave' }); return; }
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
addEventListener('keydown', e => { if (document.getElementById('cbox').style.display === 'block') return; if (e.target.tagName === 'TEXTAREA' || (e.target.tagName === 'INPUT' && /^(text|search|number)$/.test(e.target.type))) return; keys.add(e.key.toLowerCase()); if ('wasdqe'.includes(e.key.toLowerCase())) e.preventDefault(); });
addEventListener('keyup', e => keys.delete(e.key.toLowerCase()));
addEventListener('keydown', e => {
  if (document.getElementById('cbox').style.display === 'block') { if (e.key === 'Enter') { e.preventDefault(); closeComment(true); } else if (e.key === 'Escape') closeComment(false); return; }
  if (walk && rec && e.key === 'Enter') { e.preventDefault(); keys.clear(); openComment(); return; }
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
// 姿勢: 体の高さと目の高さ (立つ・しゃがむ・寝そべる)。ゲームのデータでは確かめていない値
const STANCE = { stand: { h: 1.8, eye: 1.7, sp: 1 }, crouch: { h: 1.2, eye: 1.05, sp: 0.5 }, prone: { h: 0.5, eye: 0.35, sp: 0.25 } };
let stance = 'stand';
// ジャンプ: 上向きの速さ 4.43 m/秒 (重力 9.81 m/秒² で高さ 1.0 m)。よじ登り: 正方形のコンテナの高さ 1 個分 (container_dry_1_025x0375x025_a・storage_container_1_025x05x025_a の高さ 2.52 m) まで
const JUMP_V = 4.43, GRAV = 9.81, CLIMB = 2.52, LADDER_V = 1.5;
let vz = 0, air = false, onLadder = null;
let rec = null, play = null, noclip = false;
let walk = false, run = false, wyaw = 0, wpitch = 0, wpos = new THREE.Vector3();
// ===== 当たり判定の升 (引いたときだけ作る) =====
// (2026-11-05、利用者の指示「当たり判定も範囲を決めればメモリ節約になるんじゃない？」「物理的にいけないところを省いて、直線で」)
// 以前は解析の糸 (view3d-worker.js) が 1 m (頭の上は 2 m) の升目 f・c・w・fl を全部作って渡していた (Seraphim ±30 m で約 4 GB)。
// 今は解析の糸が渡すのは荒い区切り coarse (16 m の箱ごとの三角形の位置 b の一覧 all・fl と、64 箱を超える大きな三角形の並び bigAll・bigFl) だけ。
// 升は cellGet(kind, i, j) で初めて引いたときに、その升の入る 16 m の箱の一覧と big から、以前の升目と同じ条件で三角形を選んで作り、cells に持つ (2 回目からは cells から返す)。
//   f (足元): 上向き (cz > 0)・1 m の升・升の数 ≤ 4096。ドアも入れる
//   c (頭の上): 下向き (cz < 0)・2 m の升 (floor(x/2))・升の数 ≤ 1024。ドア (名前に door・doors があり elevator の無い部品) は入れない
//   w (横): ドア以外の全部・1 m の升・升の数 ≤ 4096
//   fl (判定の床): floor2・tube2 の三角形・1 m の升・升の数 ≤ 4096
// 升の中身の並びは三角形の位置 b の昇順 (以前の升目と同じ)。作った升は捨てない (親の決定。読み込み直しで中心が変わるときは全部捨てる: 鍵がずれるため)
// 形 (coarse.all・coarse.fl): 並べた鍵 (I × 100003 + J)・始まり・中身 (三角形の位置)。引くのは二分探し
// ----- CELLS_BEGIN (この印の間は試しの道具が切り出して使う。cur・curBuf・COL・coarse だけを使う) -----
let coarse = null;
// a: 歩くモードの「粗い形に物体が無い升」を探す用 (ドアも含む全部の三角形。1 m。利用者の指示「床でなく物体で」)
const cells = { f: new Map(), c: new Map(), w: new Map(), fl: new Map(), a: new Map() };
const CELL_BOX = 16;
// ドアの部品か (worker の以前の isDoor と同じ決まり)。cur.parts の添字ごと。地点を替えたら作り直す
let doorParts = null;
function cellReset() { for (const m of Object.values(cells)) m.clear(); doorParts = null; }
function boxGet(g, key) {
  if (!g) return null; const K = g.keys; let lo = 0, hi = K.length - 1;
  while (lo <= hi) { const m = (lo + hi) >> 1, v = K[m]; if (v === key) return g.items.subarray(g.starts[m], g.starts[m + 1]); if (v < key) lo = m + 1; else hi = m - 1; }
  return null;
}
// 三角形の位置 b の部品がドアか (b の入る種類の範囲 cur.offs[k] から、三角形の番号 → 部品の添字 (cur.ioffs[k]) を引く)
function triIsDoor(b) {
  if (!doorParts) doorParts = cur.parts.map(q => { const w = q[1].split('_'); return w.some(x => x === 'door' || x === 'doors') && !w.includes('elevator'); });
  for (const k of Object.keys(COL)) {
    const [o, n] = cur.offs[k]; if (b < o || b >= o + n) continue;
    const io = cur.ioffs[k]; return doorParts[new Uint32Array(curBuf, io[0] * 4, io[1])[(b - o) / 9]];
  }
  return false;
}
// 升 (kind, i, j) に入る三角形か (以前の worker の sweep と同じ条件)
function triInCell(F, kind, b, i, j) {
  const x0 = Math.min(F[b], F[b + 3], F[b + 6]), x1 = Math.max(F[b], F[b + 3], F[b + 6]), y0 = Math.min(F[b + 1], F[b + 4], F[b + 7]), y1 = Math.max(F[b + 1], F[b + 4], F[b + 7]);
  if (kind === 'c') {
    const cz = (F[b + 3] - F[b]) * (F[b + 7] - F[b + 1]) - (F[b + 4] - F[b + 1]) * (F[b + 6] - F[b]); if (!(cz < 0)) return false;
    if (triIsDoor(b)) return false;
    const a0 = Math.floor(x0 / 2), a1 = Math.floor(x1 / 2), c0 = Math.floor(y0 / 2), c1 = Math.floor(y1 / 2);
    return (a1 - a0 + 1) * (c1 - c0 + 1) <= 1024 && a0 <= i && i <= a1 && c0 <= j && j <= c1;
  }
  const i0 = Math.floor(x0), i1 = Math.floor(x1), j0 = Math.floor(y0), j1 = Math.floor(y1);
  if ((i1 - i0 + 1) * (j1 - j0 + 1) > 4096 || i < i0 || i > i1 || j < j0 || j > j1) return false;
  if (kind === 'f') return (F[b + 3] - F[b]) * (F[b + 7] - F[b + 1]) - (F[b + 4] - F[b + 1]) * (F[b + 6] - F[b]) > 0;
  if (kind === 'w') return !triIsDoor(b);
  return true;   // fl・a
}
// 升 (kind, i, j。c は 2 m の升の番号、ほかは 1 m) の三角形の位置の並び (b の昇順)。無ければ null。初めて引いたときに作って cells に持つ
function cellGet(kind, i, j) {
  if (!coarse) return null;
  const M = cells[kind], key = i * 100003 + j; const got = M.get(key); if (got !== undefined) return got;
  const F = new Float32Array(curBuf);
  const s = kind === 'c' ? 2 : 1;   // 升の 1 辺 (m)。2 m の升 [2i, 2i+2) も 1 つの 16 m の箱に収まる (16 は 2 の倍数)
  const box = boxGet(kind === 'fl' ? coarse.fl : coarse.all, Math.floor(i * s / CELL_BOX) * 100003 + Math.floor(j * s / CELL_BOX)) || [];
  const big = kind === 'fl' ? coarse.bigFl : coarse.bigAll;
  // 箱の一覧と big はどちらも b の昇順なので、小さい方から順に取って昇順にそろえる
  const out = []; let p = 0, q = 0;
  while (p < box.length || q < big.length) {
    const b = q >= big.length || (p < box.length && box[p] < big[q]) ? box[p++] : big[q++];
    if (triInCell(F, kind, b, i, j)) out.push(b);
  }
  const res = out.length ? Uint32Array.from(out) : null; M.set(key, res); return res;
}
// ----- CELLS_END -----
function buildGrid() { }
function buildCeil() { }
function ceilAt(x, y, zmin) {
  // (x, y) の真上で、zmin より高い下向きの面のいちばん低い所
  const all = new Float32Array(curBuf); const l = cellGet('c', Math.floor(x / 2), Math.floor(y / 2)); let best = null;
  if (l) for (const b of l) {
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
  const all = new Float32Array(curBuf); const dx = bx - ax, dy = by - ay, dz = bz - az; const seen = new Set();
  for (const [cx, cy] of [[ax, ay], [bx, by]]) {
    const l = cellGet('w', Math.floor(cx), Math.floor(cy));
    if (l) for (const b of l) {
      if (seen.has(b)) continue; seen.add(b);
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
  return false;
}
function fits(x, y, z) { if (noclip) return true; const c = ceilAt(x, y, z + 0.05); return c === null || c - z >= STANCE[stance].h; }
function groundAt(x, y, zmax) {
  // (x, y) の真下で、zmax より低い歩く面のいちばん高い所
  const all = new Float32Array(curBuf); const l = cellGet('f', Math.floor(x), Math.floor(y)); let best = null;
  if (l) for (const b of l) {
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
  const all = new Float32Array(curBuf); const l = cellGet('f', Math.floor(x), Math.floor(y)); let best = null;
  if (l) for (const b of l) {
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
// 粗い形の三角形 (横の当たりの升 cellGet('w', …): ドア以外の全部) の z の範囲が足元 ±WALK_ZTOL m に 1 つも掛からない「物体の無い升」を、アプリに知らせる
// ({type:'walkCheck', pos (足元。場所の座標), zTol, cells: [[升の中心 x, y], …] (場所の座標), site})。同じ升は 1 回の 3D の中で二度送らない (送った升を持つ。親の決定)。1 回に最大 WALK_MAX_CELLS 升 (親の決定。近い順)。
// アプリが細かい形 (LOD0) で見つけた三角形 ({type:'walkFill', center, items:[{inst, name, tris}]}。tris は center からの相対) を extra に入れ、描き (部品の色の Mesh を root に足す。groups には入れない)、当たり判定に足す。
// 升 (cellGet の f・c・w・fl) は粗い形の三角形 (curBuf) から作るものなので、extra は別の表 (三角形の並び Float32Array と、1 m の升ごとの添字の Map) に持ち、
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
// 描く: 部品の色 (COL.other) の Mesh (buildScene の部品と同じ作り: flatShading・両面・切る面)。root に足す。groups には入れない (pick は cur.ioffs の添字で引くため)
function extraDraw(li) {
  const e = extra.list[li];
  const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(e.tris, 3));
  const me = new THREE.Mesh(g, new THREE.MeshLambertMaterial({ color: COL.other, side: THREE.DoubleSide, flatShading: true, clippingPlanes: [clip, clipLo] }));
  me.renderOrder = 0; me.userData = { k: 'extra', li }; e.mesh = me; root.add(me);
}
function extraAdd(tris) { const li = extra.list.length; extra.list.push({ tris, mesh: null }); extraIndex(li); extraDraw(li); }
// 読み込み直し (buildScene の入れ替えの後): keep なら、中心の差 off (新しい中心 − 前の中心) で座標を直し (新しい相対 = 前の相対 − off)、新しい root に描き直し、送った升も直す。keep でなければ捨てる
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
// 1 秒ごとに、物体の無い升を探してアプリに送る
let lastWalkCheck = 0;
function walkCheckTick() {
  if (!APP || !walk || !cur || loading || !coarse) return;
  const now = performance.now(); if (now - lastWalkCheck < 1000) return; lastWalkCheck = now;
  const F = new Float32Array(curBuf); const foot = wpos.z, z0 = foot - WALK_ZTOL, z1 = foot + WALK_ZTOL;
  const out = [];
  for (let i = Math.floor(wpos.x - WALK_R); i <= Math.floor(wpos.x + WALK_R); i++) for (let j = Math.floor(wpos.y - WALK_R); j <= Math.floor(wpos.y + WALK_R); j++) {
    const cx = i + 0.5, cy = j + 0.5, d = Math.hypot(cx - wpos.x, cy - wpos.y); if (d > WALK_R) continue;
    const key = ekey(i, j); if (extra.sentKeys.has(sentKey(cx, cy, foot))) continue;
    let empty = true;
    const l = cellGet('a', i, j);   // ドアも物体なので含めて見る (w はドアを除く)
    if (l) for (const b of l) { if (Math.max(F[b + 2], F[b + 5], F[b + 8]) >= z0 && Math.min(F[b + 2], F[b + 5], F[b + 8]) <= z1) { empty = false; break; } }
    if (empty) out.push([cx, cy, d]);
  }
  if (!out.length) return;
  out.sort((a, b) => a[2] - b[2]);
  const send = out.slice(0, WALK_MAX_CELLS), c = cur.center;
  for (const s of send) { extra.sent.push([s[0], s[1], foot]); extra.sentKeys.add(sentKey(s[0], s[1], foot)); }
  window.__walkCheck = { sent: (window.__walkCheck?.sent || 0) + send.length, last: send.length, pending: out.length - send.length };
  window.chrome.webview.postMessage({ type: 'walkCheck', pos: [wpos.x + c[0], wpos.y + c[1], foot + c[2]], zTol: WALK_ZTOL, cells: send.map(s => [s[0] + c[0], s[1] + c[1]]), site: SITE });
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
    if (wpos.z >= onLadder[3][2]) { wpos.z = onLadder[3][2]; wpos.x += Math.cos(wyaw) * 0.6; wpos.y += Math.sin(wyaw) * 0.6; const g = groundAt(wpos.x, wpos.y, wpos.z + STEP); if (g !== null) wpos.z = g; onLadder = null; }
    else if (wpos.z <= onLadder[2][2] - 0.5 || !keys.size) { if (!keys.has('w') && !keys.has('s')) {} else if (wpos.z <= onLadder[2][2]) onLadder = null; }
    walkCam(); showElev(); recStep(dt); return;
  }
  const spd = (run ? RUN : WALK) * STANCE[stance].sp * dt;
  if (mv.lengthSq() > 0) {
    mv.normalize().multiplyScalar(spd); const nx = wpos.x + mv.x, ny = wpos.y + mv.y;
    const g = groundGap(nx, ny, wpos.z + STEP);
    const nz_ = air ? wpos.z : (g !== null && g >= wpos.z - STEP ? g : wpos.z);
    let tx = nx, ty = ny;
    if (blocked(wpos.x, wpos.y, wpos.z, tx, ty)) { if (!blocked(wpos.x, wpos.y, wpos.z, nx, wpos.y)) ty = wpos.y; else if (!blocked(wpos.x, wpos.y, wpos.z, wpos.x, ny)) tx = wpos.x; else { tx = wpos.x; ty = wpos.y; } }
    if ((tx !== wpos.x || ty !== wpos.y) && fits(tx, ty, nz_)) { const g3 = groundGap(tx, ty, wpos.z + STEP); wpos.x = tx; wpos.y = ty; if (!air && g3 !== null) { if (g3 < wpos.z - 0.05) air = true; else wpos.z = g3; } }
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
const stopDist = (t, q) => { const c = cur.center; return Math.hypot(t[1][0] - c[0] - q.x, t[1][1] - c[1] - q.y, t[1][2] - c[2] - q.z); };
function hideTbox(closeAt) { const b = document.getElementById('tbox'); if (closeAt) tClosed = closeAt; elevPinned = null; b.style.display = 'none'; b.dataset.key = ''; }
function tboxRow(list, text, cls, fn) { const b = document.createElement('button'); b.textContent = text; if (cls) b.className = cls; b.onclick = fn; list.appendChild(b); }
function showElev(at, pinned) {
  if (!cur) return;
  const box = document.getElementById('tbox'), head = document.getElementById('tboxHead'), list = document.getElementById('tboxList');
  const q = at ? at : wpos, lim = at ? 5 : 2;
  const all = (cur.transit || []).filter(t => t[0].startsWith('elevator') || t[0] === 'tram');
  if (!pinned && elevPinned) { if (stopDist(elevPinned, wpos) < 5) return; hideTbox(); }
  if (!pinned && tClosed) { if (stopDist(tClosed, wpos) < 3) return; tClosed = null; }
  let near = null, nd = lim;
  for (const t of all) { const d = stopDist(t, q); if (d < nd) { nd = d; near = t; } }
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
function elevOrder(rows, near) {
  const num = (re, t) => { const m = re.exec(t[5] || ''); return m ? +m[1] : null; };
  const dk = t => num(/dockingport_(\d+)$/i, t), pd = t => num(/pad_(\d+)$/i, t);
  const lift = !!near[6] || rows.some(t => dk(t) !== null || pd(t) !== null);
  const normal = rows.filter(t => !(lift && (dk(t) !== null || pd(t) !== null))).sort((a, b) => b[1][2] - a[1][2]);
  const docks = lift ? rows.filter(t => dk(t) !== null).sort((a, b) => dk(a) - dk(b)) : [];
  const pads = lift ? rows.filter(t => dk(t) === null && pd(t) !== null).sort((a, b) => pd(a) - pd(b)) : [];
  const sizes = lift && !hangarBack ? HANGAR_SIZES.filter(z => (VIEW.hangarSizes || []).includes(z)) : [];
  const back = hangarBack && near[6] ? [hangarBack] : [];
  return { normal, docks, pads, sizes, back };
}
function tboxHead(list, text) { const d = document.createElement('div'); d.textContent = text; d.style.cssText = 'padding:4px 8px;background:#2a3038;color:#9cf;font-size:12px'; list.appendChild(d); }
// 録画に「ここで移った」を入れる (再生で前の点からなめらかにつながず、その時刻に飛ぶ。点の 9 番目が 1)
function recJump() { if (!rec) return; const ro = recOff(); rec.points.push([+rec.t.toFixed(2), +(wpos.x + ro[0]).toFixed(3), +(wpos.y + ro[1]).toFixed(3), +(wpos.z + ro[2]).toFixed(3), +wyaw.toFixed(4), +wpitch.toFixed(4), stance, noclip ? 1 : 0, 1]); recAcc = 0; }
// エレベーター: 行き先の扉の向きで立つ所を決める (2026-11-04、利用者の指示「扉の向きで決められないの？ 元々ずっとそう言ってる」。
// 同じ x・y の床はかごの床になるので探さない)。利用者の決定「2 行先の扉の床がある側で扉に背を向ける」: 行き先の乗り場に近いエレベーターの扉 (cur.elevDoors) のうち、
// 扉の箱の薄い向きの両側 (箱の中心から、薄い向きに 箱の厚みの半分 + DOOR_STEP m) で、行き先の高さの床があり、乗り場から遠い側 (乗り場はかごの中) に立ち、扉に背を向ける。
// 扉は、乗り場から xy で DOOR_NEAR m 以内・高さが乗り場の高さ −ELEV_BAND〜+3 m に掛かるものを、近い順に見る。どれにも床が無ければ、動かずに窓を閉じる (window.__elevHow = 'none')
const ELEV_BAND = 1.5, DOOR_STEP = 0.6, DOOR_NEAR = 6;
function elevMove(from, t) {
  // 行き先の乗り場が読み込んだ範囲 (中心から ±r/2) の外なら (Orison のスペースポート → シャトル・ステーションは横に約 200 m・下に 212 m)、
  // 行き先の案内と同じく、乗り場のまわり ±FIRST_R m を先に読み込んでから立つ所を決め、続けて ±r m を裏で読み込む
  const c = cur.center, h = cur.r / 2, xyAbs = [wpos.x + c[0], wpos.y + c[1]], yaw0 = wyaw;
  hideTbox();
  if (Math.abs(t[1][0] - c[0]) <= h && Math.abs(t[1][1] - c[1]) <= h && Math.abs(t[1][2] - c[2]) <= h) { elevPlace(t, xyAbs, yaw0); return; }
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
      const ex = o.d[2][0] - o.d[1][0], ey = o.d[2][1] - o.d[1][1];
      const [nx, ny, half] = ex < ey ? [1, 0, ex / 2] : [0, 1, ey / 2];
      let cand = null;
      for (const sgn of [1, -1]) {
        const px = o.mx + sgn * nx * (half + DOOR_STEP), py = o.my + sgn * ny * (half + DOOR_STEP);
        const gg = band(groundAt(px, py, tz + STEP)); if (gg === null) continue;
        const far = Math.hypot(px - sx, py - sy);
        if (!cand || far > cand.far) cand = { o, px, py, gg, far, yaw: Math.atan2(py - o.my, px - o.mx) };
      }
      if (cand) { best = cand; break; }
    }
    if (best) { wpos.set(best.px, best.py, best.gg); wyaw = best.yaw; window.__elevHow = 'door'; window.__elevDoor = best.o.d[0]; }
    // 扉の床も見つからないとき: 乗り場の位置・高さに立つ (担当が決めたもの)
    else { wpos.set(sx, sy, tz); wyaw = yaw0 + Math.PI; window.__elevHow = 'none'; }
  }
  air = false; vz = 0; onLadder = null;
  hideTbox(t); walkCam(); recJump();
}
// トラム: 行き先の駅の乗り場の床へ、向きは反対。読み込んでいない所なら、行き先の案内と同じく ±FIRST_R m を先に、続けて ±r m を裏で (window.__tram に時間)
function tramMove(t) { window.__tram = { to: t[4] }; travelTo(t[1], wyaw + Math.PI, t, window.__tram); }
// 行き先 abs (絶対の位置) の床へ、向き yaw で移る。closeAt: 着いた乗り場 (そこから 3 m 離れるまで窓を出さない)。tm: かかった時間を書く所 (first・all 秒)
// 読み込んでいない所なら、行き先の案内と同じく ±FIRST_R m を先に、続けて ±r m を裏で
function travelTo(abs, yaw, closeAt, tm, force) {
  const r = regionR();
  const place = () => { const c = cur.center; const x = abs[0] - c[0], y = abs[1] - c[1], z = abs[2] - c[2]; const g = groundAt(x, y, z + STEP); wpos.set(x, y, g !== null ? g : z); wyaw = yaw; air = false; vz = 0; onLadder = null; hideTbox(closeAt); walkCam(); recJump(); };
  const c = cur.center, h = cur.r / 2;
  if (!force && Math.abs(abs[0] - c[0]) <= h && Math.abs(abs[1] - c[1]) <= h && Math.abs(abs[2] - c[2]) <= h) { place(); return Promise.resolve(); }
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
  hangarBack = { label: `${from[4]}（${stationSaved.VIEW.station}）`, pos: from[1].slice(), yaw: wyaw + Math.PI, stop: from };
  SITE = info.site; BASE = info.data; VIEW = await (await fetch(BASE + 'station.json')).json();
  document.getElementById('title').textContent = `マイハンガー（${size}）: ${info.title || ''}`; document.getElementById('hangarHow').textContent = info.how || ''; document.getElementById('hangarBar').style.display = 'block';
  renderTargets();
  const lift = (VIEW.transit || []).find(t => t[6]);
  const abs = lift ? lift[1] : VIEW.center;
  window.__hangar = { size, site: SITE, start: lift ? 'lift' : 'center' };
  await travelTo(abs, wyaw + Math.PI, lift || null, window.__hangar, true);
}
async function leaveHangar() {
  if (!stationSaved || !hangarBack) return;
  const b = hangarBack; SITE = ''; VIEW = stationSaved.VIEW; BASE = stationSaved.BASE; document.getElementById('title').textContent = stationSaved.title;
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
  if (ok && t && rec) { const q = cPos || cam.position, ro = recOff(); rec.comments.push({ t: +rec.t.toFixed(2), pos: [+(q.x + ro[0]).toFixed(3), +(q.y + ro[1]).toFixed(3), +(q.z + ro[2]).toFixed(3)], text: t }); document.getElementById('recInfo').textContent = `録画中 ${rec.points.length} 点・コメント ${rec.comments.length}`; }
  cPos = null; cv.focus?.();
}
// 文字の札 (1 行の文字か、行の配列)
function textSprite(text) {
  const lines = Array.isArray(text) ? text : [text];
  const c = document.createElement('canvas'); const g = c.getContext('2d'); g.font = '28px sans-serif';
  const w = Math.ceil(Math.max(...lines.map(l => g.measureText(l).width))) + 24, hh = 44 * lines.length; c.width = w; c.height = hh; g.font = '28px sans-serif';
  g.fillStyle = 'rgba(0,0,0,0.75)'; g.fillRect(0, 0, w, hh); g.strokeStyle = '#ffee66'; g.strokeRect(1, 1, w - 2, hh - 2); g.fillStyle = '#ffffff';
  lines.forEach((l, i) => g.fillText(l, 12, 31 + 44 * i));
  const sp = new THREE.Sprite(new THREE.SpriteMaterial({ map: new THREE.CanvasTexture(c), depthTest: false })); sp.scale.set(w / 44 * 0.5, 0.5 * lines.length, 1); sp.renderOrder = 20; return sp;
}
function showComments(list) {
  // 再生: コメントの地点に印 (玉)、そこから上へ 1.5 m の引き出し線、その先に文を浮かせる
  for (const m of cMarks) root.remove(m); cMarks = [];
  const co = play && play.center ? [play.center[0] - cur.center[0], play.center[1] - cur.center[1], play.center[2] - cur.center[2]] : [0, 0, 0];
  for (const cm of list) {
    const g = new THREE.Group(); const p0 = new THREE.Vector3(cm.pos[0] + co[0], cm.pos[1] + co[1], cm.pos[2] + co[2]), p1 = p0.clone(); p1.z += 1.5;
    const ball = new THREE.Mesh(new THREE.SphereGeometry(0.12), new THREE.MeshBasicMaterial({ color: 0xffee66, depthTest: false })); ball.position.copy(p0); ball.renderOrder = 20;
    const lg = new THREE.BufferGeometry().setFromPoints([p0, p1]); const line = new THREE.Line(lg, new THREE.LineBasicMaterial({ color: 0xffee66, depthTest: false })); line.renderOrder = 20;
    const sp = textSprite(cm.text); sp.position.copy(p1); sp.position.z += 0.3;
    g.add(ball, line, sp); g.visible = false; g.userData.t = cm.t; root.add(g); cMarks.push(g);
  }
}
const recOff = () => rec ? [cur.center[0] - rec.center[0], cur.center[1] - rec.center[1], cur.center[2] - rec.center[2]] : [0, 0, 0];
function recStep(dt) { if (!rec) return; recAcc += dt; rec.t += dt; const ro = recOff(); if (recAcc >= 0.1) { recAcc = 0; rec.points.push([+rec.t.toFixed(2), +(wpos.x + ro[0]).toFixed(3), +(wpos.y + ro[1]).toFixed(3), +(wpos.z + ro[2]).toFixed(3), +wyaw.toFixed(4), +wpitch.toFixed(4), stance, noclip ? 1 : 0]); document.getElementById('recInfo').textContent = `録画中 ${rec.points.length} 点`; } }
function playStep(dt) {
  if (!play.points.length) { play = null; return; }
  play.t += dt; const P = play.points; while (play.i < P.length - 1 && P[play.i + 1][0] <= play.t) play.i++;
  const a = P[play.i], b = P[Math.min(play.i + 1, P.length - 1)], f = b[0] > a[0] ? Math.min(1, (play.t - a[0]) / (b[0] - a[0])) : 0;
  const po = play.center ? [play.center[0] - cur.center[0], play.center[1] - cur.center[1], play.center[2] - cur.center[2]] : [0, 0, 0];
  wpos.set(a[1] + (b[1] - a[1]) * f + po[0], a[2] + (b[2] - a[2]) * f + po[1], a[3] + (b[3] - a[3]) * f + po[2]); wyaw = a[4] + (b[4] - a[4]) * f; wpitch = a[5]; stance = a[6]; noclip = a[7] === 1; updWalkUi();
  for (const m of cMarks) m.visible = play.t >= m.userData.t;
  walkCam(); document.getElementById('recInfo').textContent = `再生中 ${play.i + 1} / ${P.length}`;
  if (play.i >= P.length - 1) { play = null; document.getElementById('recInfo').textContent = '再生おわり'; }
}
// 確かめ用 (Edge headless の試しから呼ぶ)
window.__probe = () => { cam.updateMatrixWorld(); ray.setFromCamera(new THREE.Vector2(0, 0), cam); const hs = ray.intersectObjects(Object.values(groups).filter(g => g).flat().filter(m => m.visible), false); if (!hs.length) return ['当たり無し', cam.position.toArray(), cam.getWorldDirection(new THREE.Vector3()).toArray()]; const h0 = hs[0]; const k = h0.object.userData.k; const io = cur.ioffs[k]; const id = io ? new Uint32Array(curBuf, io[0] * 4, io[1])[h0.object.userData.t0 + h0.faceIndex] : -1; return [k, +h0.distance.toFixed(2), id >= 0 ? cur.parts[id][1] : '', h0.point.toArray().map(v => +v.toFixed(2))]; };
window.__cur = () => cur; window.__buf = () => curBuf; window.__tbox = (x, y, z) => showElev(new THREE.Vector3(x, y, z), true); window.__gravList = () => grav; window.__gravAt = (x, y, z) => gravAt(x, y, z); window.__loadAt = (c, r) => loadRegion(c, r);
window.__walk = { low: (x, y) => lowestNear(x, y), setPitch: v => { wpitch = v; walkCam(); }, place: (x, y, z, yaw) => { wpos.set(x, y, z); wyaw = yaw; air = false; vz = 0; walkCam(); }, ground: (x, y, z) => groundAt(x, y, z), gaps: () => gapLog, seg: (...a) => segHit(...a), on: () => walk, pos: () => [wpos.x, wpos.y, wpos.z, wyaw, run, stance, air, noclip], cam, rec: () => rec, setRec: r => { play = { t: 0, i: 0, points: r.points, center: r.center }; showComments(r.comments || []); }, marks: () => cMarks.map(m => [m.visible, m.userData.t]), openComment, closeComment, keys };

function keyMove() {
  const now = performance.now(), dt = Math.min(0.5, (now - lastT) / 1000); lastT = now;
  if (!cur) return;
  if (walk) { walkStep(dt); return; }
  if (!keys.size) return;
  // 速さは時間で決める (描くのが遅くても進む): 毎秒 max(3 m, 中心までの距離) (Shift で 4 倍)
  const sp = Math.max(3, dist) * dt * (keys.has('shift') ? 4 : 1);
  const fwd = cam.getWorldDirection(new THREE.Vector3()), right = new THREE.Vector3(-Math.sin(az), Math.cos(az), 0);
  if (keys.has('w')) tgt.addScaledVector(fwd, sp); if (keys.has('s')) tgt.addScaledVector(fwd, -sp);
  if (keys.has('d')) tgt.addScaledVector(right, sp); if (keys.has('a')) tgt.addScaledVector(right, -sp);
  if (keys.has('e')) tgt.z += sp; if (keys.has('q')) tgt.z -= sp;
  upd();
}
const ray = new THREE.Raycaster();
function partFloors(p, pi) {
  const all = new Float32Array(curBuf); const res = [];
  for (const k of Object.keys(OCOL)) {
    const io = p.ioffs[k], o = p.offs[k]; if (!io || !io[1]) continue;
    const ids = new Uint32Array(curBuf, io[0] * 4, io[1]); const hs = new Map(); let n = 0;
    for (let t = 0; t < io[1]; t++) if (ids[t] === pi) {
      n++; const z = (all[o[0] + t * 9 + 2] + all[o[0] + t * 9 + 5] + all[o[0] + t * 9 + 8]) / 3 + p.center[2];
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
const SALES_R = 1.5;
function requestSales(t) {
  window.__salesAsked = { guid: t.shopGuid, entity: t.shop };
  if (APP) window.chrome.webview.postMessage({ type: 'sales', guid: t.shopGuid, entity: t.shop });
  else if (qs.get('api')) fetch(qs.get('api') + 'sales?guid=' + encodeURIComponent(t.shopGuid) + '&entity=' + encodeURIComponent(t.shop)).then(x => x.json()).then(showSales).catch(e => showSales({ title: '販売品', shops: [{ name: t.shop, note: e.message }] }));
}
// 販売品の枠 (アプリの地図の［MAP］の販売品の枠と同じ中身: {title, shops: [{name, note} | {name, tables: [{cols, rows, left}]}]})。✕ か Esc で閉じる
function showSales(d) {
  const box = document.getElementById('sbox'); box.innerHTML = ''; window.__sales = d;
  const h = document.createElement('div'); h.style.cssText = 'display:flex;justify-content:space-between;font-weight:bold;margin-bottom:6px';
  const ti = document.createElement('span'); ti.textContent = d.title || '販売品'; const x = document.createElement('button'); x.textContent = '✕'; x.style.cssText = 'width:auto;display:inline;border:1px solid #666'; x.onclick = () => { box.style.display = 'none'; };
  h.appendChild(ti); h.appendChild(x); box.appendChild(h);
  for (const sh of d.shops || []) {
    const n = document.createElement('div'); n.textContent = sh.name; n.style.cssText = 'margin:6px 0 2px;color:#9cf'; box.appendChild(n);
    if (sh.note) { const m = document.createElement('div'); m.textContent = sh.note; box.appendChild(m); continue; }
    for (const tb of sh.tables || []) {
      const table = document.createElement('table'); const tr = document.createElement('tr');
      for (const c of tb.cols) { const th = document.createElement('th'); th.textContent = c; tr.appendChild(th); } table.appendChild(tr);
      for (const r of tb.rows) { const tr2 = document.createElement('tr'); r.forEach((v, i) => { const td = document.createElement('td'); td.textContent = v; if (i >= tb.left) td.style.textAlign = 'right'; tr2.appendChild(td); }); table.appendChild(tr2); }
      box.appendChild(table);
    }
  }
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
  if (hit) { hit.kind = hit.object.userData.k; hit.tri = hit.object.userData.t0 + hit.faceIndex; }
  const box = document.getElementById('pick');
  if (!hit) { box.style.display = 'none'; return; }
  if (document.getElementById('cbox').style.display === 'block') { cPos = hit.point.clone(); document.getElementById('cpos').textContent = `(位置: 押した所 ${cPos.x.toFixed(1)}, ${cPos.y.toFixed(1)}, ${cPos.z.toFixed(1)})`; document.getElementById('ctext').focus(); return; }
  // 店の端末 (station.json の端末のうち、店 (shopGuid) の結び付いたもの) から 1.5 m 以内を押したら、その店の販売品を出す
  // (2026-11-04、利用者の指示「押したら売ってるリストと値段を出してほしい」。端末と店の結び付きは地図のデータ (端末の shop・shopEntity) のまま)
  { const c = cur.center; let tn = null, td = SALES_R; for (const t of cur.targets || []) { if (!t.kind.startsWith('terminal_') || !t.shopGuid) continue; const d = Math.hypot(t.pos[0] - c[0] - hit.point.x, t.pos[1] - c[1] - hit.point.y, t.pos[2] - c[2] - hit.point.z); if (d < td) { td = d; tn = t; } } if (tn) { requestSales(tn); return; } }
  if (!walk) setCenter(hit.point);
  else { const k0 = hit.kind; const io0 = cur.ioffs[k0]; if (io0) { const id0 = new Uint32Array(curBuf, io0[0] * 4, io0[1])[hit.tri]; const w_ = cur.parts[id0][1].split('_'); if (w_.includes('elevator') && w_.includes('panel')) showElev(hit.point, true); }
    // トラム: 乗り場 (地図のデータの tram) から 5 m 以内のどの面を押しても、その駅の一覧を出す (トラムのパネルの部品の名前は確かめていないので、名前では決めない)
    if ((cur.transit || []).some(t => t[0] === 'tram' && stopDist(t, hit.point) < 5)) showElev(hit.point, true); }
  const k = hit.kind; if (!cur.ioffs[k]) return;
  const [o, n] = cur.ioffs[k]; const ids = new Uint32Array(curBuf, o * 4, n);
  const part = cur.parts[ids[hit.tri]];
  box.style.display = 'block';
  box.textContent = `${part[1]}  (inst ${part[0]}・${part[4]})\n押した面: ${NAME[k]}\n部品の種類: ${NAME[part[2]]}\n部品の中の判定の面:\n${partFloors(cur, ids[hit.tri])}\n押した所の高さ: 中心から ${hit.point.z.toFixed(2)} m (絶対 ${(hit.point.z + cur.center[2]).toFixed(2)} m)
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
// 地点: 中心 c (場所の座標) のまわり ±r m の立方体。立方体に掛かる区画の部品を全部描く (試験の頁と同じ)
let loading = false;
// keep: 近づいて読み込み直すとき (2026-11-04、利用者の指示「近づいたら書かないと」)。見ている所・歩いている所・選んだフロアをそのままにする
async function loadRegion(c, r, keep, small) {
  if (loading) return; loading = true; prewarmStop();
  document.getElementById('prog').classList.toggle('small', !!small);
  const msg = document.getElementById('msg'); msg.textContent = ''; showProgress(0, 0);
  try {
    const t0 = performance.now();
    const rq = await requestRegion(c, r);
    const tReady = performance.now();
    // 取り出しの後は、同じ大きさで「作画中」(2026-11-04、利用者の指示)。バーは解析の糸が読み込んだ区画の数、続けて GPU へ送った形の数
    msg.textContent = ''; showProgress(0, 0, '作画中');
    REGBASE = rq.base; REG = await (await fetch(REGBASE + 'region.json')).json();
    // アプリの外で試すとき (?region=) は、前もって作った地点の中心と範囲を使う
    if (!APP && !qs.get('api')) { c = REG.center; r = REG.r; }
    window.__lastRegion = { app: rq, wait: (tReady - t0) / 1000 };
    const ph = window.__phase = { region: (tReady - t0) / 1000 }; let tp = performance.now();
    window.__stage = 'worker';
    // 解析の結果の保存 (WC。view3d-worker.js): アプリが鍵・容量・ディスクの空きを渡す。鍵が無ければ保存しない。容量 0 なら今ある保存を消す
    const wcache = rq.wkey ? { key: rq.wkey, budget: rq.wbudget, free: rq.wfree } : null;
    if (!wcache && rq.wbudget === 0) worker.postMessage({ type: 'wstat', id: 0, budget: 0 });
    prewarmStop();
    const res = await workerBuild(new URL(REGBASE, location.href).href, REG, c, r, (d, n) => showProgress(d, n, '作画中'), bx => drawOutline(bx, c), wcache);
    if (APP && res.wcache && typeof res.wcache.used === 'number') window.chrome.webview.postMessage({ type: 'wcacheUsed', bytes: res.wcache.used });
    ph.worker = res.times; ph.workerAll = (performance.now() - tp) / 1000; tp = performance.now();
    // 区画のファイルはもう要らない。アプリに置き場を消してもらう
    if (APP && rq.id) window.chrome.webview.postMessage({ type: 'regionLoaded', id: rq.id });
    else if (!APP && qs.get('api') && rq.base) fetch(qs.get('api') + 'loaded?base=' + encodeURIComponent(rq.base)).catch(() => { });
    window.__stage = 'makeCur'; const nc = makeCur(c, r, res);
    ph.makeCur = (performance.now() - tp) / 1000; tp = performance.now();
    await buildScene(nc, res, keep, t0);
    ph.scene = (performance.now() - tp) / 1000;
    hideProgress();
    window.__lastRegion.total = (performance.now() - t0) / 1000;
  } catch (e) { hideProgress(); removeOutline(); if (!keep) msg.textContent = '読み込めませんでした: ' + e.message; report('loadRegion', e); }
  finally {
    loading = false;
    if (pendingGo) { const [pt, pb] = pendingGo; pendingGo = null; setTimeout(() => goTarget(pt, pb), 0); }
  }
}
// 解析の糸 (view3d-worker.js) に地点の区画の読み取り・解析・当たり判定の升目を頼む
const worker = new Worker(new URL('./view3d-worker.js', import.meta.url));
let workerSeq = 0; const workerWait = new Map();
worker.onmessage = e => {
  const d = e.data;
  if (d.type === 'wstat') { if (APP && typeof d.used === 'number') window.chrome.webview.postMessage({ type: 'wcacheUsed', bytes: d.used }); return; }
  const w = workerWait.get(d.id); if (!w) return;
  if (d.type === 'progress') w.progress(d.done, d.total);
  else if (d.type === 'boxes') w.boxes(d.boxes);
  else if (d.type === 'done') { workerWait.delete(d.id); w.ok(d); }
  else if (d.type === 'error') { workerWait.delete(d.id); w.ng(new Error(d.message)); }
};
// 解析の糸の中で拾われない失敗 (メモリ不足など) があったら、待っているものを失敗にする (「作画中」のまま止まらないように)
worker.onerror = e => {
  report('worker', e.message);
  for (const w of workerWait.values()) w.ng(new Error('解析の途中で止まりました' + (e.message ? ': ' + e.message : '')));
  workerWait.clear();
};
function workerBuild(base, region, c, r, progress, boxes, wcache) {
  const id = ++workerSeq;
  return new Promise((ok, ng) => { workerWait.set(id, { ok, ng, progress, boxes }); worker.postMessage({ type: 'build', id, base, region, c, r, keys: { col: Object.keys(COL), ocol: Object.keys(OCOL), labk: LABK }, names: NAME, wcache }); });
}
// 先読み (アプリが地点を保存した後、メモリに余裕があるときだけ頼んでくる): 画面の糸とは別の糸で解析して保存だけする (結果は返さない)。
// 利用者が地点を頼んだら、すぐ止める (糸を捨てる = メモリを返す)
var pw = null;
function prewarmStop() { if (pw) { try { pw.w.terminate(); } catch (_) { } pw = null; } }
async function prewarmStart(d) {
  prewarmStop();
  const reply = used => { if (APP) window.chrome.webview.postMessage({ type: 'prewarmDone', pid: d.pid, used }); };
  if (loading) { reply(null); return; }
  try {
    const region = await (await fetch(d.base + 'region.json')).json();
    if (loading) { reply(null); return; }
    const w = new Worker(new URL('./view3d-worker.js', import.meta.url)); const me = pw = { w };
    const fin = used => { if (pw === me) pw = null; try { w.terminate(); } catch (_) { } reply(used); };
    w.onmessage = e => { const m = e.data; if (m.type === 'done' || m.type === 'error') fin(m.wcache && typeof m.wcache.used === 'number' ? m.wcache.used : null); };
    w.onerror = () => fin(null);
    w.postMessage({ type: 'build', id: 1, base: new URL(d.base, location.href).href, region, c: d.c, r: d.r, keys: { col: Object.keys(COL), ocol: Object.keys(OCOL), labk: LABK }, names: NAME, prewarm: true, wcache: { key: d.key, budget: d.budget, free: d.free } });
  } catch (_) { reply(null); }
}
// 外形 (部品の箱の線): 細かい形の前に先に出す (2026-11-04、利用者の案「外形だけ先に書くとか」)。新しい地点の中心 c の座標で、今の中心からずらして置く
let outlineG = null;
function removeOutline() { if (outlineG) { scene.remove(outlineG); outlineG.geometry.dispose(); outlineG.material.dispose(); outlineG = null; } }
function drawOutline(bx, c) {
  removeOutline(); if (!bx.length) return;
  const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(bx, 3));
  outlineG = new THREE.LineSegments(g, new THREE.LineBasicMaterial({ color: 0x88aacc, transparent: true, opacity: 0.6, clippingPlanes: [clip, clipLo] }));
  if (cur) outlineG.position.set(c[0] - cur.center[0], c[1] - cur.center[1], c[2] - cur.center[2]);
  scene.add(outlineG);
}
// 解析の結果から、地点の表 (cur) を作る (前の buildCur の後ろの半分。中身は同じ)
function makeCur(c, r, res) {
  const { offs, ioffs, loffs, parts, ladders, elevDoors, stairs, fz, fa } = res;
  window.__lightParts = new Set(res.lightParts);
  const near = q => Math.abs(q[0] - c[0]) <= r && Math.abs(q[1] - c[1]) <= r && Math.abs(q[2] - c[2]) <= r;
  // 乗り場: 立方体の中のものと、それと同じ路線のもの (試験の頁の transit と同じ)
  const nearLines = new Set(VIEW.transit.filter(t => near(t[1])).map(t => t[3]));
  const transit = VIEW.transit.filter(t => near(t[1]) || nearLines.has(t[3]));
  const shopNames = new Set(VIEW.targets.filter(t => ['shop', 'admin', 'pharmacy'].includes(t.kind) && near(t.pos)).map(t => t.name));
  const targets = VIEW.targets.filter(t => t.kind === 'stair' ? (near(t.pos) || near(t.top)) : near(t.pos));
  const floorsOld = VIEW.floors.filter(f => Math.abs(f[2] - c[2]) <= r);
  // 店の入口 (部品の番号を、読み込んだ部品の番号に直す。無ければ -1)
  const pidx = new Map(res.pidx);
  // 地点の立方体の中の店のもの (同じ名前の店がほかの所にもあるので位置で選ぶ。位置の無い前のデータは名前で)
  const entrances = REG.entrances.filter(e => e.pos ? near(e.pos) : shopNames.has(e.shop)).map(e => ({ shop: e.shop, pos: e.pos, floor: e.floor, h: e.h, list: e.list.map(o => [pidx.has(o[0] + ':' + o[1]) ? pidx.get(o[0] + ':' + o[1]) : -1, o[2], o[0], o[3], o[4], o[5], o[6], o[7], o[8], o[9].map((v, a) => v - c[a]), o[10].map((v, a) => v - c[a]), o[11]]) }));
  const flist = floorList(fz, fa, transit, floorsOld);
  // 階段の行き先 (2026-11-04、利用者の決定「作り直す」。2D の地図の階段の一覧の代わり): 読み込んだ区画の階段の部品ごとに、下の端 (箱の低い所) を行き先にし、
  // 名前は「下の端の階 → 上の端の階」。行き先の一覧 (VIEW.targets) に足す (同じ位置 (0.1 m) のものは足さない。区画を読み込むたびに増える)
  const flAt = z => { for (const fl of flist) if (fl.lo - 0.5 <= z + c[2] && z + c[2] <= fl.hi + 2.0) return fl.name; return `高さ ${(z + c[2]).toFixed(1)} m`; };
  const seen = new Set(VIEW.targets.filter(t => t.kind === 'stair').map(t => t.pos.map(v => v.toFixed(1)).join()));
  for (const [nm, lo, hi] of stairs || []) {
    const pos = [(lo[0] + hi[0]) / 2 + c[0], (lo[1] + hi[1]) / 2 + c[1], lo[2] + c[2]], key = pos.map(v => v.toFixed(1)).join();
    if (seen.has(key)) continue; seen.add(key);
    VIEW.targets.push({ kind: 'stair', label: '階段', name: nm, ja: `${flAt(lo[2])} → ${flAt(hi[2])}`, pos, top: [pos[0], pos[1], hi[2] + c[2]] });
  }
  if (stairs && stairs.length) setTimeout(renderTargets, 0);
  return { center: c, r, offs, ioffs, loffs, parts, ladders, elevDoors: elevDoors || [], transit, targets, entrances, flist, guide: guideText(targets, entrances, flist), shops: VIEW.targets.filter(t => ['shop', 'admin', 'pharmacy'].includes(t.kind) && near(t.pos)), station: VIEW.station, label: `${VIEW.station} (${c.map(v => v.toFixed(0)).join(', ')})` };
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
    const p_ = tg.pos; let txt = `フロア ${floorOf(p_[2])}。`;
    if (['shop', 'admin', 'pharmacy'].includes(tg.kind)) {
      const en = entrances.find(e => e.shop === tg.name); const ok_ = (en ? en.list : []).filter(o => o[5]);
      if (en && en.floor) txt += `店の中心の床 ${en.floor[0].toFixed(2)} m (${en.floor[1]})、直線の高さ ${en.h.toFixed(2)} m。`;
      if (ok_.length) { const o = ok_[0]; txt += `入口 ${o[1]} (店の中心から ${o[3].toFixed(1)} m)。入口の道の点 ${o[6]}・${o[8] ? '乗り場につながる' : '乗り場につながらない'}。`; }
      else txt += '入口になる開口部が無い (' + (en ? en.list : []).map(o => `${o[1]} ${o[3].toFixed(1)} m: 直線が ${o[11].join(', ')} に当たる`).join('; ') + ')。';
    }
    if (els.length) {
      const d3 = t => Math.hypot(t.pos[0] - p_[0], t.pos[1] - p_[1], t.pos[2] - p_[2]);
      const e = els.reduce((a, b) => d3(b) < d3(a) ? b : a);
      txt += `いちばん近い乗り口: ${e.label} (行き先 ${e.name}・フロア ${floorOf(e.pos[2])})、直線で ${d3(e).toFixed(0)} m。`;
    }
    res.push({ shop: dispRow(tg), text: txt });
  }
  return res;
}
// 描く形は、種類ごとに CHUNK 三角形ずつのかたまり (Mesh) に分ける (2026-11-04、利用者の指摘「向かいの建物は観葉植物だけが掛かれてた」:
// Crusader の地点で「部品」が 2,981 万三角形 (位置の配列 1.07 GB) になり、1 つの形として描けず、面が全部出なかった。輪郭の線と、小さな種類の形だけが出ていた)。
// groups[k] はかたまりの配列。かたまりの userData.t0 は、その種類の中の最初の三角形の番号 (押したときに部品を引くのに使う)
const CHUNK = 250_000;
// 1 コマに足す形の上限 (ms。担当が決めた値)。形を作るのと、GPU へ送るのは次の描画なので、1 コマに足すかたまりも 2 つまで
const FRAME_BUDGET = 8, FRAME_CHUNKS = 2;
const nextFrame = () => new Promise(res => requestAnimationFrame(() => res()));
// 地点の形を作る。近づいて読み込み直すとき (keep) は、今の形を出したまま、新しい形を新しい中心の座標で横に足していき、全部できたら入れ替える
// (見ている所・歩いている所は、入れ替えるときに新しい中心の座標に移す)。面は flatShading (三角形の向きの明るさ。前の computeVertexNormals と同じ見え方で、法線の配列を作らない)
async function buildScene(p, res, keep, t0) {
  const buf = res.buf, all = new Float32Array(buf);
  const prevCenter = cur ? cur.center.slice() : null;
  const off = keep && prevCenter ? [p.center[0] - prevCenter[0], p.center[1] - prevCenter[1], p.center[2] - prevCenter[2]] : [0, 0, 0];
  if (!keep) { scene.remove(root); root.traverse(o => { o.geometry?.dispose(); o.material?.dispose?.(); }); root = new THREE.Group(); scene.add(root); for (const k of Object.keys(groups)) groups[k] = null; }
  const nroot = new THREE.Group(); nroot.position.set(off[0], off[1], off[2]); scene.add(nroot);
  const ngroups = {};
  const jobs = [];
  for (const k of Object.keys(COL)) {
    const [o, n] = p.offs[k]; ngroups[k] = null; if (!n) continue; ngroups[k] = [];
    const wall = k === 'wall';
    for (let a = 0; a < n / 9; a += CHUNK) jobs.push(() => {
      const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(all.subarray(o + a * 9, o + Math.min(n / 9, a + CHUNK) * 9), 3));
      const me = new THREE.Mesh(g, new THREE.MeshLambertMaterial({ color: COL[k], side: THREE.DoubleSide, flatShading: true, transparent: wall, opacity: wall ? 0.25 : 1, depthWrite: !wall, clippingPlanes: [clip, clipLo] }));
      me.renderOrder = wall ? 1 : 0; me.userData = { k, t0: a }; ngroups[k].push(me); nroot.add(me);
    });
  }
  for (const k of Object.keys(OCOL)) {
    const [o, n] = p.offs[k]; ngroups[k] = null; if (!n) continue; ngroups[k] = [];
    for (let a = 0; a < n / 9; a += CHUNK) jobs.push(() => {
      const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(all.subarray(o + a * 9, o + Math.min(n / 9, a + CHUNK) * 9), 3));
      const me = new THREE.Mesh(g, new THREE.MeshBasicMaterial({ color: OCOL[k], side: THREE.DoubleSide, transparent: true, opacity: 0.8, clippingPlanes: [clip, clipLo], polygonOffset: true, polygonOffsetFactor: -4, polygonOffsetUnits: -4 }));
      me.renderOrder = 3; me.userData = { k, t0: a }; ngroups[k].push(me); nroot.add(me);
    });
    // 融合した形の輪郭 (1 辺を共有し同じ平面に乗る三角形どうしを融合した形の、1 つの三角形にしか使われない辺)。最初のかたまりの子にして、種類の表示と一緒に消す
    const [lo_, ln_] = p.loffs[k];
    for (let a = 0; a < ln_ / 6; a += CHUNK * 2) jobs.push(() => {
      const lg = new THREE.BufferGeometry(); lg.setAttribute('position', new THREE.BufferAttribute(all.subarray(lo_ + a * 6, lo_ + Math.min(ln_ / 6, a + CHUNK * 2) * 6), 3));
      const ls = new THREE.LineSegments(lg, new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.7, clippingPlanes: [clip, clipLo] }));
      ls.renderOrder = 4; ngroups[k][0].add(ls);
    });
  }
  // 少しずつ足す (1 コマに FRAME_BUDGET ms・FRAME_CHUNKS 個まで)。見ている所に近いかたまりから、という並べ替えはしていない (種類の順)
  let done = 0;
  while (done < jobs.length) {
    const st = performance.now(); let k = 0;
    while (done < jobs.length && k < FRAME_CHUNKS && performance.now() - st < FRAME_BUDGET) { jobs[done++](); k++; }
    showProgress(done, jobs.length, '作画中'); window.__stage = 'jobs ' + done + '/' + jobs.length;
    await nextFrame();
  }
  // 入れ替え
  window.__stage = 'swap'; removeOutline();
  if (keep) {
    scene.remove(root); root.traverse(o => { o.geometry?.dispose(); o.material?.dispose?.(); });
    const d = new THREE.Vector3(-off[0], -off[1], -off[2]); tgt.add(d); wpos.add(d); cam.position.add(d);
  }
  nroot.position.set(0, 0, 0); root = nroot;
  for (const k of new Set([...Object.keys(groups), ...Object.keys(ngroups)])) groups[k] = ngroups[k] ?? null;
  cur = p; curBuf = buf;
  // 当たり判定: 荒い区切り coarse を受け取り、作ってあった升は全部捨てる (keep でも中心が変わり、升の鍵がずれるため)
  coarse = res.coarse; cellReset();
  // 細かい形で補った面 (extra): keep なら中心の差 off で座標を直して新しい root に描き直す。別の場所に替わった (keep でない) なら捨てる
  extraRelocate(off, !!keep);
  window.__stage = 'show'; show(t0, keep);
}
// 地点の印・札・一覧などを作る (形は buildScene が作った後)
function show(t0, keep) {
  const p = cur;
  hiM = null; ghostG = null; cMarks = []; if (!keep) selFloor = null;
  document.querySelectorAll('#shopforms button').forEach(b => b.classList.remove('on')); document.getElementById('pick').style.display = 'none';
  const c = p.center;
  shpG = new THREE.Group(); root.add(shpG);
  setGravity(REG && REG.gravity, p.center); drawGravity();
  for (const s of p.shops) {
    const q = s.pos, ok = s.ok;
    const x = q[0] - c[0], y = q[1] - c[1], z = q[2] - c[2];
    const pole = new THREE.Mesh(new THREE.CylinderGeometry(0.08, 0.08, 4), new THREE.MeshBasicMaterial({ color: 0xffffff }));
    pole.rotation.x = Math.PI / 2; pole.position.set(x, y, z + 2); pole.renderOrder = 6; shpG.add(pole);
    const b = new THREE.Mesh(new THREE.SphereGeometry(0.4), new THREE.MeshBasicMaterial({ color: ok === true ? 0x33ff66 : ok === false ? 0xff3333 : 0xaaaaaa }));
    b.position.set(x, y, z); b.renderOrder = 7; shpG.add(b);
  }
  // 開いたときは切らない (2026-11-04、利用者の指摘「crusader の本社の建物がない」: 前は「この高さより上を消す」の初めの値 25 m で、中心から 25 m より上が消えていた)
  if (keep) { applyFloor(); applyVis(); if (walk) walkCam(); else upd(); }
  else { document.getElementById('cut').value = 25; applyFloor(); applyVis(); tgt.set(0, 0, 0); dist = p.r * 1.6; upd(); }
  document.getElementById('info').textContent = `${p.station}\n中心 (${c.map(v => v.toFixed(1)).join(', ')})、中心から ±${p.r} m の立方体\n部品 ${p.parts.length}\n三角形: ` +
    Object.keys(COL).filter(k => p.offs[k][1]).map(k => `${NAME[k]} ${p.offs[k][1] / 9}`).join(' / ') +
    `\n重ねた面の三角形: ` + Object.keys(OCOL).map(k => `${NAME[k]} ${p.offs[k][1] / 9}`).join(' / ') +
    `\n読み込み ${((performance.now() - (t0 || performance.now())) / 1000).toFixed(1)} 秒・${(curBuf.byteLength / 1048576).toFixed(0)} MB` +
    (REG.missing.length ? `\n形の出なかった socpak (この地点): ${REG.missing.length}` : '') +
    (window.__lastRegion?.app?.extractSeconds != null ? `\n時間: 選んでから描き終わるまで ${(((performance.now() - (t0 || performance.now())) / 1000)).toFixed(1)} 秒 = 区画の用意 ${window.__lastRegion.wait.toFixed(1)} 秒 (取り出し ${window.__lastRegion.app.extractSeconds.toFixed(1)} 秒・計算 ${window.__lastRegion.app.computeSeconds.toFixed(1)} 秒。どちらも同時に動かした分の足し合わせ) + ビューアの読み込みと描く用意 ${(((performance.now() - (t0 || performance.now())) / 1000) - window.__lastRegion.wait).toFixed(1)} 秒` : '');
  document.getElementById('guide').textContent = (p.guide || []).map(g => `■ ${g.shop}\n${g.text}`).join('\n\n');
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
    const col = TCOL[tg.kind] ?? (tg.kind.startsWith('terminal_') ? 0xff9933 : 0xffffff); const q = tg.pos;
    const b = new THREE.Mesh(new THREE.SphereGeometry(0.3), new THREE.MeshBasicMaterial({ color: col, depthTest: false }));
    b.position.set(q[0] - p.center[0], q[1] - p.center[1], q[2] - p.center[2]); b.renderOrder = 7; shpG.add(b);
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
    const names = new Map(); for (const k of grp) { const nm = terms[k].text || terms[k].label; names.set(nm, (names.get(nm) || 0) + 1); }
    const lines = [...names.entries()].map(([nm, c]) => nm + (c > 1 ? ` ×${c}` : ''));
    const q = [0, 1].map(a => grp.reduce((s, k) => s + terms[k].pos[a], 0) / grp.length).concat([Math.max(...grp.map(k => terms[k].pos[2]))]);
    const sp = textSprite(lines);
    sp.material.sizeAttenuation = false; const asp = sp.scale.x / sp.scale.y; const h = 0.04 * lines.length; sp.scale.set(h * asp, h, 1);
    sp.center.set(0.5, 0); sp.position.set(q[0] - p.center[0], q[1] - p.center[1], q[2] - p.center[2] + 0.8); shpG.add(sp);
    window.__termLabels.push(lines.join(' / '));
  }
  // エレベーターの乗り口の名前 (2026-11-04、利用者の指摘「エレベーターがたくさん並んでる。何のエレベーターかわからない」):
  // 乗り口 (地点の立方体の中のエレベーター・トラム) を、同じ路線・同じ行き先の名前 (訳) で、水平に 10 m・高さ 3 m 以内につながるものごとに 1 つの札にまとめ、
  // 「行き先 ×数」(数が 1 なら行き先だけ) を、いちばん高い乗り口の 2.4 m 上に出す (端末の札 (0.8 m 上) と重ならないように高くした)
  window.__elevLabels = [];
  {
    const cc = p.center, near = q => Math.abs(q[0] - cc[0]) <= p.r && Math.abs(q[1] - cc[1]) <= p.r && Math.abs(q[2] - cc[2]) <= p.r;
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
      sp.center.set(0.5, 0); sp.position.set(q[0] - cc[0], q[1] - cc[1], q[2] - cc[2] + 2.4); shpG.add(sp);
      window.__elevLabels.push(text);
    }
  }
  // 店の入口を探した直線 (緑 = 障害物なし・入口、赤 = 当たった)
  for (const en of (p.entrances || [])) for (const o of en.list) {
    const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.Float32BufferAttribute([...o[10], ...o[9]], 3));
    const l = new THREE.Line(g, new THREE.LineBasicMaterial({ color: o[5] ? 0x33ff33 : 0xff3333, depthTest: false })); l.renderOrder = 8; shpG.add(l);
  }
  const sf = document.getElementById('shopforms'); sf.innerHTML = '';
  const addH = t => { const h = document.createElement('div'); h.style.marginTop = '4px'; h.textContent = t; sf.appendChild(h); };
  const addB = (pi, t) => { const b = document.createElement('button'); b.textContent = t; b.onclick = () => { if (pi >= 0) focusPart(p, pi, b); }; sf.appendChild(b); };
  addH('店の入口 (名前の語に door・doors・doorway・window・opening のある部品で、店の中心から 10 m 以内。店の中心の真下の床 + 1 m の高さで、店の中心から開口部へ水平に引いた直線が、どの三角形にも当たらないもの)');
  for (const en of (p.entrances || [])) {
    addH(`■ ${en.shop}: 店の中心の床 ${en.floor ? en.floor[0] + ' m (' + en.floor[1] + ')' : 'なし'}・直線の高さ ${en.h ?? '-'} m`);
    en.list.forEach(o => addB(o[0], `${o[5] ? '入口' : '×'} ${o[1]} (inst ${o[2]}) 中心から ${o[3]} m・当たり ${o[4]}${o[11] && o[11].length ? ' (' + o[11].join(', ') + ')' : ''}・道の点 ${o[6]}・${o[8] ? '乗り場につながる' : 'つながらない'}`));
  }
}
function focusPart(p, pi, btn) {
  document.querySelectorAll('#shopforms button').forEach(b => b.classList.remove('on')); btn.classList.add('on');
  if (hiM) { root.remove(hiM); hiM.geometry.dispose(); }
  const all = new Float32Array(curBuf); const arr = [];
  for (const k of Object.keys(COL)) {
    const [o, n] = p.offs[k], [io, inn] = p.ioffs[k]; if (!n) continue;
    const ids = new Uint32Array(curBuf, io * 4, inn);
    for (let t = 0; t < inn; t++) if (ids[t] === pi) for (let j = 0; j < 9; j++) arr.push(all[o + t * 9 + j]);
  }
  const a = new Float32Array(arr);
  const g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.BufferAttribute(a, 3)); g.computeVertexNormals(); g.computeBoundingBox();
  hiM = new THREE.LineSegments(new THREE.EdgesGeometry(g, 20), new THREE.LineBasicMaterial({ color: 0x33ffff, depthTest: false, transparent: true, opacity: 0.9, clippingPlanes: [clip, clipLo] }));
  hiM.renderOrder = 10; root.add(hiM);
  if (!walk) { const bb = g.boundingBox; bb.getCenter(tgt); const s = bb.getSize(new THREE.Vector3()); dist = Math.max(8, Math.min(120, Math.max(s.x, s.y, s.z) * 1.3)); upd(); }
  const part = p.parts[pi];
  const box = document.getElementById('pick'); box.style.display = 'block';
  box.textContent = `${part[1]}  (inst ${part[0]}・${part[4]})\n種類: ${NAME[part[2]]}\n強調した三角形 ${a.length / 9} (部品まるごと。水色の縁取り)\n部品の中の判定の面:\n${partFloors(p, pi)}`;
}
// 選んだフロアの床 (いちばん低い高さ − 0.1 m) から H m までを描き、床より下のほかの階は薄い写し (濃さ ghostA %) で描く。
// 切る高さより上は、薄い写しも描かない (2026-11-04、利用者の指示「切った方が作画が早くて良くないか？」・親の指示「薄く出す形にはしません」)
function applyFloor() {
  if (ghostG) { root.remove(ghostG); ghostG.traverse(o => o.material?.dispose?.()); ghostG = null; }
  if (!selFloor || !cur) { clip.constant = 1e9; clipLo.constant = 1e9; document.getElementById('cutv').textContent = '切らない'; return; }
  const H = +document.getElementById('cutH').value || 2, A = Math.max(0, Math.min(100, +document.getElementById('ghostA').value)) / 100;
  const z0 = selFloor.lo - cur.center[2] - 0.1;
  clip.constant = z0 + 0.1 + H; clipLo.constant = -z0; document.getElementById('cutv').textContent = (z0 + 0.1 + H).toFixed(1);
  ghostG = new THREE.Group();
  for (const k of [...Object.keys(COL), ...Object.keys(OCOL)]) for (const g of groups[k] || []) {
    if (!g.visible) continue;
    const m = new THREE.Mesh(g.geometry, new THREE.MeshBasicMaterial({ color: g.material.color, transparent: true, opacity: A, depthWrite: false, side: THREE.DoubleSide, clippingPlanes: [clip] }));
    m.renderOrder = -1; ghostG.add(m);
  }
  root.add(ghostG);
}
function applyVis() {
  document.querySelectorAll('[data-k]').forEach(cb => { for (const m of groups[cb.dataset.k] || []) m.visible = cb.checked; });
  if (shpG) shpG.visible = document.getElementById('shp').checked;
  // 店の印 (柱と玉) は、部品の中・後ろにあっても見えるよう、いつも手前に描く
  shpG?.traverse(o => { if (o.material) { o.material.depthTest = false; o.material.needsUpdate = true; } });
}
document.querySelectorAll('.lg input').forEach(i => i.addEventListener('change', applyVis));
['cutH', 'ghostA'].forEach(id => document.getElementById(id).addEventListener('input', applyFloor));
const ORBIT_D = 10;
document.getElementById('walkBtn').onclick = () => {
  if (!cur) return; walk = !walk; document.getElementById('walkBtn').textContent = '歩くモード: ' + (walk ? 'ON' : 'OFF');
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
document.getElementById('saveBtn').onclick = () => {
  const r = rec || window.__lastRec; if (!r) return;
  const blob = new Blob([JSON.stringify({ format: r.format, station: r.station, label: r.label, center: r.center, points: r.points, comments: r.comments || [] })], { type: 'application/json' });
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `walk_${r.label.replace(/[^A-Za-z0-9_]/g, '_')}.json`; a.click();
};
document.getElementById('loadF').onchange = async e => {
  const f = e.target.files[0]; if (!f) return; const r = JSON.parse(await f.text());
  if (r.format !== 'scjt-walk-path-1' || !Array.isArray(r.points) || !r.points.length) { document.getElementById('recInfo').textContent = '読めない形'; return; }
  if (!walk) document.getElementById('walkBtn').click();
  play = { t: 0, i: 0, points: r.points, center: r.center }; showComments(r.comments || []);
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
// 決めたものは window.__startHow に書く (確かめ用)
function doorHit(ax, ay, az, bx, by, bz) {
  const [o, n] = cur.offs.door || [0, 0]; if (!n) return false; const F = new Float32Array(curBuf);
  const x0 = Math.min(ax, bx), x1 = Math.max(ax, bx), y0 = Math.min(ay, by), y1 = Math.max(ay, by), z0 = Math.min(az, bz), z1 = Math.max(az, bz);
  const dx = bx - ax, dy = by - ay, dz = bz - az;
  for (let t = 0; t < n / 9; t++) {
    const b = o + t * 9;
    if (Math.max(F[b], F[b + 3], F[b + 6]) < x0 || Math.min(F[b], F[b + 3], F[b + 6]) > x1 || Math.max(F[b + 1], F[b + 4], F[b + 7]) < y0 || Math.min(F[b + 1], F[b + 4], F[b + 7]) > y1 || Math.max(F[b + 2], F[b + 5], F[b + 8]) < z0 || Math.min(F[b + 2], F[b + 5], F[b + 8]) > z1) continue;
    const e1x = F[b + 3] - F[b], e1y = F[b + 4] - F[b + 1], e1z = F[b + 5] - F[b + 2], e2x = F[b + 6] - F[b], e2y = F[b + 7] - F[b + 1], e2z = F[b + 8] - F[b + 2];
    const px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x, det = e1x * px + e1y * py + e1z * pz; if (Math.abs(det) < 1e-12) continue;
    const inv = 1 / det, tx = ax - F[b], ty = ay - F[b + 1], tz = az - F[b + 2]; const u = (tx * px + ty * py + tz * pz) * inv; if (u < 0 || u > 1) continue;
    const qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x; const v = (dx * qx + dy * qy + dz * qz) * inv; if (v < 0 || u + v > 1) continue;
    const tt = (e2x * qx + e2y * qy + e2z * qz) * inv; if (tt > 0 && tt < 1 && visZ(az + dz * tt)) return true;
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
  const F = new Float32Array(curBuf); const l = cellGet('fl', Math.floor(x), Math.floor(y));
  if (l) for (const b of l) {
    const v0x = F[b + 3] - F[b], v0y = F[b + 4] - F[b + 1], v1x = F[b + 6] - F[b], v1y = F[b + 7] - F[b + 1], den = v0x * v1y - v1x * v0y; if (Math.abs(den) < 1e-12) continue;
    const wx = x - F[b], wy = y - F[b + 1], s_ = (wx * v1y - v1x * wy) / den, t_ = (v0x * wy - wx * v0y) / den; if (s_ < 0 || t_ < 0 || s_ + t_ > 1) continue;
    const z = F[b + 2] + s_ * (F[b + 5] - F[b + 2]) + t_ * (F[b + 8] - F[b + 2]); if (Math.abs(z - g) <= 0.02 && visZ(z)) return true;
  }
  // 細かい形で補った面 (extra) の判定の床 (傾き 46° 以内の上向きの面) も見る
  const xl = extraCells(extra.fl, x, y);
  if (xl) for (let q = 0; q < xl.length; q += 2) { const z = triZ(extra.list[xl[q]].tris, xl[q + 1], x, y); if (z !== null && Math.abs(z - g) <= 0.02 && visZ(z)) return true; }
  return false;
}
// 足元の面の部品の名前 (確かめ用)
function groundPart(x, y, zmax) {
  const g = groundAt(x, y, zmax); if (g === null) return null; const F = new Float32Array(curBuf);
  for (const k of Object.keys(COL)) {
    const [o, n] = cur.offs[k]; const io = cur.ioffs[k]; const ids = new Uint32Array(curBuf, io[0] * 4, io[1]);
    for (let t = 0; t < n / 9; t++) { const b = o + t * 9; const xs = [F[b], F[b + 3], F[b + 6]], ys = [F[b + 1], F[b + 4], F[b + 7]];
      if (Math.min(...xs) > x || Math.max(...xs) < x || Math.min(...ys) > y || Math.max(...ys) < y) continue;
      const v0x = F[b + 3] - F[b], v0y = F[b + 4] - F[b + 1], v1x = F[b + 6] - F[b], v1y = F[b + 7] - F[b + 1], den = v0x * v1y - v1x * v0y; if (Math.abs(den) < 1e-12) continue;
      const wx = x - F[b], wy = y - F[b + 1], s_ = (wx * v1y - v1x * wy) / den, t_ = (v0x * wy - wx * v0y) / den; if (s_ < 0 || t_ < 0 || s_ + t_ > 1) continue;
      const z = F[b + 2] + s_ * (F[b + 5] - F[b + 2]) + t_ * (F[b + 8] - F[b + 2]); if (Math.abs(z - g) < 1e-4) return [cur.parts[ids[t]][1], cur.parts[ids[t]][0]]; } }
  return null;
}
function placeStart(t) {
  const c = cur.center, q = [t.pos[0] - c[0], t.pos[1] - c[1], t.pos[2] - c[2]];
  buildGrid(); buildWall(); buildCeil();
  const done = (how, x, y, g, extra) => { window.__startHow = Object.assign({ target: t.name, how, start: [x + c[0], y + c[1], g + c[2]].map(v => +v.toFixed(2)), floor: groundPart(x, y, g + 0.05) }, extra || {}); };
  let term = t.kind.startsWith('terminal_') ? t : null;
  if (!term && ['shop', 'admin', 'pharmacy'].includes(t.kind)) {
    const d3 = x => Math.hypot(x.pos[0] - t.pos[0], x.pos[1] - t.pos[1], x.pos[2] - t.pos[2]);
    const cands = VIEW.targets.filter(x => x.kind.startsWith('terminal_') && x.shop === t.name && d3(x) <= 30).sort((a, b) => d3(a) - d3(b));
    for (const x of cands) { const r = standBefore(x); if (r) { done('端末の前', r[0], r[1], r[2], { terminal: x.name }); return; } }
  }
  if (term) { const r = standBefore(term); if (r) { done('端末の前', r[0], r[1], r[2], { terminal: term.name }); return; } }
  if (['shop', 'admin', 'pharmacy'].includes(t.kind)) {
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
function regionR() { return Math.max(10, +document.getElementById('regionR').value || 150); }
// 読み込み中に行き先を選んだら、今の読み込みが終わってから、その行き先を始める (前は押しても読み込まれなかった)。
// 行き先を選び直したら、前の行き先の残りの読み込み (裏の ±R) はしない (goGen)
let goGen = 0, pendingGo = null;
function goTarget(t, btn) {
  if (loading) { pendingGo = [t, btn]; document.querySelectorAll('#tlist button').forEach(b => b.classList.remove('on')); btn?.classList.add('on'); ++goGen; return; }
  document.querySelectorAll('#tlist button').forEach(b => b.classList.remove('on')); btn?.classList.add('on');
  const q = t.pos, r = regionR();
  const inside = cur && cur.r === r && Math.abs(q[0] - cur.center[0]) <= r && Math.abs(q[1] - cur.center[1]) <= r && Math.abs(q[2] - cur.center[2]) <= r;
  // 最初の見え方: 行き先の印を中心に、印から 8 m・上へ約 3.5 m (傾き 0.45) から見下ろす (2026-11-04、利用者の指摘「初期位置の問題」。前は 25 m・約 14 m 上で、上の階・屋根の中から見ていた)
  const focus = () => { if (walk) return; startAt = null; placeStart(t); };
  // まだ読み込んでいない所なら、まず行き先のまわり ±FIRST_R m だけを取り出して描き、始める所に立つ (操作できる)。
  // 続けて ±r m 全部を裏で取り出し、できたら見ている所・歩いている所をそのままに入れ替える (先に作った区画は使い回されるので、取り出し直さない)
  if (inside) focus();
  else if (r <= FIRST_R) loadRegion(q.slice(), r).then(() => { if (cur) focus(); });
  else {
    const gen = ++goGen;
    loadRegion(q.slice(), FIRST_R).then(() => {
      if (!cur || gen !== goGen) return; focus(); window.__firstDone = performance.now();
      return loadRegion(q.slice(), r, true, true);
    }).then(() => { if (gen === goGen) window.__allDone = performance.now(); });
  }
}
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
let lastNear = 0;
function nearCheck() {
  const now = performance.now(); if (now - lastNear < 1000 || loading || !cur) return; lastNear = now;
  const q = walk ? wpos : tgt, r = cur.r;
  if (Math.abs(q.x) > r / 2 || Math.abs(q.y) > r / 2 || Math.abs(q.z) > r / 2) loadRegion([q.x + cur.center[0], q.y + cur.center[1], q.z + cur.center[2]], regionR(), true);
}
// 確かめ用: いちばん長い 1 コマ (ms。window.__frameMax = 0 で計り直す) と、読み込みの段ごとの時間 (window.__phase)
// window.__frames: 長いコマの上位 8 件 [コマの間 ms, そのうち描画 (R.render) ms, その間の段 (window.__stage)]
let lastFrame = performance.now(), lastRender = 0; window.__frameMax = 0; window.__frames = []; window.__stage = '';
(function loop() { requestAnimationFrame(loop); { const nowF = performance.now(), gap = nowF - lastFrame; window.__frameMax = Math.max(window.__frameMax, gap); if (gap > 50) { const f = window.__frames; f.push([+gap.toFixed(0), +lastRender.toFixed(0), window.__stage]); f.sort((a, b) => b[0] - a[0]); f.length = Math.min(f.length, 8); } lastFrame = nowF; } try { nearCheck(); } catch (e) { window.__err = String(e && e.stack || e); } try { walkCheckTick(); } catch (e) { window.__err = String(e && e.stack || e); } try { keyMove(); } catch (e) { window.__err = String(e && e.stack || e); } const tr = performance.now(); R.render(scene, cam); lastRender = performance.now() - tr; window.__stage = loading ? window.__stage : ''; })();
// ===== 始め =====
(async () => {
  try {
    if (!BASE) { document.getElementById('msg').textContent = 'データの置き場が指定されていません'; return; }
    VIEW = await (await fetch(BASE + 'station.json')).json();
    if (VIEW.format !== 'scjt-view3d-1') throw new Error('形の違うデータ: ' + VIEW.format);
    document.getElementById('title').textContent = qs.get('title') || VIEW.station;
    renderTargets();
    // 最初の地点: 店があればいちばん上の店、無ければ一覧の最初、何も無ければ最初のインスタンスの箱の中心
    // 最初の行き先: ?goto=<名前の一部> があればそれ。無ければハンガーロビーの艦隊マネージャーの端末の前 (2026-11-04、利用者の指示「初期位置は hanger lobby 船舶マネージャーターミナル前がいい」):
    //   艦隊マネージャーの端末 (種類 fleet) のうち、hangar_lobby の乗り口 (エレベーターの行き先に hangar) にいちばん近いもの。hangar_lobby が無ければ一覧のいちばん上の艦隊マネージャー。
    //   艦隊マネージャーの端末が無ければ、一覧のいちばん上の行き先
    const gt = qs.get('goto');
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
    else document.getElementById('msg').textContent = 'この場所には行き先がありません';
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
function onHere(d) {
  const msg = document.getElementById('msg');
  if (!d.ok) { msg.textContent = d.note || '現在位置を出せませんでした'; return; }
  msg.textContent = d.note || '';
  if (SITE) leaveHangarQuiet();
  if (!walk) document.getElementById('walkBtn').click();
  travelTo(d.pos, wyaw, null, window.__here = {}, false);
}
// マイハンガーの中から、ステーションへ戻る (［現在位置］で移るとき。移る先は呼ぶ側が決める)
function leaveHangarQuiet() { if (!stationSaved) return; SITE = ''; VIEW = stationSaved.VIEW; BASE = stationSaved.BASE; document.getElementById('title').textContent = stationSaved.title; stationSaved = null; hangarBack = null; document.getElementById('hangarBar').style.display = 'none'; renderTargets(); }
