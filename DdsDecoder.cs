using System.Buffers.Binary;
using System.IO.Compression;

namespace StarCitizenJapaneseTextCreater;

// DDS の読み取りと展開 (2026-11-04、利用者の決定「starbreaker の処理を自前で持つ」)。
// StarBreaker v0.2.2 (crates/starbreaker-dds の dds_file.rs・decode.rs・types.rs) と、それが使う bcdec_rs 0.2.0 (bc1・bc3・bc4・bc5・bc7) の移植。
// BC6H は starbreaker と同じく扱わない (失敗)。細かさで分けたファイル (.dds.1〜.8、.dds.Na・.dds.a) を合わせる読み方 (from_split) も同じ
internal sealed class DdsImage
{
    public byte[] Header = new byte[124];       // DDS_HEADER (印の後の 124 バイト)
    public byte[]? Dx10;                         // DDS_HEADER_DXT10 (20 バイト)
    public List<byte[]> Mips = new();
    public List<byte[]> AlphaMips = new();

    public uint Height => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(8));
    public uint Width => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(12));
    public uint MipmapCount => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(24));
    private ReadOnlySpan<byte> Pf => Header.AsSpan(72, 32);
    private uint PfFlags => BinaryPrimitives.ReadUInt32LittleEndian(Pf[4..]);
    private ReadOnlySpan<byte> FourCC => Pf.Slice(8, 4);
    private uint RgbBitCount => BinaryPrimitives.ReadUInt32LittleEndian(Pf[12..]);
    private uint RMask => BinaryPrimitives.ReadUInt32LittleEndian(Pf[16..]);
    private uint GMask => BinaryPrimitives.ReadUInt32LittleEndian(Pf[20..]);
    private uint BMask => BinaryPrimitives.ReadUInt32LittleEndian(Pf[24..]);
    private uint AMask => BinaryPrimitives.ReadUInt32LittleEndian(Pf[28..]);
    private uint CubemapFlags => BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(108));

    public enum Fmt { BC1, BC1Srgb, BC3, BC3Srgb, BC4U, BC4S, BC5U, BC5S, BC6H, BC7, BC7Srgb }

    private Fmt? ResolveFormat()
    {
        if (Dx10 != null)
            return BinaryPrimitives.ReadUInt32LittleEndian(Dx10) switch
            {
                71 => Fmt.BC1, 72 => Fmt.BC1Srgb, 77 => Fmt.BC3, 78 => Fmt.BC3Srgb, 80 => Fmt.BC4U, 81 => Fmt.BC4S,
                83 => Fmt.BC5U, 84 => Fmt.BC5S, 95 => Fmt.BC6H, 98 => Fmt.BC7, 99 => Fmt.BC7Srgb, _ => null,
            };
        var cc = System.Text.Encoding.Latin1.GetString(FourCC);
        return cc switch
        {
            "DXT1" => Fmt.BC1, "DXT3" => Fmt.BC3, "DXT5" => Fmt.BC3, "ATI1" => Fmt.BC4U, "ATI2" => Fmt.BC5U,
            "BC4U" => Fmt.BC4U, "BC4S" => Fmt.BC4S, "BC5U" => Fmt.BC5U, "BC5S" => Fmt.BC5S, _ => null,
        };
    }

    private static int BlockSize(Fmt f) => f is Fmt.BC1 or Fmt.BC1Srgb or Fmt.BC4U or Fmt.BC4S ? 8 : 16;

    private int MipSize(int level)
    {
        uint w = Math.Max(1, Width >> level), h = Math.Max(1, Height >> level);
        if (ResolveFormat() is Fmt f) return (int)((w + 3) / 4 * ((h + 3) / 4) * BlockSize(f));
        int bpp = (int)RgbBitCount;
        int bytepp = bpp > 0 ? bpp / 8 : 4;
        return (int)(w * h * bytepp);
    }

    private int FaceCount()
    {
        if (Dx10 != null && (BinaryPrimitives.ReadUInt32LittleEndian(Dx10.AsSpan(8)) & 0x4) != 0) return 6;
        if ((CubemapFlags & 0x200) != 0)
        {
            int faces = 0;
            foreach (var bit in new uint[] { 0x400, 0x800, 0x1000, 0x2000, 0x4000, 0x8000 }) if ((CubemapFlags & bit) != 0) faces++;
            return faces == 0 ? 6 : faces;
        }
        return 1;
    }

    private static int ReadHeaders(byte[] data, DdsImage img)
    {
        if (data.Length < 4 || data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ') throw new InvalidDataException("DDS の印がありません");
        if (data.Length < 128) throw new InvalidDataException("DDS のヘッダーが切れています");
        Buffer.BlockCopy(data, 4, img.Header, 0, 124);
        int p = 128;
        if (img.FourCC.SequenceEqual("DX10"u8))
        {
            if (data.Length < 148) throw new InvalidDataException("DDS の DX10 ヘッダーが切れています");
            img.Dx10 = data.AsSpan(128, 20).ToArray();
            p = 148;
        }
        return p;
    }

    // DdsFile::from_bytes
    public static DdsImage FromBytes(byte[] data)
    {
        var img = new DdsImage();
        int p = ReadHeaders(data, img);
        int mipCount = (int)Math.Max(1, img.MipmapCount);
        int faces = img.FaceCount();
        int off = p;
        for (int level = 0; level < mipCount; level++)
        {
            int total = img.MipSize(level) * (faces == 1 ? 1 : faces);
            if ((long)off + total > data.Length) break;
            img.Mips.Add(data.AsSpan(off, total).ToArray());
            off += total;
        }
        return img;
    }

    // DdsFile::from_split: readSibling(suffix) は「元のパス + suffix」のファイルの中身 (無ければ null)
    public static DdsImage FromSplit(byte[] baseData, Func<string, byte[]?> readSibling)
    {
        var img = new DdsImage();
        int p = ReadHeaders(baseData, img);
        int mipCount = (int)Math.Max(1, img.MipmapCount);
        var small = baseData.AsSpan(p);
        var sizes = Enumerable.Range(0, mipCount).Select(img.MipSize).ToList();
        var sib = new List<byte[]>();
        for (int n = 8; n >= 1; n--) if (readSibling($".{n}") is { } d) sib.Add(d);
        int faces;
        if (sib.Count > 0 && sizes.Count > 0)
        {
            int largest = sizes[0];
            faces = largest > 0 && sib[0].Length % largest == 0 ? sib[0].Length / largest : 1;
        }
        else faces = img.FaceCount();
        img.Mips.AddRange(sib);
        int off = 0;
        if (faces <= 1)
        {
            foreach (var size in sizes.Skip(sib.Count))
            {
                if (off + size > small.Length) break;
                img.Mips.Add(small.Slice(off, size).ToArray());
                off += size;
            }
        }
        else
        {
            foreach (var per in sizes.Skip(sib.Count))
            {
                int total = per * faces;
                if (off + total > small.Length)
                {
                    if (off + per <= small.Length) { img.Mips.Add(small.Slice(off, per).ToArray()); off += per; }
                    else break;
                }
                else { img.Mips.Add(small.Slice(off, total).ToArray()); off += total; }
            }
        }
        for (int n = 8; n >= 1; n--) if (readSibling($".{n}a") is { } d) img.AlphaMips.Add(d);
        if (readSibling(".a") is { } a) img.AlphaMips.Add(a);
        return img;
    }

    public (int W, int H) Dimensions(int mip) => ((int)Math.Max(1, Width >> mip), (int)Math.Max(1, Height >> mip));

    // decode_rgba: RGBA 8 ビット × 4。展開できない形は例外
    public byte[] DecodeRgba(int mip)
    {
        if (mip >= Mips.Count) throw new InvalidDataException($"mip {mip} がありません (最大 {Math.Max(0, Mips.Count - 1)})");
        var (w, h) = Dimensions(mip);
        var data = Mips[mip];
        if (FourCC.SequenceEqual(stackalloc byte[4]) && Dx10 == null && RgbBitCount > 0) return DecodeUncompressed(data, w, h);
        var f = ResolveFormat() ?? throw new InvalidDataException("対応していない DDS の形です");
        bool snorm = f is Fmt.BC4S or Fmt.BC5S;
        return f switch
        {
            Fmt.BC1 or Fmt.BC1Srgb => Decode4bpp(data, w, h, Bc1),
            Fmt.BC3 or Fmt.BC3Srgb => Decode4bpp(data, w, h, Bc3),
            Fmt.BC4U or Fmt.BC4S => DecodeBc4(data, w, h, snorm),
            Fmt.BC5U or Fmt.BC5S => DecodeBc5(data, w, h, snorm),
            Fmt.BC7 or Fmt.BC7Srgb => Decode4bpp(data, w, h, Bc7),
            _ => throw new InvalidDataException("BC6H decoding is not yet supported (HDR format)"),
        };
    }

    // decode_alpha_mip: 細かさ mip の alpha のファイル (.Na・.a。BC4) を 1 画素 1 バイトに (R の値)。無ければ例外
    // (2026-11-04、船の 3D の読み取り (ShipExport) のために足した。移植元 dds_file.rs decode_alpha_mip)
    public byte[] DecodeAlphaMip(int mip)
    {
        if (mip >= AlphaMips.Count) throw new InvalidDataException($"alpha の mip {mip} がありません (最大 {Math.Max(0, AlphaMips.Count - 1)})");
        var (w, h) = Dimensions(mip);
        var rgba = DecodeBc4(AlphaMips[mip], w, h, false);
        var o = new byte[rgba.Length / 4];
        for (int i = 0; i < o.Length; i++) o[i] = rgba[i * 4];
        return o;
    }

    private byte[] DecodeUncompressed(byte[] data, int w, int h)
    {
        int bpp = (int)RgbBitCount, bytepp = bpp / 8;
        if (bytepp == 0 || bpp % 8 != 0) throw new InvalidDataException($"uncompressed {bpp}-bit (non-byte-aligned)");
        long count = (long)w * h;
        if (data.Length < count * bytepp) throw new InvalidDataException("uncompressed data too short");
        int rs = System.Numerics.BitOperations.TrailingZeroCount(RMask), gs = System.Numerics.BitOperations.TrailingZeroCount(GMask), bs = System.Numerics.BitOperations.TrailingZeroCount(BMask);
        bool hasA = AMask != 0;
        int asft = System.Numerics.BitOperations.TrailingZeroCount(AMask);
        var o = new byte[count * 4];
        Array.Fill(o, (byte)255);
        for (long i = 0; i < count; i++)
        {
            uint raw = 0;
            for (int b = 0; b < bytepp; b++) raw |= (uint)data[i * bytepp + b] << (b * 8);
            o[i * 4] = (byte)(raw >> rs); o[i * 4 + 1] = (byte)(raw >> gs); o[i * 4 + 2] = (byte)(raw >> bs);
            if (hasA) o[i * 4 + 3] = (byte)(raw >> asft);
        }
        return o;
    }

    private delegate void BlockFn(ReadOnlySpan<byte> src, Span<byte> dst, int pitch);

    private static byte[] Decode4bpp(byte[] data, int w, int h, BlockFn fn)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        int blockSize = data.Length / (bw * bh) == 8 ? 8 : 16;
        var o = new byte[w * h * 4];
        Span<byte> blk = stackalloc byte[64];
        blk.Clear();
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int off = (by * bw + bx) * blockSize;
                if (off + blockSize > data.Length) break;
                fn(data.AsSpan(off), blk, 16);
                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        blk.Slice((py * 4 + px) * 4, 4).CopyTo(o.AsSpan((y * w + x) * 4));
                    }
            }
        return o;
    }

    private static byte[] DecodeBc4(byte[] data, int w, int h, bool snorm)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        var o = new byte[w * h * 4];
        Span<byte> blk = stackalloc byte[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int off = (by * bw + bx) * 8;
                if (off + 8 > data.Length) break;
                Bc4Block(data.AsSpan(off), blk, 4, 1, snorm);
                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        byte r = blk[py * 4 + px];
                        int d = (y * w + x) * 4;
                        o[d] = r; o[d + 1] = r; o[d + 2] = r; o[d + 3] = 255;
                    }
            }
        return o;
    }

    private static byte[] DecodeBc5(byte[] data, int w, int h, bool snorm)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        var o = new byte[w * h * 4];
        Span<byte> blk = stackalloc byte[32];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int off = (by * bw + bx) * 16;
                if (off + 16 > data.Length) break;
                Bc4Block(data.AsSpan(off), blk, 8, 2, snorm);
                Bc4Block(data.AsSpan(off + 8), blk[1..], 8, 2, snorm);
                for (int py = 0; py < 4; py++)
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px, y = by * 4 + py;
                        if (x >= w || y >= h) continue;
                        int bi = (py * 4 + px) * 2;
                        byte rr = blk[bi], rg = blk[bi + 1];
                        float nr, ng;
                        if (snorm) { nr = Math.Max((sbyte)rr, (sbyte)-127) / 127.0f; ng = Math.Max((sbyte)rg, (sbyte)-127) / 127.0f; }
                        else { nr = rr / 255.0f * 2.0f - 1.0f; ng = rg / 255.0f * 2.0f - 1.0f; }
                        float nzsq = 1.0f - nr * nr - ng * ng;
                        float nz = nzsq > 0.0f ? MathF.Sqrt(nzsq) : 0.0f;
                        int d = (y * w + x) * 4;
                        o[d] = SatU8((nr * 0.5f + 0.5f) * 255.0f);
                        o[d + 1] = SatU8((ng * 0.5f + 0.5f) * 255.0f);
                        o[d + 2] = SatU8((nz * 0.5f + 0.5f) * 255.0f);
                        o[d + 3] = 255;
                    }
            }
        return o;
    }

    // Rust の (v.clamp(0, 255)) as u8 (小数は切り捨て、NaN は 0)
    private static byte SatU8(float v) => float.IsNaN(v) ? (byte)0 : (byte)Math.Clamp(v, 0f, 255f);

    // ===== bcdec_rs 0.2.0 =====
    private static void Bc1(ReadOnlySpan<byte> s, Span<byte> d, int pitch) => ColorBlock(s, d, pitch, false);
    private static void Bc3(ReadOnlySpan<byte> s, Span<byte> d, int pitch)
    {
        ColorBlock(s[8..], d, pitch, true);
        SmoothAlpha(s, d[3..], pitch, 4);
    }

    private static void ColorBlock(ReadOnlySpan<byte> s, Span<byte> d, int pitch, bool onlyOpaque)
    {
        var refc = new byte[16];
        uint c0 = BinaryPrimitives.ReadUInt16LittleEndian(s), c1 = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
        uint r0 = (c0 >> 11) & 0x1F, g0 = (c0 >> 5) & 0x3F, b0 = c0 & 0x1F;
        uint r1 = (c1 >> 11) & 0x1F, g1 = (c1 >> 5) & 0x3F, b1 = c1 & 0x1F;
        void Set(int k, uint r, uint g, uint b, byte a) { refc[k * 4] = (byte)r; refc[k * 4 + 1] = (byte)g; refc[k * 4 + 2] = (byte)b; refc[k * 4 + 3] = a; }
        Set(0, (r0 * 527 + 23) >> 6, (g0 * 259 + 33) >> 6, (b0 * 527 + 23) >> 6, 255);
        Set(1, (r1 * 527 + 23) >> 6, (g1 * 259 + 33) >> 6, (b1 * 527 + 23) >> 6, 255);
        if (c0 > c1 || onlyOpaque)
        {
            Set(2, ((2 * r0 + r1) * 351 + 61) >> 7, ((2 * g0 + g1) * 2763 + 1039) >> 11, ((2 * b0 + b1) * 351 + 61) >> 7, 255);
            Set(3, ((r0 + r1 * 2) * 351 + 61) >> 7, ((g0 + g1 * 2) * 2763 + 1039) >> 11, ((b0 + b1 * 2) * 351 + 61) >> 7, 255);
        }
        else
        {
            Set(2, ((r0 + r1) * 1053 + 125) >> 8, ((g0 + g1) * 4145 + 1019) >> 11, ((b0 + b1) * 1053 + 125) >> 8, 255);
            Set(3, 0, 0, 0, 0);
        }
        uint idx = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                int k = (int)(idx & 3);
                refc.AsSpan(k * 4, 4).CopyTo(d[(i * pitch + j * 4)..]);
                idx >>= 2;
            }
    }

    private static void SmoothAlpha(ReadOnlySpan<byte> s, Span<byte> d, int pitch, int pixelSize)
    {
        Span<uint> a = stackalloc uint[8];
        a[0] = s[0]; a[1] = s[1];
        if (a[0] > a[1])
        {
            a[2] = (6 * a[0] + a[1] + 1) / 7; a[3] = (5 * a[0] + 2 * a[1] + 1) / 7; a[4] = (4 * a[0] + 3 * a[1] + 1) / 7;
            a[5] = (3 * a[0] + 4 * a[1] + 1) / 7; a[6] = (2 * a[0] + 5 * a[1] + 1) / 7; a[7] = (a[0] + 6 * a[1] + 1) / 7;
        }
        else
        {
            a[2] = (4 * a[0] + a[1] + 1) / 5; a[3] = (3 * a[0] + 2 * a[1] + 1) / 5; a[4] = (2 * a[0] + 3 * a[1] + 1) / 5; a[5] = (a[0] + 4 * a[1] + 1) / 5;
            a[6] = 0; a[7] = 0xFF;
        }
        ulong ind = BinaryPrimitives.ReadUInt64LittleEndian(s) >> 16;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) { d[i * pitch + j * pixelSize] = (byte)a[(int)(ind & 7)]; ind >>= 3; }
    }

    private static void Bc4Block(ReadOnlySpan<byte> s, Span<byte> d, int pitch, int pixelSize, bool signed)
    {
        Span<int> a = stackalloc int[8];
        ReadOnlySpan<int> w4 = [13107, 26215, 39321, 52429];
        ReadOnlySpan<int> w6 = [9363, 18724, 28086, 37450, 46812, 56173];
        if (signed)
        {
            a[0] = (sbyte)s[0]; a[1] = (sbyte)s[1];
            if (a[0] < -127) a[0] = -127;
            if (a[1] < -127) a[1] = -127;
        }
        else { a[0] = s[0]; a[1] = s[1]; }
        if (a[0] > a[1])
        {
            a[2] = (w6[5] * a[0] + w6[0] * a[1] + 32768) >> 16; a[3] = (w6[4] * a[0] + w6[1] * a[1] + 32768) >> 16;
            a[4] = (w6[3] * a[0] + w6[2] * a[1] + 32768) >> 16; a[5] = (w6[2] * a[0] + w6[3] * a[1] + 32768) >> 16;
            a[6] = (w6[1] * a[0] + w6[4] * a[1] + 32768) >> 16; a[7] = (w6[0] * a[0] + w6[5] * a[1] + 32768) >> 16;
        }
        else
        {
            a[2] = (w4[3] * a[0] + w4[0] * a[1] + 32768) >> 16; a[3] = (w4[2] * a[0] + w4[1] * a[1] + 32768) >> 16;
            a[4] = (w4[1] * a[0] + w4[2] * a[1] + 32768) >> 16; a[5] = (w4[0] * a[0] + w4[3] * a[1] + 32768) >> 16;
            a[6] = signed ? -127 : 0; a[7] = signed ? 127 : 255;
        }
        ulong ind = BinaryPrimitives.ReadUInt64LittleEndian(s) >> 16;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++) { d[i * pitch + j * pixelSize] = (byte)a[(int)(ind & 7)]; ind >>= 3; }
    }

    private struct Bits(ulong low, ulong high)
    {
        private ulong _low = low, _high = high;
        public uint Read(int n)
        {
            ulong mask = (1UL << n) - 1;
            ulong bits = _low & mask;
            _low >>= n;
            _low |= (_high & mask) << (64 - n);
            _high >>= n;
            return (uint)bits;
        }
    }

    private static readonly byte[] Bc7Bits0 = [4, 6, 5, 7, 5, 7, 7, 5], Bc7Bits1 = [0, 0, 0, 0, 6, 8, 7, 5];
    private static readonly uint[] W2 = [0, 21, 43, 64], W3 = [0, 9, 18, 27, 37, 46, 55, 64], W4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];
    private static uint Interp(uint a, uint b, uint w) => (a * (64 - w) + b * w + 32) >> 6;

    private static void Bc7(ReadOnlySpan<byte> s, Span<byte> d, int pitch)
    {
        var bs = new Bits(BinaryPrimitives.ReadUInt64LittleEndian(s), BinaryPrimitives.ReadUInt64LittleEndian(s[8..]));
        const int pbits = 0b11001011;
        var ep = new uint[6, 4];
        var idx = new byte[4, 4];
        int mode = 0;
        while (mode < 8 && bs.Read(1) == 0) mode++;
        if (mode >= 8) { for (int i = 0; i < 4; i++) d.Slice(i * pitch, 16).Clear(); return; }
        uint partition = 0; int nparts = 1; uint rotation = 0, isb = 0;
        if (mode is 0 or 1 or 2 or 3 or 7) { nparts = mode is 0 or 2 ? 3 : 2; partition = bs.Read(mode == 0 ? 4 : 6); }
        int nep = nparts * 2;
        if (mode is 4 or 5) { rotation = bs.Read(2); if (mode == 4) isb = bs.Read(1); }
        for (int i = 0; i < 3; i++) for (int e = 0; e < nep; e++) ep[e, i] = bs.Read(Bc7Bits0[mode]);
        if (Bc7Bits1[mode] > 0) for (int e = 0; e < nep; e++) ep[e, 3] = bs.Read(Bc7Bits1[mode]);
        if (mode is 0 or 1 or 3 or 6 or 7)
        {
            for (int e = 0; e < nep; e++) for (int k = 0; k < 4; k++) ep[e, k] <<= 1;
            if (mode == 1)
            {
                uint i1 = bs.Read(1), j1 = bs.Read(1);
                for (int k = 0; k < 3; k++) { ep[0, k] |= i1; ep[1, k] |= i1; ep[2, k] |= j1; ep[3, k] |= j1; }
            }
            else if ((pbits & (1 << mode)) != 0)
                for (int e = 0; e < nep; e++) { uint j1 = bs.Read(1); for (int k = 0; k < 4; k++) ep[e, k] |= j1; }
        }
        for (int e = 0; e < nep; e++)
        {
            int j = Bc7Bits0[mode] + ((pbits >> mode) & 1);
            for (int k = 0; k < 3; k++) { ep[e, k] <<= 8 - j; ep[e, k] |= ep[e, k] >> j; }
            j = Bc7Bits1[mode] + ((pbits >> mode) & 1);
            ep[e, 3] <<= 8 - j; ep[e, 3] |= ep[e, 3] >> j;
        }
        if (Bc7Bits1[mode] == 0) for (int e = 0; e < nep; e++) ep[e, 3] = 0xFF;
        int indexBits = mode is 0 or 1 ? 3 : mode == 6 ? 4 : 2;
        int indexBits2 = mode == 4 ? 3 : mode == 5 ? 2 : 0;
        var weights = indexBits == 2 ? W2 : indexBits == 3 ? W3 : W4;
        var weights2 = indexBits2 == 2 ? W2 : W3;
        int PSet(int i, int j) => nparts == 1 ? ((i | j) != 0 ? 0 : 128) : Bc7Partitions[nparts - 2][partition * 16 + i * 4 + j];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                int ib = mode is 0 or 1 ? 3 : mode == 6 ? 4 : 2;
                if ((PSet(i, j) & 0x80) != 0) ib--;
                idx[i, j] = (byte)bs.Read(ib);
            }
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                int ps = PSet(i, j) & 3;
                uint w = weights[idx[i, j]];
                uint r, g, b, a;
                if (indexBits2 == 0)
                {
                    r = Interp(ep[ps * 2, 0], ep[ps * 2 + 1, 0], w); g = Interp(ep[ps * 2, 1], ep[ps * 2 + 1, 1], w);
                    b = Interp(ep[ps * 2, 2], ep[ps * 2 + 1, 2], w); a = Interp(ep[ps * 2, 3], ep[ps * 2 + 1, 3], w);
                }
                else
                {
                    uint i2 = bs.Read((i | j) != 0 ? indexBits2 : indexBits2 - 1);
                    uint w2 = weights2[i2];
                    if (isb == 0)
                    {
                        r = Interp(ep[ps * 2, 0], ep[ps * 2 + 1, 0], w); g = Interp(ep[ps * 2, 1], ep[ps * 2 + 1, 1], w);
                        b = Interp(ep[ps * 2, 2], ep[ps * 2 + 1, 2], w); a = Interp(ep[ps * 2, 3], ep[ps * 2 + 1, 3], w2);
                    }
                    else
                    {
                        r = Interp(ep[ps * 2, 0], ep[ps * 2 + 1, 0], w2); g = Interp(ep[ps * 2, 1], ep[ps * 2 + 1, 1], w2);
                        b = Interp(ep[ps * 2, 2], ep[ps * 2 + 1, 2], w2); a = Interp(ep[ps * 2, 3], ep[ps * 2 + 1, 3], w);
                    }
                }
                switch (rotation) { case 1: (a, r) = (r, a); break; case 2: (a, g) = (g, a); break; case 3: (a, b) = (b, a); break; }
                int o = i * pitch + j * 4;
                d[o] = (byte)r; d[o + 1] = (byte)g; d[o + 2] = (byte)b; d[o + 3] = (byte)a;
            }
    }

    private static readonly byte[][] Bc7Partitions = [Bc7Tables.Partition2, Bc7Tables.Partition3];

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n => { uint c = (uint)n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; return c; }).ToArray();
    private static uint Crc32(byte[] data) { uint c = 0xFFFFFFFFu; foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFFu; }

    // ===== PNG (RGBA 8 ビット・フィルタ無し) の書き出しと読み取り (画素だけを比べる・置く用) =====
    public static byte[] EncodePng(byte[] rgba, int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] body)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
            ms.Write(len);
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            ms.Write(t); ms.Write(body);
            uint crc = Crc32([.. t, .. body]);
            BinaryPrimitives.WriteUInt32BigEndian(len, crc);
            ms.Write(len);
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk("IHDR", ihdr);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
                for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
            Chunk("IDAT", raw.ToArray());
        }
        Chunk("IEND", []);
        return ms.ToArray();
    }
}

// starbreaker の dds to-png-all・dds decode (cli/src/dds.rs) の置き換え
internal static class DdsTools
{
    // dds to-png-all -i <in> -o <out> (--filter "*.dds"): in の下の元の .dds (名前が .dds で終わり「.dds.」を含まない) を、同じ相対パスの .png に (細かさ 0)。
    // 分けたファイル (.dds.N 等) は同じフォルダから読む。1 枚ずつの失敗は数えるだけ。書けた数を返す
    public static int ToPngAll(string inDir, string outDir, Action<string>? onError = null)
    {
        var files = Directory.EnumerateFiles(inDir, "*", SearchOption.AllDirectories)
            .Where(f => { var n = Path.GetFileName(f); return n.EndsWith(".dds", StringComparison.Ordinal) && !n.Contains(".dds.", StringComparison.Ordinal) && GlobMatch.IsMatch("*.dds", n); }).ToList();
        int ok = 0;
        Parallel.ForEach(files, f =>
        {
            var outPath = Path.ChangeExtension(Path.Combine(outDir, Path.GetRelativePath(inDir, f)), ".png");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                var img = DdsImage.FromSplit(File.ReadAllBytes(f), suf => File.Exists(f + suf) ? File.ReadAllBytes(f + suf) : null);
                var (w, h) = img.Dimensions(0);
                File.WriteAllBytes(outPath, DdsImage.EncodePng(img.DecodeRgba(0), w, h));
                Interlocked.Increment(ref ok);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { onError?.Invoke($"Error converting {f}: {e.Message}"); }
        });
        return ok;
    }

    // dds decode <input> <output.png> --mip <mip> --p4k: p4k の中の DDS (input は「Data/」の無いパスなら付ける) を、細かさ min(mip, 最後) で PNG に。できなければ false
    public static bool DecodeFromP4k(string p4kPath, string input, string output, int mip)
    {
        P4kArchive a;
        try { a = P4kArchive.Shared(p4kPath); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
        return DecodeFromP4k(a, input, output, mip);
    }

    public static bool DecodeFromP4k(P4kArchive p4k, string input, string output, int mip)
    {
        try
        {
            var withPrefix = input.StartsWith("Data/", StringComparison.Ordinal) || input.StartsWith("Data\\", StringComparison.Ordinal) ? input : "Data/" + input;
            var norm = withPrefix.Replace('/', '\\');
            int e = p4k.Find(norm);
            if (e < 0) return false;
            var img = DdsImage.FromSplit(p4k.Read(e), suf => { int j = p4k.Find(norm + suf); if (j < 0) return null; try { return p4k.Read(j); } catch (InvalidDataException) { return null; } });
            int m = Math.Min(mip, Math.Max(0, img.Mips.Count - 1));
            var (w, h) = img.Dimensions(m);
            File.WriteAllBytes(output, DdsImage.EncodePng(img.DecodeRgba(m), w, h));
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ZstdSharp.ZstdException) { return false; }
    }
}
