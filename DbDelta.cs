using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace StarCitizenJapaneseTextCreater;

// 同梱の大きな DB (gamedata_cache.db・translations.db・equipment_cache.db) の、ファイルの中の差分。
// 作る (release.ps1 が exe の --make-db-delta で呼ぶ) と当てる (AppUpdater がダウンロードのときに呼ぶ) を、ここ 1 か所にまとめる。
// 差分の作り方は codec の名前で選び、manifest の deltas[].codec に書く。アプリは知らない codec の差分は使わず、DB を丸ごと取る。
// 作り方を入れ替えるときは、IDbDeltaCodec を足して Codecs に登録し、Default を替える (前の codec の差分も、登録が残っていれば当てられる)
internal interface IDbDeltaCodec
{
    string Name { get; }
    // oldPath から newPath への差分を patchPath に書く
    void Create(string oldPath, string newPath, string patchPath);
    // basePath に patchPath を当てたものを outPath に書く (basePath は変えない)
    void Apply(string basePath, string patchPath, string outPath);
}

internal static class DbDelta
{
    public static readonly IDbDeltaCodec PageV1 = new PageDeltaCodec();
    public static readonly IDbDeltaCodec ZstdPrefixV1 = new ZstdPrefixDeltaCodec();
    private static readonly Dictionary<string, IDbDeltaCodec> Codecs = new(StringComparer.OrdinalIgnoreCase)
    {
        [PageV1.Name] = PageV1,
        [ZstdPrefixV1.Name] = ZstdPrefixV1,
    };
    // 作るのは zstd (前の版の DB を前置きの辞書にする)。page-v1 は前の manifest の差分のために当てられるまま残す
    public static IDbDeltaCodec Default => ZstdPrefixV1;
    public static IDbDeltaCodec? Get(string? name) => name != null && Codecs.TryGetValue(name, out var c) ? c : null;

    // 差分で更新する同梱の DB (manifest の deltas を作る・使うファイル)
    public static readonly string[] DeltaFiles = ["gamedata_cache.db", "translations.db", "equipment_cache.db"];
    public static bool IsDeltaFile(string relPath) => DeltaFiles.Contains(Path.GetFileName(relPath), StringComparer.OrdinalIgnoreCase);
}

// ページ単位の差分 (page-v1)。SQLite の DB はページ (見出しの 16〜17 バイト目の大きさ) の並びなので、新しい DB のページのうち
// 古い DB の同じ番号のページと違うものだけを入れる。中身: gzip で包んだ
//   "SCPD" (4) / 版 1 (4) / ページの大きさ (4) / 新しい DB の大きさ (8) / [ページの番号 (4) / ページの中身 (ページの大きさ)]…
// VACUUM などでページの並びが大きく変わると、差分はほぼ丸ごとの大きさになる (そのときは AppUpdater が丸ごと取るほうを選ぶ)
internal sealed class PageDeltaCodec : IDbDeltaCodec
{
    public string Name => "page-v1";
    private static readonly byte[] Magic = "SCPD"u8.ToArray();

    private static int PageSize(string path)
    {
        using var fs = File.OpenRead(path);
        var hdr = new byte[100];
        if (fs.Read(hdr, 0, 100) < 100) throw new InvalidDataException($"SQLite の DB ではありません: {path}");
        int ps = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(16, 2));
        return ps == 1 ? 65536 : ps;
    }

    public void Create(string oldPath, string newPath, string patchPath)
    {
        var ps = PageSize(newPath);
        if (PageSize(oldPath) != ps) throw new InvalidDataException("ページの大きさが違う DB どうしの差分は作れません");
        using var oldFs = File.OpenRead(oldPath);
        using var newFs = File.OpenRead(newPath);
        using var outFs = File.Create(patchPath);
        using var gz = new GZipStream(outFs, CompressionLevel.SmallestSize);
        var head = new byte[20];
        Magic.CopyTo(head, 0);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(8), ps);
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(12), newFs.Length);
        gz.Write(head);
        var a = new byte[ps]; var b = new byte[ps]; var idx = new byte[4];
        long pages = (newFs.Length + ps - 1) / ps;
        for (long i = 0; i < pages; i++)
        {
            int nb = ReadFull(newFs, b, ps);
            int na = oldFs.Position < oldFs.Length ? ReadFull(oldFs, a, ps) : 0;
            if (na == nb && a.AsSpan(0, na).SequenceEqual(b.AsSpan(0, nb))) continue;
            if (nb < ps) Array.Clear(b, nb, ps - nb);
            BinaryPrimitives.WriteUInt32LittleEndian(idx, (uint)i);
            gz.Write(idx);
            gz.Write(b, 0, ps);
        }
    }

    public void Apply(string basePath, string patchPath, string outPath)
    {
        File.Copy(basePath, outPath, true);
        using var inFs = File.OpenRead(patchPath);
        using var gz = new GZipStream(inFs, CompressionMode.Decompress);
        var head = new byte[20];
        if (ReadFull(gz, head, 20) != 20 || !head.AsSpan(0, 4).SequenceEqual(Magic) || BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4)) != 1)
            throw new InvalidDataException("差分の形が正しくありません");
        int ps = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
        long newLen = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(12));
        if (ps < 512 || ps > 65536) throw new InvalidDataException("差分のページの大きさが正しくありません");
        using var outFs = new FileStream(outPath, FileMode.Open, FileAccess.ReadWrite);
        outFs.SetLength(newLen);
        var idx = new byte[4]; var page = new byte[ps];
        while (true)
        {
            int n = ReadFull(gz, idx, 4);
            if (n == 0) break;
            if (n != 4 || ReadFull(gz, page, ps) != ps) throw new InvalidDataException("差分が途中で切れています");
            long pos = (long)BinaryPrimitives.ReadUInt32LittleEndian(idx) * ps;
            if (pos >= newLen) throw new InvalidDataException("差分のページの番号が DB の外です");
            outFs.Position = pos;
            outFs.Write(page, 0, (int)Math.Min(ps, newLen - pos));
        }
    }

    private static int ReadFull(Stream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

// zstd の前置き (refPrefix。zstd の --patch-from と同じ形) の差分 (zstd-prefix-v1)。前の版の DB を前置きにして新しい DB を 1 つの zstd の
// フレームに圧縮したものが、そのまま差分のファイル (フレームに新しい DB の大きさが入る)。長距離一致 (LDM) を使い、windowLog は
// 前の版と新しい版の DB の大きさの和が入る大きさ (2^27〜2^31)。レベルは CompressionLevel。
// メモリ: 前の版の DB・新しい DB・出力は、どれもメモリマップで読み書きし、一度に (one-shot) 圧縮・展開する (zstd の中の窓の大きさの
// 受け皿を持たない)。展開の側で zstd が持つのは、ほぼ展開の表だけになる (前置きはマップしたファイルを指すだけで、写さない)
internal sealed unsafe class ZstdPrefixDeltaCodec : IDbDeltaCodec
{
    public string Name => "zstd-prefix-v1";
    public static int CompressionLevel { get; set; } = 9;

    private static int WindowLogFor(long oldSize, long newSize)
    {
        var need = Math.Max(1, oldSize + newSize);
        int wlog = 27;
        while (wlog < 31 && (1L << wlog) < need) wlog++;
        return wlog;
    }

    private static void Check(nuint code, string what)
    {
        if (ZstdSharp.Unsafe.Methods.ZSTD_isError(code)) throw new InvalidDataException($"zstd ({what}): {ZstdSharp.Unsafe.Methods.ZSTD_getErrorName(code)}");
    }

    // 読み取り専用のマップ (大きさ 0 のファイルはマップできないので null)
    private sealed class MappedRead : IDisposable
    {
        private readonly System.IO.MemoryMappedFiles.MemoryMappedFile? _mmf;
        private readonly System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? _view;
        public readonly byte* Ptr;
        public readonly long Length;
        public MappedRead(string path)
        {
            Length = new FileInfo(path).Length;
            if (Length == 0) return;
            _mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            _view = _mmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            Ptr = p + _view.PointerOffset;
        }
        public void Dispose()
        {
            if (_view != null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _view.Dispose(); }
            _mmf?.Dispose();
        }
    }

    // 書き込みのマップ (capacity の大きさで作り、終わったら written に縮める)
    private static long WriteMapped(string path, long capacity, Func<IntPtr, long, long> write)
    {
        long written;
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            fs.SetLength(Math.Max(1, capacity));
            using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(fs, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite,
                System.IO.HandleInheritability.None, leaveOpen: true);
            using var view = mmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            try { written = write((IntPtr)(p + view.PointerOffset), capacity); }
            finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write)) fs.SetLength(written);
        return written;
    }

    public void Create(string oldPath, string newPath, string patchPath)
    {
        using var oldMap = new MappedRead(oldPath);
        using var newMap = new MappedRead(newPath);
        var cctx = ZstdSharp.Unsafe.Methods.ZSTD_createCCtx();
        if (cctx == null) throw new OutOfMemoryException("zstd の圧縮の準備ができません");
        try
        {
            Check(ZstdSharp.Unsafe.Methods.ZSTD_CCtx_setParameter(cctx, ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_compressionLevel, CompressionLevel), "level");
            Check(ZstdSharp.Unsafe.Methods.ZSTD_CCtx_setParameter(cctx, ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_windowLog, WindowLogFor(oldMap.Length, newMap.Length)), "windowLog");
            Check(ZstdSharp.Unsafe.Methods.ZSTD_CCtx_setParameter(cctx, ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1), "LDM");
            Check(ZstdSharp.Unsafe.Methods.ZSTD_CCtx_setParameter(cctx, ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 1), "checksum");
            if (oldMap.Length > 0) Check(ZstdSharp.Unsafe.Methods.ZSTD_CCtx_refPrefix(cctx, oldMap.Ptr, (nuint)oldMap.Length), "refPrefix");
            var bound = (long)ZstdSharp.Unsafe.Methods.ZSTD_compressBound((nuint)newMap.Length);
            WriteMapped(patchPath, bound, (dst, cap) =>
            {
                var n = ZstdSharp.Unsafe.Methods.ZSTD_compress2(cctx, (void*)dst, (nuint)cap, newMap.Ptr, (nuint)newMap.Length);
                Check(n, "compress");
                return (long)n;
            });
        }
        finally { ZstdSharp.Unsafe.Methods.ZSTD_freeCCtx(cctx); }
    }

    public void Apply(string basePath, string patchPath, string outPath)
    {
        using var baseMap = new MappedRead(basePath);
        using var patchMap = new MappedRead(patchPath);
        if (patchMap.Length == 0) throw new InvalidDataException("差分が空です");
        var size = ZstdSharp.Unsafe.Methods.ZSTD_getFrameContentSize(patchMap.Ptr, (nuint)patchMap.Length);
        if (size == ulong.MaxValue || size == ulong.MaxValue - 1 || size > (ulong)long.MaxValue) throw new InvalidDataException("差分に新しい DB の大きさがありません");
        var dctx = ZstdSharp.Unsafe.Methods.ZSTD_createDCtx();
        if (dctx == null) throw new OutOfMemoryException("zstd の展開の準備ができません");
        try
        {
            Check(ZstdSharp.Unsafe.Methods.ZSTD_DCtx_setParameter(dctx, ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, 31), "windowLogMax");
            if (baseMap.Length > 0) Check(ZstdSharp.Unsafe.Methods.ZSTD_DCtx_refPrefix(dctx, baseMap.Ptr, (nuint)baseMap.Length), "refPrefix");
            var written = WriteMapped(outPath, (long)size, (dst, cap) =>
            {
                var n = ZstdSharp.Unsafe.Methods.ZSTD_decompressDCtx(dctx, (void*)dst, (nuint)cap, patchMap.Ptr, (nuint)patchMap.Length);
                Check(n, "decompress");
                return (long)n;
            });
            if (written != (long)size) throw new InvalidDataException("差分を当てた大きさが違います");
        }
        finally { ZstdSharp.Unsafe.Methods.ZSTD_freeDCtx(dctx); }
    }
}
