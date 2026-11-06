// ステーションの 3D (新しい方式) の、地点の区画の読み取り・解析を画面の糸の外で行う (2026-11-04、利用者の指摘「移動して読み込む時止まるのはどうにかなる？」)。
// ===== 升ごとに持つ形 (2026-11-06 の作り替え) =====
// (利用者の指示「広いところならいいが、ステーション内なら 150m もみても仕方なくないか？ ほんらい遠いところも小さいパーツは省くべきで、近くなったら疲労、遠ざかったら捨てるが 50m 単位3段階くらいじゃないか？
//  開けたとこでも150m 以上先は逆に建物など大まかなものが取れた方がいいし」「50 / 100 / 150 で区切り、10m 移動ごと、エレベータやリフトや階段はしごリフト移動ごとに見直し」
//  「細かく書く範囲戸当たり判定は一致しないとだめではないのか？ 読み込みで止まるんだからどっちかが短ければ意味ないよな？」。親の決定)
// 前は、地点 (中心・半径の立方体) の全部の三角形を 1 つの ArrayBuffer にまとめて画面の側へ渡していた (Checkmate の ±150 m で約 5,700 万三角形・2.3 GB)。
// 今は、地点の区画のファイル (g/<番号>.bin) をこの糸が持ったままにし、部品を「升 × 部品の大きさの組 × 外装・内装」のかたまりに分けた索引だけを先に返し、
// 画面の側が頼んだかたまり (升のかたまり) だけを、その都度作って渡す。かたまりには「描く形」と「当たり」が一緒に入る (描く範囲と当たりの範囲は、同じかたまりの出し入れで決まる)。
//   升: 場所の座標で固定の格子。1 辺 CELL m の立方体 (x・y・z とも floor(場所の座標 / CELL))。
//   部品の大きさ = 置かれた部品の箱 (区画のファイルの部品の箱の 8 隅を場所の座標にした箱) のいちばん長い辺。組は 小 (0)・中 (1)・大 (2)・大きな物体 (3)。境は画面の側が渡す (sizes = [小と中, 中と大, 大きな物体])。
//   1 つの部品は、箱の中心の入る升 1 つに入れる (升をまたぐ部品も丸ごとその升のかたまりに入る。三角形は切らない)。
//   外装・内装: 区画 (socpak) のパスの語で決める (socInterior。前の「読む部品を選ぶ」と同じ決まり)。かたまりの種類 cls = 大きさの組 + (内装なら 4) + (下の KEEP なら 8) + (場所の全部の大きな物体なら 16)。
//   KEEP (8): 小の組の部品のうち、前の「読む部品を選ぶ」で大きさ・距離に関係なく残していた部品 (keepAlways: エレベーターの扉・はしご・階段・判定の色のある部品・上下が逆の重力の範囲に掛かる部品)。
//     画面の側は、速さに関係なく (走っている間も) 読む (2026-11-06、親の決定: 前の版の動きを戻す。走っている間に 1 m 未満の床の部品の所で床が無くなるため)。距離の決まりは小の組と同じ
//   場所の全部の大きな物体 (16): アプリが場所ごとに 1 回作って保存する「大きな物体の境以上の部品だけ」の区画のファイル (StationView3D.Session.BuildBigAsync) を { type:'big' } で受け取り、
//     今の地点の索引に無い置かれた所の部品 (= アプリが取り出した立方体に掛からない区画) を、大きな物体の組のかたまりとして足す (地点の区画と同じ置かれた所は二重に出さない)。
//     判定の色はアプリが付けていない (0)。その区画に近づいたときは、地点の取り出し直しで地点の索引に入り、そちらの判定の色つきの形に替わる
// 受け取る:
//   { type:'build', id, og, base (地点の置き場の URL), region (region.json), o (座標の原点 = 画面の側の cur.center), c・r (地点の立方体の中心・半径), keys: { col, ocol, labk }, names, pal, site, sizes }
//     → { type:'progress', id, done, total } (用意した区画のファイルの数)、{ type:'done', id, index, textures, texGen, ladders, elevDoors, stairs, fz, fa, lightParts, entParts, stat, times }
//     (index = { keys: ['ix,iy,iz,cls', …], cells (Int32Array x3), cls (Uint8Array), tris (Uint32Array), parts (Uint32Array), hash (Uint32Array) }。hash = かたまりの中身 (部品の並び) の印。
//      地点を読み直しても、同じ鍵で hash が同じなら、画面の側はそのかたまりを作り直さない)
//   { type:'sizes', id, og, sizes } → { type:'index', id, index, stat } (部品の大きさの境を変えたとき。索引を分け直す)
//   { type:'big', id, og, base, region, thr, site, pal } → { type:'bigReady', id, og, index, textures, stat } (場所の全部の大きな物体。読み込んで持ち、今の地点の索引を分け直して返す)
//   { type:'chunk', id, og, key } → { type:'chunk', id, og, key, hash, cls, texGen, F, ID, LB, FL, UV, TX, offs, parts, labN, box, col: { g2, g16, big } } (無ければ { type:'chunk', id, og, key, missing: true })
//     F = 三角形の位置 (原点 o からの相対。float32 x9)。COLK の種類ごとに offs[k] = [F の中の位置, 長さ]。種類の中は画像の番号の順 (-1 が先)。
//     ID = 三角形 → かたまりの中の部品の番号、LB = 三角形の判定の色 (LABK の番号)、UV・TX = UV (x6) と画像の番号 (無ければ null)、box[k] = 種類ごとの箱 [lo, hi]。
//     FL = 三角形の当たりの印 (UP・DOWN・DOOR・FLR・OK1・OK2)、col = 当たりの索引 (下の「当たり」)
//   { type:'part', id, og, key ('インスタンス:部品の番号') } → { type:'part', id, tris (Float32Array。原点からの相対), lab (Uint8Array), part } (部品の強調用。無ければ missing)
//   { type:'out', id, og, kind:'scan', p, dist, floor, foot } → { type:'out', id, outside } / { kind:'cross', a, b } → { type:'out', id, hit } (外判定。下の「外判定」)
//   { type:'wpurge', id } → { type:'wstat', id, used } (前の形の保存 (解析の結果を丸ごと OPFS に置いていたもの) を消す)
// 失敗は { type:'error', id, message }
// 前にあった、解析の結果の丸ごとの保存 (WC)・大きい ArrayBuffer を分けて渡す処理 (postDone・xferHead など。2026-11-06) は、1 つの大きなかたまりを作らなくなったので外した
'use strict';
const CELL = 10;
// 外装か内装かは、区画 (socpak) のパス (region.json の instances の 6 番目。ObjectContainers の下の相対パスの小文字) のフォルダの名前で決める (ファイルの名前は見ない):
//   外装: フォルダの名前を _ で分けた語に EXT_WORDS がある (ext・ext_cargo・reststop_ext・hab_a_ext など)。
//   内装: 外装でなく、フォルダの名前の語に INT_WORDS がある (int・reststop_int・hab_a_int など) か、パスが INT_DIRS のどれかで始まる。
//   どちらでもない区画は外装として扱う
const PICK = {
  EXT_WORDS: ['ext', 'exterior'], INT_WORDS: ['int', 'interior', 'interiors'],
  INT_DIRS: ['pu/loc/mod/common/hab/', 'pu/loc/mod/common/medical/', 'setup/spawn_closet/', 'setup/vent_spawncloset/'],
};
// 区画のパス → 内装か (true = 内装。外装・どちらでもない = false)
function socInterior(path) {
  const words = new Set(); for (const seg of path.split('/').slice(0, -1)) for (const w of seg.split('_')) words.add(w);
  if (PICK.EXT_WORDS.some(w => words.has(w))) return false;
  return PICK.INT_WORDS.some(w => words.has(w)) || PICK.INT_DIRS.some(d => path.startsWith(d));
}
// 階段の部品 (名前の語に stair・stairs があり、手すり・柱・飾りの語の無いもの。アプリの StationFloorBuilder.IsStairMesh と同じ決まり)
const STAIR_NOT = new Set(['rail', 'railing', 'decal', 'wall', 'structure', 'strut', 'divider', 'platform', 'cap', 'capplate', 'addon', 'sign', 'cover']);
const COS_WALK_INV = Math.cos(46 * Math.PI / 180);
// ===== 当たり =====
// (2026-11-06、利用者の指摘 (升を踏んだその場で作る形について)「馬鹿なやり方だと思うのは俺だけか？」「止まるのは当たり前だよな？」。親の決定: 当たりは、升を用意するときに一緒に作る)
// かたまりの三角形ごとに印 FL を付け、xy の升目の索引 col を作って一緒に渡す。画面の側 (view3d.js の cellTris) は、引いた升の入る索引の一覧から、印と升の条件 (前の升目と同じ条件) で選ぶだけ。
//   印: UP = 上向き (cz > 0)、DOWN = 下向き (cz < 0)、DOOR = ドアの部品 (名前の語に door・doors があり elevator の無い部品)、FLR = 判定の床 (判定の色が floor2・tube2)、
//       OK1 = 1 m の升の数が 4096 以下、OK2 = 2 m の升の数が 1024 以下 (前の升目の上限と同じ)
//   索引: 三角形の掛かる 2 m の升 (floor(x/2), floor(y/2)。原点からの相対) の数が BIG2 以下なら g2 (2 m の升ごとの一覧) に、それより大きく 16 m の升の数が BIG16 以下なら g16 (16 m の升ごとの一覧) に、
//       それより大きい三角形は big (並び) に入れる (どの三角形も 3 つのどれか 1 つに入る)。形: 並べた鍵 (I × 100003 + J)・始まり・中身 (三角形の位置 b)
const UP = 1, DOWN = 2, DOOR = 4, FLR = 8, OK1 = 16, OK2 = 32, BIG2 = 64, BIG16 = 64;
let GEO = new Map();   // 区画 (socpak) のパス → 読んだ区画のファイル (今の地点の分。次の地点で同じ区画・同じ置かれた所がそろっていれば使い回す)
let R = null;          // 今の地点 (索引)
let BIG = null;         // 場所の全部の大きな物体 (地点と同じ形の持ち物 + site・pal・thr・textures)
let TEXGEN = 0;        // 画像の番号の並びの代 (region.json の textures の並びが前の地点と合わなくなるたびに増える。かたまりに付けて返す)
async function fetchGeo(url) {
  const resp = await fetch(url);
  const buf = await resp.arrayBuffer();
  if (!resp.ok || buf.byteLength < 24) throw new Error(`区画のファイルを読めませんでした: ${url} (応答 ${resp.status}・${buf.byteLength} バイト)`);
  const dv = new DataView(buf);
  const magic = new TextDecoder().decode(new Uint8Array(buf, 0, 8)); if (magic !== 'SCJT3DG2') throw new Error('形の違うファイル ' + url);
  const hp = Number(dv.getBigInt64(8, true)), jl = dv.getInt32(16, true); const head = JSON.parse(new TextDecoder().decode(new Uint8Array(buf, hp, jl)));
  const g = { buf, base: 24, parts: head.parts, inst: new Map(head.inst.map(x => [x[0], x])), triStart: [] };
  let s = 0; for (const p of g.parts) { g.triStart.push(s); s += p[5]; } g.tris = s;
  return g;
}
function worldVerts(P, A) {
  const W = new Float64Array(P.length);
  for (let i = 0; i < P.length; i += 3) { const x = P[i], y = P[i + 1], z = P[i + 2]; W[i] = A[0] * x + A[1] * y + A[2] * z + A[3]; W[i + 1] = A[4] * x + A[5] * y + A[6] * z + A[7]; W[i + 2] = A[8] * x + A[9] * y + A[10] * z + A[11]; }
  return W;
}
// 部品の名前から決まるもの (区画の部品 p に 1 回だけ求めて覚える。同じ区画が何か所にも置かれる):
//   light = 名前の語に light、cat = 種類 (light は除外、種類 other で名前の語に door・doors があればドア)、
//   door = 当たりのドア (名前の語に door・doors があり elevator の無い部品。前の view3d.js の triIsDoor と同じ決まり)、ladder・stair = はしご・階段の部品か
function metaOf(p) {
  let m = p.__m;
  if (!m) {
    const w = p[0].split('_'), light = w.includes('light'), dw = w.some(x => x === 'door' || x === 'doors');
    m = p.__m = { light, cat: light ? 'excluded' : p[1] === 'other' && dw ? 'door' : p[1], door: dw && !w.includes('elevator'), ladder: w.includes('ladder'), stair: w.some(x => x === 'stair' || x === 'stairs') && !w.some(x => STAIR_NOT.has(x)) };
  }
  return m;
}
const isLight = p => metaOf(p).light;
const catOf = p => metaOf(p).cat;
// 上下が逆の重力の範囲 (region.json の gravity の up。2026-11-04、利用者が決めた案 1「重力が上向きの範囲では、下向きの面を床にし、上向きの面は床にしない」):
// 範囲の中 (三角形の重心) では、上向きの床 (判定 floor2) を床にせず、下向きの面 (法線 z ≤ -COS_WALK_INV) を床 (floor2) にする。
// アプリの判定 2 (同じ平面の形をまとめる) はせず、三角形ごとに決める (範囲の外は今までどおり)
function upVolsOf(region) {
  return (region.gravity || []).filter(g => g[0] === 'up').map(([, W, size, poly, h]) => {
    const a = W[0], b = W[1], d = W[2], e = W[4], f = W[5], g2 = W[6], hh = W[8], i = W[9], j = W[10];
    const det = a * (f * j - g2 * i) - b * (e * j - g2 * hh) + d * (e * i - f * hh);
    const Rm = [(f * j - g2 * i) / det, (d * i - b * j) / det, (b * g2 - d * f) / det, (g2 * hh - e * j) / det, (a * j - d * hh) / det, (d * e - a * g2) / det, (e * i - f * hh) / det, (b * hh - a * i) / det, (a * f - b * e) / det];
    // 範囲の箱 (場所の座標の AABB。部品の箱と比べて、掛からない部品は見ない)
    const corners = size ? [[-1, -1, -1], [1, -1, -1], [-1, 1, -1], [1, 1, -1], [-1, -1, 1], [1, -1, 1], [-1, 1, 1], [1, 1, 1]].map(([x, y, z]) => [x * size[0] / 2, y * size[1] / 2, z * size[2] / 2])
      : poly.flatMap(q => [[q[0], q[1], 0], [q[0], q[1], h]]);
    const lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
    for (const [x, y, z] of corners) for (let k = 0; k < 3; k++) { const v = W[k * 4] * x + W[k * 4 + 1] * y + W[k * 4 + 2] * z + W[k * 4 + 3]; lo[k] = Math.min(lo[k], v); hi[k] = Math.max(hi[k], v); }
    return { W, R: Rm, size, poly, h, lo, hi };
  }).filter(v => isFinite(v.R[0]));
}
function inUp(upVols, x, y, z) {
  return upVols.some(v => {
    if (x < v.lo[0] || x > v.hi[0] || y < v.lo[1] || y > v.hi[1] || z < v.lo[2] || z > v.hi[2]) return false;
    const px = x - v.W[3], py = y - v.W[7], pz = z - v.W[11], Rm = v.R;
    const lx = Rm[0] * px + Rm[1] * py + Rm[2] * pz, ly = Rm[3] * px + Rm[4] * py + Rm[5] * pz, lz = Rm[6] * px + Rm[7] * py + Rm[8] * pz;
    if (v.size) return Math.abs(lx) <= v.size[0] / 2 && Math.abs(ly) <= v.size[1] / 2 && Math.abs(lz) <= v.size[2] / 2;
    if (lz < 0 || lz > v.h) return false;
    let inside = false; const P_ = v.poly;
    for (let k = 0, m = P_.length - 1; k < P_.length; m = k++) if ((P_[k][1] > ly) !== (P_[m][1] > ly) && lx < (P_[m][0] - P_[k][0]) * (ly - P_[k][1]) / (P_[m][1] - P_[k][1]) + P_[k][0]) inside = !inside;
    return inside;
  });
}
// 部品 (地点 Rg の ri 番目) の点 (場所の座標)・三角形の番号・判定の色。判定の色は、前の buildCur と同じ決まりで直したもの:
//   名前に light のある部品で、アプリの判定の色が 1 つも無いものは、色なし (除外のまま)。(2026-11-04、利用者の決定「上場の高さが床とほぼ同じ物体は除去しない」: 色の付いたものは色を使う)
//   上下が逆の重力の範囲の中の三角形は、下向きの面を床にし、上向きの床を床にしない
function partGeom(Rg, ri) {
  const cd = Rg.cand[Rg.CI[ri]], pi = Rg.PI[ri], g = cd.g, p = g.parts[pi];
  const P = new Float32Array(g.buf, g.base + p[2], p[3] * 3), I = p[8] === 2 ? new Uint16Array(g.buf, g.base + p[4], p[5] * 3) : new Uint32Array(g.buf, g.base + p[4], p[5] * 3);
  let lab = new Uint8Array(g.buf, g.base + cd.ii[1] + g.triStart[pi], p[5]), lightEx = false;
  if (isLight(p) && !lab.some(v => v)) { lab = new Uint8Array(p[5]); lightEx = true; }
  const W = worldVerts(P, cd.A);
  if (Rg.upVols.length && Rg.FLOOR2 > 0 && !lightEx) {
    let lab2 = null;
    for (let t = 0; t < p[5]; t++) {
      const v0 = I[t * 3] * 3, v1 = I[t * 3 + 1] * 3, v2 = I[t * 3 + 2] * 3;
      const cx = (W[v0] + W[v1] + W[v2]) / 3, cy = (W[v0 + 1] + W[v1 + 1] + W[v2 + 1]) / 3, cz = (W[v0 + 2] + W[v1 + 2] + W[v2 + 2]) / 3;
      if (!inUp(Rg.upVols, cx, cy, cz)) continue;
      const ux = W[v1] - W[v0], uy = W[v1 + 1] - W[v0 + 1], uz = W[v1 + 2] - W[v0 + 2], wx = W[v2] - W[v0], wy = W[v2 + 1] - W[v0 + 1], wz = W[v2 + 2] - W[v0 + 2];
      const nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx, nl = Math.hypot(nx, ny, nz); if (!(nl > 0)) continue;
      const want = nz / nl <= -COS_WALK_INV ? Rg.FLOOR2 : lab[t] === Rg.FLOOR2 ? 0 : lab[t];
      if (want !== lab[t]) { lab2 ??= Uint8Array.from(lab); lab2[t] = want; }
    }
    if (lab2) lab = lab2;
  }
  return { cd, pi, p, P, I, W, lab };
}
// 部品の大きさの境 sizes で、部品を「升 × 大きさの組 × 外装・内装」のかたまりに分ける (索引)。三角形の無い部品は入れない
function bucket(Rg, sizes) {
  const [s1, s2, s3] = sizes; const B = new Map(); const BX = Rg.BX;
  // stat の並びは cls & 7 (外装 小・中・大・大きな物体、内装 小・中・大・大きな物体)。keep = KEEP の部品、big = 場所の全部の大きな物体から足した部品 [部品, 三角形]
  const stat = { parts: [0, 0, 0, 0, 0, 0, 0, 0], tris: [0, 0, 0, 0, 0, 0, 0, 0], keep: [0, 0], big: [0, 0] };
  const put = (src, ri, cls, nt) => {
    const BX = src.BX, o = ri * 6;
    const ix = Math.floor((BX[o] + BX[o + 3]) / 2 / CELL), iy = Math.floor((BX[o + 1] + BX[o + 4]) / 2 / CELL), iz = Math.floor((BX[o + 2] + BX[o + 5]) / 2 / CELL);
    const key = ix + ',' + iy + ',' + iz + ',' + cls;
    let a = B.get(key); if (!a) B.set(key, a = { ix, iy, iz, cls, list: [], tris: 0, hash: 0, rg: src });
    a.list.push(ri); a.tris += nt; stat.parts[cls & 7]++; stat.tris[cls & 7] += nt;
  };
  for (let ri = 0; ri < Rg.n; ri++) {
    const cd = Rg.cand[Rg.CI[ri]], nt = cd.g.parts[Rg.PI[ri]][5]; if (!nt) continue;
    const o = ri * 6, size = Math.max(BX[o + 3] - BX[o], BX[o + 4] - BX[o + 1], BX[o + 5] - BX[o + 2]);
    let cls = (size >= s3 ? 3 : size >= s2 ? 2 : size >= s1 ? 1 : 0) | (cd.interior ? 4 : 0);
    if ((cls & 3) === 0 && Rg.keep[ri]) { cls |= 8; stat.keep[0]++; stat.keep[1] += nt; }
    put(Rg, ri, cls, nt);
  }
  // 場所の全部の大きな物体: 同じ場所・同じ色付けの物だけ。今の地点の索引にある置かれた所の部品は足さない。大きさが境 s3 以上の部品だけ
  const Bg = BIG && BIG.site === Rg.site && BIG.pal === Rg.pal ? BIG : null;
  if (Bg) for (let ri = 0; ri < Bg.n; ri++) {
    const cd = Bg.cand[Bg.CI[ri]]; if (Rg.instCi.has(cd.inst[0])) continue;
    const nt = cd.g.parts[Bg.PI[ri]][5]; if (!nt) continue;
    const o = ri * 6, X = Bg.BX, size = Math.max(X[o + 3] - X[o], X[o + 4] - X[o + 1], X[o + 5] - X[o + 2]); if (size < s3) continue;
    put(Bg, ri, 3 | (cd.interior ? 4 : 0) | 16, nt); stat.big[0]++; stat.big[1] += nt;
  }
  // 中身の印 (FNV-1a): 色付け・部品 (インスタンスの番号と部品の番号) の並び
  const n = B.size, keys = new Array(n), cells = new Int32Array(n * 3), cls = new Uint8Array(n), tris = new Uint32Array(n), parts = new Uint32Array(n), hash = new Uint32Array(n);
  let q = 0;
  for (const [key, a] of B) {
    let h = Rg.pal ? 0x811c9dc5 : 0x9dc5811c;
    const mix = v => { for (let s = 0; s < 32; s += 8) { h ^= (v >>> s) & 255; h = Math.imul(h, 0x01000193); } };
    const src = a.rg; if (src !== Rg) mix(0xB16);
    for (const ri of a.list) { mix(src.cand[src.CI[ri]].inst[0] | 0); mix(src.PI[ri]); }
    a.hash = h >>> 0;
    keys[q] = key; cells[q * 3] = a.ix; cells[q * 3 + 1] = a.iy; cells[q * 3 + 2] = a.iz; cls[q] = a.cls; tris[q] = a.tris; parts[q] = a.list.length; hash[q] = a.hash; q++;
  }
  Rg.buckets = B; Rg.sizes = sizes.slice();
  return { index: { keys, cells, cls, tris, parts, hash }, stat };
}
// 当たりの印と索引 (上の「当たり」)。F = かたまりの三角形の位置、FL = 印 (DOOR・FLR は入れてある。ここで UP・DOWN・OK1・OK2 を足す)
function buildCol(F, nTri, FL) {
  const lvl = new Uint8Array(nTri), big = [], RG = new Int32Array(nTri * 4);
  // bb[L] = 段 L (0 = 2 m、1 = 16 m) の升の番号の範囲 [a の最小, 最大, c の最小, 最大]
  const bb = [[Infinity, -Infinity, Infinity, -Infinity], [Infinity, -Infinity, Infinity, -Infinity]];
  for (let q = 0; q < nTri; q++) {
    const b = q * 9;
    const x0 = Math.min(F[b], F[b + 3], F[b + 6]), x1 = Math.max(F[b], F[b + 3], F[b + 6]), y0 = Math.min(F[b + 1], F[b + 4], F[b + 7]), y1 = Math.max(F[b + 1], F[b + 4], F[b + 7]);
    const cz = (F[b + 3] - F[b]) * (F[b + 7] - F[b + 1]) - (F[b + 4] - F[b + 1]) * (F[b + 6] - F[b]);
    const n1 = (Math.floor(x1) - Math.floor(x0) + 1) * (Math.floor(y1) - Math.floor(y0) + 1);
    let a0 = Math.floor(x0 / 2), a1 = Math.floor(x1 / 2), c0 = Math.floor(y0 / 2), c1 = Math.floor(y1 / 2);
    const n2 = (a1 - a0 + 1) * (c1 - c0 + 1);
    FL[q] |= (cz > 0 ? UP : 0) | (cz < 0 ? DOWN : 0) | (n1 <= 4096 ? OK1 : 0) | (n2 <= 1024 ? OK2 : 0);
    let L = 0;
    if (!(n2 <= BIG2)) { a0 = Math.floor(x0 / 16); a1 = Math.floor(x1 / 16); c0 = Math.floor(y0 / 16); c1 = Math.floor(y1 / 16); L = (a1 - a0 + 1) * (c1 - c0 + 1) <= BIG16 ? 1 : 2; }
    lvl[q] = L;
    if (L === 2) { big.push(b); continue; }
    RG[q * 4] = a0; RG[q * 4 + 1] = a1; RG[q * 4 + 2] = c0; RG[q * 4 + 3] = c1;
    const B = bb[L]; if (a0 < B[0]) B[0] = a0; if (a1 > B[1]) B[1] = a1; if (c0 < B[2]) B[2] = c0; if (c1 > B[3]) B[3] = c1;
  }
  // 升の番号の範囲が広すぎるとき (とても遠くまで散らばったかたまり) は、表を作らずに Map で数える (結果は同じ)
  const gridMap = L => {
    const m = new Map();
    for (let q = 0; q < nTri; q++) { if (lvl[q] !== L) continue; const r = q * 4; for (let a = RG[r]; a <= RG[r + 1]; a++) for (let c = RG[r + 2]; c <= RG[r + 3]; c++) { const key = a * 100003 + c; m.set(key, (m.get(key) || 0) + 1); } }
    let keys = Float64Array.from(m.keys()), kmin = Infinity, kmax = -Infinity;
    for (const k of keys) { if (k < kmin) kmin = k; if (k > kmax) kmax = k; }
    if (keys.length && kmin >= -2147483648 && kmax <= 2147483647) keys = Int32Array.from(keys);
    keys.sort();
    const starts = new Uint32Array(keys.length + 1), at = new Map(); let tot = 0;
    for (let i = 0; i < keys.length; i++) { starts[i] = tot; at.set(keys[i], tot); tot += m.get(keys[i]); }
    starts[keys.length] = tot;
    const items = new Uint32Array(tot);
    for (let q = 0; q < nTri; q++) { if (lvl[q] !== L) continue; const r = q * 4, b = q * 9; for (let a = RG[r]; a <= RG[r + 1]; a++) for (let c = RG[r + 2]; c <= RG[r + 3]; c++) { const key = a * 100003 + c, p = at.get(key); items[p] = b; at.set(key, p + 1); } }
    return { keys, starts, items };
  };
  // 段 L の索引: 升の番号の範囲の表 (幅 × 高さ) で数えて詰め、中身のある升だけを鍵の昇順 (a の昇順・c の昇順) に並べる。升の中身は三角形の順
  const grid = L => {
    const B = bb[L]; if (!(B[0] <= B[1])) return { keys: new Int32Array(0), starts: new Uint32Array(1), items: new Uint32Array(0) };
    const A0 = B[0], C0 = B[2], Wd = B[1] - A0 + 1, Hd = B[3] - C0 + 1, NC = Wd * Hd;
    if (!(NC <= 8000000)) return gridMap(L);
    const cnt = new Uint32Array(NC + 1);
    for (let q = 0; q < nTri; q++) { if (lvl[q] !== L) continue; const r = q * 4; for (let a = RG[r] - A0; a <= RG[r + 1] - A0; a++) for (let c = RG[r + 2] - C0; c <= RG[r + 3] - C0; c++) cnt[a * Hd + c + 1]++; }
    let used = 0; for (let i = 0; i < NC; i++) { if (cnt[i + 1]) used++; cnt[i + 1] += cnt[i]; }
    const items = new Uint32Array(cnt[NC]), at = cnt.slice(0, NC);
    for (let q = 0; q < nTri; q++) { if (lvl[q] !== L) continue; const r = q * 4, b = q * 9; for (let a = RG[r] - A0; a <= RG[r + 1] - A0; a++) for (let c = RG[r + 2] - C0; c <= RG[r + 3] - C0; c++) items[at[a * Hd + c]++] = b; }
    let keys = new Float64Array(used); const starts = new Uint32Array(used + 1); let k = 0, kmin = Infinity, kmax = -Infinity;
    for (let a = 0; a < Wd; a++) for (let c = 0; c < Hd; c++) { const i = a * Hd + c; if (cnt[i + 1] === cnt[i]) continue; const key = (a + A0) * 100003 + (c + C0); keys[k] = key; starts[k] = cnt[i]; k++; if (key < kmin) kmin = key; if (key > kmax) kmax = key; }
    starts[used] = cnt[NC];
    // 鍵が 32 ビット整数に収まるなら Int32Array にする (値は同じ)
    if (used && kmin >= -2147483648 && kmax <= 2147483647) keys = Int32Array.from(keys);
    return { keys, starts, items };
  };
  return { g2: grid(0), g16: grid(1), big: Uint32Array.from(big) };
}
// かたまり (鍵 key) を作る。部品の並び・種類ごとのまとめ方・画像の番号の順・判定の色は、前の buildCur と同じ決まり (範囲がかたまりの部品だけになった)
function buildChunk(Rg, key) {
  const bk = Rg.buckets.get(key); if (!bk) return null;
  const COLK = Rg.COLK, LABK = Rg.LABK, names = Rg.names, o = Rg.o;
  const baseN = {}, texCnt = {}; for (const k of COLK) { baseN[k] = 0; texCnt[k] = new Map(); }
  const geo = bk.list.map(ri => partGeom(bk.rg, ri));
  let anyUv = false;
  for (const s of geo) {
    const p = s.p, k = catOf(p); baseN[k] += p[5];
    const tc = texCnt[k]; if (p[11] >= 0) { const T = new Int32Array(s.cd.g.buf, s.cd.g.base + p[11], p[5]); for (let t = 0; t < p[5]; t++) tc.set(T[t], (tc.get(T[t]) || 0) + 1); } else tc.set(-1, (tc.get(-1) || 0) + p[5]);
    // (2026-11-05) UV の無い部品でも画像の番号 (場所のパレットの色だけの項目) があれば作る (その部品の UV は 0 のまま)
    if (p[10] >= 0 || p[11] >= 0) anyUv = true;
  }
  let pos = 0; const offs = {};
  for (const k of COLK) { offs[k] = [pos * 9, baseN[k] * 9]; pos += baseN[k]; }
  const nTri = pos;
  const F = new Float32Array(nTri * 9), ID = new Uint32Array(nTri), LB = new Uint8Array(nTri), FL = new Uint8Array(nTri);
  const UV = anyUv ? new Float32Array(nTri * 6) : null, TX = anyUv ? new Int32Array(nTri) : null;
  // 種類の中の書く位置は画像の番号ごと (番号の昇順。-1 が先)
  const wq = {};
  for (const k of COLK) { const m = new Map(); let q = 0; for (const id of [...texCnt[k].keys()].sort((a, b) => a - b)) { m.set(id, q); q += texCnt[k].get(id); } wq[k] = m; }
  const parts = [], labN = new Array(LABK.length).fill(0), box = {};
  // 種類ごとの箱 (返すのは低い角。高い角は .hi)
  const bx0 = k => { let b = box[k]; if (!b) { b = box[k] = [[Infinity, Infinity, Infinity], [-Infinity, -Infinity, -Infinity]]; } const lo = b[0]; lo.hi = b[1]; return lo; };
  const FLOOR2 = LABK.indexOf('floor2'), TUBE2 = LABK.indexOf('tube2');
  for (const s of geo) {
    const { cd, pi, p, I, W, lab } = s, g = cd.g, id_ = parts.length, k = catOf(p), door = metaOf(p).door;
    const UVs = p[10] >= 0 ? new Float32Array(g.buf, g.base + p[10], p[3] * 2) : null, TXs = p[11] >= 0 ? new Int32Array(g.buf, g.base + p[11], p[5]) : null;
    const qk = wq[k], ob = offs[k][0] / 9, cnt = {}, lo = bx0(k), hi = lo.hi;
    let ltid = NaN, lq = 0;   // 今の画像の番号と、その番号の次に書く位置 (番号が替わるとき・部品の終わりに qk へ戻す)
    for (let t = 0; t < p[5]; t++) {
      const tid = TXs ? TXs[t] : -1;
      if (tid !== ltid) { if (ltid === ltid) qk.set(ltid, lq); ltid = tid; lq = qk.get(tid); }
      const q = ob + lq++;
      let w = q * 9;
      for (let c3 = 0; c3 < 3; c3++) { const v = I[t * 3 + c3] * 3; for (let a = 0; a < 3; a++) { const x = F[w++] = W[v + a] - o[a]; if (x < lo[a]) lo[a] = x; if (x > hi[a]) hi[a] = x; } }
      ID[q] = id_; LB[q] = lab[t];
      FL[q] = (door ? DOOR : 0) | (lab[t] && (lab[t] === FLOOR2 || lab[t] === TUBE2) ? FLR : 0);
      if (anyUv) { TX[q] = tid; if (UVs) { let u = q * 6; for (let c3 = 0; c3 < 3; c3++) { const vi = I[t * 3 + c3] * 2; UV[u++] = UVs[vi]; UV[u++] = UVs[vi + 1]; } } }
      if (lab[t]) { const lk = LABK[lab[t]]; cnt[lk] = (cnt[lk] || 0) + 1; labN[lab[t]]++; }
    }
    if (ltid === ltid) qk.set(ltid, lq);
    const sp = (cd.inst[5] || '').split('/').pop();
    // [インスタンスの番号, 部品の名前, 種類, 判定の面の数, 区画の名前, '', 区画の中の部品の番号]
    parts.push([cd.inst[0], p[0], k, Object.entries(cnt).map(([kk, n]) => `${names[kk]} ${n}`).join(', ') || '歩ける面に色なし', sp, '', pi]);
  }
  const col = buildCol(F, nTri, FL);
  // big: 場所の全部の大きな物体のかたまり (画像の番号は、大きな物体の textures の番号)
  const msg = { type: 'chunk', key, hash: bk.hash, cls: bk.cls, texGen: Rg.texGen, big: bk.rg !== Rg, F, ID, LB, FL, UV, TX, offs, parts, labN, box, col };
  const tr = [F.buffer, ID.buffer, LB.buffer, FL.buffer, col.g2.keys.buffer, col.g2.starts.buffer, col.g2.items.buffer, col.g16.keys.buffer, col.g16.starts.buffer, col.g16.items.buffer, col.big.buffer];
  if (UV) tr.push(UV.buffer, TX.buffer);
  return { msg, tr };
}
// ===== 外判定 =====
// (2026-11-06、利用者の指示「外に出てるのに外判定がない。宇宙空間にあって xyz 各方向 +/-200m のどちらかに何もなければ外判定がいいかな？」
//  「外判定に一個追加。外でも足の 0.5m 以内に床があれば中判定。 ORISON などの屋外想定」「モード関係なく、足位置の想定があるだろ？」。親の決定)
// 見ている位置 p (場所の座標) から x・y・z の ＋・− の 6 つの向きへ dist m の線分を引き、地点の部品の三角形に 1 つも当たらない向きが 1 つでもあれば「外」、6 つ全部が当たれば「中」。
//   地点の立方体 (中心 c から ±r m) の外へ出る線分は、立方体の端までで見る (端に届いたら「何も無い」)。見ている位置が立方体の外なら「外」(前の view3d.js の outScan と同じ決まり)。
//   「外」でも、足 foot の下 floor m 以内に立てる面があれば「中」(foot.mode 'g' = 上向きの面で高さが足の floor m 下〜0.05 m 上、'c' = 上下が逆: 下向きの面 (ドア以外) で高さが足の 0.05 m 下より上〜floor m 上)。
// 前は画面の側が、読み込んである三角形だけで見ていた。今は、読み込むかたまりを外判定で変える (「外」のときは内装の区画を読まない) ので、読み込みに左右されないよう、
// この糸が地点の全部の部品 (大きさ・距離に関係なく) で見る: 部品の箱で絞ってから、その部品の三角形に当てる
const OUT_EPS = 0.01;
function outGeom(Rg, cache, ri) {
  let t = cache.get(ri);
  if (!t) { const s = partGeom(Rg, ri); t = { W: s.W, I: s.I, n: s.p[5], door: metaOf(s.p).door }; cache.set(ri, t); }
  return t;
}
function outFloor(Rg, cache, foot, floor) {
  const [x, y, z] = foot.p, up = foot.mode !== 'c', zl = up ? z - floor : z - 0.05, zh = up ? z + 0.05 : z + floor, BX = Rg.BX, o = Rg.o, E = OUT_EPS;
  for (let ri = 0; ri < Rg.n; ri++) {
    const q = ri * 6;
    if (BX[q] - E > x || BX[q + 3] + E < x || BX[q + 1] - E > y || BX[q + 4] + E < y || BX[q + 2] - E > zh || BX[q + 5] + E < zl) continue;
    const { W, I, n, door } = outGeom(Rg, cache, ri);
    if (!up && door) continue;
    for (let t = 0; t < n; t++) {
      const v0 = I[t * 3] * 3, v1 = I[t * 3 + 1] * 3, v2 = I[t * 3 + 2] * 3;
      const e1x = W[v1] - W[v0], e1y = W[v1 + 1] - W[v0 + 1], e2x = W[v2] - W[v0], e2y = W[v2 + 1] - W[v0 + 1], den = e1x * e2y - e2x * e1y; if (Math.abs(den) < 1e-12) continue;
      if (up ? !(den > 0) : !(den < 0)) continue;
      // 升の数の上限 (画面の側の升と同じ: 上向きは 1 m の升で 4096、下向きは 2 m の升で 1024)
      const sc = up ? 1 : 2, x0 = Math.min(W[v0], W[v1], W[v2]) - o[0], x1 = Math.max(W[v0], W[v1], W[v2]) - o[0], y0 = Math.min(W[v0 + 1], W[v1 + 1], W[v2 + 1]) - o[1], y1 = Math.max(W[v0 + 1], W[v1 + 1], W[v2 + 1]) - o[1];
      if ((Math.floor(x1 / sc) - Math.floor(x0 / sc) + 1) * (Math.floor(y1 / sc) - Math.floor(y0 / sc) + 1) > (up ? 4096 : 1024)) continue;
      const wx = x - W[v0], wy = y - W[v0 + 1], s = (wx * e2y - e2x * wy) / den, u = (e1x * wy - wx * e1y) / den; if (s < 0 || u < 0 || s + u > 1) continue;
      const zt = W[v0 + 2] + s * (W[v1 + 2] - W[v0 + 2]) + u * (W[v2 + 2] - W[v0 + 2]);
      if (up ? zt >= zl && zt <= zh : zt > zl && zt <= zh) return true;
    }
  }
  return false;
}
function outScan(Rg, m) {
  const P = m.p, cc = Rg.c, r = Rg.r, BX = Rg.BX, E = OUT_EPS, cache = new Map();
  if (Math.abs(P[0] - cc[0]) > r || Math.abs(P[1] - cc[1]) > r || Math.abs(P[2] - cc[2]) > r) return true;
  if (m.foot && outFloor(Rg, cache, m.foot, m.floor)) return false;
  for (let d = 0; d < 6; d++) {
    const a = d >> 1, sg = d & 1 ? -1 : 1, b1 = (a + 1) % 3, b2 = (a + 2) % 3, p1 = P[b1], p2 = P[b2];
    const L = Math.min(m.dist, sg > 0 ? cc[a] + r - P[a] : P[a] - (cc[a] - r)), lo = sg > 0 ? P[a] : P[a] - L, hi = sg > 0 ? P[a] + L : P[a];
    // 線分に箱の掛かる部品を、近い順に
    const cs = [];
    for (let ri = 0; ri < Rg.n; ri++) {
      const q = ri * 6;
      if (BX[q + b1] - E > p1 || BX[q + 3 + b1] + E < p1 || BX[q + b2] - E > p2 || BX[q + 3 + b2] + E < p2 || BX[q + 3 + a] + E < lo || BX[q + a] - E > hi) continue;
      cs.push(ri);
    }
    cs.sort((x, y) => sg > 0 ? BX[x * 6 + a] - BX[y * 6 + a] : BX[y * 6 + 3 + a] - BX[x * 6 + 3 + a]);
    let found = false;
    for (const ri of cs) {
      const { W, I, n } = outGeom(Rg, cache, ri);
      for (let t = 0; t < n && !found; t++) {
        const v0 = I[t * 3] * 3, v1 = I[t * 3 + 1] * 3, v2 = I[t * 3 + 2] * 3;
        // 三角形に線分が当たるか: 線分に垂直な面での三角形の中か → その点の、線分の向きの座標が線分の中か (前の outScan と同じ求め方)
        const u0 = W[v0 + b1], u1 = W[v1 + b1], u2 = W[v2 + b1]; if ((u0 < p1 && u1 < p1 && u2 < p1) || (u0 > p1 && u1 > p1 && u2 > p1)) continue;
        const w0 = W[v0 + b2], w1 = W[v1 + b2], w2 = W[v2 + b2]; if ((w0 < p2 && w1 < p2 && w2 < p2) || (w0 > p2 && w1 > p2 && w2 > p2)) continue;
        const a0 = W[v0 + a], a1 = W[v1 + a], a2 = W[v2 + a]; if ((a0 < lo && a1 < lo && a2 < lo) || (a0 > hi && a1 > hi && a2 > hi)) continue;
        const e1u = u1 - u0, e1w = w1 - w0, e2u = u2 - u0, e2w = w2 - w0, den = e1u * e2w - e2u * e1w; if (Math.abs(den) < 1e-12) continue;
        const du = p1 - u0, dw = p2 - w0, s = (du * e2w - e2u * dw) / den, t2 = (e1u * dw - du * e1w) / den; if (s < 0 || t2 < 0 || s + t2 > 1) continue;
        const v = a0 + s * (a1 - a0) + t2 * (a2 - a0);
        if (sg > 0 ? v > P[a] && v <= hi : v < P[a] && v >= lo) found = true;
      }
      if (found) break;
    }
    if (!found) return true;
  }
  return false;
}
// 前に外判定した位置 a から今の位置 b までの線分が、地点の部品の三角形に当たるか (ドア・壁を通り抜けたか。前の view3d.js の outCross と同じ求め方)
function outCross(Rg, A_, B_) {
  const [ax, ay, az] = A_, [bx, by, bz] = B_, dx = bx - ax, dy = by - ay, dz = bz - az, BX = Rg.BX, E = OUT_EPS, cache = new Map();
  const x0 = Math.min(ax, bx), x1 = Math.max(ax, bx), y0 = Math.min(ay, by), y1 = Math.max(ay, by), z0 = Math.min(az, bz), z1 = Math.max(az, bz);
  for (let ri = 0; ri < Rg.n; ri++) {
    const q = ri * 6;
    if (BX[q] - E > x1 || BX[q + 3] + E < x0 || BX[q + 1] - E > y1 || BX[q + 4] + E < y0 || BX[q + 2] - E > z1 || BX[q + 5] + E < z0) continue;
    const { W, I, n } = outGeom(Rg, cache, ri);
    for (let t = 0; t < n; t++) {
      const v0 = I[t * 3] * 3, v1 = I[t * 3 + 1] * 3, v2 = I[t * 3 + 2] * 3;
      const tx0 = W[v0], tx1 = W[v1], tx2 = W[v2]; if ((tx0 < x0 && tx1 < x0 && tx2 < x0) || (tx0 > x1 && tx1 > x1 && tx2 > x1)) continue;
      const ty0 = W[v0 + 1], ty1 = W[v1 + 1], ty2 = W[v2 + 1]; if ((ty0 < y0 && ty1 < y0 && ty2 < y0) || (ty0 > y1 && ty1 > y1 && ty2 > y1)) continue;
      const tz0 = W[v0 + 2], tz1 = W[v1 + 2], tz2 = W[v2 + 2]; if ((tz0 < z0 && tz1 < z0 && tz2 < z0) || (tz0 > z1 && tz1 > z1 && tz2 > z1)) continue;
      const e1x = tx1 - tx0, e1y = ty1 - ty0, e1z = tz1 - tz0, e2x = tx2 - tx0, e2y = ty2 - ty0, e2z = tz2 - tz0;
      const px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x, det = e1x * px + e1y * py + e1z * pz; if (Math.abs(det) < 1e-12) continue;
      const inv = 1 / det, sx = ax - tx0, sy = ay - ty0, sz = az - tz0; const u = (sx * px + sy * py + sz * pz) * inv; if (u < 0 || u > 1) continue;
      const qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x; const v = (dx * qx + dy * qy + dz * qz) * inv; if (v < 0 || u + v > 1) continue;
      const tt = (e2x * qx + e2y * qy + e2z * qz) * inv; if (tt > 0 && tt < 1) return true;
    }
  }
  return false;
}
// ===== 地点の索引を作る =====
async function build(m) {
  const { id, base, region, o, c, r, keys, names, og } = m, pal = !!m.pal, site = m.site || '';
  const times = {}; let tp = performance.now(); const lap = k => { const t = performance.now(); times[k] = (t - tp) / 1000; tp = t; };
  const lo = c.map(v => v - r), hi = c.map(v => v + r);
  const ins = region.instances.filter(i => i[3][0] <= hi[0] && i[4][0] >= lo[0] && i[3][1] <= hi[1] && i[4][1] >= lo[1] && i[3][2] <= hi[2] && i[4][2] >= lo[2]);
  // 区画のファイル: 前の地点と同じ場所・同じ色付けで、画像の番号の並びが合い (region.json の textures の重なる所が同じ)、同じ区画で要る置かれた所がそろっていれば、前に読んだものを使い回す
  const texNew = region.textures || []; let compat = !!R && R.site === site && R.pal === pal;
  if (compat) { const tOld = R.textures, n = Math.min(tOld.length, texNew.length); for (let i = 0; i < n && compat; i++) if (JSON.stringify(tOld[i]) !== JSON.stringify(texNew[i])) compat = false; }
  const textures = compat && R.textures.length > texNew.length ? R.textures : texNew, texGen = compat ? R.texGen : ++TEXGEN;
  const need = new Map();
  for (const i of ins) { let s = need.get(i[1]); if (!s) need.set(i[1], s = { sp: i[5] || '', ids: [] }); s.ids.push(i[0]); }
  const geoNew = new Map(), byGi = new Map(); let done = 0, reused = 0;
  for (const [gi, nd] of need) {
    let g = compat && nd.sp ? GEO.get(nd.sp) : null;
    if (g && !nd.ids.every(x => g.inst.has(x))) g = null;
    if (g) reused++; else g = await fetchGeo(base + region.geos[gi]);
    byGi.set(gi, g); if (nd.sp) geoNew.set(nd.sp, g);
    self.postMessage({ type: 'progress', id, done: ++done, total: need.size });
  }
  lap('read');
  // 区画 (インスタンス) の選び方 (前と同じ): 部品の箱 (8 隅) の 1 つでも立方体に掛かり、その箱の x の幅が 5000 m 未満のインスタンス。選んだ区画の部品は全部、索引に入れる
  const cand = [], CI = [], PI = [], BXs = [];
  for (const inst of ins) {
    const g = byGi.get(inst[1]), A = inst[2], ii = g.inst.get(inst[0]); if (!ii) continue;
    const bx = new Float64Array(g.parts.length * 6); let hit = false;
    for (let pi = 0; pi < g.parts.length; pi++) {
      const p = g.parts[pi];
      const bl = [Infinity, Infinity, Infinity], bh = [-Infinity, -Infinity, -Infinity];
      for (const x of [p[6][0], p[7][0]]) for (const y of [p[6][1], p[7][1]]) for (const z of [p[6][2], p[7][2]]) for (let a = 0; a < 3; a++) { const v = A[a * 4] * x + A[a * 4 + 1] * y + A[a * 4 + 2] * z + A[a * 4 + 3]; if (v < bl[a]) bl[a] = v; if (v > bh[a]) bh[a] = v; }
      bx.set(bl, pi * 6); bx.set(bh, pi * 6 + 3);
      if (bl[0] <= hi[0] && bh[0] >= lo[0] && bl[1] <= hi[1] && bh[1] >= lo[1] && bl[2] <= hi[2] && bh[2] >= lo[2] && bh[0] - bl[0] < 5000) hit = true;
    }
    if (!hit) continue;
    const ci = cand.length; cand.push({ inst, g, ii, A, interior: socInterior(inst[5] || '') });
    for (let pi = 0; pi < g.parts.length; pi++) { CI.push(ci); PI.push(pi); }
    BXs.push(bx);
  }
  const n = CI.length, BX = new Float64Array(n * 6); { let at = 0; for (const b of BXs) { BX.set(b, at); at += b.length; } }
  const LABK = keys.labk, FLOOR2 = LABK.indexOf('floor2'), upVols = upVolsOf(region);
  const Rg = { og, o, c, r, pal, site, textures, texGen, cand, n, CI: Uint32Array.from(CI), PI: Uint32Array.from(PI), BX, upVols, FLOOR2, COLK: keys.col, LABK, names, buckets: null, sizes: null, instCi: new Map(cand.map((cd, i) => [cd.inst[0], i])) };
  // 地点の全体で使うもの (部品の形以外の仕組みが使う。どのかたまりを読んでいるかに関係なく、地点の全部の部品から作る):
  //   はしご・エレベーターの扉・階段の箱、判定の床の高さと広さ (フロアの一覧 fz・fa)、名前に light のある部品の名前
  const ladders = [], elevDoors = [], stairs = [], fz = [], fa = [], lightParts = new Set();
  // KEEP の印 (上の「升ごとに持つ形」の KEEP。前の keepAlways と同じ決まり: エレベーターの扉・はしご・階段 (高さは見ない)・判定の色のある部品・上下が逆の重力の範囲の箱に掛かる部品)
  const keep = Rg.keep = new Uint8Array(n);
  for (let ri = 0; ri < n; ri++) {
    const cd = cand[CI[ri]], pi = PI[ri], g = cd.g, p = g.parts[pi], nm = metaOf(p);
    if (nm.light) lightParts.add(p[0]);
    const isLadder = nm.ladder, isElevDoor = !!p[9], isStair = nm.stair;
    const q = ri * 6;
    keep[ri] = isElevDoor || isLadder || isStair || new Uint8Array(g.buf, g.base + cd.ii[1] + g.triStart[pi], p[5]).some(v => v) ||
      upVols.some(v => BX[q] <= v.hi[0] && BX[q + 3] >= v.lo[0] && BX[q + 1] <= v.hi[1] && BX[q + 4] >= v.lo[1] && BX[q + 2] <= v.hi[2] && BX[q + 5] >= v.lo[2]) ? 1 : 0;
    let floorish = FLOOR2 > 0 && new Uint8Array(g.buf, g.base + cd.ii[1] + g.triStart[pi], p[5]).includes(FLOOR2);
    // 上下が逆の重力の範囲の箱に掛かる部品は、判定の色が無くても床になりうる
    if (!floorish && FLOOR2 > 0) floorish = upVols.some(v => BX[q] - OUT_EPS <= v.hi[0] && BX[q + 3] + OUT_EPS >= v.lo[0] && BX[q + 1] - OUT_EPS <= v.hi[1] && BX[q + 4] + OUT_EPS >= v.lo[1] && BX[q + 2] - OUT_EPS <= v.hi[2] && BX[q + 5] + OUT_EPS >= v.lo[2]);
    if (!(isLadder || isElevDoor || isStair || floorish)) continue;
    const { I, W, lab } = partGeom(Rg, ri);
    const bl = [Infinity, Infinity, Infinity], bh = [-Infinity, -Infinity, -Infinity];
    for (let t = 0; t < p[5]; t++) {
      const vs = [I[t * 3] * 3, I[t * 3 + 1] * 3, I[t * 3 + 2] * 3];
      for (const v of vs) for (let a = 0; a < 3; a++) { if (W[v + a] < bl[a]) bl[a] = W[v + a]; if (W[v + a] > bh[a]) bh[a] = W[v + a]; }
      if (lab[t] && lab[t] === FLOOR2) {
        let inreg = true; for (let a = 0; a < 3 && inreg; a++) { const mn = Math.min(W[vs[0] + a], W[vs[1] + a], W[vs[2] + a]), mx = Math.max(W[vs[0] + a], W[vs[1] + a], W[vs[2] + a]); if (!(mn <= hi[a] && mx >= lo[a])) inreg = false; }
        if (inreg) {
          fz.push((W[vs[0] + 2] + W[vs[1] + 2] + W[vs[2] + 2]) / 3);
          const ux = W[vs[1]] - W[vs[0]], uy = W[vs[1] + 1] - W[vs[0] + 1], uz = W[vs[1] + 2] - W[vs[0] + 2], vx = W[vs[2]] - W[vs[0]], vy = W[vs[2] + 1] - W[vs[0] + 1], vz = W[vs[2] + 2] - W[vs[0] + 2];
          fa.push(Math.hypot(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx) / 2);
        }
      }
    }
    const rel = b => b.map((v, a) => +(v - o[a]).toFixed(3));
    if (isLadder) ladders.push([p[0], cd.inst[0], rel(bl), rel(bh)]);
    // エレベーターの扉 (アプリの BuildGeo が見分けて parts の 10 番目を付けたもの) の箱と前の向き [名前, 箱の低い角, 高い角, [fx, fy] | null] (箱は原点からの相対。向きは場所の座標の xy、正規化)。
    // エレベーターで移った先の立つ所に使う (view3d.js の elevPlace。2026-11-05、利用者の指示「指示はドアの向きを見て」)
    if (isElevDoor) {
      let fwd = null; const [vx, vy, vz] = p[9], A = cd.A; const fx = A[0] * vx + A[1] * vy + A[2] * vz, fy = A[4] * vx + A[5] * vy + A[6] * vz, l = Math.hypot(fx, fy); if (l > 1e-6) fwd = [+(fx / l).toFixed(4), +(fy / l).toFixed(4)];
      elevDoors.push([p[0], rel(bl), rel(bh), fwd]);
    }
    // 階段の部品の箱 [名前, 低い角, 高い角]。行き先の一覧の「階段」に使う (2026-11-04、利用者の決定「作り直す」)
    if (isStair && bh[2] - bl[2] >= 0.5) stairs.push([p[0], rel(bl), rel(bh)]);
  }
  // 店の入口の部品 (region.json の entrances の [インスタンス, 部品の番号]) のうち、この地点の索引にあるもの ('インスタンス:部品の番号')
  const entParts = [];
  for (const e of region.entrances || []) for (const x of e.list || []) { const ci = Rg.instCi.get(x[0]); if (ci !== undefined && x[1] < cand[ci].g.parts.length) entParts.push(x[0] + ':' + x[1]); }
  lap('meta');
  const { index, stat } = bucket(Rg, m.sizes);
  lap('index');
  // 入れ替え (ここまでは前の地点のまま、かたまりの頼みに答えられる)
  R = Rg; GEO = geoNew;
  stat.geos = need.size; stat.reused = reused; stat.insts = cand.length; stat.partsAll = n;
  self.postMessage({ type: 'done', id, index, textures, texGen, ladders, elevDoors, stairs, fz, fa, lightParts: [...lightParts], entParts, stat, times },
    [index.cells.buffer, index.cls.buffer, index.tris.buffer, index.parts.buffer, index.hash.buffer]);
}
// 場所の全部の大きな物体 (アプリの BuildBigAsync が書いた region.json と g/<番号>.bin) を読む。部品は、置かれた所の箱 (場所の座標) のいちばん長い辺が thr 以上で、
// x の幅が 5000 m 未満のもの (地点の区画の選び方と同じ幅の決まり) だけを持つ
async function loadBig(m) {
  const { base, region, thr } = m;
  const cand = [], CI = [], PI = [], BXs = [], files = new Map();
  for (const inst of region.instances || []) {
    let g = files.get(inst[1]); if (!g) { g = await fetchGeo(base + region.geos[inst[1]]); files.set(inst[1], g); }
    const A = inst[2], ii = g.inst.get(inst[0]); if (!ii) continue;
    const ci = cand.length; let any = false;
    for (let pi = 0; pi < g.parts.length; pi++) {
      const p = g.parts[pi], bl = [Infinity, Infinity, Infinity], bh = [-Infinity, -Infinity, -Infinity];
      for (const x of [p[6][0], p[7][0]]) for (const y of [p[6][1], p[7][1]]) for (const z of [p[6][2], p[7][2]]) for (let a = 0; a < 3; a++) { const v = A[a * 4] * x + A[a * 4 + 1] * y + A[a * 4 + 2] * z + A[a * 4 + 3]; if (v < bl[a]) bl[a] = v; if (v > bh[a]) bh[a] = v; }
      if (Math.max(bh[0] - bl[0], bh[1] - bl[1], bh[2] - bl[2]) < thr || bh[0] - bl[0] >= 5000) continue;
      CI.push(ci); PI.push(pi); BXs.push(...bl, ...bh); any = true;
    }
    if (any) cand.push({ inst, g, ii, A, interior: socInterior(inst[5] || '') });
  }
  return { site: m.site || '', pal: !!m.pal, thr, textures: region.textures || [], cand, n: CI.length, CI: Uint32Array.from(CI), PI: Uint32Array.from(PI), BX: Float64Array.from(BXs), upVols: [], FLOOR2: R ? R.FLOOR2 : -1 };
}
// 前の形の保存 (解析の結果の丸ごとの保存。OPFS の w-<鍵>\) を消す。全部消せたら 0、分からなければ null
async function wpurge() {
  try {
    if (typeof navigator === 'undefined' || !navigator.storage || typeof navigator.storage.getDirectory !== 'function') return null;
    const root = await navigator.storage.getDirectory(); const names = [];
    for await (const [name] of root.entries()) if (name.startsWith('w-')) names.push(name);
    let left = 0;
    for (const nm of names) { try { await root.removeEntry(nm, { recursive: true }); } catch (e) { if (!(e && e.name === 'NotFoundError')) left++; } }
    return left ? null : 0;
  } catch (_) { return null; }
}
self.onmessage = async e => {
  const m = e.data;
  try {
    if (m.type === 'wpurge') { self.postMessage({ type: 'wstat', id: m.id, used: await wpurge() }); return; }
    if (m.type === 'build') { await build(m); return; }
    if (m.type === 'big') {
      const t0 = performance.now(), Bg = await loadBig(m);
      BIG = Bg;
      if (!R || R.og !== m.og) { self.postMessage({ type: 'bigReady', id: m.id, og: m.og, stale: true, textures: Bg.textures }); return; }
      const { index, stat } = bucket(R, R.sizes);
      stat.bigAll = Bg.n; stat.bigLoad = (performance.now() - t0) / 1000;
      self.postMessage({ type: 'bigReady', id: m.id, og: m.og, index, textures: Bg.textures, stat }, [index.cells.buffer, index.cls.buffer, index.tris.buffer, index.parts.buffer, index.hash.buffer]);
      return;
    }
    if (m.type === 'sizes') {
      if (!R || R.og !== m.og) { self.postMessage({ type: 'index', id: m.id, stale: true }); return; }
      const { index, stat } = bucket(R, m.sizes);
      self.postMessage({ type: 'index', id: m.id, index, stat }, [index.cells.buffer, index.cls.buffer, index.tris.buffer, index.parts.buffer, index.hash.buffer]);
      return;
    }
    if (m.type === 'chunk') {
      const res = R && R.og === m.og ? buildChunk(R, m.key) : null;
      if (!res) { self.postMessage({ type: 'chunk', id: m.id, og: m.og, key: m.key, missing: true }); return; }
      res.msg.id = m.id; res.msg.og = m.og;
      self.postMessage(res.msg, res.tr);
      return;
    }
    if (m.type === 'part') {
      let ri = -1;
      if (R && R.og === m.og) { const [a, b] = String(m.key).split(':'), ci = R.instCi.get(+a), pi = +b; if (ci !== undefined) for (let k = 0; k < R.n; k++) if (R.CI[k] === ci && R.PI[k] === pi) { ri = k; break; } }
      if (ri < 0) { self.postMessage({ type: 'part', id: m.id, missing: true }); return; }
      const { cd, pi, p, I, W, lab } = partGeom(R, ri), o = R.o, tris = new Float32Array(p[5] * 9), cnt = {};
      for (let t = 0; t < p[5]; t++) { for (let k = 0; k < 3; k++) { const v = I[t * 3 + k] * 3; for (let a = 0; a < 3; a++) tris[t * 9 + k * 3 + a] = W[v + a] - o[a]; } if (lab[t]) { const lk = R.LABK[lab[t]]; cnt[lk] = (cnt[lk] || 0) + 1; } }
      const part = [cd.inst[0], p[0], catOf(p), Object.entries(cnt).map(([kk, n]) => `${R.names[kk]} ${n}`).join(', ') || '歩ける面に色なし', (cd.inst[5] || '').split('/').pop(), '', pi];
      const labOut = Uint8Array.from(lab);
      self.postMessage({ type: 'part', id: m.id, tris, lab: labOut, part }, [tris.buffer, labOut.buffer]);
      return;
    }
    if (m.type === 'out') {
      if (!R || R.og !== m.og) { self.postMessage({ type: 'out', id: m.id, stale: true }); return; }
      if (m.kind === 'scan') self.postMessage({ type: 'out', id: m.id, outside: outScan(R, m) });
      else self.postMessage({ type: 'out', id: m.id, hit: outCross(R, m.a, m.b) });
      return;
    }
  } catch (err) {
    self.postMessage({ type: 'error', id: m.id, kind: m.type, key: m.key, message: String(err && err.message || err) });
  }
};
