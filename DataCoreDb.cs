using System.Buffers.Binary;
using System.Text;

namespace StarCitizenJapaneseTextCreater;

// DataCore (Game2.dcb) の読み取り。StarBreaker v0.2.2 (crates/starbreaker-datacore の database.rs・types.rs・enums.rs・query/*) の移植。
// 表 (構造・性質・列挙・対応・レコード) は配列にし、値の配列・文字列・インスタンスは元のバイト列の位置で持つ。
internal sealed partial class DataCoreDb
{
    public enum DT : ushort
    {
        Boolean = 0x0001, SByte = 0x0002, Int16 = 0x0003, Int32 = 0x0004, Int64 = 0x0005, Byte = 0x0006, UInt16 = 0x0007, UInt32 = 0x0008, UInt64 = 0x0009,
        String = 0x000A, Single = 0x000B, Double = 0x000C, Locale = 0x000D, Guid = 0x000E, EnumChoice = 0x000F, Class = 0x0010,
        StrongPointer = 0x0110, WeakPointer = 0x0210, Reference = 0x0310,
    }
    public enum CT : ushort { Attribute = 0, ComplexArray = 1, SimpleArray = 2, ClassArray = 3 }

    public static bool IsKnownDT(ushort v) => v is >= 1 and <= 0x10 || v is 0x0110 or 0x0210 or 0x0310;
    public static bool IsKnownCT(ushort v) => v <= 3;

    public static int InlineSize(DT t) => t switch
    {
        DT.Boolean or DT.SByte or DT.Byte => 1,
        DT.Int16 or DT.UInt16 => 2,
        DT.Int32 or DT.UInt32 or DT.EnumChoice or DT.Single or DT.String or DT.Locale => 4,
        DT.Int64 or DT.UInt64 or DT.Double => 8,
        DT.Guid => 16,
        DT.StrongPointer or DT.WeakPointer => 8,
        DT.Reference => 20,
        _ => 0,
    };

    public readonly record struct StructDef(int NameOffset, int ParentIndex, ushort AttributeCount, ushort FirstAttribute, uint StructSize);
    public readonly record struct PropDef(int NameOffset, ushort StructIndex, ushort DataType, ushort ConversionType);
    public readonly record struct EnumDef(int NameOffset, ushort ValueCount, ushort FirstValue);
    public readonly record struct Record(int NameOffset, int FileNameOffset, int TagOffset, int StructIndex, Guid128 Id, ushort InstanceIndex, ushort StructSize);

    // CigGuid (16 バイトそのまま)。文字にするときは b[7]b[6]b[5]b[4]-b[3]b[2]-b[1]b[0]-b[15]b[14]-b[13]…b[8]
    public readonly record struct Guid128(ulong Lo, ulong Hi)
    {
        public static Guid128 Read(ReadOnlySpan<byte> b) => new(BinaryPrimitives.ReadUInt64LittleEndian(b), BinaryPrimitives.ReadUInt64LittleEndian(b[8..]));
        public bool IsEmpty => Lo == 0 && Hi == 0;
        public byte this[int i] => (byte)((i < 8 ? Lo >> (i * 8) : Hi >> ((i - 8) * 8)) & 0xFF);
        public override string ToString()
        {
            Span<int> order = [7, 6, 5, 4, -1, 3, 2, -1, 1, 0, -1, 15, 14, -1, 13, 12, 11, 10, 9, 8];
            var sb = new StringBuilder(36);
            foreach (var i in order) { if (i < 0) sb.Append('-'); else sb.Append(this[i].ToString("x2")); }
            return sb.ToString();
        }
        // FromStr: 「-」を除いて 32 文字の 16 進。読めなければ null
        public static Guid128? Parse(string s)
        {
            var hex = s.Replace("-", "");
            if (hex.Length != 32) return null;
            Span<int> pos = [7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8];
            Span<byte> b = stackalloc byte[16];
            for (int k = 0; k < 16; k++)
            {
                var part = hex.Substring(k * 2, 2);
                if (part[0] == '+' || !byte.TryParse(part, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var v))
                {
                    // Rust の u8::from_str_radix は先頭の「+」を許す (「+f」)
                    if (part[0] == '+' && byte.TryParse(part[1..], System.Globalization.NumberStyles.AllowHexSpecifier, null, out var v2)) v = v2;
                    else return null;
                }
                b[pos[k]] = v;
            }
            return Read(b);
        }
    }

    public byte[] Data { get; }
    public int Version { get; }
    public StructDef[] Structs { get; }
    public PropDef[] Props { get; }
    public EnumDef[] Enums { get; }
    public (uint Count, int StructIndex)[] Mappings { get; }
    public Record[] Records { get; }

    // 値の配列・文字列・インスタンスの位置
    private readonly int _int8, _int16, _int32, _int64, _uint8, _uint16, _uint32, _uint64, _bool, _single, _double, _guid, _stringId, _locale, _enumVal, _strong, _weak, _ref, _enumOpt, _st1, _st2, _inst;
    public readonly int Int8Count, Int16Count, Int32Count, Int64Count, UInt8Count, UInt16Count, UInt32Count, UInt64Count, BoolCount, SingleCount, DoubleCount, GuidCount, StringIdCount, LocaleCount, EnumValueCount, StrongCount, WeakCount, ReferenceCount, EnumOptionCount;
    private readonly int _st1Len, _st2Len;

    private readonly long[] _instanceOffsets;
    private readonly ushort[][] _cachedProps;
    private readonly int[][] _cachedOffsets;
    private readonly Dictionary<Guid128, int> _recordMap;
    private readonly Dictionary<string, int> _structNames;

    private DataCoreDb(byte[] data)
    {
        Data = data;
        if (data.Length < 120) throw new InvalidDataException("Game2.dcb のヘッダが切れています");
        var h = data.AsSpan();
        int I(int k) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(k * 4));
        Version = I(1);
        if (Version != 6 && Version != 8) throw new InvalidDataException($"DataCore の版 {Version} には対応していません");
        int structCount = I(4), propCount = I(5), enumCount = I(6), mapCount = I(7), recCount = I(8);
        BoolCount = I(9); Int8Count = I(10); Int16Count = I(11); Int32Count = I(12); Int64Count = I(13);
        UInt8Count = I(14); UInt16Count = I(15); UInt32Count = I(16); UInt64Count = I(17); SingleCount = I(18); DoubleCount = I(19);
        GuidCount = I(20); StringIdCount = I(21); LocaleCount = I(22); EnumValueCount = I(23); StrongCount = I(24); WeakCount = I(25); ReferenceCount = I(26); EnumOptionCount = I(27);
        uint textLen = (uint)I(28), textLen2 = (uint)I(29);
        long off = 120;
        long Take(long count, int size)
        {
            if (count < 0) throw new InvalidDataException("Game2.dcb の数が負です");
            long start = off, end = off + count * size;
            if (end > data.Length) throw new InvalidDataException("Game2.dcb が切れています");
            off = end;
            return start;
        }
        long sOff = Take(structCount, 16);
        Structs = new StructDef[structCount];
        for (int i = 0; i < structCount; i++)
        {
            var s = h[(int)(sOff + i * 16)..];
            Structs[i] = new StructDef(BinaryPrimitives.ReadInt32LittleEndian(s), BinaryPrimitives.ReadInt32LittleEndian(s[4..]), BinaryPrimitives.ReadUInt16LittleEndian(s[8..]),
                BinaryPrimitives.ReadUInt16LittleEndian(s[10..]), BinaryPrimitives.ReadUInt32LittleEndian(s[12..]));
        }
        long pOff = Take(propCount, 12);
        Props = new PropDef[propCount];
        for (int i = 0; i < propCount; i++)
        {
            var s = h[(int)(pOff + i * 12)..];
            Props[i] = new PropDef(BinaryPrimitives.ReadInt32LittleEndian(s), BinaryPrimitives.ReadUInt16LittleEndian(s[4..]), BinaryPrimitives.ReadUInt16LittleEndian(s[6..]), BinaryPrimitives.ReadUInt16LittleEndian(s[8..]));
        }
        long eOff = Take(enumCount, 8);
        Enums = new EnumDef[enumCount];
        for (int i = 0; i < enumCount; i++)
        {
            var s = h[(int)(eOff + i * 8)..];
            Enums[i] = new EnumDef(BinaryPrimitives.ReadInt32LittleEndian(s), BinaryPrimitives.ReadUInt16LittleEndian(s[4..]), BinaryPrimitives.ReadUInt16LittleEndian(s[6..]));
        }
        long mOff = Take(mapCount, 8);
        Mappings = new (uint, int)[mapCount];
        for (int i = 0; i < mapCount; i++) { var s = h[(int)(mOff + i * 8)..]; Mappings[i] = (BinaryPrimitives.ReadUInt32LittleEndian(s), BinaryPrimitives.ReadInt32LittleEndian(s[4..])); }
        int recSize = Version >= 8 ? 36 : 32;
        long rOff = Take(recCount, recSize);
        Records = new Record[recCount];
        for (int i = 0; i < recCount; i++)
        {
            var s = h[(int)(rOff + (long)i * recSize)..];
            if (Version >= 8)
                Records[i] = new Record(BinaryPrimitives.ReadInt32LittleEndian(s), BinaryPrimitives.ReadInt32LittleEndian(s[4..]), BinaryPrimitives.ReadInt32LittleEndian(s[8..]),
                    BinaryPrimitives.ReadInt32LittleEndian(s[12..]), Guid128.Read(s[16..]), BinaryPrimitives.ReadUInt16LittleEndian(s[32..]), BinaryPrimitives.ReadUInt16LittleEndian(s[34..]));
            else
                Records[i] = new Record(BinaryPrimitives.ReadInt32LittleEndian(s), BinaryPrimitives.ReadInt32LittleEndian(s[4..]), -1,
                    BinaryPrimitives.ReadInt32LittleEndian(s[8..]), Guid128.Read(s[12..]), BinaryPrimitives.ReadUInt16LittleEndian(s[28..]), BinaryPrimitives.ReadUInt16LittleEndian(s[30..]));
        }
        _int8 = (int)Take(Int8Count, 1); _int16 = (int)Take(Int16Count, 2); _int32 = (int)Take(Int32Count, 4); _int64 = (int)Take(Int64Count, 8);
        _uint8 = (int)Take(UInt8Count, 1); _uint16 = (int)Take(UInt16Count, 2); _uint32 = (int)Take(UInt32Count, 4); _uint64 = (int)Take(UInt64Count, 8);
        _bool = (int)Take(BoolCount, 1); _single = (int)Take(SingleCount, 4); _double = (int)Take(DoubleCount, 8);
        _guid = (int)Take(GuidCount, 16); _stringId = (int)Take(StringIdCount, 4); _locale = (int)Take(LocaleCount, 4); _enumVal = (int)Take(EnumValueCount, 4);
        _strong = (int)Take(StrongCount, 8); _weak = (int)Take(WeakCount, 8); _ref = (int)Take(ReferenceCount, 20); _enumOpt = (int)Take(EnumOptionCount, 4);
        _st1 = (int)Take(textLen, 1); _st1Len = (int)textLen;
        _st2 = (int)Take(textLen2, 1); _st2Len = (int)textLen2;
        _inst = (int)off;

        _instanceOffsets = new long[structCount];
        long running = 0;
        foreach (var (cnt, si) in Mappings) { _instanceOffsets[si] = running; running += (long)cnt * Structs[si].StructSize; }

        _recordMap = new Dictionary<Guid128, int>(recCount);
        for (int i = 0; i < recCount; i++) _recordMap[Records[i].Id] = i;

        _structNames = new Dictionary<string, int>(structCount, StringComparer.Ordinal);
        for (int i = 0; i < structCount; i++) _structNames[String2(Structs[i].NameOffset)] = i;

        _cachedProps = new ushort[structCount][];
        _cachedOffsets = new int[structCount][];
        for (int si = 0; si < structCount; si++)
        {
            var t = Structs[si];
            if (t.AttributeCount == 0 && t.ParentIndex == -1) { _cachedProps[si] = []; _cachedOffsets[si] = []; continue; }
            int total = t.AttributeCount;
            var walk = t;
            while (walk.ParentIndex != -1) { walk = Structs[walk.ParentIndex]; total += walk.AttributeCount; }
            var idx = new ushort[total];
            int pos = total;
            var cur = t;
            while (true)
            {
                pos -= cur.AttributeCount;
                for (int i = 0; i < cur.AttributeCount; i++) idx[pos + i] = (ushort)(cur.FirstAttribute + i);
                if (cur.ParentIndex == -1) break;
                cur = Structs[cur.ParentIndex];
            }
            _cachedProps[si] = idx;
            var offs = new int[total];
            int run = 0;
            for (int k = 0; k < total; k++)
            {
                offs[k] = run;
                var p = Props[idx[k]];
                if (IsKnownDT(p.DataType) && IsKnownCT(p.ConversionType))
                {
                    if ((CT)p.ConversionType != CT.Attribute) run += 8;
                    else if ((DT)p.DataType == DT.Class) run += (int)Structs[p.StructIndex].StructSize;
                    else run += InlineSize((DT)p.DataType);
                }
            }
            _cachedOffsets[si] = offs;
        }
    }

    public static DataCoreDb Load(byte[] data) => new(data);

    // Data.p4k から Game2.dcb (無ければ Game.dcb) を読む (load_dcb_bytes)
    public static DataCoreDb FromP4k(P4kArchive p4k)
    {
        var b = p4k.ReadFile(@"Data\Game2.dcb") ?? p4k.ReadFile(@"Data\Game.dcb") ?? throw new FileNotFoundException("Data.p4k に Game2.dcb がありません");
        return new DataCoreDb(b);
    }

    // ===== 文字列 =====
    private string ResolveFrom(int table, int len, int offset)
    {
        if (offset < 0 || offset > len) throw new InvalidDataException("DataCore の文字列の位置が範囲の外です");
        var s = Data.AsSpan(table + offset, len - offset);
        int z = s.IndexOf((byte)0);
        if (z < 0) z = s.Length;
        try { return Utf8Strict.GetString(s[..z]); }
        catch (DecoderFallbackException) { return "<invalid utf8>"; }
    }
    private static readonly UTF8Encoding Utf8Strict = new(false, true);
    public string String1(int offset) => ResolveFrom(_st1, _st1Len, offset);
    public string String2(int offset) => ResolveFrom(_st2, _st2Len, offset);
    public string StructName(int si) => String2(Structs[si].NameOffset);
    public string PropName(int pi) => String2(Props[pi].NameOffset);
    public int? StructIndexByName(string name) => _structNames.TryGetValue(name, out var i) ? i : null;
    public ushort[] AllPropertyIndices(int si) => _cachedProps[si];
    public int PropertyByteOffset(int si, int position) => _cachedOffsets[si][position];

    public Record? RecordById(Guid128 id) => _recordMap.TryGetValue(id, out var i) ? Records[i] : null;

    // インスタンスの位置 (Data の中) と大きさ
    public (int Offset, int Size) Instance(int structIndex, int instanceIndex)
    {
        var size = (int)Structs[structIndex].StructSize;
        long start = _inst + _instanceOffsets[structIndex] + (long)instanceIndex * size;
        if (instanceIndex < 0 || start + size > Data.Length) throw new InvalidDataException("DataCore のインスタンスが範囲の外です");
        return ((int)start, size);
    }

    // 値の配列
    public (int StructIndex, int InstanceIndex) Strong(int i) { var s = Data.AsSpan(_strong + i * 8); return (BinaryPrimitives.ReadInt32LittleEndian(s), BinaryPrimitives.ReadInt32LittleEndian(s[4..])); }
    public (int StructIndex, int InstanceIndex) Weak(int i) { var s = Data.AsSpan(_weak + i * 8); return (BinaryPrimitives.ReadInt32LittleEndian(s), BinaryPrimitives.ReadInt32LittleEndian(s[4..])); }
    public int StringIdValue(int i) { CheckIndex(i, StringIdCount); return BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(_stringId + i * 4)); }
    public int LocaleValue(int i) { CheckIndex(i, LocaleCount); return BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(_locale + i * 4)); }
    public int EnumValue(int i) { CheckIndex(i, EnumValueCount); return BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(_enumVal + i * 4)); }
    private static void CheckIndex(int i, int count) { if ((uint)i >= (uint)count) throw new InvalidDataException("DataCore の値の番号が範囲の外です"); }

    // ===== クエリ (query/path.rs・compile.rs・execute.rs) =====
    public sealed record Segment(int ContextStruct, int Position, CT Conversion, DT DataType, int? TypeFilter, int? Target);
    public sealed record CompiledPath(List<Segment> Segments, int RootStruct, DT LeafType);

    public sealed class QueryException(string message) : Exception(message);

    private bool Inherits(int child, int parent)
    {
        int cur = child;
        while (true)
        {
            if (cur == parent) return true;
            var sd = Structs[cur];
            if (sd.ParentIndex == -1) return false;
            cur = sd.ParentIndex;
        }
    }

    private static List<(string Name, string? Filter, bool IsArray)> ParsePath(string path)
    {
        if (path.Length == 0) throw new QueryException("path is empty");
        var res = new List<(string, string?, bool)>();
        foreach (var part in path.Split('.'))
        {
            if (part.Length == 0) throw new QueryException("empty segment");
            int b = part.IndexOf('[');
            if (b >= 0)
            {
                if (b == 0) throw new QueryException("segment has no name before '['");
                if (!part.EndsWith(']')) throw new QueryException("unclosed bracket");
                var f = part[(b + 1)..^1];
                res.Add((part[..b], f.Length == 0 ? null : f, true));
            }
            else res.Add((part, null, false));
        }
        return res;
    }

    // compile_path (leafTypes: 終わりの性質の型として認めるもの)
    public CompiledPath CompilePath(int rootStruct, string path, DT[] leafTypes) => CompileSegments(rootStruct, ParsePath(path), leafTypes);

    public CompiledPath CompileRooted(string path, DT[] leafTypes)
    {
        var segs = ParsePath(path);
        var root = segs[0];
        if (root.Filter != null || root.IsArray) throw new QueryException($"root type '{root.Name}' must not have brackets");
        var rootSi = StructIndexByName(root.Name) ?? throw new QueryException($"struct not found: {root.Name}");
        if (segs.Count == 1) throw new QueryException("rooted path needs at least one property after the type name");
        return CompileSegments(rootSi, segs.Skip(1).ToList(), leafTypes);
    }

    private CompiledPath CompileSegments(int rootStruct, List<(string Name, string? Filter, bool IsArray)> raw, DT[] leafTypes)
    {
        int cur = rootStruct;
        var resolved = new List<Segment>();
        foreach (var seg in raw)
        {
            var props = AllPropertyIndices(cur);
            int found = -1;
            for (int pos = 0; pos < props.Length; pos++) if (PropName(props[pos]) == seg.Name) { found = pos; break; }
            if (found < 0) throw new QueryException($"property not found: {seg.Name} in {StructName(cur)}");
            var prop = Props[props[found]];
            if (!IsKnownDT(prop.DataType) || !IsKnownCT(prop.ConversionType)) throw new QueryException("unknown type");
            var dt = (DT)prop.DataType; var ct = (CT)prop.ConversionType;
            bool isPtr = dt is DT.StrongPointer or DT.WeakPointer;
            if ((seg.IsArray || seg.Filter != null) && ct == CT.Attribute && !isPtr) throw new QueryException($"type filter on scalar: {seg.Name}");
            if (isPtr && ct != CT.Attribute && seg.Filter == null) throw new QueryException($"type filter required: {seg.Name}");
            int? filter = null;
            if (seg.Filter != null)
            {
                var fsi = StructIndexByName(seg.Filter) ?? throw new QueryException($"struct not found: {seg.Filter}");
                if (!Inherits(fsi, prop.StructIndex)) throw new QueryException($"type filter mismatch: {seg.Filter}");
                filter = fsi;
            }
            int? target = dt switch
            {
                DT.Class => prop.StructIndex,
                DT.StrongPointer or DT.WeakPointer or DT.Reference => filter ?? prop.StructIndex,
                _ => null,
            };
            resolved.Add(new Segment(cur, found, ct, dt, filter, target));
            if (target is int t) cur = t; else break;
        }
        if (resolved.Count == 0) throw new QueryException("path produced no segments");
        var leaf = resolved[^1].DataType;
        if (!leafTypes.Contains(leaf)) throw new QueryException($"leaf type mismatch: {raw[^1].Name}");
        return new CompiledPath(resolved, rootStruct, leaf);
    }

    public static readonly DT[] StringLeaf = [DT.String, DT.Locale, DT.EnumChoice];

    // query_first::<String> (文字列の終わりだけ。Class・ポインタ・参照が終わりのものは扱わない)
    public string? QueryFirstString(CompiledPath path, Record record)
    {
        if (record.StructIndex != path.RootStruct) throw new QueryException("struct mismatch");
        var (o, s) = Instance(record.StructIndex, record.InstanceIndex);
        return FirstString(path.Segments, 0, o, s);
    }

    private string? FirstString(List<Segment> segs, int idx, int start, int size)
    {
        if (idx >= segs.Count) return null;
        var seg = segs[idx];
        bool leaf = idx == segs.Count - 1;
        int p = start, end = start + size;
        void Adv(int n) { if (n > end - p) throw new InvalidDataException("DataCore の読み取りが範囲の外です"); p += n; }
        if (seg.Position > 0) Adv(PropertyByteOffset(seg.ContextStruct, seg.Position));
        if (seg.Conversion == CT.Attribute)
        {
            switch (seg.DataType)
            {
                case DT.Class:
                    if (leaf) throw new QueryException("Class leaf is not supported for String");
                    return FirstString(segs, idx + 1, p, end - p);
                case DT.StrongPointer:
                case DT.WeakPointer:
                    {
                        Adv(8);
                        int si = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(p - 8)), ii = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(p - 4));
                        if (si == -1 && ii == -1) return null;
                        if (seg.TypeFilter is int tf && !Inherits(si, tf)) return null;
                        if (leaf) throw new QueryException("pointer leaf is not supported for String");
                        var (io, isz) = Instance(si, ii);
                        return FirstString(segs, idx + 1, io, isz);
                    }
                case DT.Reference:
                    {
                        Adv(20);
                        var rid = Guid128.Read(Data.AsSpan(p - 16));
                        int inst = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(p - 20));
                        if (inst == 0 && rid.IsEmpty) return null;
                        if (RecordById(rid) is not Record tr) return null;
                        if (leaf) throw new QueryException("reference leaf is not supported for String");
                        var (io, isz) = Instance(tr.StructIndex, tr.InstanceIndex);
                        return FirstString(segs, idx + 1, io, isz);
                    }
                default:
                    if (!leaf) return null;
                    Adv(4);
                    int sid = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(p - 4));
                    return String1(sid);
            }
        }
        Adv(8);
        int count = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(p - 8)), first = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(p - 4));
        switch (seg.DataType)
        {
            case DT.StrongPointer:
            case DT.WeakPointer:
                for (int i = 0; i < count; i++)
                {
                    var (si, ii) = seg.DataType == DT.StrongPointer ? Strong(first + i) : Weak(first + i);
                    if (si == -1 && ii == -1) continue;
                    if (seg.TypeFilter is int tf && !Inherits(si, tf)) continue;
                    if (leaf) throw new QueryException("pointer leaf is not supported for String");
                    var (io, isz) = Instance(si, ii);
                    if (FirstString(segs, idx + 1, io, isz) is string v) return v;
                }
                return null;
            case DT.Class:
                {
                    int tsi = seg.Target ?? throw new QueryException("missing target");
                    for (int i = 0; i < count; i++)
                    {
                        if (leaf) throw new QueryException("Class leaf is not supported for String");
                        var (io, isz) = Instance(tsi, first + i);
                        if (FirstString(segs, idx + 1, io, isz) is string v) return v;
                    }
                    return null;
                }
            default:
                if (leaf && count > 0)
                {
                    // read_from_array::<String>: String は文字列の配列、Locale は locale の配列、EnumChoice は列挙の値の配列
                    int sid = seg.DataType switch { DT.String => StringIdValue(first), DT.Locale => LocaleValue(first), _ => EnumValue(first) };
                    return String1(sid);
                }
                return null;
        }
    }
}
