using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace StarCitizenJapaneseTextCreater;

// RGBA8 の PNG 書き出し (2026-11-04、利用者の決定「B」: StarBreaker とバイト単位まで同じにする)。
// StarBreaker v0.2.2 (crates/starbreaker-3d の pipeline.rs: RgbaImage::from_raw → write_to(ImageFormat::Png)) の移植。
// 使われる処理はソースで確かめた: image 0.25.10 (codecs/png.rs の PngEncoder::new、既定 CompressionType::Fast・FilterType::Adaptive)
// → png 0.18.1 (common.rs の Compression::Fast → DeflateCompression::FdeflateUltraFast、filter/mod.rs の Adaptive・filter/paeth.rs、
// encoder.rs の encode_header・write_image_data・write_zlib_encoded_idat・IEND) → fdeflate 0.3.7 (compress.rs の Compressor と
// StoredOnlyCompressor、tables.rs の固定 Huffman 表、lib.rs の compute_codes)。flate2・miniz_oxide はこの経路では使われない。
// 圧縮した結果が無圧縮 (stored) より大きいときは無圧縮に切り替える所も同じ。チャンクは IHDR・IDAT (0x7FFFFFFF バイトごと)・IEND だけ
internal static class RustPng
{
    // ---- fdeflate 0.3.7 tables.rs ----
    private static readonly byte[] HuffmanLengths =
    {
        2, 3, 4, 5, 5, 6, 6, 7, 7, 7, 8, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 10, 10, 10, 10, 10, 10, 10,
        10, 10, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 11, 11, 11, 11, 11, 11, 11,
        11, 11, 11, 10, 11, 10, 10, 10, 10, 10, 10, 10, 10, 10, 9, 9, 9, 9, 9, 8, 9, 8, 8, 8, 8, 8, 7,
        7, 7, 6, 6, 6, 5, 4, 3, 12, 12, 12, 9, 9, 11, 10, 11, 11, 10, 11, 11, 11, 11, 11, 11, 12, 11,
        12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 9,
    };

    private static readonly ushort[] LengthToSymbol =
    {
        257, 258, 259, 260, 261, 262, 263, 264, 265, 265, 266, 266, 267, 267, 268, 268, 269, 269, 269,
        269, 270, 270, 270, 270, 271, 271, 271, 271, 272, 272, 272, 272, 273, 273, 273, 273, 273, 273,
        273, 273, 274, 274, 274, 274, 274, 274, 274, 274, 275, 275, 275, 275, 275, 275, 275, 275, 276,
        276, 276, 276, 276, 276, 276, 276, 277, 277, 277, 277, 277, 277, 277, 277, 277, 277, 277, 277,
        277, 277, 277, 277, 278, 278, 278, 278, 278, 278, 278, 278, 278, 278, 278, 278, 278, 278, 278,
        278, 279, 279, 279, 279, 279, 279, 279, 279, 279, 279, 279, 279, 279, 279, 279, 279, 280, 280,
        280, 280, 280, 280, 280, 280, 280, 280, 280, 280, 280, 280, 280, 280, 281, 281, 281, 281, 281,
        281, 281, 281, 281, 281, 281, 281, 281, 281, 281, 281, 281, 281, 281, 281, 281, 281, 281, 281,
        281, 281, 281, 281, 281, 281, 281, 281, 282, 282, 282, 282, 282, 282, 282, 282, 282, 282, 282,
        282, 282, 282, 282, 282, 282, 282, 282, 282, 282, 282, 282, 282, 282, 282, 282, 282, 282, 282,
        282, 282, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283,
        283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 283, 284, 284, 284, 284,
        284, 284, 284, 284, 284, 284, 284, 284, 284, 284, 284, 284, 284, 284, 284, 284, 284, 284, 284,
        284, 284, 284, 284, 284, 284, 284, 284, 285,
    };

    private static readonly byte[] LengthToLenExtra =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 0,
    };

    private static readonly uint[] BitMasks =
    {
        0x0000, 0x0001, 0x0003, 0x0007, 0x000F, 0x001F, 0x003F, 0x007F, 0x00FF, 0x01FF, 0x03FF, 0x07FF,
        0x0FFF, 0x1FFF, 0x3FFF, 0x7FFF, 0xFFFF,
    };

    // fdeflate compress.rs の write_headers (zlib の頭と固定の動的 Huffman ブロックの頭。最後の 1 バイトは 5 ビットだけ)
    private static readonly byte[] FdeflateHeader =
    {
        120, 1, 237, 192, 3, 160, 36, 89, 150, 198, 241, 255, 119, 238, 141, 200, 204, 167,
        114, 75, 99, 174, 109, 219, 182, 109, 219, 182, 109, 219, 182, 109, 105, 140, 158, 150,
        74, 175, 158, 50, 51, 34, 238, 249, 118, 183, 106, 122, 166, 135, 59, 107, 213, 15,
    };

    private static readonly ushort[] HuffmanCodes = ComputeCodes(HuffmanLengths);
    // 文字 0〜255 の (符号 | 長さ << 16)。速さのためにまとめただけで中身は HuffmanCodes・HuffmanLengths と同じ
    private static readonly uint[] LiteralCodeLen = BuildLiteralCodeLen();

    private static uint[] BuildLiteralCodeLen()
    {
        var t = new uint[256];
        for (int b = 0; b < 256; b++) t[b] = HuffmanCodes[b] | ((uint)HuffmanLengths[b] << 16);
        return t;
    }
    private static readonly uint[] CrcTable = BuildCrcTable();

    // fdeflate lib.rs の compute_codes
    private static ushort[] ComputeCodes(byte[] lengths)
    {
        var codes = new ushort[lengths.Length];
        uint code = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < lengths.Length; i++)
            {
                if (lengths[i] == len)
                {
                    codes[i] = (ushort)(ReverseBits16((ushort)code) >> (16 - len));
                    code++;
                }
            }
            code <<= 1;
        }
        if (code != 2u << 16) throw new InvalidOperationException("HUFFMAN_LENGTHS is invalid");
        return codes;
    }

    private static ushort ReverseBits16(ushort v)
    {
        uint r = 0;
        for (int i = 0; i < 16; i++) r |= (uint)((v >> i) & 1) << (15 - i);
        return (ushort)r;
    }

    // 試験から反射で切り替えるため readonly にしない (SIMD の無い道を試す)
    private static bool s_simd = Avx2.IsSupported;

    // 1 スレッドごとの使い回しの配列 (並列から呼ばれても混ざらない)
    [ThreadStatic] private static Scratch? t_scratch;

    private sealed class Scratch
    {
        public byte[] Zero = Array.Empty<byte>();
        public byte[][] Rows = new byte[5][];   // [1]=Sub [2]=Up [3]=Avg [4]=Paeth。[0] は先頭にフィルタの番号
        public byte[] Out = Array.Empty<byte>();
        public int RowCap = -1;

        public void Ensure(int inLen, int outCap)
        {
            if (RowCap < inLen)
            {
                Zero = new byte[inLen];
                for (int f = 1; f <= 4; f++) Rows[f] = new byte[inLen + 1];
                RowCap = inLen;
            }
            if (Out.Length < outCap) Out = new byte[outCap];
        }
    }

    /// <summary>StarBreaker の image::RgbaImage::write_to(Png) と同じバイト列の PNG を返す。rgba は幅×高さ×4 バイト以上 (余りは使わない)</summary>
    public static byte[] EncodeRgba8(int width, int height, byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        // image の from_raw (足りなければ None) と png の write_header (幅・高さ 0 はエラー) に当たる所。C# では例外にする
        if (width <= 0 || height <= 0) throw new ArgumentException($"幅・高さが 0 以下 ({width}×{height})");
        long need = (long)width * height * 4;
        if (need > int.MaxValue - 64) throw new ArgumentException($"画像が大きすぎる ({width}×{height})");
        if (rgba.Length < need) throw new ArgumentException($"RGBA の長さが足りない ({rgba.Length} < {need})");

        int inLen = width * 4;
        long rawSize = (long)(inLen + 1) * height;
        if (rawSize > int.MaxValue - 1024) throw new ArgumentException($"画像が大きすぎる ({width}×{height})");
        long storedSizeL = StoredCompressedSize(rawSize);
        // 無圧縮で実際に書く大きさ (ちょうど 65535 の倍数のとき compressed_size の式より大きい)
        long storedActual = rawSize + 5 * ((rawSize + ushort.MaxValue - 1) / ushort.MaxValue) + 6;
        // 圧縮は無圧縮の大きさを超えた行で打ち切るので、どちらの道でも 1 行の書き込み分と端数の余裕を足せば足りる
        long outCapL = Math.Max(Math.Max(storedSizeL, storedActual), (long)FdeflateHeader.Length + 16) + (inLen + 1) * 2L + 64;
        if (outCapL > Array.MaxLength) throw new ArgumentException($"画像が大きすぎる ({width}×{height})");

        var s = t_scratch ??= new Scratch();
        s.Ensure(inLen, (int)outCapL);

        int zlen = CompressFdeflate(s, rgba, inLen, height, storedSizeL);
        if (zlen < 0) zlen = CompressStored(s, rgba, inLen, height);
        return BuildPng(width, height, s.Out, zlen);
    }

    // ---- png 0.18.1 encoder.rs write_image_data の FdeflateUltraFast の枝 ----
    // 戻り値: zlib の長さ。無圧縮より大きくなったら -1
    private static int CompressFdeflate(Scratch s, byte[] data, int inLen, int height, long storedSize)
    {
        var bw = new BitWriter(s.Out);
        // write_headers
        FdeflateHeader.AsSpan(0, 53).CopyTo(s.Out);
        bw.Pos = 53;
        bw.WriteBits(FdeflateHeader[53], 5);

        uint adlerA = 1, adlerB = 0;
        ReadOnlySpan<byte> prev = s.Zero.AsSpan(0, inLen);
        for (int y = 0; y < height; y++)
        {
            var line = new ReadOnlySpan<byte>(data, y * inLen, inLen);
            int f = AdaptiveFilter(s, prev, line, inLen);
            byte[] cur = s.Rows[f];
            cur[0] = (byte)f;
            var row = new ReadOnlySpan<byte>(cur, 0, inLen + 1);
            Adler32(row, ref adlerA, ref adlerB);
            WriteData(ref bw, row);
            prev = line;
            if (bw.Pos > storedSize) return -1;   // これから先も短くならない (書いたバイトは増えるだけ)
        }

        // finish: ブロックの終わり → 端数のビットを 0 で埋める → Adler-32 (ビッグエンディアン)
        bw.WriteBits(HuffmanCodes[256], HuffmanLengths[256]);
        bw.Flush();
        BinaryPrimitives.WriteUInt32BigEndian(s.Out.AsSpan(bw.Pos), (adlerB << 16) | adlerA);
        int len = bw.Pos + 4;
        // compressed.len() > StoredOnlyCompressor::compressed_size((in_len + 1) * height) なら無圧縮
        return len > storedSize ? -1 : len;
    }

    // fdeflate compress.rs の StoredOnlyCompressor::compressed_size (そのまま。割り切れるときの値も同じ式で出す)
    private static long StoredCompressedSize(long rawSize)
    {
        const long M = ushort.MaxValue;
        return (Math.Max(rawSize - 1, 0) / M) * (M + 5) + (rawSize % M + 5) + 6;
    }

    // ---- fdeflate compress.rs StoredOnlyCompressor (各行の前に 0 を置き、フィルタはかけない) ----
    private static int CompressStored(Scratch s, byte[] data, int inLen, int height)
    {
        byte[] o = s.Out;
        o[0] = 0x78; o[1] = 0x01;
        int pos = 2;
        int headerPos = pos;
        pos += 5;           // 仮のブロックの頭
        int blockBytes = 0;
        uint adlerA = 1, adlerB = 0;
        Span<byte> zeroByte = stackalloc byte[1];
        zeroByte[0] = 0;

        for (int y = 0; y < height; y++)
        {
            var line = new ReadOnlySpan<byte>(data, y * inLen, inLen);
            Adler32(zeroByte, ref adlerA, ref adlerB);
            Adler32(line, ref adlerA, ref adlerB);
            StoredWrite(o, ref pos, ref headerPos, ref blockBytes, zeroByte);
            StoredWrite(o, ref pos, ref headerPos, ref blockBytes, line);
        }

        SetStoredHeader(o, headerPos, blockBytes, true);
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(pos), (adlerB << 16) | adlerA);
        return pos + 4;
    }

    private static void StoredWrite(byte[] o, ref int pos, ref int headerPos, ref int blockBytes, ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (blockBytes == ushort.MaxValue)
            {
                SetStoredHeader(o, headerPos, ushort.MaxValue, false);
                headerPos = pos;
                pos += 5;
                blockBytes = 0;
            }
            int n = Math.Min(data.Length, ushort.MaxValue - blockBytes);
            data[..n].CopyTo(o.AsSpan(pos));
            pos += n;
            blockBytes += n;
            data = data[n..];
        }
    }

    private static void SetStoredHeader(byte[] o, int at, int size, bool last)
    {
        o[at] = last ? (byte)1 : (byte)0;
        o[at + 1] = (byte)(size & 0xFF);
        o[at + 2] = (byte)((size >> 8) & 0xFF);
        o[at + 3] = (byte)(~size & 0xFF);
        o[at + 4] = (byte)((~size >> 8) & 0xFF);
    }

    // ---- fdeflate compress.rs Compressor ----
    private ref struct BitWriter
    {
        public readonly byte[] Out;
        public int Pos;
        public ulong Buffer;
        public int NBits;

        public BitWriter(byte[] o) { Out = o; Pos = 0; Buffer = 0; NBits = 0; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBits(ulong bits, int nbits)
        {
            Buffer |= bits << NBits;     // NBits は常に 64 未満
            NBits += nbits;
            if (NBits >= 64)
            {
                // 容量は EncodeRgba8 で 1 行ぶん以上の余裕を取ってある (Pos は無圧縮の大きさを超えたら止まる)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Out), Pos),
                    BitConverter.IsLittleEndian ? Buffer : BinaryPrimitives.ReverseEndianness(Buffer));
                Pos += 8;
                NBits -= 64;
                int sh = nbits - NBits;  // Rust の checked_shr: 64 以上なら 0
                Buffer = sh >= 64 ? 0 : bits >> sh;
            }
        }

        public void Flush()
        {
            if (NBits % 8 != 0) WriteBits(0, 8 - NBits % 8);
            if (NBits > 0)
            {
                int n = NBits / 8;
                for (int i = 0; i < n; i++) Out[Pos + i] = (byte)(Buffer >> (8 * i));
                Pos += n;
                Buffer = 0;
                NBits = 0;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteLiteral(ref BitWriter bw, byte b) => bw.WriteBits(HuffmanCodes[b], HuffmanLengths[b]);

    private static void WriteRun(ref BitWriter bw, int run)
    {
        bw.WriteBits(HuffmanCodes[0], HuffmanLengths[0]);
        run -= 1;

        while (run >= 258)
        {
            bw.WriteBits(HuffmanCodes[285], HuffmanLengths[285] + 1);
            run -= 258;
        }

        if (run > 4)
        {
            int sym = LengthToSymbol[run - 3];
            bw.WriteBits(HuffmanCodes[sym], HuffmanLengths[sym]);
            int lenExtra = LengthToLenExtra[run - 3];
            ulong extra = (ulong)((uint)(run - 3) & BitMasks[lenExtra]);
            bw.WriteBits(extra, lenExtra + 1);
        }
        else
        {
            bw.WriteBits(0, run * HuffmanLengths[0]);
        }
    }

    private static void WriteData(ref BitWriter bw, ReadOnlySpan<byte> data)
    {
        uint[] codeLen = LiteralCodeLen;
        int run = 0;
        int n8 = data.Length & ~7;
        int i = 0;
        ref byte d0 = ref MemoryMarshal.GetReference(data);
        for (; i < n8; i += 8)
        {
            ReadOnlySpan<byte> chunk = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref d0, i), 8);
            ulong ichunk = BinaryPrimitives.ReadUInt64LittleEndian(chunk);

            if (ichunk == 0)
            {
                run += 8;
                continue;
            }
            else if (run > 0)
            {
                int runExtra = BitOperations.TrailingZeroCount(ichunk) / 8;
                WriteRun(ref bw, run + runExtra);
                run = 0;

                if (runExtra > 0)
                {
                    run = BitOperations.LeadingZeroCount(ichunk) / 8;
                    for (int j = runExtra; j < 8 - run; j++) WriteLiteral(ref bw, chunk[j]);
                    continue;
                }
            }

            int runStart = BitOperations.LeadingZeroCount(ichunk) / 8;
            if (runStart > 0)
            {
                for (int j = 0; j < 8 - runStart; j++) WriteLiteral(ref bw, chunk[j]);
                run = runStart;
                continue;
            }

            // 8 文字を 4 文字ずつ 2 回で書く (Rust と同じまとめ方。ビットの並びは 1 文字ずつ書いても同じ)
            ref uint cl = ref MemoryMarshal.GetArrayDataReference(codeLen);
            uint e0 = Unsafe.Add(ref cl, (int)(byte)ichunk), e1 = Unsafe.Add(ref cl, (int)(byte)(ichunk >> 8));
            uint e2 = Unsafe.Add(ref cl, (int)(byte)(ichunk >> 16)), e3 = Unsafe.Add(ref cl, (int)(byte)(ichunk >> 24));
            int n0 = (int)(e0 >> 16), n1 = (int)(e1 >> 16), n2 = (int)(e2 >> 16), n3 = (int)(e3 >> 16);
            ulong bits = (e0 & 0xFFFF)
                | ((ulong)(e1 & 0xFFFF) << n0)
                | ((ulong)(e2 & 0xFFFF) << (n0 + n1))
                | ((ulong)(e3 & 0xFFFF) << (n0 + n1 + n2));
            int nbits = n0 + n1 + n2 + n3;

            uint e4 = Unsafe.Add(ref cl, (int)(byte)(ichunk >> 32)), e5 = Unsafe.Add(ref cl, (int)(byte)(ichunk >> 40));
            uint e6 = Unsafe.Add(ref cl, (int)(byte)(ichunk >> 48)), e7 = Unsafe.Add(ref cl, (int)(byte)(ichunk >> 56));
            int n4 = (int)(e4 >> 16), n5 = (int)(e5 >> 16), n6 = (int)(e6 >> 16), n7 = (int)(e7 >> 16);
            ulong bits2 = (e4 & 0xFFFF)
                | ((ulong)(e5 & 0xFFFF) << n4)
                | ((ulong)(e6 & 0xFFFF) << (n4 + n5))
                | ((ulong)(e7 & 0xFFFF) << (n4 + n5 + n6));
            int nbits2 = n4 + n5 + n6 + n7;

            // bw.WriteBits を 2 回、状態をローカルに持って行う (中身は WriteBits と同じ)
            ulong buf = bw.Buffer;
            int nb = bw.NBits;
            int pos = bw.Pos;
            ref byte o = ref MemoryMarshal.GetArrayDataReference(bw.Out);

            buf |= bits << nb;
            nb += nbits;
            if (nb >= 64)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, pos), BitConverter.IsLittleEndian ? buf : BinaryPrimitives.ReverseEndianness(buf));
                pos += 8;
                nb -= 64;
                int sh = nbits - nb;
                buf = sh >= 64 ? 0 : bits >> sh;
            }
            buf |= bits2 << nb;
            nb += nbits2;
            if (nb >= 64)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, pos), BitConverter.IsLittleEndian ? buf : BinaryPrimitives.ReverseEndianness(buf));
                pos += 8;
                nb -= 64;
                int sh = nbits2 - nb;
                buf = sh >= 64 ? 0 : bits2 >> sh;
            }
            bw.Buffer = buf;
            bw.NBits = nb;
            bw.Pos = pos;
        }

        if (run > 0) WriteRun(ref bw, run);

        for (; i < data.Length; i++) WriteLiteral(ref bw, data[i]);
    }

    // ---- png 0.18.1 filter/mod.rs adaptive_filter (sum_buffer) ----
    // Up → Sub → Avg → Paeth の順に試し、費用が「以下」なら選び直す。費用 0 ならそこで決める。NoFilter は試さない
    private static int AdaptiveFilter(Scratch s, ReadOnlySpan<byte> prev, ReadOnlySpan<byte> cur, int len)
    {
        ulong min = ulong.MaxValue;
        int choice = 0;
        ReadOnlySpan<int> order = [2, 1, 3, 4];
        foreach (int f in order)
        {
            Span<byte> output = s.Rows[f].AsSpan(1, len);
            ulong cost = f switch
            {
                2 => FilterUp(prev, cur, output),
                1 => FilterSub(cur, output),
                3 => FilterAvg(prev, cur, output),
                _ => FilterPaeth(prev, cur, output),
            };
            if (cost <= min)
            {
                min = cost;
                choice = f;
                if (cost == 0) return choice;
            }
        }
        return choice;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AbsI8(byte b) => (uint)Math.Abs((int)(sbyte)b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> SumAbs(Vector256<byte> v, Vector256<ulong> acc)
    {
        // (b as i8).unsigned_abs() の合計。|-128| = 128 はバイトの 0x80 のままで合う
        var abs = Vector256.Abs(v.AsSByte()).AsByte();
        return acc + Avx2.SumAbsoluteDifferences(abs, Vector256<byte>.Zero).AsUInt64();
    }

    private static ulong FilterUp(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> cur, Span<byte> output)
    {
        int len = cur.Length;
        ulong sum = 0;
        int i = 0;
        if (s_simd)
        {
            var acc = Vector256<ulong>.Zero;
            ref byte c = ref MemoryMarshal.GetReference(cur);
            ref byte p = ref MemoryMarshal.GetReference(prev);
            ref byte o = ref MemoryMarshal.GetReference(output);
            for (; i + 32 <= len; i += 32)
            {
                var r = Vector256.LoadUnsafe(ref c, (nuint)i) - Vector256.LoadUnsafe(ref p, (nuint)i);
                r.StoreUnsafe(ref o, (nuint)i);
                acc = SumAbs(r, acc);
            }
            sum += Vector256.Sum(acc);
        }
        for (; i < len; i++)
        {
            byte r = (byte)(cur[i] - prev[i]);
            output[i] = r;
            sum += AbsI8(r);
        }
        return sum;
    }

    private static ulong FilterSub(ReadOnlySpan<byte> cur, Span<byte> output)
    {
        const int bpp = 4;
        int len = cur.Length;
        ulong sum = 0;
        for (int k = 0; k < bpp; k++)
        {
            output[k] = cur[k];
            sum += AbsI8(cur[k]);
        }
        int i = bpp;
        if (s_simd)
        {
            var acc = Vector256<ulong>.Zero;
            ref byte c = ref MemoryMarshal.GetReference(cur);
            ref byte o = ref MemoryMarshal.GetReference(output);
            for (; i + 32 <= len; i += 32)
            {
                var r = Vector256.LoadUnsafe(ref c, (nuint)i) - Vector256.LoadUnsafe(ref c, (nuint)(i - bpp));
                r.StoreUnsafe(ref o, (nuint)i);
                acc = SumAbs(r, acc);
            }
            sum += Vector256.Sum(acc);
        }
        for (; i < len; i++)
        {
            byte r = (byte)(cur[i] - cur[i - bpp]);
            output[i] = r;
            sum += AbsI8(r);
        }
        return sum;
    }

    private static ulong FilterAvg(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> cur, Span<byte> output)
    {
        const int bpp = 4;
        int len = cur.Length;
        ulong sum = 0;
        for (int k = 0; k < bpp; k++)
        {
            byte r = (byte)(cur[k] - (prev[k] / 2));
            output[k] = r;
            sum += AbsI8(r);
        }
        int i = bpp;
        if (s_simd)
        {
            var acc = Vector256<ulong>.Zero;
            ref byte c = ref MemoryMarshal.GetReference(cur);
            ref byte p = ref MemoryMarshal.GetReference(prev);
            ref byte o = ref MemoryMarshal.GetReference(output);
            var mask7F = Vector256.Create((byte)0x7F);
            for (; i + 32 <= len; i += 32)
            {
                var a = Vector256.LoadUnsafe(ref c, (nuint)(i - bpp));
                var b = Vector256.LoadUnsafe(ref p, (nuint)i);
                // (a & b) + ((a ^ b) >> 1)
                var half = Vector256.ShiftRightLogical((a ^ b).AsUInt16(), 1).AsByte() & mask7F;
                var r = Vector256.LoadUnsafe(ref c, (nuint)i) - ((a & b) + half);
                r.StoreUnsafe(ref o, (nuint)i);
                acc = SumAbs(r, acc);
            }
            sum += Vector256.Sum(acc);
        }
        for (; i < len; i++)
        {
            int a = cur[i - bpp], b = prev[i];
            byte r = (byte)(cur[i] - ((a & b) + ((a ^ b) >> 1)));
            output[i] = r;
            sum += AbsI8(r);
        }
        return sum;
    }

    // png 0.18.1 filter/paeth.rs filter_paeth_fpnge
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte PaethFpnge(byte a, byte b, byte c)
    {
        int pa = Math.Max(b, c) - Math.Min(c, b);
        int pb = Math.Max(a, c) - Math.Min(c, a);
        int pc = (a < c) == (c < b) ? Math.Max(pa, pb) - Math.Min(pa, pb) : 255;
        if (pa <= pb && pa <= pc) return a;
        else if (pb <= pc) return b;
        else return c;
    }

    private static ulong FilterPaeth(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> cur, Span<byte> output)
    {
        const int bpp = 4;
        int len = cur.Length;
        ulong sum = 0;
        for (int k = 0; k < bpp; k++)
        {
            byte r = (byte)(cur[k] - PaethFpnge(0, prev[k], 0));
            output[k] = r;
            sum += AbsI8(r);
        }
        int i = bpp;
        if (s_simd)
        {
            var acc = Vector256<ulong>.Zero;
            ref byte cu = ref MemoryMarshal.GetReference(cur);
            ref byte p = ref MemoryMarshal.GetReference(prev);
            ref byte o = ref MemoryMarshal.GetReference(output);
            for (; i + 32 <= len; i += 32)
            {
                var a = Vector256.LoadUnsafe(ref cu, (nuint)(i - bpp));
                var b = Vector256.LoadUnsafe(ref p, (nuint)i);
                var c = Vector256.LoadUnsafe(ref p, (nuint)(i - bpp));
                var pa = Vector256.Max(b, c) - Vector256.Min(c, b);
                var pb = Vector256.Max(a, c) - Vector256.Min(c, a);
                var same = ~(Vector256.LessThan(a, c) ^ Vector256.LessThan(c, b));
                var pc = Vector256.ConditionalSelect(same, Vector256.Max(pa, pb) - Vector256.Min(pa, pb), Vector256<byte>.AllBitsSet);
                var selA = Vector256.LessThanOrEqual(pa, pb) & Vector256.LessThanOrEqual(pa, pc);
                var selB = Vector256.LessThanOrEqual(pb, pc);
                var pred = Vector256.ConditionalSelect(selA, a, Vector256.ConditionalSelect(selB, b, c));
                var r = Vector256.LoadUnsafe(ref cu, (nuint)i) - pred;
                r.StoreUnsafe(ref o, (nuint)i);
                acc = SumAbs(r, acc);
            }
            sum += Vector256.Sum(acc);
        }
        for (; i < len; i++)
        {
            byte r = (byte)(cur[i] - PaethFpnge(cur[i - bpp], prev[i], prev[i - bpp]));
            output[i] = r;
            sum += AbsI8(r);
        }
        return sum;
    }

    // ---- Adler-32 (zlib の定義どおり。simd-adler32 と同じ値) ----
    private static void Adler32(ReadOnlySpan<byte> data, ref uint a, ref uint b)
    {
        const uint Mod = 65521;
        const int NMax = 5552;
        if (s_simd && data.Length >= 32)
        {
            // 32 バイトずつ: a += Σx、b += 32·a(前) + Σ(32−i)·x_i。5536 (32 の倍数で NMAX 以下) ごとに剰余を取る
            var weights = Vector256.Create((sbyte)32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17,
                16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);
            var ones = Vector256.Create((short)1);
            ref byte d0 = ref MemoryMarshal.GetReference(data);
            int off = 0;
            while (data.Length - off >= 32)
            {
                int n = Math.Min((data.Length - off) & ~31, NMax / 32 * 32);
                var vs1 = Vector256.CreateScalar(a);
                var vs2 = Vector256.CreateScalar(b);
                var vs3 = Vector256<uint>.Zero;
                int end = off + n;
                for (; off < end; off += 32)
                {
                    var v = Vector256.LoadUnsafe(ref d0, (nuint)off);
                    vs3 += vs1;
                    vs1 += Avx2.SumAbsoluteDifferences(v, Vector256<byte>.Zero).AsUInt32();
                    vs2 += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(v, weights), ones).AsUInt32();
                }
                vs2 += vs3 << 5;
                a = (uint)(HSum(vs1) % Mod);
                b = (uint)(HSum(vs2) % Mod);
            }
            data = data[off..];
        }
        while (!data.IsEmpty)
        {
            int n = Math.Min(data.Length, NMax);
            var blk = data[..n];
            int i = 0;
            for (; i + 8 <= n; i += 8)
            {
                a += blk[i]; b += a;
                a += blk[i + 1]; b += a;
                a += blk[i + 2]; b += a;
                a += blk[i + 3]; b += a;
                a += blk[i + 4]; b += a;
                a += blk[i + 5]; b += a;
                a += blk[i + 6]; b += a;
                a += blk[i + 7]; b += a;
            }
            for (; i < n; i++) { a += blk[i]; b += a; }
            a %= Mod;
            b %= Mod;
            data = data[n..];
        }
    }

    private static ulong HSum(Vector256<uint> v)
    {
        ulong s = 0;
        for (int i = 0; i < 8; i++) s += v.GetElement(i);
        return s;
    }

    // ---- PNG の組み立て (png 0.18.1 encoder.rs: 印 → IHDR → IDAT → IEND。ほかのチャンクは出ない) ----
    private const int MaxIdatChunkLen = int.MaxValue;   // u32::MAX >> 1

    private static byte[] BuildPng(int width, int height, byte[] z, int zlen)
    {
        int nChunks = zlen == 0 ? 0 : (int)(((long)zlen + MaxIdatChunkLen - 1) / MaxIdatChunkLen);
        long total = 8 + (12 + 13) + (long)nChunks * 12 + zlen + 12;
        var png = GC.AllocateUninitializedArray<byte>((int)total);   // 全部のバイトを下で書く
        int pos = 0;
        ReadOnlySpan<byte> sig = [137, 80, 78, 71, 13, 10, 26, 10];
        sig.CopyTo(png);
        pos += 8;

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8;    // BitDepth::Eight
        ihdr[9] = 6;    // ColorType::Rgba
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;   // interlaced: false
        WriteChunk(png, ref pos, "IHDR"u8, ihdr);

        for (int off = 0; off < zlen; off += MaxIdatChunkLen)
        {
            int n = Math.Min(MaxIdatChunkLen, zlen - off);
            WriteChunk(png, ref pos, "IDAT"u8, z.AsSpan(off, n));
        }
        WriteChunk(png, ref pos, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return png;
    }

    private static void WriteChunk(byte[] png, ref int pos, ReadOnlySpan<byte> name, ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(pos), (uint)data.Length);
        name.CopyTo(png.AsSpan(pos + 4));
        data.CopyTo(png.AsSpan(pos + 8));
        uint crc = Crc32(png.AsSpan(pos + 4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(pos + 8 + data.Length), crc);
        pos += 12 + data.Length;
    }

    // ---- CRC-32 (PNG・zlib の多項式 0xEDB88320。crc32fast と同じ値)。8 バイトずつの表引き ----
    private static uint[] BuildCrcTable()
    {
        var t = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        for (int n = 0; n < 256; n++)
            for (int k = 1; k < 8; k++)
                t[k * 256 + n] = (t[(k - 1) * 256 + n] >> 8) ^ t[t[(k - 1) * 256 + n] & 0xFF];
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint[] t = CrcTable;
        uint crc = 0xFFFFFFFFu;
        int i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            uint lo = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) ^ crc;
            uint hi = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            crc = t[7 * 256 + (lo & 0xFF)] ^ t[6 * 256 + ((lo >> 8) & 0xFF)]
                ^ t[5 * 256 + ((lo >> 16) & 0xFF)] ^ t[4 * 256 + (lo >> 24)]
                ^ t[3 * 256 + (hi & 0xFF)] ^ t[2 * 256 + ((hi >> 8) & 0xFF)]
                ^ t[1 * 256 + ((hi >> 16) & 0xFF)] ^ t[hi >> 24];
        }
        for (; i < data.Length; i++) crc = t[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
