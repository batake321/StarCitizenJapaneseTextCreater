// ステーションの 3D (新しい方式) の、地点の区画の読み取り・解析を画面の糸の外で行う (2026-11-04、利用者の指摘「移動して読み込む時止まるのはどうにかなる？」)。
// view3d.js の buildCur・当たり判定の升目を作る所を、ここへ移した (計算の中身は同じ)。
// 受け取る: { type:'build', id, base (地点の置き場の URL), region (region.json), c (中心), r, keys: { col, ocol, labk }, names (色の種類の名前) }
// 返す: { type:'progress', id, done, total } (読んだ区画の数)、{ type:'boxes', id, boxes } (外形: 描く部品の箱の線。中心からの相対、Float32Array)、
//       { type:'done', id, buf, offs, ioffs, loffs, parts, ladders, elevDoors, stairs, fz, fa, pidx, lightParts, grids, times }、{ type:'error', id, message }
'use strict';
const geoCache = new Map();
async function loadGeo(base, region, gi) {
  if (geoCache.has(gi)) return geoCache.get(gi);
  const url = base + region.geos[gi];
  const resp = await fetch(url);
  const buf = await resp.arrayBuffer();
  if (!resp.ok || buf.byteLength < 24) throw new Error(`区画のファイルを読めませんでした: ${url} (応答 ${resp.status}・${buf.byteLength} バイト)`);
  const dv = new DataView(buf);
  const magic = new TextDecoder().decode(new Uint8Array(buf, 0, 8)); if (magic !== 'SCJT3DG2') throw new Error('形の違うファイル ' + url);
  const hp = Number(dv.getBigInt64(8, true)), jl = dv.getInt32(16, true); const head = JSON.parse(new TextDecoder().decode(new Uint8Array(buf, hp, jl)));
  const g = { buf, base: 24, parts: head.parts, inst: new Map(head.inst.map(x => [x[0], x])), triStart: [] };
  let s = 0; for (const p of g.parts) { g.triStart.push(s); s += p[5]; } g.tris = s;
  geoCache.set(gi, g); return g;
}
function worldVerts(P, A) {
  const W = new Float64Array(P.length);
  for (let i = 0; i < P.length; i += 3) { const x = P[i], y = P[i + 1], z = P[i + 2]; W[i] = A[0] * x + A[1] * y + A[2] * z + A[3]; W[i + 1] = A[4] * x + A[5] * y + A[6] * z + A[7]; W[i + 2] = A[8] * x + A[9] * y + A[10] * z + A[11]; }
  return W;
}
function lineIndex(g, ii) {
  const key = JSON.stringify(ii[2]); g.lineIdx ??= new Map(); if (g.lineIdx.has(key)) return g.lineIdx.get(key); const res = {};
  for (const [lk, l] of Object.entries(ii[2])) { const tr = new Uint32Array(g.buf, g.base + l[0], l[1] * 3); const m = new Map(); for (let q = 0; q < l[1]; q++) { let a = m.get(tr[q * 3]); if (!a) m.set(tr[q * 3], a = []); a.push(q); } res[lk] = m; }
  g.lineIdx.set(key, res); return res;
}
// 升目 (Map: 鍵 → 三角形の位置の一覧) を、並べた鍵・始まり・中身の配列にする (画面の糸へ移せる形)
function flat(map) {
  const keys = Float64Array.from(map.keys()).sort();
  const starts = new Uint32Array(keys.length + 1); let n = 0;
  for (let i = 0; i < keys.length; i++) { starts[i] = n; n += map.get(keys[i]).length; } starts[keys.length] = n;
  const items = new Uint32Array(n); let k = 0;
  for (let i = 0; i < keys.length; i++) for (const b of map.get(keys[i])) items[k++] = b;
  return { keys, starts, items };
}
self.onmessage = async e => {
  const m = e.data; if (m.type !== 'build') return;
  const { id, base, region, c, r, keys, names } = m;
  const COLK = keys.col, OCOLK = keys.ocol, LABK = keys.labk;
  const times = {}; let tp = performance.now(); const lap = k => { const t = performance.now(); times[k] = (t - tp) / 1000; tp = t; };
  try {
    geoCache.clear();
    const lo = c.map(v => v - r), hi = c.map(v => v + r);
    const ins = region.instances.filter(i => i[3][0] <= hi[0] && i[4][0] >= lo[0] && i[3][1] <= hi[1] && i[4][1] >= lo[1] && i[3][2] <= hi[2] && i[4][2] >= lo[2]);
    const sel = [];
    const geoN = new Set(ins.map(i => i[1])).size, geoDone = new Set();
    const boxes = [];
    // 区画 (インスタンス) の選び方: 部品の箱 (8 隅) の 1 つでも立方体に掛かり、その箱の x の幅が 5000 m 未満のインスタンス。選んだ区画の部品は全部描く (view3d.js と同じ)
    for (const inst of ins) {
      const g = await loadGeo(base, region, inst[1]); const A = inst[2];
      if (!geoDone.has(inst[1])) { geoDone.add(inst[1]); self.postMessage({ type: 'progress', id, done: geoDone.size, total: geoN }); }
      const ii = g.inst.get(inst[0]); if (!ii) continue;
      const pboxes = [];
      let hit = false;
      for (let pi = 0; pi < g.parts.length; pi++) {
        const p = g.parts[pi];
        const bl = [Infinity, Infinity, Infinity], bh = [-Infinity, -Infinity, -Infinity];
        for (const x of [p[6][0], p[7][0]]) for (const y of [p[6][1], p[7][1]]) for (const z of [p[6][2], p[7][2]]) for (let a = 0; a < 3; a++) { const v = A[a * 4] * x + A[a * 4 + 1] * y + A[a * 4 + 2] * z + A[a * 4 + 3]; if (v < bl[a]) bl[a] = v; if (v > bh[a]) bh[a] = v; }
        pboxes.push([bl, bh]);
        if (bl[0] <= hi[0] && bh[0] >= lo[0] && bl[1] <= hi[1] && bh[1] >= lo[1] && bl[2] <= hi[2] && bh[2] >= lo[2] && bh[0] - bl[0] < 5000) hit = true;
      }
      if (!hit) continue;
      // 外形: 部品の箱の 12 本の辺 (2026-11-04、利用者の案「外形だけ先に書くとか」。細かい形の前に、先に描く)
      for (const [bl, bh] of pboxes) {
        if (bh[0] - bl[0] > 2000 || bh[1] - bl[1] > 2000) continue;
        const X = [bl[0] - c[0], bh[0] - c[0]], Y = [bl[1] - c[1], bh[1] - c[1]], Z = [bl[2] - c[2], bh[2] - c[2]];
        for (const y of Y) for (const z of Z) boxes.push(X[0], y, z, X[1], y, z);
        for (const x of X) for (const z of Z) boxes.push(x, Y[0], z, x, Y[1], z);
        for (const x of X) for (const y of Y) boxes.push(x, y, Z[0], x, y, Z[1]);
      }
      for (let pi = 0; pi < g.parts.length; pi++) {
        const p = g.parts[pi];
        const P = new Float32Array(g.buf, g.base + p[2], p[3] * 3), I = p[8] === 2 ? new Uint16Array(g.buf, g.base + p[4], p[5] * 3) : new Uint32Array(g.buf, g.base + p[4], p[5] * 3);
        sel.push([inst, g, pi, P, I, ii, A]);
      }
    }
    const bx = new Float32Array(boxes); self.postMessage({ type: 'boxes', id, boxes: bx }, [bx.buffer]);
    lap('read');
    // ===== buildCur (view3d.js と同じ) =====
    const parts = [], baseN = {}, overN = {}, lineN = {};
    for (const k of COLK) baseN[k] = 0; for (const k of OCOLK) { overN[k] = 0; lineN[k] = 0; }
    const fz = [], fa = [];
    const isLight = p => p[0].split('_').includes('light');
    const catOf = p => isLight(p) ? 'excluded' : p[1] === 'other' && p[0].split('_').some(w => w === 'door' || w === 'doors') ? 'door' : p[1];
    const lightParts = new Set();
    // 上下が逆の重力の範囲 (region.json の gravity の up。2026-11-04、利用者が決めた案 1「重力が上向きの範囲では、下向きの面を床にし、上向きの面は床にしない」):
    // 範囲の中 (三角形の重心) では、上向きの床 (判定 floor2) を床にせず、下向きの面 (法線 z ≤ -COS_WALK_INV) を床 (floor2) にする。
    // アプリの判定 2 (同じ平面の形をまとめる) はせず、三角形ごとに決める (範囲の外は今までどおり)
    const COS_WALK_INV = Math.cos(46 * Math.PI / 180);
    const FLOOR2 = LABK.indexOf('floor2');
    const upVols = (region.gravity || []).filter(g => g[0] === 'up').map(([, W, size, poly, h]) => {
      const a = W[0], b = W[1], d = W[2], e = W[4], f = W[5], g2 = W[6], hh = W[8], i = W[9], j = W[10];
      const det = a * (f * j - g2 * i) - b * (e * j - g2 * hh) + d * (e * i - f * hh);
      const R = [(f * j - g2 * i) / det, (d * i - b * j) / det, (b * g2 - d * f) / det, (g2 * hh - e * j) / det, (a * j - d * hh) / det, (d * e - a * g2) / det, (e * i - f * hh) / det, (b * hh - a * i) / det, (a * f - b * e) / det];
      // 範囲の箱 (場所の座標の AABB。部品の箱と比べて、掛からない部品は見ない)
      const corners = size ? [[-1, -1, -1], [1, -1, -1], [-1, 1, -1], [1, 1, -1], [-1, -1, 1], [1, -1, 1], [-1, 1, 1], [1, 1, 1]].map(([x, y, z]) => [x * size[0] / 2, y * size[1] / 2, z * size[2] / 2])
        : poly.flatMap(q => [[q[0], q[1], 0], [q[0], q[1], h]]);
      const lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
      for (const [x, y, z] of corners) for (let k = 0; k < 3; k++) { const v = W[k * 4] * x + W[k * 4 + 1] * y + W[k * 4 + 2] * z + W[k * 4 + 3]; lo[k] = Math.min(lo[k], v); hi[k] = Math.max(hi[k], v); }
      return { W, R, size, poly, h, lo, hi };
    }).filter(v => isFinite(v.R[0]));
    const inUp = (x, y, z) => upVols.some(v => {
      if (x < v.lo[0] || x > v.hi[0] || y < v.lo[1] || y > v.hi[1] || z < v.lo[2] || z > v.hi[2]) return false;
      const px = x - v.W[3], py = y - v.W[7], pz = z - v.W[11], R = v.R;
      const lx = R[0] * px + R[1] * py + R[2] * pz, ly = R[3] * px + R[4] * py + R[5] * pz, lz = R[6] * px + R[7] * py + R[8] * pz;
      if (v.size) return Math.abs(lx) <= v.size[0] / 2 && Math.abs(ly) <= v.size[1] / 2 && Math.abs(lz) <= v.size[2] / 2;
      if (lz < 0 || lz > v.h) return false;
      let inside = false; const P_ = v.poly;
      for (let k = 0, m = P_.length - 1; k < P_.length; m = k++) if ((P_[k][1] > ly) !== (P_[m][1] > ly) && lx < (P_[m][0] - P_[k][0]) * (ly - P_[k][1]) / (P_[m][1] - P_[k][1]) + P_[k][0]) inside = !inside;
      return inside;
    });
    // (2026-11-04、利用者の決定「上場の高さが床とほぼ同じ物体は除去しない」) 名前に light のある部品でも、アプリの判定で色の付いたもの
    // (上面の高さがまわりの床とほぼ同じで、除外の決まりを当てずに判定したもの) は、色を使い、ほかの部品と同じに扱う (lightEx は除外のままの部品)
    const lightEx = new Set();
    for (const s of sel) {
      const [inst, g, pi, P, I, ii, A] = s; const p = g.parts[pi]; baseN[catOf(p)] += p[5];
      let lab = new Uint8Array(g.buf, g.base + ii[1] + g.triStart[pi], p[5]);
      if (isLight(p) && !lab.some(v => v)) { lab = new Uint8Array(p[5]); lightEx.add(s); }
      if (upVols.length && FLOOR2 > 0 && !lightEx.has(s)) {
        const W = worldVerts(P, A);
        let lab2 = null;
        for (let t = 0; t < p[5]; t++) {
          const v0 = I[t * 3] * 3, v1 = I[t * 3 + 1] * 3, v2 = I[t * 3 + 2] * 3;
          const cx = (W[v0] + W[v1] + W[v2]) / 3, cy = (W[v0 + 1] + W[v1 + 1] + W[v2 + 1]) / 3, cz = (W[v0 + 2] + W[v1 + 2] + W[v2 + 2]) / 3;
          if (!inUp(cx, cy, cz)) continue;
          const ux = W[v1] - W[v0], uy = W[v1 + 1] - W[v0 + 1], uz = W[v1 + 2] - W[v0 + 2], wx = W[v2] - W[v0], wy = W[v2 + 1] - W[v0 + 1], wz = W[v2 + 2] - W[v0 + 2];
          const nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx, nl = Math.hypot(nx, ny, nz); if (!(nl > 0)) continue;
          const want = nz / nl <= -COS_WALK_INV ? FLOOR2 : lab[t] === FLOOR2 ? 0 : lab[t];
          if (want !== lab[t]) { lab2 ??= Uint8Array.from(lab); lab2[t] = want; }
        }
        if (lab2) lab = lab2;
      }
      s.push(lab);
      if (isLight(p)) lightParts.add(p[0]);
      for (let t = 0; t < p[5]; t++) if (lab[t]) overN[LABK[lab[t]]]++;
      if (!lightEx.has(s)) for (const [lk, qs_] of Object.entries(lineIndex(g, ii))) lineN[lk] += (qs_.get(pi) || []).length;
    }
    let pos = 0; const offs = {}, ioffs = {}, loffs = {};
    for (const k of COLK) { offs[k] = [pos, baseN[k] * 9]; pos += baseN[k] * 9; }
    for (const k of COLK) { ioffs[k] = [pos, baseN[k]]; pos += baseN[k]; }
    for (const k of OCOLK) { offs[k] = [pos, overN[k] * 9]; pos += overN[k] * 9; }
    for (const k of OCOLK) { ioffs[k] = [pos, overN[k]]; pos += overN[k]; }
    for (const k of OCOLK) { loffs[k] = [pos, lineN[k] * 6]; pos += lineN[k] * 6; }
    const buf = new ArrayBuffer(pos * 4); const F = new Float32Array(buf), U = new Uint32Array(buf);
    const wp = {}, wi = {}, wl = {};
    for (const k of Object.keys(offs)) { wp[k] = offs[k][0]; wi[k] = ioffs[k][0]; } for (const k of Object.keys(loffs)) wl[k] = loffs[k][0];
    const pbox = [];
    for (const s of sel) {
      const [inst, g, pi, P, I, ii, A, lab] = s; const p = g.parts[pi]; const id_ = parts.length;
      const W = worldVerts(P, A);
      const bl = [Infinity, Infinity, Infinity], bh = [-Infinity, -Infinity, -Infinity];
      const cnt = {};
      const k = catOf(p);
      for (let t = 0; t < p[5]; t++) {
        const vs = [I[t * 3] * 3, I[t * 3 + 1] * 3, I[t * 3 + 2] * 3];
        let o = wp[k];
        for (const v of vs) for (let a = 0; a < 3; a++) { F[o++] = W[v + a] - c[a]; if (W[v + a] < bl[a]) bl[a] = W[v + a]; if (W[v + a] > bh[a]) bh[a] = W[v + a]; }
        wp[k] = o; U[wi[k]++] = id_;
        if (lab[t]) {
          const lk = LABK[lab[t]]; cnt[lk] = (cnt[lk] || 0) + 1; let o2 = wp[lk];
          for (const v of vs) for (let a = 0; a < 3; a++) F[o2++] = W[v + a] - c[a];
          wp[lk] = o2; U[wi[lk]++] = id_;
          if (lk === 'floor2') {
            let inreg = true; for (let a = 0; a < 3 && inreg; a++) { const mn = Math.min(W[vs[0] + a], W[vs[1] + a], W[vs[2] + a]), mx = Math.max(W[vs[0] + a], W[vs[1] + a], W[vs[2] + a]); if (!(mn <= hi[a] && mx >= lo[a])) inreg = false; }
            if (inreg) {
              fz.push((W[vs[0] + 2] + W[vs[1] + 2] + W[vs[2] + 2]) / 3);
              const ux = W[vs[1]] - W[vs[0]], uy = W[vs[1] + 1] - W[vs[0] + 1], uz = W[vs[1] + 2] - W[vs[0] + 2], vx = W[vs[2]] - W[vs[0]], vy = W[vs[2] + 1] - W[vs[0] + 1], vz = W[vs[2] + 2] - W[vs[0] + 2];
              fa.push(Math.hypot(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx) / 2);
            }
          }
        }
      }
      if (!lightEx.has(s)) for (const [lk, l] of Object.entries(ii[2])) {
        const tr = new Uint32Array(g.buf, g.base + l[0], l[1] * 3);
        for (const q of (lineIndex(g, ii)[lk].get(pi) || [])) for (const v of [tr[q * 3 + 1] * 3, tr[q * 3 + 2] * 3]) for (let a = 0; a < 3; a++) F[wl[lk]++] = W[v + a] - c[a];
      }
      const sp = (inst[5] || '').split('/').pop();
      parts.push([inst[0], p[0], k, Object.entries(cnt).map(([kk, n]) => `${names[kk]} ${n}`).join(', ') || '歩ける面に色なし', sp, '']);
      pbox.push([bl, bh, p[0].split('_')]);
    }
    const ladders = [];
    pbox.forEach(([bl, bh, w], i) => { if (w.includes('ladder')) ladders.push([parts[i][1], parts[i][0], bl.map((v, a) => +(v - c[a]).toFixed(3)), bh.map((v, a) => +(v - c[a]).toFixed(3))]); });
    // エレベーターの扉 (部品の名前の語に elevator と door のあるもの) の箱 [名前, 箱の低い角, 高い角] (中心からの相対)。エレベーターで移った先の立つ所に使う (view3d.js の elevMove)
    const elevDoors = [];
    pbox.forEach(([bl, bh, w], i) => { if (w.includes('elevator') && (w.includes('door') || w.includes('doors'))) elevDoors.push([parts[i][1], bl.map((v, a) => +(v - c[a]).toFixed(3)), bh.map((v, a) => +(v - c[a]).toFixed(3))]); });
    // 階段の部品 (名前の語に stair・stairs があり、手すり・柱・飾りの語の無いもの。アプリの StationFloorBuilder.IsStairMesh と同じ決まり) の箱 [名前, 低い角, 高い角] (中心からの相対)。
    // 行き先の一覧の「階段」に使う (2026-11-04、利用者の決定「作り直す」)
    const STAIR_NOT = new Set(['rail', 'railing', 'decal', 'wall', 'structure', 'strut', 'divider', 'platform', 'cap', 'capplate', 'addon', 'sign', 'cover']);
    const stairs = [];
    pbox.forEach(([bl, bh, w], i) => { if (w.some(x => x === 'stair' || x === 'stairs') && !w.some(x => STAIR_NOT.has(x)) && bh[2] - bl[2] >= 0.5) stairs.push([parts[i][1], bl.map((v, a) => +(v - c[a]).toFixed(3)), bh.map((v, a) => +(v - c[a]).toFixed(3))]); });
    const pidx = sel.map((s, i) => [s[0][0] + ':' + s[2], i]);
    geoCache.clear();
    lap('buildCur');
    // ===== 当たり判定の升目 (view3d.js の buildGrid・buildCeil・buildWall・buildFloorGrid と同じ中身) =====
    const isDoor = parts.map(q => { const w = q[1].split('_'); return w.some(x => x === 'door' || x === 'doors') && !w.includes('elevator'); });
    const fg = new Map(), cg = new Map(), wg = new Map(), flg = new Map();
    const push = (map, key, b) => { let l = map.get(key); if (!l) map.set(key, l = []); l.push(b); };
    for (const k of COLK) {
      const [o, n] = offs[k]; const [io, inn] = ioffs[k]; const ids = new Uint32Array(buf, io * 4, inn);
      for (let t = 0; t < n / 9; t++) {
        const b = o + t * 9;
        const x0 = Math.min(F[b], F[b + 3], F[b + 6]), x1 = Math.max(F[b], F[b + 3], F[b + 6]), y0 = Math.min(F[b + 1], F[b + 4], F[b + 7]), y1 = Math.max(F[b + 1], F[b + 4], F[b + 7]);
        const cz = (F[b + 3] - F[b]) * (F[b + 7] - F[b + 1]) - (F[b + 4] - F[b + 1]) * (F[b + 6] - F[b]);
        const i0 = Math.floor(x0), i1 = Math.floor(x1), j0 = Math.floor(y0), j1 = Math.floor(y1);
        const small1 = (i1 - i0 + 1) * (j1 - j0 + 1) <= 4096;
        // 足元 (上向き)
        if (cz > 0 && small1) for (let i = i0; i <= i1; i++) for (let j = j0; j <= j1; j++) push(fg, i * 100003 + j, b);
        if (isDoor[ids[t]]) continue;
        // 頭の上 (下向き、2 m の升目)
        if (cz < 0) {
          const a0 = Math.floor(x0 / 2), a1 = Math.floor(x1 / 2), c0 = Math.floor(y0 / 2), c1 = Math.floor(y1 / 2);
          if ((a1 - a0 + 1) * (c1 - c0 + 1) <= 1024) for (let i = a0; i <= a1; i++) for (let j = c0; j <= c1; j++) push(cg, i * 100003 + j, b);
        }
        // 横の当たり
        if (small1) for (let i = i0; i <= i1; i++) for (let j = j0; j <= j1; j++) push(wg, i * 100003 + j, b);
      }
    }
    for (const k of ['floor2', 'tube2']) {
      const [o, n] = offs[k] || [0, 0];
      for (let t = 0; t < n / 9; t++) {
        const b = o + t * 9;
        const i0 = Math.floor(Math.min(F[b], F[b + 3], F[b + 6])), i1 = Math.floor(Math.max(F[b], F[b + 3], F[b + 6])), j0 = Math.floor(Math.min(F[b + 1], F[b + 4], F[b + 7])), j1 = Math.floor(Math.max(F[b + 1], F[b + 4], F[b + 7]));
        if ((i1 - i0 + 1) * (j1 - j0 + 1) > 4096) continue;
        for (let i = i0; i <= i1; i++) for (let j = j0; j <= j1; j++) push(flg, i * 100003 + j, b);
      }
    }
    const grids = { f: flat(fg), c: flat(cg), w: flat(wg), fl: flat(flg) };
    lap('grids');
    const tr = [buf];
    for (const g of Object.values(grids)) tr.push(g.keys.buffer, g.starts.buffer, g.items.buffer);
    self.postMessage({ type: 'done', id, buf, offs, ioffs, loffs, parts, ladders, elevDoors, stairs, fz, fa, pidx, lightParts: [...lightParts], grids, times }, tr);
  } catch (err) {
    geoCache.clear();
    self.postMessage({ type: 'error', id, message: String(err && err.message || err) });
  }
};
