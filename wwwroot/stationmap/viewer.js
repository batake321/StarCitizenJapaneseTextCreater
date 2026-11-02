// ステーションの階層マップのビューア (StationMapWeb で開く。SVG と素の JS)。
//   データ: アプリから {type:'load', map:{floors, sections, shops, transit, stairs, tracks}, floor, top} を受け取る (PostWebMessageAsJson。
//     StationMapModel.ViewerMap の形。描くのに要るものだけで、道順のグラフは送られない。道順は {type:'route', pts} で線だけ来る)。座標は局所 (メートル、z 上)。
//   描き方: 階ごとに <g>。斜めの図 (案内図のように各階の平面を一定の間隔で重ねる。実際の高さは使わない) と、選んだ階だけを真上から見た図を切り替える。
//     斜め: 平らな座標 (px, py) = (x, -y) から U = px - py·SHEAR、V = (px·A + py·B)·K - 階の順番 × GAP (GAP は各階の広がりの中央値から決める)。
//     斜めの図で描くのは、選んだ階と同じ床のまとまり (group。建物・地区) の上下 WINDOW 階まで。重ねる順番はまとまりの中の番号 (level)。
//     シャトルの線 (tracks。道筋の点) は選んだ階の面の上に描く。階段の印は選んだ階にあるものだけ。
//   地区のある都市: 開いたときは選んだ階の地図 (floors)。［← 全体図へ戻る］で全体図 (overview) → 地区を押すと、地区を真上から見た平面図 (plan。地区のすべての階の床・区画・店・シャトルの駅) →
//     区画を押すか階を選ぶと、その階の斜めに重ねた図 (floors。地区の階だけ、地区の広がりで決めた間隔で重ねる)。
//     選んだ階は濃く、ほかは薄く (viewer.css の .floor.dim)。描かない階へ続く道順は、境の点に「↑ / ↓ 行き先の階」を出す。
//   名前の札: 隠さずに空いている所へ置き、ずらしたときは引き出し線でつなぐ (placeLabels の説明)。置けない札はまとめた名札 (「店 5 件」) にする。
//   操作: 左クリック (短く押す) = 区画を選ぶ・印 (店・乗り場) を目的地にする。左ボタンの長押し (LONG_MS) = ピンを立てる (長押しのまま動かすとドラッグで地図を動かす)。
//     押してすぐ動かしたらドラッグ。右クリック = 押した所を中心に 1 段 (ZOOM_STEP 倍) 拡大。ホイール = 拡大・縮小。右クリックのメニューは出さない。
//   アプリへの知らせ (chrome.webview.postMessage): ready・error・section {id, floor}・pick {kind, index}・pin {pos, floor}・floor {id}・
//     zoomout (縮小して図の全体が小さくなった。checkZoomOut。［マップ］タブが星系図へ戻る)
'use strict';

const SHEAR = 0.5, A = 0.3, B = 0.6, K = 0.6;
const LONG_MS = 550, MOVE_PX = 6, ZOOM_STEP = 2;
const WINDOW = 3;
const SVGNS = 'http://www.w3.org/2000/svg';

const svg = document.getElementById('map');
const world = document.getElementById('world');
const overlay = document.getElementById('overlay');
const msgEl = document.getElementById('msg');

const st = {
  map: null,          // 受け取った地図
  floors: [],         // 描く階 [{id, name, z, idx}] (低い順)
  fidx: {},           // 階の Id → st.floors の番号
  stack: {},          // 階の Id → 斜めの図で重ねる順番 (まとまりの中の下からの番号 level。まとまりの無い階は st.floors の番号。地区の中では地区の階の下からの番号)
  stack0: {},         // 地区に入る前の stack
  mode: 'floors',     // 'overview' (都市の全体図)・'plan' (地区を真上から。地区のすべての階の床を重ねた平面図)・'floors' (階の地図)
  dists: [],          // 地区 (map.districts)
  groupDist: {},      // 床のまとまり → 地区の Id
  district: null,     // 階の地図で見ている地区 (無ければ null)
  routeTrams: [],     // 道順で乗るシャトルの区間 (map.tracks の番号)
  routeSpans: [],     // 乗った区間 [[map.tracks の番号, 乗る乗り場の x, y, 降りる乗り場の x, y], …] (全体図で、その間だけを太くする)
  sel: null,          // 選んでいる階の Id
  top: false,         // この階だけ (真上から)
  gap: 60,
  selSec: null,       // 選んでいる区画の Id
  route: null,        // [[x,y,z,fid], …]
  cur: null,          // {pos, floor, label}
  pin: null,          // {pos, floor}
  dest: null,         // {kind, index} または {kind:'section', id}
  start: null,        // 同上
  vs: 1, vx: 0, vy: 0,    // 画面 = (U·vs + vx, V·vs + vy)
  ovl: [],            // 画面の大きさで描く印 [{el, u, v, label, pri}]
  icons: {},          // 端末の種類 → ゲームのアイコンの data: URI (アプリが {type:'icons'} で送る。無ければ色の丸と頭の 1 字)
  hlTerm: null,       // 強調している端末のまとまりの番号 (左の「端末」の一覧で選んだもの)
};

function post(m) {
  try { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(m); else console.log('[post] ' + JSON.stringify(m)); }
  catch (e) { console.log('post failed: ' + e); }
}
// 失敗をアプリへ知らせる (アプリがログに書く。StationMapWeb.LogScriptError)。where: どの処理か (アプリからの知らせの種類 load・floor・route など、
// または event (クリック・ホイールなどの操作)・promise)。画面の下にも短く出す (何も出ないまま止まって見えないように)
// at: 起きた所 (ファイル:行:列。window の error の知らせから。無ければ '')。アプリはログに「ビューアの失敗」として書く (StationMapWeb.InitAsync)
function fail(e, where, at) {
  const message = (e && e.message !== undefined) ? String(e.message) : String(e);
  const stack = (e && e.stack) ? String(e.stack) : '';
  console.error('[stationmap] ' + (where || '') + ' ' + (stack || message));
  post({ type: 'error', where: where || '', message, stack, at: at || '' });
  try { msgEl.textContent = '地図を表示できませんでした (' + (where || '') + '): ' + message + '（アプリのログを見てください）'; } catch (x) { }
}
window.addEventListener('error', e => fail(e.error || e.message, 'event', e && e.filename ? (String(e.filename).split('/').pop() + ':' + e.lineno + ':' + e.colno) : ''));
window.addEventListener('unhandledrejection', e => fail(e.reason, 'promise'));

function el(name, attrs, parent) {
  const n = document.createElementNS(SVGNS, name);
  if (attrs) for (const k in attrs) n.setAttribute(k, attrs[k]);
  if (parent) parent.appendChild(n);
  return n;
}

// ===== 座標 =====

function fIndex(fid, z) {
  if (fid && st.fidx[fid] !== undefined) return st.fidx[fid];
  // 描く階に無い点 (階の無いドッキングの乗り場、床の無い階の点など) は高さのいちばん近い描く階
  let best = 0, bd = Infinity;
  const sf = st.sel && st.fidx[st.sel] !== undefined ? st.floors[st.fidx[st.sel]] : null;
  for (const pass of [true, false]) {
    for (const f of st.floors) {
      if (pass && (!sf || (sf.sg ? f.sg !== sf.sg : (!sf.group || f.group !== sf.group)))) continue;
      const d = Math.abs(f.z - z); if (d < bd) { bd = d; best = f.idx; }
    }
    if (bd < Infinity) break;
  }
  return best;
}
function floorOfPoint(fid, z) {
  if (fid && st.fidx[fid] !== undefined) return fid;
  const i = fIndex(fid, z);
  return st.floors[i] ? st.floors[i].id : null;
}
// 現在位置の階 (アプリが階を付けていれば、描く階ならそれ)。付いていなければ xy も含めて決める: 地区のある所では、点が入る地区の箱
// (どれにも入らなければ、地区の無い階と箱がいちばん近い地区の階) の中から、点からいちばん近い床 (区画の床の輪。中なら水平の距離 0) を持つ階。
// 高さの差は、床から 0.6 m 下 〜 6 m 上を 0 とみなし (データの階の決め方 StationRouteGraph.NodeFloor と同じ幅)、外れた分だけを足す (3 次元の距離)。
// 同じ距離なら高いほうの階 (点の真下の床。NodeFloor と同じく高い階から当てる)。
// 床の輪が無い階は選ばない (候補に床のある階が無ければ、地区を問わず全部の階から。それも無ければ高さだけの floorOfPoint)
function currentFloor(fid, x, y, z) {
  if (fid && st.fidx[fid] !== undefined) return fid;
  let cand = st.floors;
  if ((st.dists || []).length) {
    let bd = Infinity, dist = null;
    for (const d of st.dists) {
      if (!d.bounds) continue;
      const [x0, y0, x1, y1] = d.bounds;
      const dx = Math.max(x0 - x, 0, x - x1), dy = Math.max(y0 - y, 0, y - y1), dd = Math.hypot(dx, dy);
      if (dd < bd) { bd = dd; dist = d.id; }
    }
    // 箱に入る地区があればその地区の階だけ。どの箱にも入らなければ、地区の無い階と、箱がいちばん近い地区の階を合わせて候補にする
    // (地区の無い階 (Orison の「Convention Hall 付近」など) の上の点を、近い地区の階に寄せないように)
    if (dist) cand = st.floors.filter(f => st.groupDist[f.group] === dist || (bd > 0 && !st.groupDist[f.group]));
  }
  const edgeDist = (r) => {
    let inside = false, best = Infinity;
    for (let i = 0, j = r.length - 1; i < r.length; j = i++) {
      const [ax, ay] = r[j], [bx, by] = r[i];
      if ((by > y) !== (ay > y) && x < (ax - bx) * (y - by) / (ay - by) + bx) inside = !inside;
      const ex = ax - bx, ey = ay - by, l2 = ex * ex + ey * ey;
      const t = l2 > 0 ? Math.max(0, Math.min(1, ((x - bx) * ex + (y - by) * ey) / l2)) : 0;
      best = Math.min(best, Math.hypot(x - (bx + ex * t), y - (by + ey * t)));
    }
    return inside ? 0 : best;
  };
  // 階ごとの床の輪と、その xy の箱 (地図を読んだ後の最初に 1 回だけ作る)
  if (st.ringsMap !== st.map) {
    st.ringsMap = st.map; st.rings = {};
    for (const sec of st.map.sections || []) for (const fidr in (sec.floors || {})) for (const r of sec.floors[fidr]) {
      let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
      for (const [px, py] of r) { x0 = Math.min(x0, px); x1 = Math.max(x1, px); y0 = Math.min(y0, py); y1 = Math.max(y1, py); }
      (st.rings[fidr] = st.rings[fidr] || []).push({ r, x0, y0, x1, y1 });
    }
  }
  const pick = (floors) => {
    let best = null, bd = Infinity;
    for (const f of floors) {
      let h = Infinity;
      for (const o of st.rings[f.id] || []) {
        // 箱までの距離が今の最小より遠い輪は調べない
        if (Math.hypot(Math.max(o.x0 - x, 0, x - o.x1), Math.max(o.y0 - y, 0, y - o.y1)) >= h) continue;
        h = Math.min(h, edgeDist(o.r)); if (h === 0) break;
      }
      if (h === Infinity) continue;
      const v = z - f.z, dz = v < -0.6 ? -0.6 - v : v > 6 ? v - 6 : 0;
      const d = Math.hypot(h, dz);
      if (d < bd - 1e-9 || (Math.abs(d - bd) <= 1e-9 && best !== null && f.z > st.floors[st.fidx[best]].z)) { bd = d; best = f.id; }
    }
    return best;
  };
  return pick(cand) || (cand !== st.floors ? pick(st.floors) : null) || floorOfPoint(fid, z);
}
// 階 fid の床 (区画の床の輪) の、点 (x, y) にいちばん近い所 {x, y, d}。床が無ければ null (currentFloor と同じ輪の表を使う)
const CUR_FLOOR_GAP = 30;
function nearestFloorPoint(fid, x, y) {
  currentFloor(fid, x, y, 0);   // 輪の表 (st.rings) を作る
  let best = null;
  for (const o of (st.rings || {})[fid] || []) {
    const r = o.r;
    for (let i = 0, j = r.length - 1; i < r.length; j = i++) {
      const [ax, ay] = r[j], [bx, by] = r[i], ex = ax - bx, ey = ay - by, l2 = ex * ex + ey * ey;
      const t = l2 > 0 ? Math.max(0, Math.min(1, ((x - bx) * ex + (y - by) * ey) / l2)) : 0;
      const px = bx + ex * t, py = by + ey * t, d = Math.hypot(x - px, y - py);
      if (!best || d < best.d) best = { x: px, y: py, d };
    }
  }
  return best;
}
// 描くか (斜めの図は選んだ階の上下 WINDOW 階まで、真上の図は選んだ階だけ)
function visible(fid) {
  if (fid === null || fid === undefined || st.fidx[fid] === undefined) return false;
  if (st.mode === 'overview') return false;
  if (st.mode === 'plan') return distOf(fid) === st.district;
  if (st.top) return fid === st.sel;
  if (st.district) return distOf(fid) === st.district && Math.abs(st.stack[fid] - st.stack[st.sel]) <= WINDOW;
  const a = st.floors[st.fidx[fid]], b = st.floors[st.fidx[st.sel]];
  if (a.sg || b.sg) return a.sg === b.sg && Math.abs(a.sl - b.sl) <= WINDOW;
  if (a.group && b.group) return a.group === b.group && Math.abs(a.level - b.level) <= WINDOW;
  return Math.abs(st.fidx[fid] - st.fidx[st.sel]) <= WINDOW;
}
function dimClass(fid) { return (!st.top && st.mode !== 'plan' && fid !== st.sel) ? ' dim' : ''; }
// 局所の (x, y) と描く階の順番 → 図の (U, V)
function proj(x, y, idx) {
  const px = x, py = -y;
  if (st.top || st.mode === 'overview' || st.mode === 'plan') return [px, py];
  return [px - py * SHEAR, (px * A + py * B) * K - idx * st.gap];
}
// 図の (U, V) → 局所の (x, y) (その階の面の上)
function unproj(u, v, idx) {
  if (st.top || st.mode === 'overview' || st.mode === 'plan') return [u, -v];
  const vv = (v + idx * st.gap) / K;
  const py = (vv - A * u) / (A * SHEAR + B);
  const px = u + py * SHEAR;
  return [px, -py];
}
function toScreen(u, v) { return [u * st.vs + st.vx, v * st.vs + st.vy]; }
function toWorld(sx, sy) { return [(sx - st.vx) / st.vs, (sy - st.vy) / st.vs]; }

// ===== 読み込み =====

function load(m) {
  const map = m.map || {};
  // 区画の床の輪は [x, y, x, y, …] で来るので [[x, y], …] にする
  for (const s of map.sections || [])
    for (const fid in (s.floors || {}))
      s.floors[fid] = s.floors[fid].map(r => { const a = []; for (let i = 0; i + 1 < r.length; i += 2) a.push([r[i], r[i + 1]]); return a; });
  // 階段の外形の輪も [x, y, x, y, …] で来るので [[x, y], …] にする (外形の無い階段は outline が無い)
  for (const s of map.stairs || [])
    if (s.outline) s.outline = s.outline.map(r => { const a = []; for (let i = 0; i + 1 < r.length; i += 2) a.push([r[i], r[i + 1]]); return a; }).filter(r => r.length >= 3)
      // 輪の向きをそろえる (1 つの path にまとめて nonzero で塗るとき、向きの違う輪の重なりが穴にならないように)
      .map(r => { let a2 = 0; for (let i = 0; i < r.length; i++) { const p = r[i], q = r[(i + 1) % r.length]; a2 += p[0] * q[1] - q[0] * p[1]; } return a2 < 0 ? r.slice().reverse() : r; });
  st.map = map;
  st.floors = (map.floors || []).map((f, i) => ({ id: f.id, name: f.name || f.id, z: f.z, idx: i, group: f.group || '', level: f.level || 0,
    sg: f.stackGroup || '', sl: f.stackLevel || 0, listed: f.listed !== false }));
  st.fidx = {}; st.stack = {};
  // 重ねる順番: データの重ねる並び (stackGroup・stackLevel。行き先から決めたもの) があればその順、無ければ床のまとまりの中の番号、無ければ全体の順
  for (const f of st.floors) { st.fidx[f.id] = f.idx; st.stack[f.id] = f.sg ? f.sl : f.group ? f.level : f.idx; }
  // 階段でつながる高さの近い階は同じ段にする (sameTiers)。地区の中の並びは setDistrictFor で同じようにする
  const bySet = {};
  for (const f of st.floors) { const k = f.sg ? 's:' + f.sg : f.group ? 'g:' + f.group : ''; (bySet[k] = bySet[k] || []).push(f); }
  for (const k in bySet) sameTiers(bySet[k]);
  st.stack0 = Object.assign({}, st.stack);
  st.sel = (m.floor && st.fidx[m.floor] !== undefined) ? m.floor : (st.floors.length ? st.floors[st.floors.length - 1].id : null);
  st.top = !!m.top;
  st.selSec = null; st.route = null; st.cur = null; st.pin = null; st.dest = null; st.start = null; st.routeTrams = []; st.routeSpans = [];
  st.hlTerm = null;
  showSales(null);
  st.dists = map.districts || [];
  st.groupDist = {};
  for (const d of st.dists) for (const g of d.groups || []) st.groupDist[g] = d.id;
  st.gap = st.gapAll = computeGap();
  msgEl.textContent = st.floors.length ? '' : '床の形がありません';
  // 初めは、主要な地区のある都市でも階の地図 (選んだ階を濃く、斜めに重ねた図。地区のある都市ではその階の地区の階)。
  // 都市の全体図は［← 全体図へ戻る］で出す (利用者の指示 2026-11-01: Orbituary を開いて全体図が出たのを見て「全体が意味不明　2枚目スクショならわかる」)
  st.mode = 'floors'; setDistrictFor(st.sel); rebuild(); fitFloor(st.sel);
  // 読み終わったことをアプリへ知らせる (アプリは、知らせが来なければ「地図を表示できませんでした」と出してログに書く。StationMapPanel.LoadWatch)
  post({ type: 'loaded', floors: st.floors.length });
}

// 階の間隔: 各階の床の斜めの図での縦の広がりの中央値 × 0.9 + 15 (20 m 以上)。広がりは床の点の 5〜95 % の範囲 (遠くの小さな床で広がらないように)
function computeGap(floors) {
  const ranges = [];
  for (const f of floors || st.floors) {
    const vs = [];
    for (const s of st.map.sections || []) {
      const rings = (s.floors || {})[f.id];
      if (!rings) continue;
      for (const r of rings) for (const p of r) vs.push((p[0] * A - p[1] * B) * K);
    }
    if (vs.length < 3) continue;
    vs.sort((a, b) => a - b);
    const lo = vs[Math.floor(vs.length * 0.05)], hi = vs[Math.floor(vs.length * 0.95)];
    if (hi > lo) ranges.push(hi - lo);
  }
  if (!ranges.length) return 60;
  ranges.sort((a, b) => a - b);
  return Math.max(20, ranges[Math.floor(ranges.length / 2)] * 0.9 + 15);
}

// ===== 描く =====

// 階の板の形: その階の区画の床の点の凸包を 4 m 広げたもの (無ければ null)
function floorHull(fid) {
  const pts = [];
  for (const s of st.map.sections || []) {
    const rings = (s.floors || {})[fid];
    if (!rings) continue;
    for (const r of rings) for (const p of r) pts.push(p);
  }
  if (pts.length < 3) return null;
  pts.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  const cross = (o, a, b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
  const lower = [], upper = [];
  for (const p of pts) { while (lower.length >= 2 && cross(lower[lower.length - 2], lower[lower.length - 1], p) <= 0) lower.pop(); lower.push(p); }
  for (let i = pts.length - 1; i >= 0; i--) { const p = pts[i]; while (upper.length >= 2 && cross(upper[upper.length - 2], upper[upper.length - 1], p) <= 0) upper.pop(); upper.push(p); }
  upper.pop(); lower.pop();
  const hull = lower.concat(upper);
  if (hull.length < 3) return null;
  const cx = hull.reduce((a, p) => a + p[0], 0) / hull.length, cy = hull.reduce((a, p) => a + p[1], 0) / hull.length;
  return hull.map(p => { const dx = p[0] - cx, dy = p[1] - cy, d = Math.hypot(dx, dy) || 1; return [p[0] + dx / d * 4, p[1] + dy / d * 4]; });
}

function ringPath(r, idx) {
  let d = '';
  for (let i = 0; i < r.length; i++) {
    const [u, v] = proj(r[i][0], r[i][1], idx);
    d += (i ? 'L' : 'M') + u.toFixed(1) + ' ' + v.toFixed(1);
  }
  return d + 'Z';
}

// ===== 地区 (都市の全体図) =====
// 全体図: 主要な地区 (main) を箱 (bounds) の輪郭と大きな名前で、地区ごとに色を分けて出す。シャトルの線 (tracks) は向きの名前 (A・B) ごとの色と矢印。
// 道順で乗るシャトルの区間は、道筋のうち乗る所から降りる所まで (routeSpans) だけを太く、道順の出発地・目的地のある地区は縁を太くする。地区を押すとその地区の階の地図に入る。
// 地区の階の地図: その地区の床のまとまり (groups) の階だけを、下からの順に重ねる (選んだ階の上下 WINDOW 階)。［全体図へ戻る］で全体図へ
const DIST_COLORS = ['#d93025', '#1a73e8', '#188038', '#e37400', '#9334e6', '#00897b'];
const DIR_COLORS = { A: '#7b1fa2', B: '#00838f' };

function distOf(fid) {
  const i = st.fidx[fid];
  return i === undefined ? null : (st.groupDist[st.floors[i].group] || null);
}

// 同じ段 (2026-11-02、利用者の決定「重ねる順とぶつかるのは、高い方を上にずらして書く」「『隣と 1 m 以内なら同じ段』階段でつながっている場合のみに適応。
// エレベーターのみでつながっている場合は除外」): 重ねる並びの組 fs (同じ stackGroup・床のまとまり・地区) を重ねる順番 (st.stack) の順にたどり、
// 階段 (map.stairs の from・to) で今の段の階とつながっていて、高さの差が SAME_TIER_DZ 以内の階は、その段に入れる (段の上下の位置 st.stack を同じにする。連鎖する)。
// エレベーターだけでつながる階・何もつながらない階は同じ段にしない。並びの上で間に別の階がはさまる組 (例: RR_CRU_L5 の Lobby（Clinic）と Galleria) は、
// 並びの順を守って同じ段にせず、並びの順の段のまま (並びで上の階が上) にする (重ねる並びとぶつかる所は並びに従う。2026-11-02、利用者の決定「A」:
// fin42 では RR_CRU_L5 の Lobby（Clinic）と Galleria、RR_HUR_L3 の Lobby（Clinic）と Lobby（Galleria）。ずらしもしない)。
// 段にまとめた分だけ、後の階の位置を詰める (ほかの所の stackLevel のすき間はそのまま)。左の一覧の並び・［上の階］［下の階］は変えない (描く位置だけ)
const SAME_TIER_DZ = 1;
function sameTiers(fs) {
  if (!fs || fs.length < 2 || !st.map) return;
  const zOf = {};
  for (const f of st.floors) zOf[f.id] = f.z;
  const near = {};
  for (const s of st.map.stairs || []) {
    const a = s.from, b = s.to;
    if (a === b || zOf[a] === undefined || zOf[b] === undefined || Math.abs(zOf[a] - zOf[b]) > SAME_TIER_DZ) continue;
    near[a + '|' + b] = near[b + '|' + a] = true;
  }
  const sorted = fs.slice().sort((a, b) => (st.stack[a.id] - st.stack[b.id]) || (a.z - b.z));
  let shift = 0, tier = null, tierPos = 0, lastOrig = 0;
  for (const f of sorted) {
    const orig = st.stack[f.id];
    if (tier && tier.some(t => near[t.id + '|' + f.id])) { shift += orig - lastOrig; st.stack[f.id] = tierPos; tier.push(f); }
    else { tierPos = orig - shift; st.stack[f.id] = tierPos; tier = [f]; }
    lastOrig = orig;
  }
}

// 階の地図で見る地区を決め、重ねる順番を地区の中の順にする (地区の無い階なら元の順番)
function setDistrictFor(fid, dist) {
  st.stack = Object.assign({}, st.stack0);
  st.district = dist || (fid ? distOf(fid) : null);
  st.gap = st.gapAll;
  if (!st.district) return;
  // 地区の階の重ねる順: 全部の階にデータの重ねる並び (stackLevel) があればその順、無ければ高さの順
  const fs0 = st.floors.filter(f => st.groupDist[f.group] === st.district);
  const fs = fs0.every(f => f.sg) ? fs0.sort((a, b) => a.sl - b.sl) : fs0.sort((a, b) => a.z - b.z);
  fs.forEach((f, i) => { st.stack[f.id] = i; });
  sameTiers(fs);
  // 地区の階の広がりで間隔を決める (都市の全部の階の中央値では、大きな地区で板が大きく重なった)
  st.gap = computeGap(fs);
}

// 左上のボタン (viewer.html の #tools):
//   ［全体図］: 地区のある都市では都市の全体図へ (「← 全体図へ戻る」)。地区の無いステーションでは、階を斜めに重ねた図に戻して、重ねた階の全体が入るように寄せる。
//   ［地区の全体（真上）］: 地区の階の地図を見ているとき、その地区の平面図へ。
//   ［上の階］［下の階］: 階の地図で、重ねる並び (stackPeers) の 1 つ上・1 つ下の階へ。一覧に出す階 (listed) だけを数え、いちばん上・下では押せない
function updateBack() {
  const hasDist = st.dists.some(d => d.main);
  const b = document.getElementById('back');
  if (b) {
    b.style.display = st.map && st.floors.length && st.mode !== 'overview' ? 'block' : 'none';
    b.textContent = hasDist ? '← 全体図へ戻る' : '全体図';
  }
  const p = document.getElementById('plan');
  if (p) p.style.display = (st.mode === 'floors' && st.district) ? 'block' : 'none';
  const up = document.getElementById('floorUp'), dn = document.getElementById('floorDown');
  const showUD = st.mode === 'floors' && st.floors.length > 0;
  const n = showUD ? neighborFloors() : { up: null, down: null };
  if (up) { up.style.display = showUD ? 'block' : 'none'; up.disabled = !n.up; }
  if (dn) { dn.style.display = showUD ? 'block' : 'none'; dn.disabled = !n.down; }
}

// 選んだ階と同じ重ねる並びの階 (地区の中なら地区の階、データの重ねる並び (stackGroup) があればその並び、床のまとまり (group) があればそのまとまり、
// どれも無ければ全部の階) を、重ねる順番 (st.stack) の順に。一覧に出す階 (listed) だけ (選んでいる階は一覧に無くても入れる)
function stackPeers() {
  const sel = st.floors[st.fidx[st.sel]];
  if (!sel) return [];
  let peers;
  if (st.district) peers = st.floors.filter(f => distOf(f.id) === st.district);
  else if (sel.sg) peers = st.floors.filter(f => f.sg === sel.sg);
  else if (sel.group) peers = st.floors.filter(f => f.group === sel.group);
  else peers = st.floors.slice();
  return peers.filter(f => f.listed || f.id === st.sel).sort((a, b) => (st.stack[a.id] - st.stack[b.id]) || (a.z - b.z));
}

function neighborFloors() {
  const ps = stackPeers();
  const i = ps.findIndex(f => f.id === st.sel);
  if (i < 0) return { up: null, down: null };
  return { up: i + 1 < ps.length ? ps[i + 1].id : null, down: i > 0 ? ps[i - 1].id : null };
}

function moveFloor(dir) {
  const n = neighborFloors();
  const fid = dir > 0 ? n.up : n.down;
  if (!fid) return;
  st.sel = fid;
  st.selSec = null;
  setDistrictFor(fid);
  rebuild();
  fitFloor(fid);
  post({ type: 'floor', id: fid });
}

// 地区の無いステーションの［全体図］: 斜めに重ねた図にして (この階だけ (真上から) をやめる)、描いている階の板の全体が入るように寄せる
function showWhole() {
  if (st.dists.some(d => d.main)) { showOverview(); return; }
  if (st.top) { st.top = false; post({ type: 'mode', top: false }); }
  st.selSec = null;
  rebuild();
  const pts = [];
  for (const f of st.floors) {
    if (!visible(f.id)) continue;
    const h = floorHull(f.id);
    if (h) for (const p of h) pts.push(proj(p[0], p[1], st.stack[f.id]));
  }
  if (!fitPoints(pts, 60)) fitFloor(st.sel);
}

function distColor(d) { return DIST_COLORS[Math.max(0, st.dists.indexOf(d)) % DIST_COLORS.length]; }

function routeDists() {
  const r = new Set();
  if (st.route && st.route.length) for (const p of [st.route[0], st.route[st.route.length - 1]]) { const d = distOf(p[3]); if (d) r.add(d); }
  return r;
}

function drawOverview() {
  const lines = el('g', { id: 'lines' }, world);
  const hot = routeDists();
  for (const d of st.dists.filter(d => d.main)) {
    const col = distColor(d);
    let cu, cv;
    if (d.bounds) {
      const [x0, y0, x1, y1] = d.bounds;
      const c = [[x0, y0], [x1, y0], [x1, y1], [x0, y1]];
      el('path', { class: 'district' + (hot.has(d.id) ? ' hot' : ''), d: ringPath(c, 0), style: `fill:${col};stroke:${col}`, 'data-k': 'district', 'data-id': d.id }, world);
      [cu, cv] = proj((x0 + x1) / 2, (y0 + y1) / 2, 0);
    }
    if (d.center) [cu, cv] = proj(d.center[0], d.center[1], 0);
    if (cu === undefined) continue;
    addAt(cu, cv, g => {
      el('circle', { r: 9, class: 'dist-dot', style: `fill:${col}`, 'data-k': 'district', 'data-id': d.id }, g);
      el('text', { x: 12, y: 7, class: 'dname', style: `fill:${col}` }, g).textContent = d.name;
    }, 'mk dmark', { 'data-k': 'district', 'data-id': d.id }, d.name, 1);
  }
  // シャトルの線と矢印
  (st.map.tracks || []).forEach((t, i) => {
    const pts = t.pts || [];
    if (pts.length < 2) return;
    const uv = pts.map(p => proj(p[0], p[1], 0));
    const d = uv.map((q, k) => (k ? 'L' : 'M') + q[0].toFixed(1) + ' ' + q[1].toFixed(1)).join('');
    const col = DIR_COLORS[t.dir] || '#5f6368';
    // 乗った区間 (routeSpans) のある道筋は、線はふだんの見た目で描き、乗る所から降りる所までだけを下で重ねて太くする。
    // routeSpans の無い知らせ (前の版のアプリ) では、今までどおり道筋の全体を太くする
    const onRoute = st.routeTrams.includes(i) && !st.routeSpans.some(s => s[0] === i);
    if (onRoute) el('path', { class: 'route-case', d }, lines);
    el('path', { class: 'ov-track' + (onRoute ? ' on-route' : ''), d, style: onRoute ? '' : `stroke:${col}` }, lines);
    // 矢印: 線の長さの 1/3 と 2/3 の所に、進む向きの三角
    const len = []; let tot = 0;
    for (let k = 1; k < uv.length; k++) { tot += Math.hypot(uv[k][0] - uv[k - 1][0], uv[k][1] - uv[k - 1][1]); len.push(tot); }
    for (const f of [1 / 3, 2 / 3]) {
      const target = tot * f;
      let k = len.findIndex(x => x >= target); if (k < 0) k = len.length - 1;
      const a = uv[k], b = uv[k + 1];
      const prev = k ? len[k - 1] : 0, seg = len[k] - prev || 1, r = (target - prev) / seg;
      const u = a[0] + (b[0] - a[0]) * r, v = a[1] + (b[1] - a[1]) * r;
      const ang = Math.atan2(b[1] - a[1], b[0] - a[0]) * 180 / Math.PI;
      addAt(u, v, g => {
        el('path', { d: 'M8 0L-6 -6L-3 0L-6 6Z', class: 'dir-arrow', transform: `rotate(${ang.toFixed(1)})`, style: `fill:${onRoute ? '#1a73e8' : col}` }, g);
      }, 'mk arrow');
    }
    // 向きの名前 (A 回り・B 回り) を線の真ん中の矢印の横に
  });
  // 乗った区間: 道筋のうち、乗る乗り場から降りる乗り場まで (道筋の点の並びの向きに進む) を道順の色で太く
  for (const s of st.routeSpans) {
    const t = (st.map.tracks || [])[s[0]];
    if (!t || !t.pts || t.pts.length < 2) continue;
    const part = trackPart(t.pts, [s[1], s[2]], [s[3], s[4]]);
    if (part.length < 2) continue;
    const d = part.map((p, k) => { const q = proj(p[0], p[1], 0); return (k ? 'L' : 'M') + q[0].toFixed(1) + ' ' + q[1].toFixed(1); }).join('');
    el('path', { class: 'route-case', d }, lines);
    el('path', { class: 'ov-track on-route', d }, lines);
  }
  // 向きの凡例
  const dirs = [...new Set((st.map.tracks || []).map(t => t.dir).filter(x => x))];
  const leg = document.getElementById('legend');
  if (leg) leg.innerHTML = dirs.map(x => `<span style="color:${DIR_COLORS[x] || '#5f6368'}">━▶ シャトル（${x}回り）</span>`).join('　');
}

// 道筋 pts ([[x, y], …]) の上の、点 a に近い所から点 b に近い所までの折れ線 (点の並びの向きに進む)。
// b が a より手前にあるとき: 道筋が環 (始めと終わりの点が TRACK_LOOP_GAP m 以内) なら終わりから始めへ回り込む。環でなければ a と b の間 (手前から先へ)
const TRACK_LOOP_GAP = 1;
function trackPart(pts, a, b) {
  const at = p => {
    let best = null;
    for (let k = 1; k < pts.length; k++) {
      const p0 = pts[k - 1], dx = pts[k][0] - p0[0], dy = pts[k][1] - p0[1], l2 = dx * dx + dy * dy;
      const t = l2 > 0 ? Math.max(0, Math.min(1, ((p[0] - p0[0]) * dx + (p[1] - p0[1]) * dy) / l2)) : 0;
      const q = [p0[0] + t * dx, p0[1] + t * dy], dd = Math.hypot(q[0] - p[0], q[1] - p[1]);
      if (!best || dd < best.d) best = { d: dd, k, t, q };
    }
    return best;
  };
  const pa = at(a), pb = at(b);
  if (!pa || !pb) return [];
  const before = (x, y) => x.k < y.k || (x.k === y.k && x.t <= y.t);
  const n = pts.length;
  const loop = Math.hypot(pts[0][0] - pts[n - 1][0], pts[0][1] - pts[n - 1][1]) <= TRACK_LOOP_GAP;
  // 点 x から点 y まで (x が手前) の頂点の並び。区間 k は pts[k-1]〜pts[k]
  const run = (x, y) => [x.q, ...pts.slice(x.k, y.k), y.q];
  if (before(pa, pb)) return run(pa, pb);
  if (loop) return [...run(pa, { k: n - 1, t: 1, q: pts[n - 1] }), ...run({ k: 1, t: 0, q: pts[0] }, pb).slice(1)];
  return run(pb, pa);
}

function fitOverview() {
  const pts = [];
  for (const d of st.dists.filter(d => d.main)) {
    if (d.bounds) { pts.push(proj(d.bounds[0], d.bounds[1], 0)); pts.push(proj(d.bounds[2], d.bounds[3], 0)); }
    if (d.center) pts.push(proj(d.center[0], d.center[1], 0));
  }
  for (const t of st.map.tracks || []) for (const p of t.pts || []) pts.push(proj(p[0], p[1], 0));
  fitPoints(pts, 200);
}

// 地区に入る: まず地区を真上から見た平面図 (地区のすべての階の床・区画・店・シャトルの駅を重ねる。島の形が分かるように)。
// 区画を押すか、階を選ぶと、その階の斜めに重ねた図 (階の地図) にする。［地区の全体（真上）］で平面図に戻る
function enterDistrict(id) {
  if (!st.dists.some(d => d.id === id)) return;
  st.mode = 'plan';
  setDistrictFor(null, id);
  if (distOf(st.sel) !== id) {
    const cnt = {};
    for (const s2 of st.map.shops || []) if (distOf(s2.floor) === id) cnt[s2.floor] = (cnt[s2.floor] || 0) + 1;
    st.sel = Object.keys(cnt).sort((x, y) => cnt[y] - cnt[x])[0] || (st.floors.filter(f => st.groupDist[f.group] === id).sort((x, y) => x.z - y.z)[0] || {}).id || st.sel;
  }
  rebuild();
  fitPlan(id);
  post({ type: 'district', id });
}

function fitPlan(id) {
  const d = st.dists.find(x => x.id === id);
  const pts = [];
  for (const sec of st.map.sections || [])
    for (const fid in sec.floors || {}) if (distOf(fid) === id) for (const r of sec.floors[fid]) for (const q of r) pts.push(proj(q[0], q[1], 0));
  if (!pts.length && d && d.bounds) { pts.push(proj(d.bounds[0], d.bounds[1], 0)); pts.push(proj(d.bounds[2], d.bounds[3], 0)); }
  fitPoints(pts, 60);
}

function showPlan() { if (st.district) enterDistrict(st.district); }

function showOverview() {
  st.mode = 'overview';
  setDistrictFor(null);
  rebuild();
  fitOverview();
  post({ type: 'overview' });
}

function rebuild() {
  world.textContent = '';
  overlay.textContent = '';
  st.ovl = [];
  st.routeUV = [];
  updateBack();
  const leg = document.getElementById('legend');
  if (leg && st.mode !== 'overview') leg.innerHTML = '';
  if (!st.map) return;
  if (st.mode === 'overview') { drawOverview(); applyView(); return; }
  if (!st.map) return;
  const floorsToDraw = st.floors.filter(f => visible(f.id));
  const groups = {};
  for (const f of floorsToDraw) {
    const g = el('g', { class: 'floor' + (f.id === st.sel ? ' sel' : ' dim'), 'data-f': f.id });
    groups[f.id] = g;
    const c = st.mode === 'plan' ? null : floorHull(f.id);
    if (c) {
      el('path', { class: 'slab', d: ringPath(c, st.stack[f.id]) }, g);
      // 階の名前 (板の左の角)。選んだ階は、階段でつながる階の名前 (stairPartners) を後ろに添える (ほかの階の名札は長くしない。名札どうしが重なって隠れやすくなるため)
      let best = null;
      for (const p of c) { const q = proj(p[0], p[1], st.stack[f.id]); if (!best || q[0] < best[0]) best = q; }
      const fname = f.id === st.sel ? f.name + stairPartners(f.id) : f.name;
      // 階の名札 (2026-11-02、利用者の指示「階も区域も今の文字が見やすいとは思わない」): 角の丸い札の中に書く。選んだ階は濃い札に白い太字 (14px)、
      // ほかの階は白い札に灰色の枠と字 (12px)。札の位置・大きさは placeLabels・setLabel が合わせる (o.pill)
      const selF = f.id === st.sel;
      addAt(best[0], best[1], t => {
        el('rect', { class: 'pill-bg' + (selF ? ' sel' : ''), rx: 4, ry: 4, visibility: 'hidden' }, t);
        const tx = el('text', { x: -6, y: 4, 'text-anchor': 'end', class: 'pill' + (selF ? ' sel bold' : ''), 'data-fs': selF ? 14 : 12 }, t);
        tx.textContent = fname;
      }, 'mk flabel' + (selF || st.top ? '' : ' dim'), null, fname, selF ? 1 : 5, 'end');
      // 選んだ階の名札は、板の左の角が画面の外でも見えるよう、板の形を持たせておく (applyView の visibleAnchor)
      if (f.id === st.sel) st.ovl[st.ovl.length - 1].hullUV = c.map(p => proj(p[0], p[1], st.stack[f.id]));
    }
  }
  // 区画の床
  for (const s of st.map.sections || []) {
    for (const fid in (s.floors || {})) {
      const g = groups[fid];
      if (!g) continue;
      const idx = st.stack[fid];
      let d = '';
      for (const r of s.floors[fid]) if (r.length >= 3) d += ringPath(r, idx);
      if (!d) continue;
      const cls = 'sec' + (s.fallback ? ' fallback' : '') + (s.id === st.selSec ? ' selsec' : '')
        + (st.dest && st.dest.kind === 'section' && st.dest.id === s.id ? ' dest' : '');
      // 区画の形は床の板ごとの凸包の輪 (重なることがある。2026-11-02、利用者の決定「A」)。階段の形と同じく、先に全部の輪の枠を太めに描き (.sec-edge)、
      // その上に全部の輪の塗りを枠なしで描く (.sec-fill)。板どうしの境目は塗りに隠れ、外側の縁だけが残る
      // 枠の見える外側の縁を押しても区画を選べるよう、枠にも data-k を付ける
      const sg = el('g', { class: cls }, g);
      el('path', { class: 'sec-edge', d, 'data-k': 'sec', 'data-id': s.id, 'data-f': fid }, sg);
      const p = el('path', { class: 'sec-fill', d, 'data-k': 'sec', 'data-id': s.id, 'data-f': fid }, sg);
      el('title', null, p).textContent = s.name || s.id;
    }
  }
  // 選んだ区画・目的地の区画は、太い枠が後から描く隣の区画の塗りに隠れないよう、その階のほかの区画の後ろ (上) に移す
  for (const fid in groups) for (const n of groups[fid].querySelectorAll('g.sec.selsec, g.sec.dest')) n.parentNode.appendChild(n);
  // 階段の外形 (2026-11-02、利用者の指示「階段の形がわかるなら、階段アイコンでなく階段を枠で囲んでフロア同様に扱う」): 外形 (outline) のある階段は、
  // 両端の階 (下の端 bottom の階と上の端 top の階) の両方に、外形の輪を枠で描く。下の階は実線、上の階は破線 (viewer.css の .stairshape・.stairshape.upper)。
  // 外形の無い階段は今までどおり階段のアイコン (drawMarkers)。道順のグラフ・道順は変えない。
  // 階段の板 (輪) は 1 つの形として見せる (2026-11-02、親の指示「板の境目が格子に見える。1 つの形にし、外側の縁だけを出す」):
  // 先に全部の輪の枠を太めに描き (.stairshape-edge)、その上に全部の輪の塗りを枠なし・不透明で描く (.stairshape-fill)。内側の境目は塗りに隠れ、外側の縁だけが残る
  // (輪の和の形だけを描く。形は作らない)。上の端の階も塗りは不透明にし、破線の縁と薄い色で分ける。
  // 同じ階で重なる・触れる別々の階段も 1 つの形にする (2026-11-02、利用者の答え「B が好ましい」): 階ごと・端ごと (下の端・上の端は別の組) に、
  // その階の全部の階段の枠をまとめて先に描き、塗りをまとめて上に描く。重なる・触れる階段どうしの境目は塗りに隠れ、離れている階段は別の形のまま残る。
  // 選んだ階の階段の形は、名札がよける (st.stairBoxes。placeLabels)
  st.stairBoxes = [];
  const stairPaths = {};   // 階の Id → { lower: d, upper: d }
  if (st.mode !== 'plan')
    for (const s of st.map.stairs || []) {
      if (!s.outline || !s.outline.length) continue;
      for (const [p, f, upper] of [[s.bottom, s.from, false], [s.top, s.to, true]]) {
        const fid = floorOfPoint(f, p[2]);
        const g = fid !== null ? groups[fid] : null;
        if (!g) continue;
        let d = '';
        for (const r of s.outline) d += ringPath(r, st.stack[fid]);
        if (!d) continue;
        const sp = stairPaths[fid] = stairPaths[fid] || { lower: '', upper: '' };
        if (upper) sp.upper += d; else sp.lower += d;
        if (fid === st.sel) {
          let u0 = Infinity, v0 = Infinity, u1 = -Infinity, v1 = -Infinity;
          for (const r of s.outline) for (const q of r) { const [u, v] = proj(q[0], q[1], st.stack[fid]); u0 = Math.min(u0, u); v0 = Math.min(v0, v); u1 = Math.max(u1, u); v1 = Math.max(v1, v); }
          st.stairBoxes.push([u0, v0, u1, v1]);
        }
      }
    }
  for (const fid in stairPaths) {
    for (const upper of [false, true]) {
      const d = upper ? stairPaths[fid].upper : stairPaths[fid].lower;
      if (!d) continue;
      const sg = el('g', { class: 'stairshape' + (upper ? ' upper' : '') }, groups[fid]);
      el('path', { class: 'stairshape-edge', d }, sg);
      el('path', { class: 'stairshape-fill', d }, sg);
    }
  }
  // 低い階から順に重ねる。選んだ階は最後 (いちばん上)
  // 選んだ階と同じ段 (sameTiers) の階は、選んだ階の板 (白い凸包) の下に隠れないよう、選んだ階の板だけを先に描き、その上に同じ段の階 (薄く)、
  // いちばん上に選んだ階の床・区画を描く
  const mates = floorsToDraw.filter(f => f.id !== st.sel && st.stack[f.id] === st.stack[st.sel]);
  for (const f of floorsToDraw) if (f.id !== st.sel && !mates.includes(f)) world.appendChild(groups[f.id]);
  if (groups[st.sel] && mates.length) {
    const slab = groups[st.sel].querySelector('path.slab');
    if (slab) el('g', { class: 'floor sel' }, world).appendChild(slab);
  }
  for (const f of mates) world.appendChild(groups[f.id]);
  if (groups[st.sel]) world.appendChild(groups[st.sel]);

  const lines = el('g', { id: 'lines' }, world);
  drawStairLinks(lines);
  drawTracks(lines);
  drawRoute(el('g', { id: 'routeLayer' }, world));
  drawMarkers();
  drawSectionNames();
  drawSectionLabel();
  applyView();
}

// 区画の名札 (2026-11-02、利用者の指示「階も区域も今の文字が見やすいとは思わない」): 選ばなくても、選んだ階の区画 (データが一覧に出す区画 listed で名前のあるもの) の名前を
// 区画の中に出す。同じ名前の区画がその階に 2 つ以上あれば、床の輪のいちばん大きいもの 1 つにだけ出す。位置はいちばん大きい輪の頂点の平均 (drawSectionLabel と同じ)。
// 字は 12px の太め・濃い灰色に白い縁取り (枠は付けない。床の上で区画の形を隠さないように)。重なりは placeLabels でよけ、置けなければ出さない (まとめた名札にしない)。
// 選んでいる区画は drawSectionLabel が出すので、ここでは出さない。全体図・地区の平面図では出さない
function drawSectionNames() {
  if (st.mode !== 'floors' || !st.sel || !visible(st.sel)) return;
  const best = {};
  for (const s of st.map.sections || []) {
    if (!s.listed || !s.name || s.id === st.selSec) continue;
    const rings = (s.floors || {})[st.sel];
    if (!rings) continue;
    for (const r of rings) {
      if (r.length < 3) continue;
      let a = 0;
      for (let i = 0; i < r.length; i++) { const p = r[i], q = r[(i + 1) % r.length]; a += p[0] * q[1] - q[0] * p[1]; }
      a = Math.abs(a) / 2;
      const b = best[s.name];
      if (!b || a > b.a) best[s.name] = { a, r, s };
    }
  }
  const sel = (st.map.sections || []).find(x => x.id === st.selSec);
  for (const name in best) {
    if (sel && sel.name === name) continue;
    const { r } = best[name];
    const cx = r.reduce((a, p) => a + p[0], 0) / r.length, cy = r.reduce((a, p) => a + p[1], 0) / r.length;
    const mk = addMarker(cx, cy, st.sel, g => {
      el('text', { x: 0, y: 0, 'text-anchor': 'middle', class: 'secname' }, g).textContent = name;
    }, 'mk flabel secname-mk', null, name, 7);
    const o = mk ? st.ovl[st.ovl.length - 1] : null;
    if (o) { o.kind = 'section'; o.noCluster = true; o.fs = 12; o.bold = true; }
  }
}

function drawStairLinks(g) {
  if (st.top || st.mode === 'plan') return;
  for (const s of st.map.stairs || []) {
    const fa = floorOfPoint(s.from, s.bottom[2]), fb = floorOfPoint(s.to, s.top[2]);
    // 選んだ階に端のある階段だけ (都市では階段が多く、全部結ぶと縦の線で埋まる)
    if (fa === null || fb === null || fa === fb || !visible(fa) || !visible(fb) || (fa !== st.sel && fb !== st.sel)) continue;
    const a = proj(s.bottom[0], s.bottom[1], st.stack[fa]), b = proj(s.top[0], s.top[1], st.stack[fb]);
    el('path', { class: 'stairlink', d: `M${a[0]} ${a[1]}L${b[0]} ${b[1]}` }, g);
  }
}

// シャトルの線路: 道筋の点 (map.tracks の各区間 [[x, y], …]) を、選んだ階の面の上に描く
// (路線は地区をまたいで長く、どの階を選んでいても輪が分かるように。高さは使わない)
function drawTracks(g) {
  const idx = st.stack[st.sel];
  if (idx === undefined) return;
  for (const t of st.map.tracks || []) {
    const pts = t.pts || [];
    if (pts.length < 2) continue;
    let d = '';
    pts.forEach((p, i) => { const q = proj(p[0], p[1], idx); d += (i ? 'L' : 'M') + q[0].toFixed(1) + ' ' + q[1].toFixed(1); });
    el('path', { class: 'track-bed', d }, g);
    el('path', { class: 'track-tie', d }, g);
  }
}

function drawRoute(g) {
  const pts = st.route;
  if (!pts || pts.length < 2) return;
  // 同じ階の続く点は 1 本の線、階が変わるところは面の間を結ぶ破線 (片方の階を描かないときは、描く側に「↑ / ↓ 行き先の階」)
  const segs = [];
  let cur = null;
  for (const p of pts) {
    const fid = floorOfPoint(p[3], p[2]);
    if (cur && cur.fid === fid) { cur.pts.push(p); continue; }
    if (cur) segs.push({ up: true, a: cur.pts[cur.pts.length - 1], fa: cur.fid, b: p, fb: fid });
    cur = { fid, pts: [p] };
    segs.push(cur);
  }
  const paths = [];
  st.routeUV = [];
  for (const s of segs) {
    if (s.up) {
      if (visible(s.fa) && visible(s.fb)) {
        const a = proj(s.a[0], s.a[1], st.stack[s.fa]), b = proj(s.b[0], s.b[1], st.stack[s.fb]);
        paths.push({ cls: 'route-up', d: `M${a[0]} ${a[1]}L${b[0]} ${b[1]}` });
      }
      continue;
    }
    if (!visible(s.fid) || s.pts.length < 2) continue;
    let d = '';
    const uv = [];
    s.pts.forEach((p, i) => { const q = proj(p[0], p[1], st.stack[s.fid]); uv.push(q); d += (i ? 'L' : 'M') + q[0].toFixed(1) + ' ' + q[1].toFixed(1); });
    st.routeUV.push(uv);
    paths.push({ cls: 'route', d });
  }
  for (const p of paths) if (p.cls === 'route') el('path', { class: 'route-case', d: p.d }, g);
  for (const p of paths) el('path', { class: p.cls, d: p.d }, g);
  for (const s of segs) {
    if (!s.up || visible(s.fa) === visible(s.fb)) continue;
    const leaving = visible(s.fa);
    const at = leaving ? s.a : s.b, here = leaving ? s.fa : s.fb, other = leaving ? s.fb : s.fa;
    const goingUp = st.floors[st.fidx[s.fb]].z > st.floors[st.fidx[s.fa]].z;
    const name = (st.floors[st.fidx[other]] || {}).name || '';
    const label = leaving ? (goingUp ? '↑ ' : '↓ ') + name : name + ' から';
    addMarker(at[0], at[1], here, mk => {
      el('circle', { r: 5, class: 'start-ring', style: 'stroke:#1a73e8' }, mk);
      const t = el('text', { x: 8, y: 4, class: 'bold' }, mk);
      t.textContent = label;
    }, 'mk', null, label, 0);
  }
}

// 画面の大きさで描く印を (U, V) に置く。label があれば名前の札として置き場所を決める (placeLabels。pri が小さいほど先に置く)。
// 札は build が作った text のうち中身が label のもの。引き出し線 (line.leader) は印の中に足し、札をずらしたときだけ出す
function addAt(u, v, build, cls, attrs, label, pri, anchor) {
  const g = el('g', Object.assign({ class: cls || 'mk' }, attrs || {}), overlay);
  build(g);
  const o = { el: g, u, v, label: label || '', pri: pri === undefined ? 9 : pri, anchor: anchor || 'start', tx: null, leader: null, kind: '' };
  if (o.label) {
    o.tx = [...g.querySelectorAll('text')].find(t => t.textContent === o.label) || null;
    if (o.tx) {
      o.leader = el('line', { class: 'leader', x1: 0, y1: 0, x2: 0, y2: 0, visibility: 'hidden' });
      g.insertBefore(o.leader, g.firstChild);
      o.big = o.tx.classList.contains('seclabel');
      o.huge = o.tx.classList.contains('dname');
      o.bold = o.tx.classList.contains('bold');
    }
    const k = attrs && attrs['data-k'];
    o.kind = k === 'shop' ? 'shop' : k === 'transit' ? 'transit' : k === 'terminal' ? 'terminal' : k === 'district' ? 'district' : (cls || '').includes('flabel') ? 'floor' : 'other';
    // 札の付いた名札 (階の名前。viewer.css の .pill-bg): 字の大きさ (data-fs) と背景の四角
    if (o.tx && o.tx.classList.contains('pill')) { o.pill = true; o.fs = +(o.tx.getAttribute('data-fs') || 13); o.bg = g.querySelector('rect.pill-bg'); }
  }
  st.ovl.push(o);
  return g;
}
function addMarker(x, y, fid, build, cls, attrs, label, pri) {
  const idx = st.stack[fid];
  if (idx === undefined) return null;
  const [u, v] = proj(x, y, idx);
  return addAt(u, v, build, cls, attrs, label, pri);
}

function isDest(kind, i) { return st.dest && st.dest.kind === kind && st.dest.index === i; }
function isStart(kind, i) { return st.start && st.start.kind === kind && st.start.index === i; }

function drawMarkers() {
  const m = st.map;
  // 階段: 下と上の端に段の印。選んだ階にある端だけ (ほかの階の階段の印は並べない。地区の平面図では出さない)
  for (const s of m.stairs || []) {
    if (st.mode === 'plan') break;
    if (s.outline && s.outline.length) continue;   // 外形のある階段は枠で描く (rebuild)。アイコンは出さない
    for (const [p, f, up] of [[s.bottom, s.from, true], [s.top, s.to, false]]) {
      const fid = floorOfPoint(f, p[2]);
      if (fid === null || fid !== st.sel) continue;
      addMarker(p[0], p[1], fid, g => {
        el('rect', { x: -7, y: -7, width: 14, height: 14, rx: 2, class: 'stair-bg' }, g);
        el('path', { class: 'stair-icon', d: 'M-5 5H-2V1H1V-3H5' }, g);
        const other = up ? s.to : s.from;
        el('title', null, g).textContent = '階段 (' + (up ? '上: ' : '下: ') + floorName(other) + ')';
      }, 'mk' + dimClass(fid));
    }
  }
  // 乗り場 (エレベーター・シャトル)
  for (const t of m.transit || []) {
    const fid = floorOfPoint(t.floor, t.pos[2]);
    if (!visible(fid)) continue;
    const noride = t.kind === 'elevator-noride';
    if (st.mode === 'plan' && t.kind !== 'tram') continue;   // 平面図のエレベーターは、下の drawPlanElevators で乗り場の並びごとに 1 つにまとめる
    const i = t.i, label = t.name || '';
    const hot = isDest('transit', i) || isStart('transit', i);
    addMarker(t.pos[0], t.pos[1], fid, g => {
      if (noride) {
        // 乗らないエレベーター (位置だけ): 矢印の無い薄い四角
        el('rect', { x: -6, y: -7, width: 12, height: 14, rx: 2, class: 'elev-box noride' }, g);
      } else if (t.kind === 'elevator') {
        el('rect', { x: -7, y: -8, width: 14, height: 16, rx: 2, class: 'elev-box' }, g);
        el('path', { class: 'elev-arrow', d: 'M0 -6L4 -1H-4Z M0 6L4 1H-4Z' }, g);
      } else {
        el('rect', { x: -9, y: -7, width: 18, height: 14, rx: 3, class: 'tram-box' }, g);
        el('text', { x: 0, y: 3.5, 'text-anchor': 'middle', class: 'tram-text' }, g).textContent = 'S';
      }
      if (isDest('transit', i)) el('circle', { r: 12, class: 'dest-ring' }, g);
      if (isStart('transit', i)) el('circle', { r: 12, class: 'start-ring' }, g);
      if (label) el('text', { x: 10, y: 4, class: 'lbl' + (hot ? ' bold' : '') }, g).textContent = label;
      el('title', null, g).textContent = (noride ? 'エレベーター（位置だけ。道順では乗りません）' : t.kind === 'elevator' ? 'エレベーター' : 'シャトル乗り場') + (label ? ': ' + label : '');
    }, 'mk' + (hot ? '' : dimClass(fid)), { 'data-k': 'transit', 'data-i': i }, (hot || !dimClass(fid)) ? label : '', hot ? 0 : (t.kind === 'tram' ? 3 : 4));
  }
  if (st.mode === 'plan') drawPlanElevators();
  // 店 (点と名前)。階の無い店 (floor が空) はどの階の図にも描かない (高さのいちばん近い階に置くと、実際とは違う場所を見せるため。リストには出る)
  for (const s of m.shops || []) {
    if (!s.floor) continue;
    const fid = floorOfPoint(s.floor, s.pos[2]);
    if (!visible(fid)) continue;
    const i = s.i, name = s.name || '';
    const hot = isDest('shop', i) || isStart('shop', i);
    addMarker(s.pos[0], s.pos[1], fid, g => {
      el('circle', { r: 4.5, class: 'shop-dot' }, g);
      if (isDest('shop', i)) el('circle', { r: 10, class: 'dest-ring' }, g);
      if (isStart('shop', i)) el('circle', { r: 10, class: 'start-ring' }, g);
      el('text', { x: 7, y: 4, class: 'lbl' + (hot ? ' bold' : '') }, g).textContent = name;
      el('title', null, g).textContent = name ? '店: ' + name : '店';   // 名前の無い店 (ゲームの名前がデータに無い) は名札を出さない
    }, 'mk' + (hot ? '' : dimClass(fid)), { 'data-k': 'shop', 'data-i': i }, (hot || !dimClass(fid)) ? name : '', hot ? 0 : 2);
  }
  // 端末 (2026-11-02、利用者の指示「マークは端末のところがいい」): 端末の位置 (隣り合う同じ種類の端末はまとめて 1 つ。StationMapModel.TerminalGroups) に、
  // 種類の色の丸と、その上にゲームのアイコン (白一色の svg。アプリが {type:'icons'} で送る。無ければ種類の言葉の頭の 1 字)。
  // 名札は種類の言葉 (ゲームの訳。termKinds) と、まとめた数 (×N)。押すと目的地にする (店・乗り場と同じ)。左の一覧で選んだまとまりは橙の輪で強調する
  const kinds = m.termKinds || {};
  for (const t of m.terminals || []) {
    if (!t.floor) continue;
    const fid = floorOfPoint(t.floor, t.pos[2]);
    if (!visible(fid)) continue;
    const info = kinds[t.kind] || { label: t.kind, color: '#5f6368' };
    const i = t.i, label = info.label + (t.n > 1 ? ' ×' + t.n : '');
    const hl = st.hlTerm === i;
    const hot = isDest('terminal', i) || isStart('terminal', i) || hl;
    addMarker(t.pos[0], t.pos[1], fid, g => {
      if (hl) el('circle', { r: 17, class: 'hl-ring' }, g);
      el('circle', { r: 9, class: 'term-dot', style: `fill:${info.color}` }, g);
      const ic = st.icons && st.icons[t.kind];
      if (ic) el('image', { href: ic, x: -6.5, y: -6.5, width: 13, height: 13, class: 'term-icon' }, g);
      else el('text', { x: 0, y: 4, 'text-anchor': 'middle', class: 'term-letter' }, g).textContent = (info.label || '?')[0];
      if (isDest('terminal', i)) el('circle', { r: 13, class: 'dest-ring' }, g);
      if (isStart('terminal', i)) el('circle', { r: 13, class: 'start-ring' }, g);
      el('text', { x: 12, y: 4, class: 'lbl' + (hot ? ' bold' : '') }, g).textContent = label;
      el('title', null, g).textContent = '端末: ' + label;
    }, 'mk' + (hot ? '' : dimClass(fid)), { 'data-k': 'terminal', 'data-i': i }, (hot || !dimClass(fid)) ? label : '', hot ? 0 : 3);
  }
  // 現在位置 (/showlocation。赤い点)
  if (st.cur) {
    const fid = st.cur.fid;
    if (visible(fid)) addMarker(st.cur.pos[0], st.cur.pos[1], fid, g => {
      el('circle', { r: 14, class: 'cur-halo' }, g);
      el('circle', { r: 6, class: 'cur-dot' }, g);
      el('text', { x: 10, y: -8, class: 'bold' }, g).textContent = st.cur.label || '現在位置';
    }, 'mk', null, st.cur.label || '現在位置', 0);
  }
  // 手で立てたピン (しずくの形。/showlocation の赤い点とは別の見た目)
  if (st.pin && visible(st.pin.floor)) addMarker(st.pin.pos[0], st.pin.pos[1], st.pin.floor, g => {
    el('path', { class: 'pin-body', d: 'M0 0C-3 -7 -9 -11 -9 -18A9 9 0 1 1 9 -18C9 -11 3 -7 0 0Z' }, g);
    el('circle', { cx: 0, cy: -18, r: 3, class: 'pin-hole' }, g);
    el('text', { x: 11, y: -16, class: 'bold' }, g).textContent = 'ピン';
  }, 'mk', null, 'ピン', 0);
}

// 地区の平面図のエレベーター: 地区の階のエレベーターの乗り場 (データが残したもの) を、同じ路線で PLAN_ELEV_JOIN m 以内の並び (エレベーターの並び・同じ縦穴の各階の乗り場) ごとに、
// 階に関係なく 1 つの印にまとめる (乗る・乗らない (elevator-noride) は別)。名前の無い並びは近くの名前の付いた並びにまとめ、残れば名札を付けない。
// 名札は行き先の名前と、乗り場が 2 つ以上なら「×数」。
// マウスを乗せると止まる階の一覧。押すと、並びの最初の乗り場を目的地にする
const PLAN_ELEV_JOIN = 12;
// 同じ路線の並びを結ぶ線の色 (路線ごと)
const LINE_COLORS = ['#e8710a', '#12b5cb', '#e52592', '#7cb342', '#f9ab00', '#5c6bc0'];
// 名前の無い並びを、名前の付いた並びにまとめる距離 (m)
const PLAN_ELEV_NAMED_JOIN = 30;
function drawPlanElevators() {
  const banks = [];
  for (const t of st.map.transit || []) {
    if (t.kind !== 'elevator' && t.kind !== 'elevator-noride') continue;
    const fid = floorOfPoint(t.floor, t.pos[2]);
    if (!visible(fid)) continue;
    const noride = t.kind === 'elevator-noride';
    let b = banks.find(x => x.noride === noride && x.line === (t.line || '') && Math.hypot(x.x - t.pos[0], x.y - t.pos[1]) <= PLAN_ELEV_JOIN);
    if (!b) { b = { noride, line: t.line || '', x: t.pos[0], y: t.pos[1], items: [], fid }; banks.push(b); }
    b.items.push({ t, fid });
    b.x = b.items.reduce((a2, q) => a2 + q.t.pos[0], 0) / b.items.length;
    b.y = b.items.reduce((a2, q) => a2 + q.t.pos[1], 0) / b.items.length;
  }
  const named = b => b.items.some(q => q.t.name);
  // 名前の無い並び: 同じ種類 (乗る・乗らない) の名前の付いた並びが PLAN_ELEV_NAMED_JOIN m 以内にあれば、そこにまとめる
  for (const b of banks.filter(x => !named(x))) {
    let best = null, bd = PLAN_ELEV_NAMED_JOIN;
    for (const c of banks) {
      if (c === b || c.merged || c.noride !== b.noride || !named(c)) continue;
      const d = Math.hypot(c.x - b.x, c.y - b.y);
      if (d <= bd) { bd = d; best = c; }
    }
    if (best) { best.items.push(...b.items); b.merged = true; }
  }
  const shown = banks.filter(b => !b.merged);
  st.planBanks = shown.length;
  // 同じ路線の並びが 2 つ以上 (例: Spaceport のシャトルの駅の階と上の階のエレベーター) なら、路線ごとの色の細い破線で結ぶ
  // (いちばん短い線でつなぐ木 (Prim)。印の縁も同じ色にする)
  const byLine = {};
  for (const b of shown) if (!b.noride && b.line) (byLine[b.line] = byLine[b.line] || []).push(b);
  st.planLinks = 0;
  let li = 0;
  const linkLayer = document.getElementById('lines') || el('g', { id: 'lines' }, world);
  for (const line in byLine) {
    const bs = byLine[line];
    if (bs.length < 2) continue;
    const col = LINE_COLORS[li++ % LINE_COLORS.length];
    const inTree = [bs[0]], rest = bs.slice(1);
    while (rest.length) {
      let best = null, bd = Infinity;
      for (const a of inTree) for (const c of rest) { const d = Math.hypot(a.x - c.x, a.y - c.y); if (d < bd) { bd = d; best = [a, c]; } }
      const [a, c] = best;
      const p = proj(a.x, a.y, 0), q2 = proj(c.x, c.y, 0);
      el('path', { class: 'elev-link', d: `M${p[0]} ${p[1]}L${q2[0]} ${q2[1]}`, style: `stroke:${col}` }, linkLayer);
      st.planLinks++;
      inTree.push(c); rest.splice(rest.indexOf(c), 1);
    }
    for (const b of bs) b.color = col;
  }
  for (const b of shown) {
    const first = b.items[0].t;
    const names = [...new Set(b.items.map(q => q.t.name).filter(x => x))];
    // 名札 = 行き先の名前と乗り場の数 (×数)。名前の無い並びには名札を付けない (「×2」だけの名札は出さない)
    const label = names.length ? (names[0] + (names.length > 1 ? ' ほか' : '') + (b.items.length > 1 ? ' ×' + b.items.length : '')) : '';
    const floors = [...new Set(b.items.map(q => floorName(q.fid)))].join('・');
    const hot = b.items.some(q => isDest('transit', q.t.i) || isStart('transit', q.t.i));
    addMarker(b.x, b.y, b.fid, g => {
      if (b.noride) el('rect', { x: -6, y: -7, width: 12, height: 14, rx: 2, class: 'elev-box noride' }, g);
      else {
        el('rect', Object.assign({ x: -7, y: -8, width: 14, height: 16, rx: 2, class: 'elev-box' }, b.color ? { style: `stroke:${b.color};stroke-width:2.5` } : {}), g);
        el('path', { class: 'elev-arrow', d: 'M0 -6L4 -1H-4Z M0 6L4 1H-4Z' }, g);
      }
      if (hot) el('circle', { r: 12, class: 'dest-ring' }, g);
      if (label) el('text', { x: 10, y: 4, class: 'lbl' }, g).textContent = label;
      // 乗らないエレベーター (位置だけ) は、止まる階の一覧を出さない
      el('title', null, g).textContent = (b.noride ? 'エレベーター（位置だけ。道順では乗りません）' : 'エレベーター') + (names.length ? ': ' + names.join(' / ') : '')
        + (!b.noride && floors ? '\n止まる階: ' + floors : '');
    }, 'mk', { 'data-k': 'transit', 'data-i': first.i }, label, hot ? 0 : 4);
  }
}

// 選んだ区画の名前 (選んだ階の床のいちばん大きい輪の中ほど。その階に床が無ければ描く階の床)
function drawSectionLabel() {
  if (!st.selSec) return;
  const s = (st.map.sections || []).find(x => x.id === st.selSec);
  if (!s) return;
  const fid = (s.floors || {})[st.sel] ? st.sel : Object.keys(s.floors || {}).find(k => visible(k));
  if (!fid) return;
  let best = null, ba = -1;
  for (const r of s.floors[fid]) {
    let a = 0;
    for (let i = 0; i < r.length; i++) { const p = r[i], q = r[(i + 1) % r.length]; a += p[0] * q[1] - q[0] * p[1]; }
    if (Math.abs(a) > ba) { ba = Math.abs(a); best = r; }
  }
  if (!best) return;
  const cx = best.reduce((a, p) => a + p[0], 0) / best.length, cy = best.reduce((a, p) => a + p[1], 0) / best.length;
  addMarker(cx, cy, fid, g => {
    el('text', { x: 0, y: 0, 'text-anchor': 'middle', class: 'bold seclabel' }, g).textContent = s.name || s.id;
  }, 'mk flabel', null, s.name || s.id, 1);
}

// 階段でつながる階の名前 (2026-11-01、利用者の決まり「階段でつながっているフロアには双方にサブの名前として相手の名前を付けるルールにする」)。
// StationMapPanel.StairPartners と同じ決まり: 階段 (map.stairs の from・to) でこの階とつながる、階の一覧に出る階 (listed) の名前を、
// 一覧の並び (重ねる並びの上から) の順に「（階段で: A・B・C）」(同じ文字は 1 回。4 つ以上なら 3 つと「ほか N」)。
// 相手の名前が一覧に 2 つ以上ある名前なら高さを添える (例「Aparelli +13.0 m」。高さは z を小数 1 桁、符号付き)。相手が無ければ ''
const STAIR_PARTNER_MAX = 3;
function stairPartners(fid) {
  const listed = st.floors.filter(f => f.listed);
  const listedIds = new Set(listed.map(f => f.id));
  // つながる階: 一覧に出ない階 (listed でない階・描かない階) は階段の道の途中とみなしてたどり、最初に着いた一覧に出る階で止める
  // (2026-11-01、親の決定「B」。一度たどった階はたどらない)
  const next = {};
  for (const s of st.map.stairs || []) {
    if (s.from === s.to) continue;
    (next[s.from] = next[s.from] || []).push(s.to);
    (next[s.to] = next[s.to] || []).push(s.from);
  }
  const ids = new Set(), seen = new Set([fid]), queue = [fid];
  while (queue.length) {
    const cur = queue.shift();
    for (const g of next[cur] || []) {
      if (seen.has(g)) continue;
      seen.add(g);
      if (listedIds.has(g)) ids.add(g); else queue.push(g);
    }
  }
  const ps = listed.filter(f => ids.has(f.id));
  if (!ps.length) return '';
  // 一覧の並びは上の階が上 (StackOrder)。ここでは重ねる順番 (st.stack0) と高さの大きい順にする
  ps.sort((a, b) => (st.stack0[b.id] - st.stack0[a.id]) || (b.z - a.z));
  const count = {};
  for (const f of listed) count[f.name] = (count[f.name] || 0) + 1;
  const h = z => (z > 0 ? '+' : '') + z.toFixed(1) + ' m';
  const names = [];
  for (const f of ps) { const n = count[f.name] > 1 ? f.name + ' ' + h(f.z) : f.name; if (!names.includes(n)) names.push(n); }
  const shown = names.slice(0, STAIR_PARTNER_MAX).join('・');
  return names.length > STAIR_PARTNER_MAX ? '（階段で: ' + shown + ' ほか ' + (names.length - STAIR_PARTNER_MAX) + '）' : '（階段で: ' + shown + '）';
}

function floorName(fid) {
  const i = st.fidx[fid];
  return i !== undefined ? st.floors[i].name : (fid || '');
}

// ===== 表示の位置と倍率 =====

// 札の幅のめやす (13px の字: 半角 7.4px、全角 13px。viewer.css の .mk text と合わせる)
function textWidth(s) { let w = 0; for (const ch of s) w += ch.charCodeAt(0) < 0x2000 ? 7.4 : 13; return w; }

function applyView() {
  world.setAttribute('transform', `matrix(${st.vs} 0 0 ${st.vs} ${st.vx} ${st.vy})`);
  for (const o of st.ovl) {
    let [x, y] = toScreen(o.u, o.v);
    if (o.hullUV) [x, y] = visibleAnchor(o.hullUV, x, y);
    o.sx = x; o.sy = y;
    o.el.setAttribute('transform', `translate(${x.toFixed(1)} ${y.toFixed(1)})`);
  }
  placeLabels();
}

// 選んだ階の名札の置き所 (2026-11-01、親の決定: 板の左の角が画面の外で名札が隠れるのは不具合)。
// 板のいちばん左の角 (x, y) が画面の中 (placeLabels が札を置かない画面の外の幅 VIS_MARGIN の内側) ならそのまま。外なら、画面の中に見えている板の角のうち
// いちばん左のもの (今までの「板の左の角」に近い所)。見えている角が無ければ (板が画面いっぱいなど)、板の角の外枠の箱と画面の重なる所の左の辺の真ん中。
// 重なる所も無ければ (板が全部画面の外) そのまま (札は置かない)。ほかの札の置き方は変えない
const VIS_MARGIN = 40;
function visibleAnchor(hullUV, x, y) {
  const w = svg.clientWidth || 800, h = svg.clientHeight || 600;
  const inside = (px, py) => px >= VIS_MARGIN && py >= VIS_MARGIN && px <= w - VIS_MARGIN && py <= h - VIS_MARGIN;
  if (inside(x, y)) return [x, y];
  const pts = hullUV.map(q => toScreen(q[0], q[1]));
  let best = null;
  for (const p of pts) if (inside(p[0], p[1]) && (!best || p[0] < best[0])) best = p;
  if (best) return best;
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const p of pts) { x0 = Math.min(x0, p[0]); y0 = Math.min(y0, p[1]); x1 = Math.max(x1, p[0]); y1 = Math.max(y1, p[1]); }
  const ix0 = Math.max(x0, VIS_MARGIN), iy0 = Math.max(y0, VIS_MARGIN), ix1 = Math.min(x1, w - VIS_MARGIN), iy1 = Math.min(y1, h - VIS_MARGIN);
  if (ix0 <= ix1 && iy0 <= iy1) return [ix0, (iy0 + iy1) / 2];
  return [x, y];
}

// ===== 名札の置き場所 =====
// 名札は隠さず、空いている所へ置く。置く順は大事さの順 (pri)。初めは印のすぐ右 (階の名前は左)、重なれば印のまわり 8 方向に
// LEADER_STEPS の距離で探し、ずらしたときは印から細い引き出し線でつなぐ。ほかの名札・すべての印の絵 (店・乗り場・階段・出発地・目的地・現在位置・ピン)・道順の線と重ならない所にする。
// どこにも置けない札は、近くの同じ種類の札とまとめた名札 (例「店 5 件」) にし、押すとそこを中心に拡大する (拡大すると個別に置ける)。
// 重なりは 24 px の升目で引く (札の数 × 候補の数 × 近くの升の箱だけを見る)。画面の外の印の札は置かない
const LEADER_STEPS = [0, 16, 30, 48, 70, 96];
const DIRS = [[1, 0], [-1, 0], [1, -1], [1, 1], [0, -1], [0, 1], [-1, -1], [-1, 1]];
const CELL = 24;
// 印の絵の半分の大きさ (px。エレベーター 16 の高さ・シャトル 18 の幅に合わせた)
const ICON = 9;
st.layout = { ms: 0, placed: 0, moved: 0, clustered: 0, clusters: 0 };

function labelSize(o) {
  if (o.huge) return [textWidth(o.label) * 1.7 + 6, 28];
  // 札の付いた名札 (階の名前): 字の大きさ (o.fs) の幅に左右 7 px ずつの余白、高さは字 + 10 px
  if (o.pill) return [textWidth(o.label) * (o.fs / 13) * (o.bold ? 1.08 : 1) + 14, o.fs + 10];
  // 区画の名札 (12px の太め)
  if (o.kind === 'section') return [textWidth(o.label) * (12 / 13) * 1.08 + 4, 16];
  const per = o.big ? 1.2 : 1;
  return [(textWidth(o.label) * per) * (o.bold ? 1.08 : 1) + 4, o.big ? 19 : 17];
}

function placeLabels() {
  const t0 = performance.now();
  const w = svg.clientWidth || 800, h = svg.clientHeight || 600;
  const grid = new Map();
  const key = (ix, iy) => ix * 100003 + iy;
  function add(b) {
    for (let ix = Math.floor(b[0] / CELL); ix <= Math.floor(b[2] / CELL); ix++)
      for (let iy = Math.floor(b[1] / CELL); iy <= Math.floor(b[3] / CELL); iy++) {
        const k = key(ix, iy);
        let l = grid.get(k);
        if (!l) grid.set(k, l = []);
        l.push(b);
      }
  }
  function hits(b) {
    for (let ix = Math.floor(b[0] / CELL); ix <= Math.floor(b[2] / CELL); ix++)
      for (let iy = Math.floor(b[1] / CELL); iy <= Math.floor(b[3] / CELL); iy++) {
        const l = grid.get(key(ix, iy));
        if (l) for (const c of l) if (b[0] < c[2] && c[0] < b[2] && b[1] < c[3] && c[1] < b[3]) return true;
      }
    return false;
  }
  // 印の絵: 大事な印 (出発地・目的地・現在位置・ピンなど pri 0 の印。ピンのしずくは上に長い) と、ほかの印 (店・乗り場・階段。絵はいちばん大きいもので ±9 px)。
  // 階・区画の名前 (flabel) は絵が無い。画面の外の印は数えない。道順の線は 8 px おきの点
  for (const o of st.ovl) {
    if (o.sx < -30 || o.sy < -40 || o.sx > w + 30 || o.sy > h + 30) continue;
    if (o.pri === 0) add([o.sx - 12, o.sy - 28, o.sx + ICON, o.sy + 12]);
    else if (!o.el.classList.contains('flabel')) add([o.sx - ICON, o.sy - ICON, o.sx + ICON, o.sy + ICON]);
  }
  // 選んだ階の階段の形 (外形の輪の箱。rebuild の st.stairBoxes): 名札を重ねない
  for (const bb of st.stairBoxes || []) {
    const [ax, ay] = toScreen(bb[0], bb[1]), [bx, by] = toScreen(bb[2], bb[3]);
    if (bx < -20 || by < -20 || ax > w + 20 || ay > h + 20) continue;
    add([ax, ay, bx, by]);
  }
  for (const line of st.routeUV || []) {
    for (let i = 0; i + 1 < line.length; i++) {
      const [ax, ay] = toScreen(line[i][0], line[i][1]), [bx, by] = toScreen(line[i + 1][0], line[i + 1][1]);
      const n = Math.min(400, Math.ceil(Math.hypot(bx - ax, by - ay) / 8));
      for (let k = 0; k <= n; k++) {
        const x = ax + (bx - ax) * k / Math.max(n, 1), y = ay + (by - ay) * k / Math.max(n, 1);
        if (x < -20 || y < -20 || x > w + 20 || y > h + 20) continue;
        add([x - 4, y - 4, x + 4, y + 4]);
      }
    }
  }
  // 店の販売品の枠 (#sales) が出ていれば、その下には名札を置かない
  const sb = salesBox();
  if (sb) add(sb);
  const clusterLayer = document.getElementById('clusters') || el('g', { id: 'clusters' }, svg);
  clusterLayer.textContent = '';
  const order = st.ovl.filter(o => o.label && o.tx).sort((a, b) => a.pri - b.pri);
  const left = [];
  let placed = 0, moved = 0;
  for (const o of order) {
    const off = o.sx < -40 || o.sy < -40 || o.sx > w + 40 || o.sy > h + 40;
    if (off) { setLabel(o, null); continue; }
    const [lw, lh] = labelSize(o);
    let got = null;
    outer:
    // 区画の名札は、すぐ近く (0・16 px) にだけ置く (遠くへずらすと、どの区画の名前か分からなくなるため)
    for (const r of (o.kind === 'section' ? [0, 16] : LEADER_STEPS)) {
      const dirs = r === 0 ? (o.anchor === 'end' ? [[-1, 0], [1, 0]] : [[1, 0], [-1, 0]]) : DIRS;
      for (const [dx, dy] of dirs) {
        // 札の印に近い辺を、印から (8 + r) px の所に置く
        const gap = ICON + 2 + r;   // 自分の印の絵 (±ICON) の外から
        const cx = dx * (gap + lw / 2) + (dx === 0 ? 0 : 0), cy = dy * (gap + lh / 2) + (dy === 0 ? 0 : 0);
        const b = [o.sx + cx - lw / 2, o.sy + cy - lh / 2, o.sx + cx + lw / 2, o.sy + cy + lh / 2];
        if (b[0] < 0 || b[1] < 0 || b[2] > w || b[3] > h) continue;
        if (hits(b)) continue;
        got = { b, cx, cy, r };
        break outer;
      }
    }
    if (!got) { setLabel(o, null); if (!o.noCluster) left.push(o); continue; }
    add(got.b);
    setLabel(o, got);
    placed++;
    if (got.r > 0) moved++;
  }
  // 置けなかった札: 同じ種類で 120 px の升ごとにまとめる
  const groups = new Map();
  for (const o of left) {
    const k = o.kind + ':' + Math.floor(o.sx / 120) + ':' + Math.floor(o.sy / 120);
    let g = groups.get(k);
    if (!g) groups.set(k, g = []);
    g.push(o);
  }
  const words = { shop: '店', transit: '乗り場', terminal: '端末', floor: '階', other: '名前' };
  for (const g of groups.values()) {
    const cx = g.reduce((a, o) => a + o.sx, 0) / g.length, cy = g.reduce((a, o) => a + o.sy, 0) / g.length;
    const text = `${words[g[0].kind] || '名前'} ${g.length} 件`;
    const lw = textWidth(text) + 12, lh = 19;
    let b = null;
    for (const r of LEADER_STEPS.concat([130, 170])) {
      for (const [dx, dy] of DIRS) {
        const bx = cx + dx * (ICON + 2 + r + lw / 2), by = cy + dy * (ICON + 2 + r + lh / 2);
        const c = [bx - lw / 2, by - lh / 2, bx + lw / 2, by + lh / 2];
        if (c[0] < 0 || c[1] < 0 || c[2] > w || c[3] > h || hits(c)) continue;
        b = c; break;
      }
      if (b) break;
    }
    if (!b) b = [cx + 8, cy - lh / 2, cx + 8 + lw, cy + lh / 2];   // 場所が無ければ印の右に重ねて置く
    add(b);
    const cg = el('g', { class: 'cluster', 'data-k': 'cluster', 'data-x': cx.toFixed(1), 'data-y': cy.toFixed(1) }, clusterLayer);
    el('line', { class: 'leader', x1: cx, y1: cy, x2: (b[0] + b[2]) / 2, y2: (b[1] + b[3]) / 2 }, cg);
    el('rect', { x: b[0], y: b[1], width: lw, height: lh, rx: 3 }, cg);
    el('text', { x: (b[0] + b[2]) / 2, y: b[3] - 4, 'text-anchor': 'middle' }, cg).textContent = text;
    el('title', null, cg).textContent = g.map(o => o.label).join('\n') + '\n(押すと拡大して個別に出します)';
  }
  st.layout = { ms: performance.now() - t0, placed, moved, clustered: left.length, clusters: groups.size };
}

// 札を置く (got が null なら隠す)。位置は印の g の中の座標
function setLabel(o, got) {
  if (!got) { o.tx.setAttribute('visibility', 'hidden'); o.leader.setAttribute('visibility', 'hidden'); if (o.bg) o.bg.setAttribute('visibility', 'hidden'); return; }
  o.tx.setAttribute('visibility', 'visible');
  o.tx.setAttribute('text-anchor', 'middle');
  o.tx.setAttribute('x', got.cx.toFixed(1));
  o.tx.setAttribute('y', (got.cy + (o.huge ? 7 : o.fs ? o.fs * 0.36 : 5)).toFixed(1));
  if (o.bg) {
    const [bw, bh] = labelSize(o);
    o.bg.setAttribute('x', (got.cx - bw / 2).toFixed(1)); o.bg.setAttribute('y', (got.cy - bh / 2).toFixed(1));
    o.bg.setAttribute('width', bw.toFixed(1)); o.bg.setAttribute('height', bh.toFixed(1));
    o.bg.setAttribute('visibility', 'visible');
  }
  // 区画の名札は引き出し線を出さない (すぐ近くにだけ置くので。線が区画の床の上に増えて見にくくなるため)
  if (got.r > 0 && o.kind !== 'section') {
    // 引き出し線: 印から札の近い辺まで
    const [lw, lh] = labelSize(o);
    const ex = got.cx - Math.sign(got.cx) * lw / 2 * (Math.abs(got.cx) > lw / 2 ? 1 : 0);
    const ey = got.cy - Math.sign(got.cy) * lh / 2 * (Math.abs(got.cy) > lh / 2 ? 1 : 0);
    o.leader.setAttribute('x2', ex.toFixed(1));
    o.leader.setAttribute('y2', ey.toFixed(1));
    o.leader.setAttribute('visibility', 'visible');
  } else o.leader.setAttribute('visibility', 'hidden');
}

function fitUV(u0, v0, u1, v1) {
  const w = svg.clientWidth || 800, h = svg.clientHeight || 600, pad = 40;
  const bw = Math.max(u1 - u0, 1e-6), bh = Math.max(v1 - v0, 1e-6);
  st.vs = Math.min((w - pad * 2) / bw, (h - pad * 2) / bh);
  if (!isFinite(st.vs) || st.vs <= 0) st.vs = 1;
  st.vx = w / 2 - (u0 + u1) / 2 * st.vs;
  st.vy = h / 2 - (v0 + v1) / 2 * st.vs;
  applyView();
}

function fitPoints(list, minSize) {
  if (!list.length) return false;
  let u0 = Infinity, v0 = Infinity, u1 = -Infinity, v1 = -Infinity;
  for (const [u, v] of list) { u0 = Math.min(u0, u); u1 = Math.max(u1, u); v0 = Math.min(v0, v); v1 = Math.max(v1, v); }
  const cu = (u0 + u1) / 2, cv = (v0 + v1) / 2;
  const hw = Math.max((u1 - u0) / 2, minSize / 2), hh = Math.max((v1 - v0) / 2, minSize / 2);
  fitUV(cu - hw, cv - hh, cu + hw, cv + hh);
  return true;
}

// 階に寄せる: その階の店があれば店を囲む範囲 (最低 120 m)、無ければ床の点の 5〜95 % の範囲 (遠くの小さな床・線路で広がりすぎないように)
function fitFloor(fid) {
  if (!fid || st.fidx[fid] === undefined) return;
  const idx = st.stack[fid];
  // 階の無い店 (floor が空。例 Seraphim の SCShop_LandingServices_rs_ext_CRU-LEO1 は遠い所にある) は、見る範囲を決めるときに入れない
  // (floorOfPoint は階の無い点を高さのいちばん近い階に置くので、入れるとその階の範囲が大きく広がった)
  const shops = (st.map.shops || []).filter(s => s.floor && floorOfPoint(s.floor, s.pos[2]) === fid).map(s => proj(s.pos[0], s.pos[1], idx));
  if (fitPoints(shops, 120)) return;
  const us = [], vs = [];
  for (const s of st.map.sections || []) {
    const rings = (s.floors || {})[fid];
    if (!rings) continue;
    for (const r of rings) for (const p of r) { const q = proj(p[0], p[1], idx); us.push(q[0]); vs.push(q[1]); }
  }
  if (!us.length) return;
  us.sort((a, b) => a - b); vs.sort((a, b) => a - b);
  const q = (a, f) => a[Math.min(a.length - 1, Math.floor(a.length * f))];
  fitPoints([[q(us, 0.05), q(vs, 0.05)], [q(us, 0.95), q(vs, 0.95)]], 40);
}

// 区画を選んだときの初めの表示: その区画の店を全部囲む範囲 (店が無ければ区画の床)
function fitSection(id) {
  const pts = [];
  for (const s of st.map.shops || []) {
    // 階の無い店 (floor が空) は入れない (fitFloor と同じ。遠い所にある店で見る範囲が広がらないように)
    if (s.section !== id || !s.floor) continue;
    const fid = floorOfPoint(s.floor, s.pos[2]);
    if (!visible(fid)) continue;
    pts.push(proj(s.pos[0], s.pos[1], st.stack[fid]));
  }
  if (fitPoints(pts, 40)) return;
  const sec = (st.map.sections || []).find(s => s.id === id);
  if (!sec) return;
  for (const fid of [st.sel, ...Object.keys(sec.floors || {})]) {
    const rings = (sec.floors || {})[fid];
    if (!rings || !visible(fid)) continue;
    const list = [];
    for (const r of rings) for (const p of r) list.push(proj(p[0], p[1], st.stack[fid]));
    if (fitPoints(list, 30)) return;
  }
}

function zoomAt(sx, sy, factor) {
  const [u, v] = toWorld(sx, sy);
  st.vs = Math.min(Math.max(st.vs * factor, 1e-4), 400);
  st.vx = sx - u * st.vs;
  st.vy = sy - v * st.vs;
  applyView();
}

// ===== マウス =====

let press = null;   // {x, y, id, timer, long, drag, vx, vy, target}

svg.addEventListener('contextmenu', e => e.preventDefault());
svg.addEventListener('wheel', e => {
  e.preventDefault();
  const r = svg.getBoundingClientRect();
  const f = Math.pow(1.0015, -e.deltaY);
  zoomAt(e.clientX - r.left, e.clientY - r.top, f);
  if (f < 1) checkZoomOut();
}, { passive: false });

// 縮小して、描いている図の全体 (world の範囲) が画面の幅と高さの両方の ZOOMOUT_RATIO より小さくなったら、アプリへ zoomout を知らせる
// (［マップ］タブはその場所を真ん中にした星系図へ戻る。別ウィンドウは知らせを使わない)。ZOOMOUT_RATIO は仮の値 (2026-11-01、利用者の決めた案 1 で親と決めた):
// 全体が画面の 1/4 より小さければ、それ以上縮めても場所の中は読めないので、外 (星系図) を見たいときとみなす。続けて送らないよう ZOOMOUT_GAP_MS あける
const ZOOMOUT_RATIO = 0.25, ZOOMOUT_GAP_MS = 1500;
let lastZoomOut = 0;
function checkZoomOut() {
  if (!st.map) return;
  let b;
  try { b = world.getBBox(); } catch (e) { return; }
  if (!(b.width > 0 || b.height > 0)) return;
  const w = svg.clientWidth || 800, h = svg.clientHeight || 600;
  if (b.width * st.vs >= w * ZOOMOUT_RATIO || b.height * st.vs >= h * ZOOMOUT_RATIO) return;
  const now = Date.now();
  if (now - lastZoomOut < ZOOMOUT_GAP_MS) return;
  lastZoomOut = now;
  post({ type: 'zoomout' });
}

svg.addEventListener('pointerdown', e => {
  const r = svg.getBoundingClientRect();
  const sx = e.clientX - r.left, sy = e.clientY - r.top;
  if (e.button === 2) {
    // 右クリック: 押した所を中心にして 1 段拡大
    e.preventDefault();
    const [u, v] = toWorld(sx, sy);
    const w = svg.clientWidth, h = svg.clientHeight;
    st.vs = Math.min(st.vs * ZOOM_STEP, 400);
    st.vx = w / 2 - u * st.vs;
    st.vy = h / 2 - v * st.vs;
    applyView();
    return;
  }
  if (e.button !== 0) return;
  try { svg.setPointerCapture(e.pointerId); } catch { }
  press = { x: sx, y: sy, id: e.pointerId, long: false, drag: false, vx: st.vx, vy: st.vy, target: e.target };
  press.timer = setTimeout(() => {
    if (!press || press.drag) return;
    press.long = true;
    placePin(press.x, press.y);
  }, LONG_MS);
});

svg.addEventListener('pointermove', e => {
  if (!press || e.pointerId !== press.id) return;
  const r = svg.getBoundingClientRect();
  const sx = e.clientX - r.left, sy = e.clientY - r.top;
  if (!press.drag && Math.hypot(sx - press.x, sy - press.y) > MOVE_PX) {
    press.drag = true;
    clearTimeout(press.timer);
    svg.classList.add('dragging');
  }
  if (press.drag) {
    st.vx = press.vx + (sx - press.x);
    st.vy = press.vy + (sy - press.y);
    applyView();
  }
});

function endPress(e, cancel) {
  if (!press || e.pointerId !== press.id) return;
  clearTimeout(press.timer);
  svg.classList.remove('dragging');
  const p = press;
  press = null;
  if (cancel || p.drag || p.long) return;
  // 地図を押したら、店の販売品の枠と端末の強調を消す (区画・店を押したときは、アプリが知らせを受けて出し直す。2026-11-02、利用者の指示「他をクリックしたら消してほしい」)
  showSales(null);
  if (st.hlTerm !== null && !(p.target && p.target.closest && p.target.closest('[data-k="terminal"]'))) { st.hlTerm = null; rebuild(); post({ type: 'hlterm-cleared' }); }
  click(p.target);
}
svg.addEventListener('pointerup', e => endPress(e, false));
svg.addEventListener('pointercancel', e => endPress(e, true));

// 短いクリック: 印 (店・乗り場) なら目的地、区画なら区画を選ぶ
function click(target) {
  const hit = target && target.closest ? target.closest('[data-k]') : null;
  if (!hit) return;
  const k = hit.getAttribute('data-k');
  if (k === 'district') { enterDistrict(hit.getAttribute('data-id')); return; }
  if (k === 'cluster') {
    const x = +hit.getAttribute('data-x'), y = +hit.getAttribute('data-y');
    const [u, v] = toWorld(x, y);
    const w = svg.clientWidth, h = svg.clientHeight;
    st.vs = Math.min(st.vs * ZOOM_STEP, 400);
    st.vx = w / 2 - u * st.vs;
    st.vy = h / 2 - v * st.vs;
    applyView();
    return;
  }
  if (k === 'shop' || k === 'transit' || k === 'terminal') {
    post({ type: 'pick', kind: k, index: +hit.getAttribute('data-i') });
    return;
  }
  if (k === 'sec') {
    const id = hit.getAttribute('data-id'), fid = hit.getAttribute('data-f');
    if (st.mode === 'plan') { st.mode = 'floors'; st.sel = fid; setDistrictFor(fid); post({ type: 'floor', id: fid }); }
    if (fid !== st.sel) { st.sel = fid; post({ type: 'floor', id: fid }); }
    selectSection(id, true);
    post({ type: 'section', id, floor: fid });
  }
}

// 長押し: 選んでいる階の面の上にピンを立てる
function placePin(sx, sy) {
  if (!st.sel) return;
  const [u, v] = toWorld(sx, sy);
  const [x, y] = unproj(u, v, st.stack[st.sel]);
  const f = st.floors[st.fidx[st.sel]];
  st.pin = { pos: [x, y, f.z], floor: f.id };
  rebuild();
  post({ type: 'pin', pos: st.pin.pos, floor: f.id });
}

function selectSection(id, fit) {
  st.selSec = id;
  rebuild();
  if (fit && id) fitSection(id);
}

// ===== アプリからの知らせ =====

function receive(m) {
  try {
    switch (m.type) {
      case 'load': load(m); break;
      case 'floor': if (m.id && st.fidx[m.id] !== undefined) { st.sel = m.id; st.mode = 'floors'; setDistrictFor(m.id); rebuild(); if (m.fit !== false) fitFloor(m.id); } break;
      case 'overview': showOverview(); break;
      case 'district': enterDistrict(m.id); break;
      case 'mode': st.top = !!m.top; rebuild(); if (st.mode === 'overview') fitOverview(); else if (st.mode === 'plan') fitPlan(st.district); else if (st.selSec) fitSection(st.selSec); else fitFloor(st.sel); break;
      case 'section':
        if (m.floor && st.fidx[m.floor] !== undefined) { st.sel = m.floor; st.mode = 'floors'; setDistrictFor(m.floor); }
        selectSection(m.id || null, m.fit !== false);
        break;
      case 'current':
        st.cur = m.pos ? { pos: m.pos, floor: m.floor || '', label: m.label || '' } : null;
        if (st.cur) st.cur.fid = currentFloor(st.cur.floor, m.pos[0], m.pos[1], m.pos[2]);
        rebuild();
        break;
      case 'pin': st.pin = m.pos ? { pos: m.pos, floor: m.floor } : null; rebuild(); break;
      case 'ends': st.start = m.start || null; st.dest = m.dest || null; rebuild(); break;
      case 'route':
        st.route = m.pts || null;
        st.routeTrams = m.trams || [];
        st.routeSpans = m.tramSpans || [];
        rebuild();
        if (m.fit && st.mode === 'overview') { fitOverview(); break; }
        if (m.fit && st.mode === 'plan') { fitPlan(st.district); break; }
        if (m.fit && st.route && st.route.length) {
          const pts = [];
          for (const p of st.route) { const fid = floorOfPoint(p[3], p[2]); if (visible(fid)) pts.push(proj(p[0], p[1], st.stack[fid])); }
          // ボタンで現在位置を取ったとき: 現在位置の印も入れる (印が見えるように)
          if (m.withCur && st.cur) { const cf = st.cur.fid; if (visible(cf)) pts.push(proj(st.cur.pos[0], st.cur.pos[1], st.stack[cf])); }
          fitPoints(pts, 30);
        }
        break;
      // ボタンで現在位置を取ったとき: 現在位置の階へ移り (全体図・平面図からも。地区のある都市ではその階の地区へ)、印を中心に寄せる。
      // 移った階はアプリへ知らせる (階のリストを合わせる)
      case 'gotoCurrent': {
        if (!st.cur) break;
        const fid = st.cur.fid;
        if (!fid) break;
        st.sel = fid; st.mode = 'floors'; setDistrictFor(fid); rebuild();
        const [u, v] = proj(st.cur.pos[0], st.cur.pos[1], st.stack[fid]);
        // (2026-11-02、利用者の指摘「赤い点だけで床が何も見えない」) 点がその階の床から CUR_FLOOR_GAP m より離れていれば、いちばん近い床の点も入る範囲で寄せる
        const near = nearestFloorPoint(fid, st.cur.pos[0], st.cur.pos[1]);
        const pts = [[u, v]];
        if (near && near.d > CUR_FLOOR_GAP) pts.push(proj(near.x, near.y, st.stack[fid]));
        fitPoints(pts, m.size || 120);
        post({ type: 'floor', id: fid });
        break;
      }
      // 端末のアイコン (種類 → data: URI)。届いたら描き直す
      case 'icons': st.icons = m.icons || {}; if (st.map) rebuild(); break;
      // 左の「端末」の一覧で選んだまとまりを強調する (null で消す)。階に移って寄せるのは、アプリが floor・focus を送る
      case 'hlterm': st.hlTerm = (m.index === undefined || m.index === null) ? null : m.index; rebuild(); break;
      // 店の販売品の枠 ({title, shops: [{name, cols, rows, note}]}。shops が無ければ消す)
      case 'sales': showSales(m); break;
      case 'focus': {
        const fid = floorOfPoint(m.floor, m.pos[2]);
        if (visible(fid)) { const [u, v] = proj(m.pos[0], m.pos[1], st.stack[fid]); fitPoints([[u, v]], m.size || 40); }
        break;
      }
    }
  } catch (e) { fail(e, m && m.type ? String(m.type) : 'receive'); }
}
// ===== 店の販売品の枠 (2026-11-02、利用者の指示「区画でお店を選んでそのエリアがハイライトされたとき、右側に四角で囲んだ販売品リスト(価格も)を出せれば素晴らしい。他をクリックしたら消してほしい」) =====
// 地図の右上 (#legend の下) に、幅 380 px・高さは地図の高さ − 70 px までの枠。長いときは枠の中で巻き取る。店ごとに見出し「店名（種類）」と表 (列は cols、行は rows)。
// 表の無い店は note の文。右上の × で閉じる。地図を押すと閉じる (endPress)。中身はアプリが作る (StationMapPanel.SendSales。［詳細］のお店の【売っている物】と同じデータ)
function esc(t) { return String(t).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function showSales(m) {
  let box = document.getElementById('sales');
  if (!m || !m.shops || !m.shops.length) { if (box) { box.style.display = 'none'; box.innerHTML = ''; } return; }
  if (!box) { box = document.createElement('div'); box.id = 'sales'; document.body.appendChild(box); }
  let h = '<div class="sales-head"><span>' + esc(m.title || '販売品') + '</span><button type="button" class="sales-close" title="閉じる">×</button></div>';
  for (const sh of m.shops) {
    h += '<div class="sales-shop">' + esc(sh.name) + '</div>';
    if (sh.note) { h += '<div class="sales-note">' + esc(sh.note) + '</div>'; continue; }
    for (const tb of sh.tables || []) {
      h += '<table><thead><tr>' + tb.cols.map((c, ci) => '<th' + (ci >= tb.left ? ' class="num"' : '') + '>' + esc(c) + '</th>').join('') + '</tr></thead><tbody>';
      for (const r of tb.rows) h += '<tr>' + r.map((c, ci) => '<td' + (ci >= tb.left ? ' class="num"' : '') + '>' + esc(c) + '</td>').join('') + '</tr>';
      h += '</tbody></table>';
    }
  }
  box.innerHTML = h;
  box.style.display = 'block';
  box.scrollTop = 0;
  box.querySelector('.sales-close').addEventListener('click', () => { showSales(null); applyView(); });
  applyView();
}
// 販売品の枠の画面の箱 [x0, y0, x1, y1] (svg の中の座標)。出ていなければ null
function salesBox() {
  const box = document.getElementById('sales');
  if (!box || box.style.display !== 'block') return null;
  const a = box.getBoundingClientRect(), b = svg.getBoundingClientRect();
  return [a.left - b.left - 6, a.top - b.top - 6, a.right - b.left + 6, a.bottom - b.top + 6];
}

window.stationMap ={ receive, state: st, placePin, click, applyView, zoomAt, enterDistrict, showOverview, showPlan, showWhole, moveFloor, neighborFloors };
document.getElementById('back')?.addEventListener('click', () => showWhole());
document.getElementById('floorUp')?.addEventListener('click', () => moveFloor(1));
document.getElementById('floorDown')?.addEventListener('click', () => moveFloor(-1));
document.getElementById('plan')?.addEventListener('click', () => showPlan());

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', e => receive(e.data));
}
window.addEventListener('resize', () => applyView());
post({ type: 'ready' });
