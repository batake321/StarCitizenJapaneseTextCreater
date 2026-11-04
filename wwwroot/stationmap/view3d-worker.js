// ステーションの 3D (新しい方式) の、地点の区画の読み取り・解析を画面の糸の外で行う (2026-11-04、利用者の指摘「移動して読み込む時止まるのはどうにかなる？」)。
// view3d.js の buildCur・当たり判定の升目を作る所を、ここへ移した (計算の中身は同じ)。
// 受け取る: { type:'build', id, base (地点の置き場の URL), region (region.json), c (中心), r, keys: { col, ocol, labk }, names (色の種類の名前) }
// 返す: { type:'progress', id, done, total } (読んだ区画の数)、{ type:'boxes', id, boxes } (外形: 描く部品の箱の線。中心からの相対、Float32Array)、
//       { type:'done', id, buf, uvbuf, texbuf, offs, ioffs, labs, uoffs, toffs, parts, ladders, elevDoors, stairs, fz, fa, pidx, lightParts, coarse, times }、{ type:'error', id, message }
//       (buf = COLK の三角形の位置 (9 float) と ioffs (三角形 → 部品の番号) だけ。labs[k] = 判定の色 k (floor2・slope2・tube2・counter2) の三角形の、buf の中の位置 b の並び (Uint32Array、昇順)。
//        判定の色の三角形の位置の複製と輪郭線は持たない (2026-11-05、親の決定。描いていない))
//       (uvbuf・texbuf = COLK の種類ごとの UV (三角形の角ごと float32 x6) と画像の番号 (三角形ごと int32。region.json の textures の番号。-1 = 無し) の別の ArrayBuffer (UV が無ければ null)。
//        uoffs[k]・toffs[k] = [その中の位置 (要素), 長さ]。COLK の中は画像の番号の順)
//       (coarse = 当たり判定の荒い区切り { all, fl, bigAll, bigFl }。升は画面の側が引いたときに作る)
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
// 升目用の開いた番地の表 (鍵 = 升目の番号 (倍精度の整数)。空きは NaN)。数えて → 並べた鍵・始まり・中身の配列にする
function tableHash(key, mask) {
  const lo = key >>> 0, hi = Math.floor(key / 4294967296) | 0;
  return (Math.imul(lo ^ Math.imul(hi, 0x85ebca6b), 0x9E3779B1) >>> 7) & mask;
}
function makeTable() { const cap = 1 << 16; return { cap, n: 0, keys: new Float64Array(cap).fill(NaN), cnt: new Uint32Array(cap), pos: null }; }
function tableSlot(T, key) {
  const mask = T.cap - 1; let h = tableHash(key, mask);
  while (T.keys[h] !== key) { if (T.keys[h] !== T.keys[h]) return -1; h = (h + 1) & mask; }
  return h;
}
function tableCount(T, key) {
  const mask = T.cap - 1; let h = tableHash(key, mask), k;
  while ((k = T.keys[h]) !== key) {
    if (k !== k) {
      T.keys[h] = key; T.cnt[h] = 1;
      if (++T.n * 2 > T.cap) tableGrow(T);
      return;
    }
    h = (h + 1) & mask;
  }
  T.cnt[h]++;
}
function tableGrow(T) {
  const oc = T.cap, ok = T.keys, on = T.cnt; T.cap = oc * 2; T.keys = new Float64Array(T.cap).fill(NaN); T.cnt = new Uint32Array(T.cap);
  const mask = T.cap - 1;
  for (let i = 0; i < oc; i++) { const k = ok[i]; if (k !== k) continue; let h = tableHash(k, mask); while (T.keys[h] === T.keys[h]) h = (h + 1) & mask; T.keys[h] = k; T.cnt[h] = on[i]; }
}
function tableLayout(T) {
  let keys = new Float64Array(T.n); let q = 0, kmin = Infinity, kmax = -Infinity;
  for (let i = 0; i < T.cap; i++) { const k = T.keys[i]; if (k === k) { keys[q++] = k; if (k < kmin) kmin = k; if (k > kmax) kmax = k; } }
  // 鍵が 32 ビット整数に収まるなら Int32Array にする (値は同じ。引くのは二分探しで型を問わない。メモリが半分)
  if (kmin >= -2147483648 && kmax <= 2147483647) keys = Int32Array.from(keys);
  keys.sort();
  const starts = new Uint32Array(T.n + 1); let tot = 0; T.pos = new Uint32Array(T.cap);
  for (let i = 0; i < T.n; i++) { const sl = tableSlot(T, keys[i]); starts[i] = tot; T.pos[sl] = tot; tot += T.cnt[sl]; }
  starts[T.n] = tot;
  const items = new Uint32Array(tot);
  return { items, grid: { keys, starts, items } };
}
// ===== 解析の結果のディスク保存 (WC) =====
// (2026-11-04、利用者の指示「先読み、キャッシュ化 HDD 容量設定が空いていればファイル保存など早く動かす仕組みを入れて」)
// 'done' の知らせ (描く形のバッファ・当たり判定の升目・部品の一覧など) を、そのまま保存して次に使う。
//   保存の形: 知らせの中の ArrayBuffer・型付き配列は 1 つの data.bin に並べ、残り (数・文字・配列・入れ子) は meta.json の skeleton に JSON で書く。
//     知らせの項目が増減しても、そのまま保存・復元できる (項目を名前で決めていない)。数の NaN・Infinity・-0・undefined は印を付けて書き、同じ値に戻す。
//   置き場: ブラウザのオリジンの私的なファイル領域 (OPFS。WebView2 の利用者データのフォルダ = 作業ディレクトリの webview2_ship3d の中) の w-<鍵>\ (data.bin・meta.json)。
//     meta.json は最後に書く (途中で止まったものは使わない)。
//   鍵: アプリが渡す鍵 (wcache.key = 地点の保存と同じ鍵) + 中心・半径 + 色の種類の名前 (keys・names) + この解析の糸のスクリプト自身の SHA-256 (中身が変わったら別の鍵)。
//   容量: アプリが渡す budget (バイト。上限 − 地点の保存の分) に収める。超えたら最後に使った時刻が古いものから消す。budget が 0 以下なら全部消す。
//     アプリが渡す free (保存先のドライブの空き) が、保存する大きさ + WC_RESERVE に足りなければ保存しない。
const WC_RESERVE = 3 * 1024 * 1024 * 1024, WC_CHUNK = 256 * 1024 * 1024, WC_VERSION = 1;
const WC = (() => {
  const TA = { Int8Array, Uint8Array, Uint8ClampedArray, Int16Array, Uint16Array, Int32Array, Uint32Array, Float32Array, Float64Array, BigInt64Array, BigUint64Array };
  const enc = new TextEncoder(), dec = new TextDecoder();
  // 保存先 (OPFS)。試しの道具は self.__wcBackend で差し替える (handle: getSize/read/write/truncate/close の同期の取っ手)
  const opfs = {
    async root() { return await navigator.storage.getDirectory(); },
    async list() { const out = []; const root = await this.root(); for await (const [name] of root.entries()) if (name.startsWith('w-')) out.push(name); return out; },
    async handle(entry, file, create) {
      const root = await this.root(); let d, fh;
      try { d = await root.getDirectoryHandle(entry, { create }); fh = await d.getFileHandle(file, { create }); }
      catch (e) { if (e && e.name === 'NotFoundError') return null; throw e; }
      return await fh.createSyncAccessHandle();
    },
    async remove(entry) { const root = await this.root(); try { await root.removeEntry(entry, { recursive: true }); } catch (e) { if (!(e && e.name === 'NotFoundError')) throw e; } },
  };
  const be = () => self.__wcBackend || opfs;
  const available = () => !!self.__wcBackend || (typeof navigator !== 'undefined' && navigator.storage && typeof navigator.storage.getDirectory === 'function');
  const withLock = fn => (typeof navigator !== 'undefined' && navigator.locks) ? navigator.locks.request('scjt-wcache', fn) : fn();
  let verP = null;
  const version = () => verP ??= (async () => {
    if (self.__wcVersion) return self.__wcVersion;
    const txt = await (await fetch(self.location.href)).text();
    return hex(new Uint8Array(await crypto.subtle.digest('SHA-256', enc.encode(txt)))).slice(0, 16);
  })();
  const hex = u => Array.from(u, x => x.toString(16).padStart(2, '0')).join('');
  async function keyOf(wc, c, r, keys, names) {
    const s = JSON.stringify([WC_VERSION, wc.key, c, r, keys, names, await version()]);
    return 'w-' + hex(new Uint8Array(await crypto.subtle.digest('SHA-256', enc.encode(s)))).slice(0, 32);
  }
  async function readAll(h) { const n = h.getSize(); const u = new Uint8Array(n); let at = 0; while (at < n) { const k = h.read(u.subarray(at, Math.min(n, at + WC_CHUNK)), { at }); if (!(k > 0)) throw new Error('short read'); at += k; } return u; }
  async function writeAll(h, u, at) { for (let o = 0; o < u.length; o += WC_CHUNK) { const part = u.subarray(o, Math.min(u.length, o + WC_CHUNK)); let w = 0; while (w < part.length) { const k = h.write(part.subarray(w), { at: at + o + w }); if (!(k > 0)) throw new Error('short write'); w += k; } } }
  async function readMeta(entry) {
    const h = await be().handle(entry, 'meta.json', false); if (!h) return null;
    try { return JSON.parse(dec.decode(await readAll(h))); } catch (_) { return null; } finally { h.close(); }
  }
  async function writeMeta(entry, meta) {
    const h = await be().handle(entry, 'meta.json', true);
    try { const u = enc.encode(JSON.stringify(meta)); h.truncate(0); await writeAll(h, u, 0); h.flush && h.flush(); } finally { h.close(); }
  }
  // 知らせ → { skeleton, bufs: [ArrayBuffer] }
  function encode(msg) {
    const bufs = [], idx = new Map();
    const add = ab => { let i = idx.get(ab); if (i === undefined) { i = bufs.length; bufs.push(ab); idx.set(ab, i); } return i; };
    const walk = v => {
      if (v === null || typeof v === 'boolean' || typeof v === 'string') return v;
      if (typeof v === 'number') return Number.isFinite(v) && !Object.is(v, -0) ? v : { $n: Object.is(v, -0) ? '-0' : String(v) };
      if (v === undefined) return { $u: 1 };
      if (v instanceof ArrayBuffer) return { $ab: add(v) };
      if (ArrayBuffer.isView(v)) {
        if (v instanceof DataView || !TA[v.constructor.name] || !(v.buffer instanceof ArrayBuffer)) throw new Error('unsupported view');
        return { $ta: v.constructor.name, b: add(v.buffer), o: v.byteOffset, l: v.length };
      }
      if (Array.isArray(v)) { const a = new Array(v.length); for (let i = 0; i < v.length; i++) a[i] = walk(v[i]); return a; }
      if (typeof v === 'object' && (Object.getPrototypeOf(v) === Object.prototype || Object.getPrototypeOf(v) === null)) {
        const o = {}; for (const k of Object.keys(v)) { if (k.startsWith('$')) throw new Error('unsupported key ' + k); o[k] = walk(v[k]); } return o;
      }
      throw new Error('unsupported value ' + typeof v);
    };
    return { skeleton: walk(msg), bufs };
  }
  function decode(skeleton, bufs) {
    const walk = v => {
      if (v === null || typeof v !== 'object') return v;
      if (Array.isArray(v)) { const a = new Array(v.length); for (let i = 0; i < v.length; i++) a[i] = walk(v[i]); return a; }
      if ('$n' in v) return v.$n === '-0' ? -0 : Number(v.$n);
      if ('$u' in v) return undefined;
      if ('$ab' in v) return bufs[v.$ab];
      if ('$ta' in v) return new TA[v.$ta](bufs[v.b], v.o, v.l);
      const o = {}; for (const k of Object.keys(v)) o[k] = walk(v[k]); return o;
    };
    return walk(skeleton);
  }
  function uniqueBuffers(msg) {
    const seen = new Set(), out = [];
    const walk = v => {
      if (v === null || typeof v !== 'object') return;
      if (v instanceof ArrayBuffer) { if (!seen.has(v)) { seen.add(v); out.push(v); } return; }
      if (ArrayBuffer.isView(v)) { if (!seen.has(v.buffer)) { seen.add(v.buffer); out.push(v.buffer); } return; }
      if (Array.isArray(v)) { for (const x of v) walk(x); return; }
      for (const k of Object.keys(v)) walk(v[k]);
    };
    walk(msg); return out;
  }
  // 全部の保存の合計 (バイト) と一覧 [{ entry, meta }]
  async function scan() {
    const res = []; let used = 0;
    for (const entry of await be().list()) { const meta = await readMeta(entry); if (!meta || typeof meta.bytes !== 'number') { await be().remove(entry); continue; } used += meta.bytes; res.push({ entry, meta }); }
    return { used, items: res };
  }
  // budget に収める (最後に使った時刻が古いものから消す。keep は消さない)。収めた後の合計を返す
  async function enforce(budget, keep, extra) {
    return await withLock(async () => {
      let { used, items } = await scan();
      items.sort((a, b) => (a.meta.lastUsed || 0) - (b.meta.lastUsed || 0));
      for (const it of items) {
        if (used + (extra || 0) <= Math.max(0, budget)) break;
        if (it.entry === keep) continue;
        await be().remove(it.entry); used -= it.meta.bytes;
      }
      return used;
    });
  }
  // 保存したものがあれば復元した知らせ (無い・合わない・読めない = null)
  async function load(entry, expectKey) {
    try {
      const meta = await readMeta(entry); if (!meta || meta.wk !== expectKey || meta.v !== WC_VERSION) return null;
      const h = await be().handle(entry, 'data.bin', false); if (!h) return null;
      try {
        const total = meta.bufs.reduce((s, b) => s + b.len, 0);
        if (h.getSize() !== total) return null;
        const bufs = meta.bufs.map(b => new ArrayBuffer(b.len));
        for (let i = 0; i < bufs.length; i++) {
          const u = new Uint8Array(bufs[i]); let at = 0;
          while (at < u.length) { const k = h.read(u.subarray(at, Math.min(u.length, at + WC_CHUNK)), { at: meta.bufs[i].off + at }); if (!(k > 0)) return null; at += k; }
        }
        const msg = decode(meta.skeleton, bufs);
        meta.lastUsed = Date.now(); try { await writeMeta(entry, meta); } catch (_) { }
        return msg;
      } finally { h.close(); }
    } catch (_) { return null; }
  }
  // 保存する。保存したら合計 (バイト)、しなかったら null
  async function save(entry, expectKey, msg, wc) {
    try {
      const { skeleton, bufs } = encode(msg);
      let off = 0; const tab = bufs.map(b => { const t = { off, len: b.byteLength }; off += b.byteLength; return t; });
      const size = off + 4096;
      if (!(wc.budget > 0) || size > wc.budget) return null;
      if (typeof wc.free === 'number' && wc.free - size < WC_RESERVE) return null;
      const used = await enforce(wc.budget, entry, size);
      await be().remove(entry);   // 同じ鍵の古い・書きかけを消してから書く
      const h = await be().handle(entry, 'data.bin', true);
      if (!h) return null;
      try {
        h.truncate(0);
        for (let i = 0; i < bufs.length; i++) await writeAll(h, new Uint8Array(bufs[i]), tab[i].off);
        h.flush && h.flush();
      } finally { h.close(); }
      const meta = { v: WC_VERSION, wk: expectKey, bytes: off, bufs: tab, skeleton, lastUsed: Date.now(), created: Date.now() };
      await writeMeta(entry, meta);
      return used + off;
    } catch (e) { try { await be().remove(entry); } catch (_) { } return null; }
  }
  // 保存があり、中身の大きさがそろっているか (読まずに調べる)
  async function has(entry) {
    try { const meta = await readMeta(entry); if (!meta || meta.v !== WC_VERSION) return false; const h = await be().handle(entry, 'data.bin', false); if (!h) return false; try { return h.getSize() === meta.bufs.reduce((s, b) => s + b.len, 0); } finally { h.close(); } } catch (_) { return false; }
  }
  return { available, keyOf, load, save, enforce, scan, encode, decode, uniqueBuffers, has };
})();
self.__WC = WC;

self.onmessage = async e => {
  const m = e.data;
  // 保存の合計を budget に収めて、合計を返す (budget が 0 以下なら全部消す)。画面の側が地点を頼む前・後に使う
  if (m.type === 'wstat') { let used = null; try { if (WC.available()) used = await WC.enforce(m.budget || 0, null, 0); } catch (_) { } self.postMessage({ type: 'wstat', id: m.id, used }); return; }
  if (m.type !== 'build') return;
  const { id, base, region, c, r, keys, names } = m;
  const COLK = keys.col, OCOLK = keys.ocol, LABK = keys.labk;
  const times = {}; let tp = performance.now(); const lap = k => { const t = performance.now(); times[k] = (t - tp) / 1000; tp = t; };
  try {
    // 保存したものがあれば、それを返す (解析しない)。wcache = { key (アプリの鍵。無ければ保存しない), budget, free }。prewarm なら結果は返さず保存だけ
    const wc = m.wcache; let wkey = null;
    if (wc && wc.key && WC.available()) {
      try {
        wkey = await WC.keyOf(wc, c, r, keys, names);
        const used0 = await WC.enforce(wc.budget || 0, wkey, 0);
        const t0 = performance.now();
        if (m.prewarm) { const mt = await WC.has(wkey); if (mt) { self.postMessage({ type: 'done', id, prewarm: true, wcache: { hit: true, used: used0 } }); return; } }
        else {
          const hit = await WC.load(wkey, wkey);
          if (hit) { hit.times = { ...(hit.times || {}), cacheLoad: (performance.now() - t0) / 1000 }; hit.id = id; hit.wcache = { hit: true, used: used0 }; self.postMessage(hit, WC.uniqueBuffers(hit)); return; }
        }
      } catch (_) { wkey = null; }
    }
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
    const parts = [], baseN = {}, overN = {};
    for (const k of COLK) baseN[k] = 0; for (const k of OCOLK) overN[k] = 0;
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
    // 画像 (2026-11-05、利用者の指示「引いたときは色はビットマップ貼り付けにしてほしい」): 区画のファイルの parts の 11 番目 (UV の位置。点ごとの float32 x2) と
    // 12 番目 (画像の番号の位置。三角形ごとの int32。region.json の textures の番号。-1 = 無し)。COLK の種類ごとに、三角形を画像の番号の順 (-1 が先) に並べて詰め、
    // ioffs も同じ並びで作る (押した部品の引き当て = 三角形の番号 → ioffs は崩れない)。uoffs[k] = [UV の位置, 三角形の数 × 6]、toffs[k] = [画像の番号の位置, 三角形の数] (int32)。
    // 判定の色 (OCOLK)・輪郭・当たり判定の荒い区切りは今のまま (三角形の位置 b で引くので、COLK の中の並びが変わっても結果は同じ)
    const texCnt = {}; for (const k of COLK) texCnt[k] = new Map();
    for (const s of sel) {
      const [inst, g, pi, P, I, ii, A] = s; const p = g.parts[pi]; baseN[catOf(p)] += p[5];
      { const tc = texCnt[catOf(p)]; if (p[11] >= 0) { const T = new Int32Array(g.buf, g.base + p[11], p[5]); for (let t = 0; t < p[5]; t++) tc.set(T[t], (tc.get(T[t]) || 0) + 1); } else tc.set(-1, (tc.get(-1) || 0) + p[5]); }
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
    }
    // buf は COLK の三角形の位置 (9 float) と ioffs (部品の番号) だけ。判定の色 (OCOLK) は三角形の位置の複製を持たず、元の三角形の位置 b の並び labs[k] (Uint32Array。b の昇順) で持つ
    // (2026-11-05、親の決定: Orison ±150 m で buf が 1.92 GB と postMessage の上限 2 GB に迫ったため。輪郭線 (loffs) も描いていないので作らない)
    let pos = 0; const offs = {}, ioffs = {}, uoffs = {}, toffs = {};
    for (const k of COLK) { offs[k] = [pos, baseN[k] * 9]; pos += baseN[k] * 9; }
    for (const k of COLK) { ioffs[k] = [pos, baseN[k]]; pos += baseN[k]; }
    const labs = {}, wlab = {};
    for (const k of OCOLK) { labs[k] = new Uint32Array(overN[k]); wlab[k] = 0; }
    // UV・画像の番号の列は buf とは別の ArrayBuffer (uvbuf・texbuf。buf は位置・ioffs・OCOLK・輪郭だけで、前と同じ中身)。1 つの ArrayBuffer が 2 GB (postMessage の上限) を超えないため。
    // どの部品にも UV が無い (画像なしの設定) なら作らない (null。長さ 0)
    const anyUv = sel.some(s => s[1].parts[s[2]][10] >= 0);
    let upos = 0, tpos = 0;
    for (const k of COLK) { uoffs[k] = [upos, anyUv ? baseN[k] * 6 : 0]; upos += uoffs[k][1]; }
    for (const k of COLK) { toffs[k] = [tpos, anyUv ? baseN[k] : 0]; tpos += toffs[k][1]; }
    const buf = new ArrayBuffer(pos * 4); const F = new Float32Array(buf), U = new Uint32Array(buf);
    const uvbuf = anyUv ? new ArrayBuffer(upos * 4) : null, texbuf = anyUv ? new ArrayBuffer(tpos * 4) : null;
    const FU = uvbuf ? new Float32Array(uvbuf) : null, S = texbuf ? new Int32Array(texbuf) : null;
    // COLK の中の書く位置は画像の番号ごと (番号の昇順。-1 が先): 三角形の通し番号 q → 位置 offs + q*9・ioffs + q・uoffs + q*6・toffs + q
    const wq = {};
    for (const k of COLK) { const m = new Map(); let q = 0; for (const id of [...texCnt[k].keys()].sort((a, b) => a - b)) { m.set(id, q); q += texCnt[k].get(id); } wq[k] = m; }
    const pbox = [];
    for (const s of sel) {
      const [inst, g, pi, P, I, ii, A, lab] = s; const p = g.parts[pi]; const id_ = parts.length;
      const W = worldVerts(P, A);
      const bl = [Infinity, Infinity, Infinity], bh = [-Infinity, -Infinity, -Infinity];
      const cnt = {};
      const k = catOf(p);
      const UV = p[10] >= 0 ? new Float32Array(g.buf, g.base + p[10], p[3] * 2) : null, TX = p[11] >= 0 ? new Int32Array(g.buf, g.base + p[11], p[5]) : null;
      const qk = wq[k], ob = offs[k][0], ib = ioffs[k][0], ub = uoffs[k][0], tb = toffs[k][0];
      for (let t = 0; t < p[5]; t++) {
        const vs = [I[t * 3] * 3, I[t * 3 + 1] * 3, I[t * 3 + 2] * 3];
        const tid = TX ? TX[t] : -1; const q = qk.get(tid); qk.set(tid, q + 1);
        let o = ob + q * 9;
        for (const v of vs) for (let a = 0; a < 3; a++) { F[o++] = W[v + a] - c[a]; if (W[v + a] < bl[a]) bl[a] = W[v + a]; if (W[v + a] > bh[a]) bh[a] = W[v + a]; }
        U[ib + q] = id_;
        if (anyUv) { S[tb + q] = tid; if (UV) { let u = ub + q * 6; for (const v of vs) { const vi = v / 3 * 2; FU[u++] = UV[vi]; FU[u++] = UV[vi + 1]; } } }
        if (lab[t]) {
          const lk = LABK[lab[t]]; cnt[lk] = (cnt[lk] || 0) + 1;
          labs[lk][wlab[lk]++] = ob + q * 9;
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
      const sp = (inst[5] || '').split('/').pop();
      parts.push([inst[0], p[0], k, Object.entries(cnt).map(([kk, n]) => `${names[kk]} ${n}`).join(', ') || '歩ける面に色なし', sp, '']);
      // 4 番目: エレベーターの扉の前 (ロビー側) の向き (場所の座標の xy、正規化)。区画のファイルの parts の 10 番目 (socpak の枠の向き) をインスタンスの行列 A で回したもの。無い・xy の成分が無ければ null。
      // 5 番目: エレベーターの扉か (parts の 10 番目があるか。扉の見分け (名前の語に elevator と door/doors、または door/doors があり部品の種類に "/elev/") はアプリの BuildGeo が行う。
      //   部品の種類 (CgfPath) は区画のファイルに無いので、糸では見分けない。2026-11-05、親の決定)
      let fwd = null; const isElevDoor = !!p[9];
      if (p[9]) { const [vx, vy, vz] = p[9]; const fx = A[0] * vx + A[1] * vy + A[2] * vz, fy = A[4] * vx + A[5] * vy + A[6] * vz, l = Math.hypot(fx, fy); if (l > 1e-6) fwd = [+(fx / l).toFixed(4), +(fy / l).toFixed(4)]; }
      pbox.push([bl, bh, p[0].split('_'), fwd, isElevDoor]);
    }
    const ladders = [];
    pbox.forEach(([bl, bh, w], i) => { if (w.includes('ladder')) ladders.push([parts[i][1], parts[i][0], bl.map((v, a) => +(v - c[a]).toFixed(3)), bh.map((v, a) => +(v - c[a]).toFixed(3))]); });
    // エレベーターの扉 (アプリの BuildGeo が見分けて parts の 10 番目を付けたもの。pbox の 5 番目) の箱と前の向き [名前, 箱の低い角, 高い角, [fx, fy] | null] (箱は中心からの相対。向きは場所の座標の xy、正規化)。
    // エレベーターで移った先の立つ所に使う (view3d.js の elevPlace。2026-11-05、利用者の指示「指示はドアの向きを見て」)
    const elevDoors = [];
    pbox.forEach(([bl, bh, w, fwd, isElevDoor], i) => { if (isElevDoor) elevDoors.push([parts[i][1], bl.map((v, a) => +(v - c[a]).toFixed(3)), bh.map((v, a) => +(v - c[a]).toFixed(3)), fwd]); });
    // 階段の部品 (名前の語に stair・stairs があり、手すり・柱・飾りの語の無いもの。アプリの StationFloorBuilder.IsStairMesh と同じ決まり) の箱 [名前, 低い角, 高い角] (中心からの相対)。
    // 行き先の一覧の「階段」に使う (2026-11-04、利用者の決定「作り直す」)
    const STAIR_NOT = new Set(['rail', 'railing', 'decal', 'wall', 'structure', 'strut', 'divider', 'platform', 'cap', 'capplate', 'addon', 'sign', 'cover']);
    const stairs = [];
    pbox.forEach(([bl, bh, w], i) => { if (w.some(x => x === 'stair' || x === 'stairs') && !w.some(x => STAIR_NOT.has(x)) && bh[2] - bl[2] >= 0.5) stairs.push([parts[i][1], bl.map((v, a) => +(v - c[a]).toFixed(3)), bh.map((v, a) => +(v - c[a]).toFixed(3))]); });
    const pidx = sel.map((s, i) => [s[0][0] + ':' + s[2], i]);
    // 判定の三角形の位置を b の昇順に (COLK の中は画像の番号の順なので、部品の順 = b の順ではない。画像なしなら部品の順 = b の順で、並べ替えても同じ)
    for (const k of OCOLK) labs[k].sort();
    geoCache.clear();
    lap('buildCur');
    // ===== 当たり判定の荒い区切り (coarse) =====
    // (2026-11-05、利用者の指示「当たり判定も範囲を決めればメモリ節約になるんじゃない？」「物理的にいけないところを省いて、直線で」)
    // 以前はここで 1 m (頭の上は 2 m) の升目 f・c・w・fl を全部作って渡していた (Seraphim ±30 m で約 4 GB・約 124 秒)。
    // 今は 16 m の箱 (鍵 = floor(x/16) × 100003 + floor(y/16)。中心からの相対) ごとに、箱に掛かる三角形の位置 b を並べた一覧だけを作る。
    // 升そのものは、画面の側 (view3d.js の cellGet) が升を引いたときに、その升の入る箱の一覧から以前の升目と同じ条件で選んで作る (中身・並びは以前の升目と同じ)。
    //   all: COLK の全三角形 (f・c・w の元。ドアも入れる。ドアを除くのは画面の側)、fl: 判定が floor2・tube2 の三角形 (判定の床の元。labs の b の並び。元の三角形 (COLK) の位置)。
    //   big: 16 m の箱で 64 箱を超える大きな三角形 (親の決定) は、箱の一覧に入れず別の並び (bigAll・bigFl) に入れる。升を作るときは箱の一覧と big の両方を見る
    //   (ここで三角形を落とすことはしない。以前の升目の上限 (1 m の升で 4096・2 m で 1024) は画面の側が升を作るときに同じ条件で掛ける)。
    // 形: tableLayout と同じ (並べた鍵 keys・始まり starts・中身 items)。2 回走査 (数える → 詰める)。中身の並びは三角形の位置 b の昇順 (all は COLK の順・三角形の順 = b の順。fl は floor2 の b の昇順、続けて tube2 の b の昇順)
    const BOX = 16, BIG_BOXES = 64;
    const csweep = (add, big, only) => {
      const one = b => {
        const I0 = Math.floor(Math.min(F[b], F[b + 3], F[b + 6]) / BOX), I1 = Math.floor(Math.max(F[b], F[b + 3], F[b + 6]) / BOX), J0 = Math.floor(Math.min(F[b + 1], F[b + 4], F[b + 7]) / BOX), J1 = Math.floor(Math.max(F[b + 1], F[b + 4], F[b + 7]) / BOX);
        if ((I1 - I0 + 1) * (J1 - J0 + 1) > BIG_BOXES) { big(b); return; }
        for (let I = I0; I <= I1; I++) for (let J = J0; J <= J1; J++) add(I * 100003 + J, b);
      };
      if (only === 0) { for (const k of COLK) { const [o, n] = offs[k]; for (let t = 0; t < n / 9; t++) one(o + t * 9); } }
      else for (const k of ['floor2', 'tube2']) { const L = labs[k]; if (L) for (let i = 0; i < L.length; i++) one(L[i]); }
    };
    const coarseOf = gi => {
      const T = makeTable(); const bigs = [];
      csweep(key => tableCount(T, key), b => bigs.push(b), gi);
      const out = tableLayout(T);
      csweep((key, b) => { out.items[T.pos[tableSlot(T, key)]++] = b; }, () => { }, gi);
      T.keys = null; T.cnt = null; T.pos = null;
      return { grid: out.grid, big: Uint32Array.from(bigs) };
    };
    const ca = coarseOf(0), cf = coarseOf(1);
    const coarse = { all: ca.grid, fl: cf.grid, bigAll: ca.big, bigFl: cf.big };
    lap('coarse');
    const tr = [buf];
    if (uvbuf) tr.push(uvbuf, texbuf);
    for (const k of OCOLK) tr.push(labs[k].buffer);
    for (const g of [coarse.all, coarse.fl]) tr.push(g.keys.buffer, g.starts.buffer, g.items.buffer);
    tr.push(coarse.bigAll.buffer, coarse.bigFl.buffer);
    const out = { type: 'done', id, buf, uvbuf, texbuf, offs, ioffs, labs, uoffs, toffs, parts, ladders, elevDoors, stairs, fz, fa, pidx, lightParts: [...lightParts], coarse, times };
    // 保存する (転送すると中身が消えるので、先に書く)。保存した中身は times を除いた知らせ。書いた時間は times.cacheSave
    let wused = null;
    if (wkey) { const t1 = performance.now(); const { times: _t, id: _i, ...stored } = out; wused = await WC.save(wkey, wkey, { type: 'done', ...stored }, wc); times.cacheSave = (performance.now() - t1) / 1000; }
    if (m.prewarm) { self.postMessage({ type: 'done', id, prewarm: true, wcache: { hit: false, used: wused } }); return; }
    out.wcache = { hit: false, used: wused };
    self.postMessage(out, tr);
  } catch (err) {
    geoCache.clear();
    self.postMessage({ type: 'error', id, message: String(err && err.message || err) });
  }
};
