using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StarCitizenJapaneseTextCreater;

// Data.p4k (と socpak の中の ZIP) の読み取り (2026-11-04、利用者の決定「starbreaker の処理を自前で持つ」(案 A))。
// StarBreaker v0.2.2 (crates/starbreaker-p4k の archive.rs・crypto.rs・owned.rs) の移植。読み方は starbreaker と同じにする:
//   ・目次 (中央ディレクトリ) の項目の名前は、バイトを 1 文字ずつ (Latin-1) 文字にし、「/」を「\」にしたもの
//   ・ZIP64 の目次の追加の欄は 0x0001 → 0x5000 → 0x5002 → 0x5003 の順に決め打ちで読む (archive.rs read_entry)。0x5002 の値が 1 なら暗号化
//   ・大文字小文字を区別しない引き当ては ASCII の A-Z だけを小文字にした名前で引き、同じ名前が 2 つあれば後のもの (to_ascii_lowercase の表に後から入れたもの)
//   ・取り出し: ローカルヘッダの印は 0x04034B50 か 0x14034B50。方式 100 は zstd (暗号化なら AES-128-CBC で戻し、末尾の 0 を削ってから)、8 は deflate、0 は無圧縮
// 位置表はメモリを抑えるため、名前を 1 つのバイト列に並べ、位置・大きさ・方式を配列で持つ (項目ごとの文字列・オブジェクトは作らない)。
// Data.p4k の目次は 1 回だけ読み、同じファイル (大きさ・更新時刻が同じ) の間は使い回す (Shared)。
internal sealed class P4kArchive : IDisposable
{
    // ===== 開く・使い回す =====
    private static readonly object SharedLock = new();
    private static P4kArchive? _shared;

    // Data.p4k の目次を読んだもの。同じパス・大きさ・更新時刻なら前に読んだものを返す (Dispose しないこと)
    public static P4kArchive Shared(string p4kPath)
    {
        var fi = new FileInfo(p4kPath);
        lock (SharedLock)
        {
            if (_shared is { } s && string.Equals(s.Path, fi.FullName, StringComparison.OrdinalIgnoreCase) && s.Size == fi.Length && s.WriteUtcTicks == fi.LastWriteTimeUtc.Ticks)
                return s;
            var a = Open(fi.FullName);
            a._sharedInstance = true;
            var old = _shared;
            _shared = a;
            old?.ReleaseShared();
            return a;
        }
    }

    // 使い回しの表を捨てる (メモリを返す。読み取り中のものは読み終わるまで閉じない)
    public static void DropShared()
    {
        lock (SharedLock) { var old = _shared; _shared = null; old?.ReleaseShared(); }
    }

    // ===== 中身 =====
    public string Path { get; }
    public long Size { get; }
    public long WriteUtcTicks { get; }
    public int Count { get; private set; }
    public double OpenSeconds { get; private set; }

    // 名前 (「\」区切り、元の大文字小文字)。メモリを抑えるため、フォルダ (最後の「\」より前) とファイル名に分けて持つ。
    //   フォルダは重ねずに 1 回だけ _dirNames に並べ (_dirOff・_dirLen)、項目はフォルダの番号 (_dir。フォルダの無い名前は -1) とファイル名 (_names の _nameOff から _nameLen バイト)
    private byte[] _names = [];
    private int[] _nameOff = [];
    private ushort[] _nameLen = [];
    private int[] _dir = [];
    private byte[] _dirNames = [];
    private int[] _dirOff = [];
    private ushort[] _dirLen = [];
    private long[] _offset = [], _csize = [], _usize = [];
    private ushort[] _method = [];
    private bool[] _encrypted = [];

    // 大文字小文字を区別しない引き当て (開番地法。値は項目の番号 + 1、0 は空き)
    private int[] _buckets = [];

    private readonly SafeFileHandle? _handle;   // ファイルから読むとき
    private readonly byte[]? _data;             // メモリのバイト列から読むとき (socpak の中の ZIP)
    private readonly int _dataStart, _dataLength;
    private bool _sharedInstance;
    private int _users;          // Shared のとき、読み取り中の数
    private bool _released;

    private P4kArchive(string path, SafeFileHandle handle)
    {
        Path = path;
        _handle = handle;
        var fi = new FileInfo(path);
        Size = fi.Length;
        WriteUtcTicks = fi.LastWriteTimeUtc.Ticks;
    }

    private P4kArchive(byte[] data, int start, int length)
    {
        Path = "";
        _data = data;
        _dataStart = start;
        _dataLength = length;
        Size = length;
    }

    public void Dispose()
    {
        if (_sharedInstance) return;   // 使い回しのものは DropShared で閉じる
        _handle?.Dispose();
    }

    private void ReleaseShared()
    {
        lock (SharedLock)
        {
            _released = true;
            if (_users == 0) _handle?.Dispose();
        }
    }

    // ファイル (Data.p4k) を開いて目次を読む。使い終わったら Dispose
    public static P4kArchive Open(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        try
        {
            var a = new P4kArchive(System.IO.Path.GetFullPath(path), handle);
            a.ParseCentralDirectory();
            a.OpenSeconds = sw.Elapsed.TotalSeconds;
            return a;
        }
        catch { handle.Dispose(); throw; }
    }

    // メモリの ZIP (socpak) を読む (P4kArchive::from_bytes)
    public static P4kArchive FromBytes(byte[] data) => FromBytes(data, 0, data.Length);
    public static P4kArchive FromBytes(byte[] data, int start, int length)
    {
        var a = new P4kArchive(data, start, length);
        a.ParseCentralDirectory();
        return a;
    }

    private void ReadAt(long pos, Span<byte> buf)
    {
        if (_data != null)
        {
            if (pos < 0 || pos + buf.Length > _dataLength) throw new InvalidDataException($"ZIP の範囲の外を読もうとしました (位置 {pos}、{buf.Length} バイト)");
            _data.AsSpan(_dataStart + (int)pos, buf.Length).CopyTo(buf);
            return;
        }
        int done = 0;
        while (done < buf.Length)
        {
            int n = RandomAccess.Read(_handle!, buf[done..], pos + done);
            if (n <= 0) throw new EndOfStreamException($"{Path} の終わりを越えて読もうとしました (位置 {pos + done})");
            done += n;
        }
    }

    // ===== 目次 (archive.rs locate_central_directory・parse_entries・read_entry) =====
    private void ParseCentralDirectory()
    {
        long fileLen = Size;
        int tailSize = (int)Math.Min(fileLen, 22 + 65535 + 56 + 20);
        long tailOffset = fileLen - tailSize;
        var tail = new byte[tailSize];
        ReadAt(tailOffset, tail);
        // EOCD を後ろから探す (find_eocd: 末尾から 22 + 65535 バイトの範囲)
        int searchStart = Math.Max(0, tail.Length - (22 + 65535));
        int searchEnd = tail.Length - 22;
        int eocd = -1;
        for (int i = searchEnd; i >= searchStart; i--)
            if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("ZIP の中央ディレクトリの終わり (EOCD) が見つかりません");
        var e = tail.AsSpan(eocd);
        ushort disk = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]), startDisk = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
        ushort onDisk = BinaryPrimitives.ReadUInt16LittleEndian(e[8..]), total16 = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
        uint cdSize32 = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]), cdOff32 = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
        bool zip64 = disk == 0xFFFF || startDisk == 0xFFFF || onDisk == 0xFFFF || total16 == 0xFFFF || cdSize32 == 0xFFFFFFFF || cdOff32 == 0xFFFFFFFF;
        long total, cdOffset, cdSize;
        if (zip64)
        {
            // ZIP64 の locator を EOCD の前から後ろ向きに探す (find_zip64_locator)
            int loc = -1;
            for (int i = eocd - 1; i >= Math.Max(0, eocd - (22 + 65535)); i--)
                if (i + 4 <= tail.Length && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x07064B50) { loc = i; break; }
            if (loc < 0) throw new InvalidDataException("ZIP64 の locator が見つかりません");
            long eocd64Abs = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(loc + 8));
            long rel = eocd64Abs - tailOffset;
            if (rel < 0 || rel + 56 > tail.Length) throw new InvalidDataException("ZIP64 の中央ディレクトリの終わりが読めません");
            var r = tail.AsSpan((int)rel);
            if (BinaryPrimitives.ReadUInt32LittleEndian(r) != 0x06064B50) throw new InvalidDataException("ZIP64 の中央ディレクトリの終わりの印が違います");
            total = (long)BinaryPrimitives.ReadUInt64LittleEndian(r[32..]);
            cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(r[40..]);
            cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(r[48..]);
        }
        else { total = total16; cdSize = cdSize32; cdOffset = cdOff32; }
        if (total < 0 || total > int.MaxValue) throw new InvalidDataException($"ZIP の項目の数が読めません ({total})");

        int n = (int)total;
        _nameOff = new int[n]; _nameLen = new ushort[n]; _dir = new int[n];
        var dirIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var dirNames = new MemoryStream();
        var dirOff = new List<int>(); var dirLen = new List<ushort>();
        var nameTmp = new byte[65536];
        _offset = new long[n]; _csize = new long[n]; _usize = new long[n];
        _method = new ushort[n]; _encrypted = new bool[n];
        var names = new MemoryStream((int)Math.Min(int.MaxValue, Math.Max(16, cdSize / 4)));

        // 目次を 4 MB ずつ読む (全体をメモリに読まない)
        const int Chunk = 4 << 20;
        var buf = new byte[Chunk + 65536 * 3 + 46];
        int bufLen = 0, p = 0;
        long cdPos = cdOffset, cdEnd = cdOffset + cdSize;
        void Ensure(int need)
        {
            if (bufLen - p >= need) return;
            Buffer.BlockCopy(buf, p, buf, 0, bufLen - p);
            bufLen -= p; p = 0;
            if (need > buf.Length) Array.Resize(ref buf, need);
            while (bufLen < need)
            {
                int want = (int)Math.Min(buf.Length - bufLen, cdEnd - cdPos);
                if (want <= 0) throw new InvalidDataException("ZIP の中央ディレクトリが途中で切れています");
                ReadAt(cdPos, buf.AsSpan(bufLen, want));
                cdPos += want; bufLen += want;
            }
        }
        for (int k = 0; k < n; k++)
        {
            Ensure(46);
            var h = buf.AsSpan(p, 46);
            if (BinaryPrimitives.ReadUInt32LittleEndian(h) != 0x02014B50) throw new InvalidDataException($"ZIP の中央ディレクトリの {k + 1} 件目の印が違います");
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
            uint cs32 = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]), us32 = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
            int nl = BinaryPrimitives.ReadUInt16LittleEndian(h[28..]), xl = BinaryPrimitives.ReadUInt16LittleEndian(h[30..]), cl = BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
            ushort diskStart = BinaryPrimitives.ReadUInt16LittleEndian(h[34..]);
            uint off32 = BinaryPrimitives.ReadUInt32LittleEndian(h[42..]);
            p += 46;
            Ensure(nl + xl + cl);
            // 名前: バイトを 1 文字ずつ、「/」は「\」
            int lastSep = -1;
            for (int i = 0; i < nl; i++) { byte b = buf[p + i]; if (b == (byte)'/') b = (byte)'\\'; nameTmp[i] = b; if (b == (byte)'\\') lastSep = i; }
            if (lastSep < 0) _dir[k] = -1;
            else
            {
                var dkey = Encoding.Latin1.GetString(nameTmp, 0, lastSep);
                if (!dirIds.TryGetValue(dkey, out var di))
                {
                    di = dirOff.Count; dirIds[dkey] = di;
                    dirOff.Add((int)dirNames.Position); dirLen.Add((ushort)lastSep);
                    dirNames.Write(nameTmp, 0, lastSep);
                }
                _dir[k] = di;
            }
            _nameOff[k] = (int)names.Position;
            _nameLen[k] = (ushort)(nl - lastSep - 1);
            names.Write(nameTmp, lastSep + 1, nl - lastSep - 1);
            p += nl;
            long cs = cs32, us = us32, off = off32;
            bool enc = false;
            if (zip64)
            {
                var x = new ExtraReader(buf, p, xl, k);
                if (x.U16() != 0x0001) throw x.Bad("0x0001");
                x.U16();
                if (us32 == 0xFFFFFFFF) us = (long)x.U64();
                if (cs32 == 0xFFFFFFFF) cs = (long)x.U64();
                if (off32 == 0xFFFFFFFF) off = (long)x.U64();
                if (diskStart == 0xFFFF) x.U32();
                if (x.U16() != 0x5000) throw x.Bad("0x5000");
                int s5000 = x.U16();
                x.Skip(Math.Max(0, s5000 - 4));
                if (x.U16() != 0x5002) throw x.Bad("0x5002");
                if (x.U16() != 6) throw x.Bad("6");
                enc = x.U16() == 1;
                if (x.U16() != 0x5003) throw x.Bad("0x5003");
                int s5003 = x.U16();
                x.Skip(Math.Max(0, s5003 - 4));
            }
            p += xl;
            p += cl;
            _offset[k] = off; _csize[k] = cs; _usize[k] = us; _method[k] = method; _encrypted[k] = enc;
        }
        Count = n;
        _names = names.ToArray();
        _dirNames = dirNames.ToArray();
        _dirOff = dirOff.ToArray(); _dirLen = dirLen.ToArray();
        BuildIndex();
    }

    // 目次の追加の欄を読む (SpanReader と同じく、欄の長さを越えて読めば失敗)
    private struct ExtraReader(byte[] buf, int start, int length, int entry)
    {
        private int _pos = 0;
        private readonly Span<byte> Need(int n)
        {
            if (_pos + n > length) throw new InvalidDataException($"ZIP の中央ディレクトリの {entry + 1} 件目の追加の欄が短すぎます");
            return buf.AsSpan(start + _pos, n);
        }
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(Need(2)); _pos += 2; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(Need(4)); _pos += 4; return v; }
        public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(Need(8)); _pos += 8; return v; }
        public void Skip(int n) { Need(n); _pos += n; }
        public readonly Exception Bad(string expected) => new InvalidDataException($"ZIP の中央ディレクトリの {entry + 1} 件目の追加の欄が読めません (期待 {expected})");
    }

    // ===== 引き当て =====
    private static int LowerAscii(int b) => b >= 'A' && b <= 'Z' ? b + 32 : b;

    private ReadOnlySpan<byte> DirBytes(int i) => _dir[i] < 0 ? default : _dirNames.AsSpan(_dirOff[_dir[i]], _dirLen[_dir[i]]);
    private ReadOnlySpan<byte> FileBytes(int i) => _names.AsSpan(_nameOff[i], _nameLen[i]);
    private int NameLength(int i) => _dir[i] < 0 ? _nameLen[i] : _dirLen[_dir[i]] + 1 + _nameLen[i];

    private static uint HashStep(uint h, ReadOnlySpan<byte> s) { foreach (var b in s) h = (h ^ (uint)LowerAscii(b)) * 16777619; return h; }
    private uint HashOf(int i)
    {
        uint h = 2166136261;
        if (_dir[i] >= 0) { h = HashStep(h, DirBytes(i)); h = (h ^ (uint)'\\') * 16777619; }
        return HashStep(h, FileBytes(i));
    }
    private static uint HashOf(ReadOnlySpan<byte> s) => HashStep(2166136261, s);

    private static bool EqIc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (int k = 0; k < a.Length; k++) if (LowerAscii(a[k]) != LowerAscii(b[k])) return false;
        return true;
    }
    private bool EqualsIgnoreCase(int i, ReadOnlySpan<byte> s)
    {
        if (NameLength(i) != s.Length) return false;
        if (_dir[i] < 0) return EqIc(FileBytes(i), s);
        var d = DirBytes(i);
        return EqIc(d, s[..d.Length]) && s[d.Length] == (byte)'\\' && EqIc(FileBytes(i), s[(d.Length + 1)..]);
    }

    private void BuildIndex()
    {
        int cap = 16;
        while (cap < Count * 2) cap <<= 1;
        _buckets = new int[cap];
        int mask = cap - 1;
        var tmp = new byte[65536];
        for (int i = 0; i < Count; i++)
        {
            int b = (int)(HashOf(i) & (uint)mask);
            int len = CopyName(i, tmp);
            var name = tmp.AsSpan(0, len);
            while (true)
            {
                int v = _buckets[b];
                if (v == 0) { _buckets[b] = i + 1; break; }
                // 同じ名前 (大文字小文字を問わない) なら後のもので置き換える (lowercase_index.insert と同じ)
                if (EqualsIgnoreCase(v - 1, name)) { _buckets[b] = i + 1; break; }
                b = (b + 1) & mask;
            }
        }
    }

    // 項目の名前のバイトを dst に書き、長さを返す
    public int CopyName(int i, Span<byte> dst)
    {
        if (_dir[i] < 0) { FileBytes(i).CopyTo(dst); return _nameLen[i]; }
        var d = DirBytes(i);
        d.CopyTo(dst);
        dst[d.Length] = (byte)'\\';
        FileBytes(i).CopyTo(dst[(d.Length + 1)..]);
        return d.Length + 1 + _nameLen[i];
    }
    public byte[] NameBytes(int i) { var b = new byte[NameLength(i)]; CopyName(i, b); return b; }
    // 項目の名前 (「\」区切り)
    public string Name(int i) => Encoding.Latin1.GetString(NameBytes(i));
    // ファイル名 (最後の「\」の後)
    public string FileName(int i) => Encoding.Latin1.GetString(FileBytes(i));
    public long CompressedSize(int i) => _csize[i];
    public long UncompressedSize(int i) => _usize[i];
    public int Method(int i) => _method[i];
    public bool IsEncrypted(int i) => _encrypted[i];
    public long HeaderOffset(int i) => _offset[i];

    // 大文字小文字を区別しない引き当て (entry_case_insensitive)。区切りは「\」のまま比べる (呼ぶ側で直す)。無ければ -1
    public int Find(string path)
    {
        Span<byte> tmp = path.Length <= 512 ? stackalloc byte[path.Length] : new byte[path.Length];
        for (int k = 0; k < path.Length; k++)
        {
            char c = path[k];
            if (c > 0xFF) return -1;
            tmp[k] = (byte)c;
        }
        return Find(tmp);
    }

    public int Find(ReadOnlySpan<byte> path)
    {
        if (_buckets.Length == 0) return -1;
        int mask = _buckets.Length - 1;
        int b = (int)(HashOf(path) & (uint)mask);
        while (true)
        {
            int v = _buckets[b];
            if (v == 0) return -1;
            if (EqualsIgnoreCase(v - 1, path)) return v - 1;
            b = (b + 1) & mask;
        }
    }

    // 大文字小文字も同じ名前の引き当て (entry)。無ければ -1 (同じ名前が 2 つあれば後のもの)
    public int FindExact(string path)
    {
        int i = Find(path);
        if (i < 0) return -1;
        if (Name(i) == path) return i;
        // 大文字小文字だけ違う別の項目があるかもしれないので、全部から探す (まれ)
        for (int k = Count - 1; k >= 0; k--) if (NameLength(k) == path.Length && Name(k) == path) return k;
        return -1;
    }

    // ===== 取り出し (read_from_file・read_from_data) =====
    public byte[] Read(int i)
    {
        Enter();
        try
        {
            Span<byte> lh = stackalloc byte[30];
            ReadAt(_offset[i], lh);
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(lh);
            if (sig != 0x04034B50 && sig != 0x14034B50) throw new InvalidDataException($"{Name(i)} のローカルヘッダの印が違います ({sig:X8})");
            long start = _offset[i] + 30 + BinaryPrimitives.ReadUInt16LittleEndian(lh[26..]) + BinaryPrimitives.ReadUInt16LittleEndian(lh[28..]);
            long cs = _csize[i];
            if (cs > int.MaxValue) throw new InvalidDataException($"{Name(i)} が大きすぎます ({cs} バイト)");
            var raw = new byte[cs];
            ReadAt(start, raw);
            int method = _method[i];
            bool enc = _encrypted[i];
            if (enc && method == 100) return Zstd(Decrypt(raw), _usize[i]);
            if (enc) throw new InvalidDataException($"{Name(i)}: 暗号化されたものの方式 {method} には対応していません");
            return method switch
            {
                100 => Zstd(raw, _usize[i]),
                8 => Inflate(raw, _usize[i]),
                0 => raw,
                _ => throw new InvalidDataException($"{Name(i)}: 圧縮の方式 {method} には対応していません"),
            };
        }
        finally { Exit(); }
    }

    // 名前で取り出す (大文字小文字を区別しない)。無ければ null
    public byte[]? ReadFile(string path)
    {
        int i = Find(path);
        return i < 0 ? null : Read(i);
    }

    private void Enter()
    {
        if (!_sharedInstance) return;
        lock (SharedLock)
        {
            if (_released && _users == 0 && (_handle?.IsClosed ?? false)) throw new ObjectDisposedException(nameof(P4kArchive));
            _users++;
        }
    }

    private void Exit()
    {
        if (!_sharedInstance) return;
        lock (SharedLock)
        {
            _users--;
            if (_released && _users == 0) _handle?.Dispose();
        }
    }

    // crypto.rs: AES-128-CBC (鍵は固定、IV は 0)、パディングなしで戻し、末尾の 0 を削る
    private static readonly byte[] AesKey = [0x5E, 0x7A, 0x20, 0x02, 0x30, 0x2E, 0xEB, 0x1A, 0x3B, 0xB6, 0x17, 0xC3, 0x0F, 0xDE, 0x1E, 0x47];
    internal static byte[] Decrypt(byte[] data)
    {
        if (data.Length == 0) return data;
        if (data.Length % 16 != 0) throw new InvalidDataException("暗号化されたデータの長さが 16 の倍数ではありません");
        using var aes = Aes.Create();
        aes.Key = AesKey;
        var plain = aes.DecryptCbc(data, new byte[16], PaddingMode.None);
        int last = plain.Length;
        while (last > 0 && plain[last - 1] == 0) last--;
        return last == plain.Length ? plain : plain.AsSpan(0, last).ToArray();
    }

    internal static byte[] Zstd(byte[] data, long sizeHint)
    {
        using var d = new ZstdSharp.Decompressor();
        if (sizeHint > 0 && sizeHint <= int.MaxValue)
        {
            var dst = new byte[sizeHint];
            try
            {
                int n = d.Unwrap(data, dst, 0);
                if (n == dst.Length) return dst;
                return dst.AsSpan(0, n).ToArray();
            }
            catch (ZstdSharp.ZstdException) { /* 大きさの見込みが違う: 下で */ }
        }
        return d.Unwrap(data).ToArray();
    }

    internal static byte[] Inflate(byte[] data, long sizeHint)
    {
        using var ds = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        var ms = new MemoryStream((int)Math.Clamp(sizeHint, 0, int.MaxValue));
        ds.CopyTo(ms);
        return ms.Length == ms.Capacity ? ms.GetBuffer() : ms.ToArray();
    }

    // 目次の項目の番号を、目次の順に返す
    public IEnumerable<int> Indices() { for (int i = 0; i < Count; i++) yield return i; }

    // 名前の順 (バイト順、大文字小文字を区別する) の番号 (sorted_index)。要るときに 1 回だけ作る
    private int[]? _sorted;
    public int[] SortedIndices()
    {
        if (_sorted != null) return _sorted;
        var idx = new int[Count];
        for (int i = 0; i < Count; i++) idx[i] = i;
        // Rust の String の比べは UTF-8 のバイト順。名前は 1 バイト = 1 文字 (Latin-1) を UTF-8 にしたもので、0x80 以上の文字の並びも UTF-8 の順と同じ (コードポイント順)
        var full = new byte[Count][];
        for (int i = 0; i < Count; i++) full[i] = NameBytes(i);
        Array.Sort(idx, (a, b) => full[a].AsSpan().SequenceCompareTo(full[b]));
        return _sorted = idx;
    }
}
