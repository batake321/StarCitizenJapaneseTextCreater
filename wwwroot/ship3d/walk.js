// 船の 3D の船内を歩くモード (2026-11-05、利用者の指示「船内も場所と同じように歩けるようにして 走るとはしこ上るのとジャンプ、書かぎ、寝そべりも同じように」)。
// ステーションの 3D の歩くモード (wwwroot/stationmap/view3d.js・view3d-worker.js。以下「移植元」) の計算と決まりの数字を、同じ名前・同じ値で写したもの。
// 写した所には移植元の行 (2026-11-05 の時点) を書く。移植元のファイルは変えていない。
//
// 船と場所の作りの違いで、移植元と形が違う所 (親の決定):
//   ・当たりの元 (curBuf): 移植元は解析の糸が区画の部品から作る。船は、場面に出ている GLB の全部の mesh の三角形から、歩くモードに初めて入ったときに作る (buildCur)。
//     座標は移植元の計算 (x・y が水平、z が高さ) に合うように、船の Z 上の座標 (前 = +Y) で持つ。表示の座標 (Y 上。前 = −Z) との行き来は toWalk・walkCam の 2 つだけ
//   ・ドア・はしごの見分け: 移植元の決まり (名前を '_' で分けた語) を、船の名前の形に合うように当てる (nameWords)。ノードの名前と mesh の名前の両方を見て、
//     パスはファイル名だけにし、拡張子を外し、大文字小文字を区別しない
//   ・移していない物 (船に元のデータが無い): エレベーター・トラム・マイハンガー・重力の範囲 (無重力・上下逆)・録画とコメント・細かい形での補い (walkCheck／walkFill の extra)・
//     現在位置・切る面 (visZ)・判定の床 (fl／onFloor。移植元でも歩く計算には使っていない)。移植元でこれらに当たる分かれ道は、船では「無い」ものとして通す
//   ・キー: 押すキーは移植元と同じ。日本語入力が ON でも効くように、e.key ではなく e.code (KeyW など) で見る (keyOf。船のビューアのほかのキーと同じ)。押しているキーの集まりは移植元の keys と同じ作り
// 船の作り (座席から始まる・外板が薄い・ドアが閉じた形・はしごが段ごとの部品) のために、船だけに足した決まり (2026-11-05、親の決定。ほかの決まりと数字は移植元のまま):
//   1. 入った所で立てない (「立つ」の体が入らない・真下に面が無い・頭上に面が無い = 船の外) ときは、近くの、頭上に面のある立てる床から始める (standNear)
//   2. スペースのよじ登りは、今いる所の頭上の面が移る先の床より低い (間に天井がある) とき、今いる所の頭上に面があって移る先の頭上に面が無い (船の外へ出る) ときは行わず、ふつうのジャンプにする
//   3. ドアと見分けた三角形は、足元の升 (f) にも入れない (移植元は足元には入れる)
//   4. はしごの箱のうち、水平の範囲が重なり、上下に続いている (すき間 1.5 m 以内) ものを 1 つの箱にまとめる (mergeLadders)
//   5. はしごの降り方: 上端で見ている向きの 0.6 m 先に床が無ければ、その先・まわりの床を探して立つ (ladderTop)。はしごの上でスペースを押すと、見ている向きの床へ降りる (途中の階)
import * as THREE from 'three';

// env: { camera, inside (船内の見る位置 { pos, yaw, pitch }), applyCamera(), model() (読み込んだ GLB の場面), parser() (GLTFLoader の parser), mode() ('outside'／'inside'), focus() }
export function createWalk(env) {
  const cam = env.camera, inside = env.inside;
  const $ = id => document.getElementById(id);

  // ===== 表示の座標 ⇔ 歩く計算の座標 (ここと walkCam だけ) =====
  // GLB のいちばん上のノード CryEngine_Z_up が船の Z 上 (前 = +Y) を表示の Y 上 (前 = −Z) に回している: 表示 (X, Y, Z) = 船 (x, z, −y)。逆は 船 (x, y, z) = 表示 (X, −Z, Y)
  const toWalk = (v, out) => out.set(v.x, -v.z, v.y);

  // ===== 数字と状態 (移植元 view3d.js 146-157) =====
  const STEP = 0.6, WALK = 2.8, RUN = 10.0, TURN = 1.6;
  const STEP_FLOOR_MIN = 0.35;   // これより高い段を上がるときだけ「手すりの高さ」の検査 (tallObjectAt) に掛ける
  // 姿勢: 体の高さと目の高さ (立つ・しゃがむ・寝そべる)
  const STANCE = { stand: { h: 1.8, eye: 1.7, sp: 1 }, crouch: { h: 1.2, eye: 1.05, sp: 0.5 }, prone: { h: 0.5, eye: 0.35, sp: 0.25 } };
  let stance = 'stand';
  // ジャンプ: 上向きの速さ 4.43 m/秒 (重力 9.81 m/秒² で高さ 1.0 m)。よじ登り: 2.52 m まで
  const JUMP_V = 4.43, GRAV = 9.81, CLIMB = 2.52, LADDER_V = 1.5;
  let vz = 0, air = false, onLadder = null;
  let noclip = false;
  let walk = false, run = false, wyaw = 0, wpitch = 0, wpos = new THREE.Vector3();
  // 当たりの元を作っている間 (歩く計算・キーの操作を止める)
  let busy = false;

  // ===== キー (移植元 view3d.js 123-144) =====
  const keys = new Set();
  // キーの名前 (移植元の e.key.toLowerCase() に当たる文字)。歩くモードのキーは e.code から決める (日本語入力が ON だと e.key が 'Process' になるため)。ほかのキーは移植元と同じ e.key
  const CODE_KEY = { KeyW: 'w', KeyA: 'a', KeyS: 's', KeyD: 'd', KeyQ: 'q', KeyE: 'e', KeyC: 'c', KeyN: 'n', ShiftLeft: 'shift', ShiftRight: 'shift', Space: ' ', ControlLeft: 'control', ControlRight: 'control' };
  const keyOf = e => CODE_KEY[e.code] ?? (e.key || '').toLowerCase();
  addEventListener('keydown', e => { if (e.target.tagName === 'TEXTAREA' || e.target.tagName === 'SELECT' || (e.target.tagName === 'INPUT' && /^(text|search|number)$/.test(e.target.type))) return; const k = keyOf(e); keys.add(k); if (walk && k.length === 1 && 'wasdqe'.includes(k)) e.preventDefault(); });
  addEventListener('keyup', e => keys.delete(keyOf(e)));
  addEventListener('keydown', e => {
    if (e.target.tagName === 'SELECT') return;
    if (!walk || busy || e.repeat) return;
    if (e.target.tagName === 'INPUT' && /^(text|search|number)$/.test(e.target.type)) return;
    const k = keyOf(e);
    if (k === 'shift') { run = !run; }
    else if (k === ' ') {
      e.preventDefault(); if (air) return;
      // 船だけの決まり 5 (途中の階): はしごの上でスペースを押すと、見ている向きの 0.6・1.0・1.5 m 先に、足の高さから上下 0.6 m 以内の床 (立つ体が入る所) があれば、そこへ降りる。
      // 無ければ何もしない (移植元ははしごの上のスペースで何もしない)
      if (onLadder) {
        for (const r of LADDER_OUT) { const x = wpos.x + Math.cos(wyaw) * r, y = wpos.y + Math.sin(wyaw) * r, g = floorWithin(x, y, wpos.z, STEP); if (g !== null) { wpos.set(x, y, g); onLadder = null; vz = 0; walkCam(); break; } }
        return;
      }
      const fx = wpos.x + Math.cos(wyaw) * 0.7, fy = wpos.y + Math.sin(wyaw) * 0.7, g = groundAt(fx, fy, wpos.z + CLIMB);
      // 船だけの決まり 2: 今いる所の頭上の面が、移る先の床より低い (間に天井がある) とき、今いる所の頭上に面があって移る先の頭上に面が無い (船の外へ出る) ときは、
      // よじ登らずにふつうのジャンプにする
      const c0 = g !== null ? ceilAt(wpos.x, wpos.y, wpos.z + 0.05) : null, under = c0 !== null && c0 < g, outside = c0 !== null && ceilAt(fx, fy, g + 0.05) === null;
      if (g !== null && g > wpos.z + STEP && g <= wpos.z + CLIMB && !under && !outside && fits(fx, fy, g)) { wpos.set(fx, fy, g); }
      else { air = true; vz = JUMP_V; }
    }
    else if (k === 'n') { noclip = !noclip; }   // すり抜けモード: 床の判定だけを残し、体の高さの判定を無くす
    else if (k === 'c') { const n = stance === 'crouch' ? 'stand' : 'crouch'; const keep = stance; stance = n; if (!fits(wpos.x, wpos.y, wpos.z)) stance = keep; }
    else if (k === 'control') { e.preventDefault(); const n = stance === 'prone' ? 'stand' : 'prone'; const keep = stance; stance = n; if (!fits(wpos.x, wpos.y, wpos.z)) stance = keep; }
    updWalkUi();
  });

  // ===== 当たりの元 (船の mesh の三角形) =====
  // cur: { ladders: [[ノードの名前, mesh の名前, 箱の低い角, 高い角], …] (移植元 view3d-worker.js 429-430 と同じ並び), meshes: 元にした mesh の一覧, tris: 三角形の数 }
  // curBuf: 三角形の並び (1 つ 9 個の float: 3 つの角の x, y, z。歩く計算の座標)。位置 b は float の位置 (9 の倍数)
  // doorTri: 三角形ごとに、ドアの部品か (1／0)。移植元の doorParts (部品ごと) に当たる
  let cur = null, curBuf = null, doorTri = null, geoSeq = 0, building = null;
  // 名前を語に分ける: パスはファイル名だけにし、拡張子を外し、小文字にして '_' で分ける
  const nameWords = s => (s || '').split(/[\\/]/).pop().replace(/\.[A-Za-z0-9]+$/, '').toLowerCase().split('_');
  // ドアの部品か (移植元 view3d.js 184: 語に door か doors があり、elevator が無い)
  const isDoorWords = w => w.some(x => x === 'door' || x === 'doors') && !w.includes('elevator');
  // mesh の、GLB のノードの名前と mesh の名前 (three.js は名前から '/'・'.' などを除くので、GLB の JSON の名前を引く)。
  // primitive が 2 つ以上の mesh は Group (ノード) の子に Mesh。塗装で作り直して足した部品 (paintAdded) は、その mesh を出している入れ物の子
  function namesOf(o) {
    const p = env.parser(), model = env.model(); let ni, mi;
    // mesh の番号・ノードの番号は、自分か、いちばん近い親の入れ物の対応 (GLTFLoader の associations) から引く
    for (let q = o; q && q !== model && (ni === undefined || mi === undefined); q = q.parent) { const a = p?.associations.get(q); if (mi === undefined) mi = a?.meshes; if (ni === undefined) ni = a?.nodes; }
    const nn = ni !== undefined ? p.json.nodes?.[ni]?.name : undefined, mn = mi !== undefined ? p.json.meshes?.[mi]?.name : undefined;
    return [nn ?? (ni === undefined ? o.name : ''), mn ?? '', ni !== undefined ? 'n' + ni : o.uuid];
  }
  // 場面に出ている mesh (見えない入れ物の下・見えない材質 (塗装で作り直す mesh の元の部品 HIDDEN_MAT) は入れない)
  const shown = m => Array.isArray(m) ? m.some(x => x && x.visible !== false) : !!m && m.visible !== false;
  function drawnMeshes() {
    const out = [], model = env.model(); if (!model) return out;
    const each = o => { if (!o.visible) return; if (o.isMesh && o.geometry?.attributes?.position && shown(o.material)) out.push(o); for (const c of o.children) each(c); };
    each(model); return out;
  }
  const triCount = o => Math.floor((o.geometry.index ? o.geometry.index.count : o.geometry.attributes.position.count) / 3);
  const tick = () => new Promise(r => setTimeout(r, 0));
  // ----- 升目用の開いた番地の表 (移植元 view3d-worker.js 45-84) -----
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
    if (kmin >= -2147483648 && kmax <= 2147483647) keys = Int32Array.from(keys);
    keys.sort();
    const starts = new Uint32Array(T.n + 1); let tot = 0; T.pos = new Uint32Array(T.cap);
    for (let i = 0; i < T.n; i++) { const sl = tableSlot(T, keys[i]); starts[i] = tot; T.pos[sl] = tot; tot += T.cnt[sl]; }
    starts[T.n] = tot;
    const items = new Uint32Array(tot);
    return { items, grid: { keys, starts, items } };
  }
  // 船だけの決まり 4: はしごの箱 [名前, 名前, 低い角, 高い角] のうち、水平の範囲 (x・y) が重なり、上下に続いている (すき間が LADDER_GAP m 以内。重なりも含む) ものを、
  // 1 つの箱 (いちばん下からいちばん上まで。水平の範囲は合わせたもの) にまとめる。船のはしごは段ごとの部品が縦に並ぶため (例: Carrack の anvl_crk_srvc_ladder_rungs 12 個)。
  // まとまるものが無くなるまで繰り返す
  const LADDER_GAP = 1.5;
  function mergeLadders(list) {
    const out = list.map(l => [l[0], l[1], l[2].slice(), l[3].slice()]);
    for (let again = true; again;) {
      again = false;
      for (let i = 0; i < out.length && !again; i++) for (let j = i + 1; j < out.length; j++) {
        const a = out[i], b = out[j];
        if (a[2][0] > b[3][0] || b[2][0] > a[3][0] || a[2][1] > b[3][1] || b[2][1] > a[3][1]) continue;   // 水平の範囲が重ならない
        if (Math.max(a[2][2], b[2][2]) - Math.min(a[3][2], b[3][2]) > LADDER_GAP) continue;   // 上下のすき間
        for (let k = 0; k < 3; k++) { a[2][k] = Math.min(a[2][k], b[2][k]); a[3][k] = Math.max(a[3][k], b[3][k]); }
        out.splice(j, 1); again = true; break;
      }
    }
    return out;
  }
  // 当たりの元を作る。mesh ごとの三角形を歩く計算の座標にして並べ (curBuf)、ドアの印 (doorTri)・はしごの箱 (cur.ladders)・荒い区切り (coarse) を作る。
  // 大きい船で画面が固まらないよう、40 ミリ秒ごとに描画へ返す。作っている間に形が変わった (塗装の切り替え) ら、やめて false
  let buildStat = null;
  async function buildCur() {
    const seq = ++geoSeq, t0 = performance.now(); let last = t0;
    const pause = async () => { if (performance.now() - last < 40) return true; await tick(); last = performance.now(); return seq === geoSeq; };
    const model = env.model(); model.updateMatrixWorld(true);
    const meshes = drawnMeshes(); let n = 0; for (const o of meshes) n += triCount(o);
    const F = new Float32Array(n * 9), D = new Uint8Array(n), lad = new Map();
    let b = 0;
    for (const o of meshes) {
      const [nn, mn, key] = namesOf(o), wn = nameWords(nn), wm = nameWords(mn);
      const door = isDoorWords(wn) || isDoorWords(wm), ladder = wn.includes('ladder') || wm.includes('ladder');
      const pos = o.geometry.attributes.position, idx = o.geometry.index, nt = triCount(o), e = o.matrixWorld.elements;
      const P = !pos.isInterleavedBufferAttribute && !pos.normalized && pos.itemSize === 3 ? pos.array : null, I = idx ? idx.array : null;
      let box = null; if (ladder) { box = lad.get(key); if (!box) lad.set(key, box = [nn, mn, [Infinity, Infinity, Infinity], [-Infinity, -Infinity, -Infinity]]); }
      if (door) D.fill(1, b / 9, b / 9 + nt);
      for (let t = 0; t < nt; t++) {
        for (let k = 0; k < 3; k++) {
          const vi = I ? I[t * 3 + k] : t * 3 + k;
          const x = P ? P[vi * 3] : pos.getX(vi), y = P ? P[vi * 3 + 1] : pos.getY(vi), z = P ? P[vi * 3 + 2] : pos.getZ(vi);
          // 表示の座標 (mesh の置き方 matrixWorld を当てたもの) → 歩く計算の座標 (toWalk と同じ: x, −Z, Y)
          const X = e[0] * x + e[4] * y + e[8] * z + e[12], Y = e[1] * x + e[5] * y + e[9] * z + e[13], Z = e[2] * x + e[6] * y + e[10] * z + e[14];
          F[b] = X; F[b + 1] = -Z; F[b + 2] = Y;
          if (box) for (let a = 0; a < 3; a++) { const v = F[b + a]; if (v < box[2][a]) box[2][a] = v; if (v > box[3][a]) box[3][a] = v; }
          b += 3;
        }
        if ((t & 16383) === 16383 && !(await pause())) return false;
      }
      if (!(await pause())) return false;
    }
    // はしごの箱 (移植元 view3d-worker.js 429-430: 名前の語に ladder のある部品ごとの箱。数字は小数 3 桁)
    const ladders = mergeLadders([...lad.values()].filter(l => l[2][0] <= l[3][0])).map(l => [l[0], l[1], l[2].map(v => +v.toFixed(3)), l[3].map(v => +v.toFixed(3))]);
    // 当たりの荒い区切り (移植元 view3d-worker.js 445-473 の coarse の all・bigAll。fl は船では作らない)
    const BOX = 16, BIG_BOXES = 64;
    const csweep = async (add, big) => {
      for (let b = 0; b < F.length; b += 9) {
        const I0 = Math.floor(Math.min(F[b], F[b + 3], F[b + 6]) / BOX), I1 = Math.floor(Math.max(F[b], F[b + 3], F[b + 6]) / BOX), J0 = Math.floor(Math.min(F[b + 1], F[b + 4], F[b + 7]) / BOX), J1 = Math.floor(Math.max(F[b + 1], F[b + 4], F[b + 7]) / BOX);
        if ((I1 - I0 + 1) * (J1 - J0 + 1) > BIG_BOXES) big(b);
        else for (let I = I0; I <= I1; I++) for (let J = J0; J <= J1; J++) add(I * 100003 + J, b);
        if ((b / 9 & 65535) === 65535 && !(await pause())) return false;
      }
      return true;
    };
    const T = makeTable(), bigs = [];
    if (!(await csweep(key => tableCount(T, key), b => bigs.push(b)))) return false;
    const out = tableLayout(T);
    if (!(await csweep((key, b) => { out.items[T.pos[tableSlot(T, key)]++] = b; }, () => { }))) return false;
    if (seq !== geoSeq) return false;
    cellReset();
    curBuf = F.buffer; doorTri = D; cur = { ladders, meshes, tris: n };
    coarse = { all: out.grid, bigAll: Uint32Array.from(bigs) };
    buildStat = { ms: Math.round(performance.now() - t0), tris: n, meshes: meshes.length, doorTris: D.reduce((s, v) => s + v, 0), ladders: ladders.length, bytes: F.byteLength + D.byteLength + out.grid.items.byteLength + out.grid.keys.byteLength + out.grid.starts.byteLength + bigs.length * 4, boxes: out.grid.keys.length, big: bigs.length };
    return true;
  }
  // 当たりの元が無ければ作る (作っている間は状態の文で知らせる)。できたら true
  async function ensureCur() {
    while (!coarse) {
      busy = true; $('walkInfo').textContent = '当たりの元を作っています...';
      if (!building) building = buildCur().finally(() => { building = null; });
      try { await building; }
      catch (err) {
        // 作れなかった (メモリが足りないなど): 歩くモードを OFF に戻して知らせる
        busy = false; if (walk) { walk = false; showWalkBtn(); $('walkInfo').textContent = `当たりの元を作れませんでした: ${err?.message ?? err}`; }
        return false;
      }
      if (!walk) { busy = false; return false; }
    }
    busy = false; return true;
  }
  function showWalkBtn() { $('walkBtn').textContent = '歩くモード: ' + (walk ? 'ON' : 'OFF'); $('walkBtn').classList.toggle('on', walk); helpEl.innerHTML = walk ? WALK_HELP : helpHtml; }
  // 場面に出ている mesh が変わった (塗装の切り替えで部品を作り直して足した・外した) ら、当たりの元を捨てる。歩いている間なら作り直す (立っている所はそのまま)
  function geoChanged() {
    if (!cur && !building) return;
    const now = drawnMeshes();
    if (cur && !building && now.length === cur.meshes.length && now.every((o, i) => o === cur.meshes[i])) return;
    ++geoSeq; cellReset(); coarse = null; cur = null; curBuf = null; doorTri = null;
    if (walk) ensureCur().then(ok => { if (ok && walk) updWalkUi(); });
  }

  // ===== 当たり判定の升 (引いたときだけ作る。移植元 view3d.js 158-222) =====
  //   f (足元): 上向き (cz > 0)・1 m の升・升の数 ≤ 4096。ドアは入れない (船だけの決まり 3。移植元はドアも入れる: 閉じたドアの細かい上向きの面に乗って止まるため)
  //   c (頭の上): 下向き (cz < 0)・2 m の升 (floor(x/2))・升の数 ≤ 1024。ドアは入れない
  //   w (横): ドア以外の全部・1 m の升・升の数 ≤ 4096
  let coarse = null;
  const cells = { f: new Map(), c: new Map(), w: new Map() };
  const CELL_BOX = 16;
  function cellReset() { for (const m of Object.values(cells)) m.clear(); }
  function boxGet(g, key) {
    if (!g) return null; const K = g.keys; let lo = 0, hi = K.length - 1;
    while (lo <= hi) { const m = (lo + hi) >> 1, v = K[m]; if (v === key) return g.items.subarray(g.starts[m], g.starts[m + 1]); if (v < key) lo = m + 1; else hi = m - 1; }
    return null;
  }
  // 三角形の位置 b の部品がドアか (移植元は部品の添字から引く。船は三角形ごとの印 doorTri から引く)
  function triIsDoor(b) { return doorTri[b / 9] === 1; }
  // 升 (kind, i, j) に入る三角形か
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
    if (kind === 'f') return (F[b + 3] - F[b]) * (F[b + 7] - F[b + 1]) - (F[b + 4] - F[b + 1]) * (F[b + 6] - F[b]) > 0 && !triIsDoor(b);
    if (kind === 'w') return !triIsDoor(b);
    return true;
  }
  // 升 (kind, i, j。c は 2 m の升の番号、ほかは 1 m) の三角形の位置の並び (b の昇順)。無ければ null。初めて引いたときに作って cells に持つ
  function cellGet(kind, i, j) {
    if (!coarse) return null;
    const M = cells[kind], key = i * 100003 + j; const got = M.get(key); if (got !== undefined) return got;
    const F = new Float32Array(curBuf);
    const s = kind === 'c' ? 2 : 1;
    const box = boxGet(coarse.all, Math.floor(i * s / CELL_BOX) * 100003 + Math.floor(j * s / CELL_BOX)) || [];
    const big = coarse.bigAll;
    const out = []; let p = 0, q = 0;
    while (p < box.length || q < big.length) {
      const b = q >= big.length || (p < box.length && box[p] < big[q]) ? box[p++] : big[q++];
      if (triInCell(F, kind, b, i, j)) out.push(b);
    }
    const res = out.length ? Uint32Array.from(out) : null; M.set(key, res); return res;
  }

  // ===== 頭の上・横・足元 (移植元 view3d.js 225-312。切る面 visZ と補いの面 extra の所は船に無いので除いた) =====
  function ceilAt(x, y, zmin) {
    // (x, y) の真上で、zmin より高い下向きの面のいちばん低い所
    const all = new Float32Array(curBuf); const l = cellGet('c', Math.floor(x / 2), Math.floor(y / 2)); let best = null;
    if (l) for (const b of l) {
      const ax = all[b], ay = all[b + 1], az_ = all[b + 2], bx = all[b + 3], by = all[b + 4], bz = all[b + 5], cx = all[b + 6], cy = all[b + 7], cz = all[b + 8];
      const v0x = bx - ax, v0y = by - ay, v1x = cx - ax, v1y = cy - ay, den = v0x * v1y - v1x * v0y; if (Math.abs(den) < 1e-12) continue;
      const wx = x - ax, wy = y - ay, s_ = (wx * v1y - v1x * wy) / den, t_ = (v0x * wy - wx * v0y) / den;
      if (s_ < 0 || t_ < 0 || s_ + t_ > 1) continue;
      const z = az_ + s_ * (bz - az_) + t_ * (cz - az_); if (z > zmin && (best === null || z < best)) best = z;
    }
    return best;
  }
  // 横の当たり: ドアを除く全部の三角形を 1 m の升目に入れ、動く向きへ、足から STEP より上・体の高さまでの 3 つの高さで、動く距離 + 体の半径 0.25 m の線を引き、三角形に当たったら動かない
  const BODY_R = 0.25;
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
        const t = (e2x * qx + e2y * qy + e2z * qz) * inv; if (t > 0 && t < 1) return true;
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
    if (H - 0.1 > STEP + 0.05 && segHit(ex, ey, z + STEP + 0.05, ex, ey, z + H - 0.1)) return true;
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
      const z = az_ + s_ * (bz - az_) + t_ * (cz - az_); if (z <= zmax && (best === null || z > best)) best = z;
    }
    return best;
  }
  // 落ちる下限: 今の位置の真下と、まわり 0.5 m・1 m の各 8 点 (計 17 点) で、その点を含む歩く面のうち、いちばん低いものの高さ。どの点にも面が無ければ null
  const FALL_R = 1.0;
  function lowAt(x, y) {
    const all = new Float32Array(curBuf); const l = cellGet('f', Math.floor(x), Math.floor(y)); let best = null;
    if (l) for (const b of l) {
      const ax = all[b], ay = all[b + 1], az_ = all[b + 2], bx = all[b + 3], by = all[b + 4], bz = all[b + 5], cx = all[b + 6], cy = all[b + 7], cz = all[b + 8];
      const v0x = bx - ax, v0y = by - ay, v1x = cx - ax, v1y = cy - ay, den = v0x * v1y - v1x * v0y; if (Math.abs(den) < 1e-12) continue;
      const wx = x - ax, wy = y - ay, s_ = (wx * v1y - v1x * wy) / den, t_ = (v0x * wy - wx * v0y) / den;
      if (s_ < 0 || t_ < 0 || s_ + t_ > 1) continue;
      const z = az_ + s_ * (bz - az_) + t_ * (cz - az_); if (best === null || z < best) best = z;
    }
    return best;
  }
  function lowestNear(x, y) {
    let best = lowAt(x, y);
    for (const r of [FALL_R / 2, FALL_R]) for (let k = 0; k < 8; k++) { const a = k * Math.PI / 4; const g = lowAt(x + r * Math.cos(a), y + r * Math.sin(a)); if (g !== null && (best === null || g < best)) best = g; }
    return best;
  }
  // 段を上がる先が「手すりの高さ」の物か (移植元 view3d.js 1749-1761)
  function tallObjectAt(x, y, z) {
    for (let k = -1; k < 8; k++) {
      const a = k * Math.PI / 4, px = k < 0 ? x : x + GAP_R * Math.cos(a), py = k < 0 ? y : y + GAP_R * Math.sin(a);
      const top = groundAt(px, py, z + STEP); if (top === null || top <= z + STEP_FLOOR_MIN) continue;
      const below = groundAt(px, py, top - 0.05);
      if (below !== null && below >= z - STEP && top - below > STEP) return true;
    }
    return false;
  }

  // ===== 歩く (移植元 view3d.js 414-478) =====
  const helpEl = $('help'), helpHtml = helpEl.innerHTML;
  // 歩くモード中の操作の説明 (移植元 view3d.html 110-121 の表のうち、船にある操作)
  const WALK_HELP = 'W・S: 前後に歩く　A・D: 左右に歩く　Q・E・ドラッグ: 向きを変える　Shift: 走る ON・OFF　スペース: ジャンプ (前に段があれば、よじ登る)<br>'
    + 'C: しゃがむ　Ctrl: 寝そべる　N: すり抜け ON・OFF　Insert/Delete: 拡大/縮小<br>'
    + 'はしごのそばで W: 上る / S: 下りる / スペース: 見ている向きの床へ降りる';
  function updWalkUi() {
    const sn = { stand: '立つ', crouch: 'しゃがむ', prone: '寝そべる' }[stance];
    $('runBtn').textContent = '走る: ' + (run ? 'ON' : 'OFF'); $('runBtn').classList.toggle('on', run);
    $('stBtn').textContent = '姿勢: ' + sn; $('stBtn').classList.toggle('on', stance !== 'stand');
    $('ncBtn').textContent = 'すり抜け: ' + (noclip ? 'ON' : 'OFF'); $('ncBtn').classList.toggle('on', noclip);
    if (!busy) $('walkInfo').textContent = walk ? `${run ? '走る' : '歩く'}・${sn}・すり抜け ${noclip ? 'ON' : 'OFF'}` : '';
  }
  // 足元のすき間の決まり: 足の真下と、まわり 0.25 m の 8 点の、いちばん高い上向きの面を足元にする
  const GAP_R = 0.25; let gapLog = [];
  function groundGap(x, y, zmax) {
    let best = groundAt(x, y, zmax), c0 = best;
    for (let k = 0; k < 8; k++) { const a = k * Math.PI / 4; const g = groundAt(x + GAP_R * Math.cos(a), y + GAP_R * Math.sin(a), zmax); if (g !== null && (best === null || g > best)) best = g; }
    if (best !== null && (c0 === null || c0 < best - 0.05) && gapLog.length < 200) gapLog.push([+x.toFixed(2), +y.toFixed(2), c0 === null ? null : +c0.toFixed(2), +best.toFixed(2)]);
    return best;
  }
  // カメラを足元 wpos・向き wyaw・上下 wpitch に合わせる (歩く計算の座標 → 表示の座標。船内の見る位置 inside に入れて applyCamera で出すので、歩くモードを OFF にしてもカメラは動かない)。
  // 移植元の walkCam: 目 = wpos + (0, 0, 目の高さ)、見る向き = (cos p·cos y, cos p·sin y, sin p)。表示では (x, z, −y)。船内のカメラは yaw = 0 で −Z (船の前 = +Y) を見るので yaw = wyaw − 90°
  function walkCam() {
    inside.pos.set(wpos.x, wpos.z + STANCE[stance].eye, -wpos.y);
    inside.yaw = wyaw - Math.PI / 2; inside.pitch = wpitch;
    env.applyCamera();
  }
  // (x, y) の、高さ z から上下 tol m 以内の床 (上向きの面のいちばん高い所) で、今の姿勢の体が入る所。無ければ null (船だけの決まり 5 で使う)。
  // 今いる所の頭上に面があり、その床の頭上に面が無い (船の外へ出る) ときは選ばない (船だけの決まり 2 のよじ登りと同じ条件。
  // これが無いと Carrack のはしごの上端で、すぐ横の外板の上 (頭上に面なし) に出る)
  function floorWithin(x, y, z, tol) {
    const g = groundAt(x, y, z + tol); if (g === null || g < z - tol || !fits(x, y, g)) return null;
    if (ceilAt(wpos.x, wpos.y, wpos.z + 0.05) !== null && ceilAt(x, y, g + 0.05) === null) return null;
    return g;
  }
  // 船だけの決まり 5 (上端): はしごの上端 (wpos.z) に着いたとき、見ている向きへ 0.6 m 出た所に床 (上端の高さから上下 LADDER_TOP_TOL m 以内で、体が入る所) が無ければ、
  // 同じ向きへ 1.0 m・1.5 m、それでも無ければまわり 8 方向の 0.6・1.0・1.5 m を近い順に探し、最初に見つかった床に立つ。立てたら true。どこにも無ければ false (移植元の扱いのまま)
  const LADDER_OUT = [0.6, 1.0, 1.5], LADDER_TOP_TOL = 1.0;
  function ladderTop() {
    const at = (a, r) => { const x = wpos.x + Math.cos(a) * r, y = wpos.y + Math.sin(a) * r, g = floorWithin(x, y, wpos.z, LADDER_TOP_TOL); if (g === null) return false; wpos.set(x, y, g); return true; };
    for (const r of LADDER_OUT) if (at(wyaw, r)) return true;
    for (const r of LADDER_OUT) for (let k = 1; k < 8; k++) if (at(wyaw + k * Math.PI / 4, r)) return true;
    return false;
  }
  function walkStep(dt) {
    if (!walk || busy) return;
    const f = new THREE.Vector3(Math.cos(wyaw), Math.sin(wyaw), 0), r = new THREE.Vector3(Math.sin(wyaw), -Math.cos(wyaw), 0);
    if (keys.has('q')) wyaw += TURN * dt; if (keys.has('e')) wyaw -= TURN * dt;
    const mv = new THREE.Vector3();
    if (keys.has('w')) mv.add(f); if (keys.has('s')) mv.sub(f); if (keys.has('d')) mv.add(r); if (keys.has('a')) mv.sub(r);
    // はしご: はしごの部品の箱から xy で 0.6 m 以内で W を押すと登る (S で下りる)。上の端に着いたら前へ 0.6 m 出て、そこの床に立つ
    if (!onLadder && keys.has('w')) onLadder = (cur.ladders || []).find(l => Math.max(l[2][0] - wpos.x, wpos.x - l[3][0], 0) ** 2 + Math.max(l[2][1] - wpos.y, wpos.y - l[3][1], 0) ** 2 <= 0.36 && wpos.z >= l[2][2] - 0.5 && wpos.z < l[3][2] - 0.1) || null;
    if (onLadder) {
      if (keys.has('w')) wpos.z += LADDER_V * dt; if (keys.has('s')) wpos.z -= LADDER_V * dt;
      if (wpos.z >= onLadder[3][2]) { wpos.z = onLadder[3][2]; if (!ladderTop()) { wpos.x += Math.cos(wyaw) * 0.6; wpos.y += Math.sin(wyaw) * 0.6; const g = groundAt(wpos.x, wpos.y, wpos.z + STEP); if (g !== null) wpos.z = g; } onLadder = null; }
      else if (wpos.z <= onLadder[2][2] - 0.5 || !keys.size) { if (!keys.has('w') && !keys.has('s')) {} else if (wpos.z <= onLadder[2][2]) onLadder = null; }
      walkCam(); return;
    }
    const spd = (run ? RUN : WALK) * STANCE[stance].sp * dt;
    if (mv.lengthSq() > 0) {
      mv.normalize().multiplyScalar(spd); const nx = wpos.x + mv.x, ny = wpos.y + mv.y;
      const g = groundGap(nx, ny, wpos.z + STEP);
      const nz_ = air ? wpos.z : (g !== null && g >= wpos.z - STEP ? g : wpos.z);
      let tx = nx, ty = ny;
      // 段を上がる (STEP_FLOOR_MIN より高い) ときは、上がる先が「手すりの高さ」の物 (tallObjectAt) でないときだけ
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
    walkCam();
  }

  // ===== ［歩くモード］［走る］［姿勢］［すり抜け］ (移植元 view3d.js 1657-1678) =====
  // 今のカメラの xy のまま、カメラの真下の面に立つ (面が無ければカメラの高さ − 目の高さ)。向きもカメラのまま
  function standUnderCamera() {
    const st0 = toWalk(cam.position, new THREE.Vector3()); wpos.set(st0.x, st0.y, st0.z - STANCE.stand.eye);
    const g = groundAt(st0.x, st0.y, st0.z); if (g !== null) wpos.z = g;
    const dv = toWalk(cam.getWorldDirection(new THREE.Vector3()), new THREE.Vector3()); wyaw = Math.atan2(dv.y, dv.x); wpitch = Math.asin(Math.max(-1, Math.min(1, dv.z)));
  }
  // 船だけの決まり 1: 入った所 (カメラの真下の面) で「立つ」の体が入らない (fits が成り立たない)・真下に面が無い・頭上に面が無いときは、
  // 今の位置から水平に近い順 (0.5 m 刻みで 6 m まで、まわり 16 方向) に、床 (カメラの高さより下の面) があって「立つ」の体が入り、頭上に面がある所を探し、最初に見つかった所に立つ。
  // 見つからなければそのまま (移植元の扱い)。向きはカメラのまま
  function standNear() {
    const st0 = toWalk(cam.position, new THREE.Vector3());
    // 立てる床: 面があり、「立つ」の体が入り、頭上に面がある (船の中の床だけを選ぶ)
    const ok = (x, y, g) => g !== null && fits(x, y, g) && ceilAt(x, y, g + 0.05) !== null;
    if (ok(st0.x, st0.y, groundAt(st0.x, st0.y, st0.z))) return;
    for (let r = 0.5; r <= 6; r += 0.5) for (let k = 0; k < 16; k++) {
      const a = k * Math.PI / 8, x = st0.x + r * Math.cos(a), y = st0.y + r * Math.sin(a), g = groundAt(x, y, st0.z);
      if (ok(x, y, g)) { wpos.set(x, y, g); return; }
    }
  }
  async function setWalk(on) {
    if (on === walk) return;
    if (on && (!env.model() || env.mode() !== 'inside')) return;
    walk = on; showWalkBtn();
    if (walk) {
      if (!(await ensureCur())) return;   // 作っている間に OFF にされた
      air = false; vz = 0; onLadder = null; stance = 'stand';
      standUnderCamera(); standNear(); walkCam(); updWalkUi(); $('walkInfo').textContent = run ? '走る' : '歩く';
    } else {
      // 歩くモードを OFF にしても、カメラの位置と向きは変えない (walkCam が船内の見る位置 inside に入れてある)
      busy = false; $('walkInfo').textContent = '';
    }
  }
  $('walkBtn').onclick = () => { setWalk(!walk); env.focus(); };
  $('runBtn').onclick = () => { run = !run; updWalkUi(); env.focus(); };
  $('stBtn').onclick = () => { const n = { stand: 'crouch', crouch: 'prone', prone: 'stand' }[stance]; const keep = stance; stance = n; if (walk && !busy && !fits(wpos.x, wpos.y, wpos.z)) stance = keep; updWalkUi(); env.focus(); };
  $('ncBtn').onclick = () => { noclip = !noclip; updWalkUi(); env.focus(); };

  // 確かめ用 (Edge headless の試しから呼ぶ。移植元 view3d.js 916 の window.__walk に当たる)
  window.__walk = {
    low: (x, y) => lowestNear(x, y), setPitch: v => { wpitch = v; walkCam(); }, place: (x, y, z, yaw) => { wpos.set(x, y, z); wyaw = yaw; air = false; vz = 0; onLadder = null; walkCam(); },
    ground: (x, y, z) => groundAt(x, y, z), ceil: (x, y, z) => ceilAt(x, y, z), tall: (x, y, z) => tallObjectAt(x, y, z), gap: (x, y, z) => groundGap(x, y, z), gaps: () => gapLog, seg: (...a) => segHit(...a), blocked: (...a) => blocked(...a), fits: (...a) => fits(...a),
    on: () => walk, busy: () => busy, pos: () => [wpos.x, wpos.y, wpos.z, wyaw, run, stance, air, noclip, onLadder ? onLadder[0] : null], cam, keys, step: dt => walkStep(dt),
    cur: () => cur, stat: () => buildStat, cellCount: () => Object.fromEntries(Object.entries(cells).map(([k, m]) => [k, m.size])), setWalk, geoChanged,
  };

  return {
    on: () => walk,
    // 描くたびに呼ぶ (移植元 view3d.js 918-921 の keyMove → walkStep。dt は秒、上限 0.5)
    step: walkStep,
    // 見回す (ドラッグ。移植元 view3d.js 93-97: 右ボタンのドラッグだけ上下が逆)
    look(dx, dy, button) { if (busy) return; const vy = button === 2 ? -1 : 1; wyaw -= dx * 0.005; wpitch = Math.max(-1.4, Math.min(1.4, wpitch - vy * dy * 0.005)); walkCam(); },
    off() { setWalk(false); },
    geoChanged,
  };
}
