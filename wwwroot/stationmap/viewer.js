// ステーションの階層マップのビューア (StationMapWeb で開く。SVG と素の JS)。
//   データ: アプリから {type:'load', map:{floors, sections, shops, transit, stairs, tracks}, floor, top} を受け取る (PostWebMessageAsJson。
//     StationMapModel.ViewerMap の形。描くのに要るものだけで、道順のグラフは送られない。道順は {type:'route', pts} で線だけ来る)。座標は局所 (メートル、z 上)。
//   描き方: 階ごとに <g>。斜めの図 (案内図のように各階の平面を一定の間隔で重ねる。実際の高さは使わない) と、選んだ階だけを真上から見た図を切り替える。
//     斜め: 平らな座標 (px, py) = (x, -y) から U = px - py·SHEAR、V = (px·A + py·B)·K - 階の順番 × GAP (GAP は各階の広がりの中央値から決める)。
//     斜めの図で描くのは、選んだ階と同じ床のまとまり (group。建物・地区) の上下 WINDOW 階まで。重ねる順番はまとまりの中の番号 (level)。
//     シャトルの線 (tracks。道筋の点) は選んだ階の面の上に描く。階段の印は選んだ階にあるものだけ。
//   地区のある都市: 全体図 (overview) → 地区を押すと、地区を真上から見た平面図 (plan。地区のすべての階の床・区画・店・シャトルの駅) →
//     区画を押すか階を選ぶと、その階の斜めに重ねた図 (floors。地区の階だけ、地区の広がりで決めた間隔で重ねる)。
//     選んだ階は濃く、ほかは薄く (viewer.css の .floor.dim)。描かない階へ続く道順は、境の点に「↑ / ↓ 行き先の階」を出す。
//   名前の札: 隠さずに空いている所へ置き、ずらしたときは引き出し線でつなぐ (placeLabels の説明)。置けない札はまとめた名札 (「店 5 件」) にする。
//   操作: 左クリック (短く押す) = 区画を選ぶ・印 (店・乗り場) を目的地にする。左ボタンの長押し (LONG_MS) = ピンを立てる (長押しのまま動かすとドラッグで地図を動かす)。
//     押してすぐ動かしたらドラッグ。右クリック = 押した所を中心に 1 段 (ZOOM_STEP 倍) 拡大。ホイール = 拡大・縮小。右クリックのメニューは出さない。
//   アプリへの知らせ (chrome.webview.postMessage): ready・error・section {id, floor}・pick {kind, index}・pin {pos, floor}・floor {id}
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
};

function post(m) {
  try { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(m); else console.log('[post] ' + JSON.stringify(m)); }
  catch (e) { console.log('post failed: ' + e); }
}
function fail(e) {
  const text = (e && e.stack) ? e.stack : String(e);
  console.error('[stationmap] ' + text);
  post({ type: 'error', message: text });
}
window.addEventListener('error', e => fail(e.error || e.message));

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
  st.map = map;
  st.floors = (map.floors || []).map((f, i) => ({ id: f.id, name: f.name || f.id, z: f.z, idx: i, group: f.group || '', level: f.level || 0,
    sg: f.stackGroup || '', sl: f.stackLevel || 0, listed: f.listed !== false }));
  st.fidx = {}; st.stack = {};
  // 重ねる順番: データの重ねる並び (stackGroup・stackLevel。行き先から決めたもの) があればその順、無ければ床のまとまりの中の番号、無ければ全体の順
  for (const f of st.floors) { st.fidx[f.id] = f.idx; st.stack[f.id] = f.sg ? f.sl : f.group ? f.level : f.idx; }
  st.stack0 = Object.assign({}, st.stack);
  st.sel = (m.floor && st.fidx[m.floor] !== undefined) ? m.floor : (st.floors.length ? st.floors[st.floors.length - 1].id : null);
  st.top = !!m.top;
  st.selSec = null; st.route = null; st.cur = null; st.pin = null; st.dest = null; st.start = null; st.routeTrams = [];
  st.dists = map.districts || [];
  st.groupDist = {};
  for (const d of st.dists) for (const g of d.groups || []) st.groupDist[g] = d.id;
  st.gap = st.gapAll = computeGap();
  msgEl.textContent = st.floors.length ? '' : '床の形がありません';
  // 主要な地区があれば都市の全体図から、無ければ今までどおり階の地図
  if (st.dists.some(d => d.main)) { st.mode = 'overview'; setDistrictFor(null); rebuild(); fitOverview(); }
  else { st.mode = 'floors'; setDistrictFor(st.sel); rebuild(); fitFloor(st.sel); }
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
// 道順で乗るシャトルの区間 (routeTrams) は太く、道順の出発地・目的地のある地区は縁を太くする。地区を押すとその地区の階の地図に入る。
// 地区の階の地図: その地区の床のまとまり (groups) の階だけを、下からの順に重ねる (選んだ階の上下 WINDOW 階)。［全体図へ戻る］で全体図へ
const DIST_COLORS = ['#d93025', '#1a73e8', '#188038', '#e37400', '#9334e6', '#00897b'];
const DIR_COLORS = { A: '#7b1fa2', B: '#00838f' };

function distOf(fid) {
  const i = st.fidx[fid];
  return i === undefined ? null : (st.groupDist[st.floors[i].group] || null);
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
    const onRoute = st.routeTrams.includes(i);
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
  // 向きの凡例
  const dirs = [...new Set((st.map.tracks || []).map(t => t.dir).filter(x => x))];
  const leg = document.getElementById('legend');
  if (leg) leg.innerHTML = dirs.map(x => `<span style="color:${DIR_COLORS[x] || '#5f6368'}">━▶ シャトル（${x}回り）</span>`).join('　');
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
      // 階の名前 (板の左の角)
      let best = null;
      for (const p of c) { const q = proj(p[0], p[1], st.stack[f.id]); if (!best || q[0] < best[0]) best = q; }
      addAt(best[0], best[1], t => {
        const tx = el('text', { x: -6, y: 4, 'text-anchor': 'end', class: f.id === st.sel ? 'bold' : '' }, t);
        tx.textContent = f.name;
      }, 'mk flabel' + (f.id === st.sel || st.top ? '' : ' dim'), null, f.name, f.id === st.sel ? 1 : 5, 'end');
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
      const p = el('path', { class: cls, d, 'data-k': 'sec', 'data-id': s.id, 'data-f': fid }, g);
      el('title', null, p).textContent = s.name || s.id;
    }
  }
  // 低い階から順に重ねる。選んだ階は最後 (いちばん上)
  for (const f of floorsToDraw) if (f.id !== st.sel) world.appendChild(groups[f.id]);
  if (groups[st.sel]) world.appendChild(groups[st.sel]);

  const lines = el('g', { id: 'lines' }, world);
  drawStairLinks(lines);
  drawTracks(lines);
  drawRoute(el('g', { id: 'routeLayer' }, world));
  drawMarkers();
  drawSectionLabel();
  applyView();
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
    o.kind = k === 'shop' ? 'shop' : k === 'transit' ? 'transit' : k === 'district' ? 'district' : (cls || '').includes('flabel') ? 'floor' : 'other';
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
    const [x, y] = toScreen(o.u, o.v);
    o.sx = x; o.sy = y;
    o.el.setAttribute('transform', `translate(${x.toFixed(1)} ${y.toFixed(1)})`);
  }
  placeLabels();
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
    for (const r of LEADER_STEPS) {
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
    if (!got) { setLabel(o, null); left.push(o); continue; }
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
  const words = { shop: '店', transit: '乗り場', floor: '階', other: '名前' };
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
  if (!got) { o.tx.setAttribute('visibility', 'hidden'); o.leader.setAttribute('visibility', 'hidden'); return; }
  o.tx.setAttribute('visibility', 'visible');
  o.tx.setAttribute('text-anchor', 'middle');
  o.tx.setAttribute('x', got.cx.toFixed(1));
  o.tx.setAttribute('y', (got.cy + (o.huge ? 7 : 5)).toFixed(1));
  if (got.r > 0) {
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
  zoomAt(e.clientX - r.left, e.clientY - r.top, Math.pow(1.0015, -e.deltaY));
}, { passive: false });

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
  if (k === 'shop' || k === 'transit') {
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
        fitPoints([[u, v]], m.size || 120);
        post({ type: 'floor', id: fid });
        break;
      }
      case 'focus': {
        const fid = floorOfPoint(m.floor, m.pos[2]);
        if (visible(fid)) { const [u, v] = proj(m.pos[0], m.pos[1], st.stack[fid]); fitPoints([[u, v]], m.size || 40); }
        break;
      }
    }
  } catch (e) { fail(e); }
}
window.stationMap = { receive, state: st, placePin, click, applyView, zoomAt, enterDistrict, showOverview, showPlan, showWhole, moveFloor, neighborFloors };
document.getElementById('back')?.addEventListener('click', () => showWhole());
document.getElementById('floorUp')?.addEventListener('click', () => moveFloor(1));
document.getElementById('floorDown')?.addEventListener('click', () => moveFloor(-1));
document.getElementById('plan')?.addEventListener('click', () => showPlan());

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', e => receive(e.data));
}
window.addEventListener('resize', () => applyView());
post({ type: 'ready' });
