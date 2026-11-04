using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace StarCitizenJapaneseTextCreater;

// DataCore の値の配列の読み取り (database.rs の get_* と同じく、範囲の外は失敗)
internal sealed partial class DataCoreDb
{
    private void Need(int count, int i) { if ((uint)i >= (uint)count) throw new InvalidDataException("DataCore の値の番号が範囲の外です"); }
    public bool GetBool(int i) { Need(BoolCount, i); return Data[_bool + i] != 0; }
    public sbyte GetInt8(int i) { Need(Int8Count, i); return (sbyte)Data[_int8 + i]; }
    public short GetInt16(int i) { Need(Int16Count, i); return BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan(_int16 + i * 2)); }
    public int GetInt32(int i) { Need(Int32Count, i); return BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(_int32 + i * 4)); }
    public long GetInt64(int i) { Need(Int64Count, i); return BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(_int64 + i * 8)); }
    public byte GetUInt8(int i) { Need(UInt8Count, i); return Data[_uint8 + i]; }
    public ushort GetUInt16(int i) { Need(UInt16Count, i); return BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(_uint16 + i * 2)); }
    public uint GetUInt32(int i) { Need(UInt32Count, i); return BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(_uint32 + i * 4)); }
    public ulong GetUInt64(int i) { Need(UInt64Count, i); return BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(_uint64 + i * 8)); }
    public float GetSingle(int i) { Need(SingleCount, i); return BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan(_single + i * 4)); }
    public double GetDouble(int i) { Need(DoubleCount, i); return BinaryPrimitives.ReadDoubleLittleEndian(Data.AsSpan(_double + i * 8)); }
    public Guid128 GetGuid(int i) { Need(GuidCount, i); return Guid128.Read(Data.AsSpan(_guid + i * 16)); }
    public (int InstanceIndex, Guid128 RecordId) GetReference(int i) { Need(ReferenceCount, i); var s = Data.AsSpan(_ref + i * 20); return (BinaryPrimitives.ReadInt32LittleEndian(s), Guid128.Read(s[4..])); }

    // 主なレコード (同じファイルの名前のレコードのうち、最後のもの)
    private HashSet<Guid128>? _main;
    public bool IsMainRecord(Record r)
    {
        if (_main == null)
        {
            var last = new Dictionary<int, Guid128>();
            foreach (var rec in Records) last[rec.FileNameOffset] = rec.Id;
            Interlocked.CompareExchange(ref _main, last.Values.ToHashSet(), null);
        }
        return _main.Contains(r.Id);
    }

    // 構造が WeakPointer を (たどった先で) 持ちうるか (has_weak_pointers)
    private bool[]? _hasWeak;
    public bool StructHasWeakPointers(int si)
    {
        if (_hasWeak == null)
        {
            int n = Structs.Length;
            var direct = new bool[n];
            var edges = new List<int>[n];
            for (int s = 0; s < n; s++)
            {
                edges[s] = new List<int>();
                foreach (var pi in AllPropertyIndices(s))
                {
                    var p = Props[pi];
                    if (!IsKnownDT(p.DataType)) continue;
                    switch ((DT)p.DataType)
                    {
                        case DT.WeakPointer: direct[s] = true; break;
                        case DT.Class: case DT.StrongPointer: edges[s].Add(p.StructIndex); break;
                        case DT.Reference: direct[s] = true; break;
                    }
                }
            }
            var res = (bool[])direct.Clone();
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int s = 0; s < n; s++)
                {
                    if (res[s]) continue;
                    foreach (var t in edges[s]) if (res[t]) { res[s] = true; changed = true; break; }
                }
            }
            Interlocked.CompareExchange(ref _hasWeak, res, null);
        }
        return _hasWeak[si];
    }
}

// serde_json の数・文字の書き方 (zmij 1.0.21・itoa・format_escaped_str)
internal static class RustJson
{
    // 浮動小数: 最短の 10 進の桁。f64 は 1 桁目の指数が -5〜15、f32 は -6〜12 なら固定の書き方 (整数なら「.0」)、ほかは「d.ddde+NN」「de-NN」
    public static string F64(double v) => Format(v.ToString("R", CultureInfo.InvariantCulture), -5, 15, double.IsNegative(v), v == 0);
    public static string F32(float v) => Format(v.ToString("R", CultureInfo.InvariantCulture), -6, 12, float.IsNegative(v), v == 0);

    private static string Format(string r, int lo, int hi, bool neg, bool zero)
    {
        if (zero) return neg ? "-0.0" : "0.0";
        // r を桁と指数に
        var s = r.StartsWith('-') ? r[1..] : r;
        int exp = 0;
        int e = s.IndexOfAny(['E', 'e']);
        if (e >= 0) { exp = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture); s = s[..e]; }
        int dot = s.IndexOf('.');
        string intPart = dot >= 0 ? s[..dot] : s, frac = dot >= 0 ? s[(dot + 1)..] : "";
        var digits = (intPart + frac).TrimStart('0');
        int lead = (intPart + frac).Length - digits.Length;   // 先頭の 0 の数
        int decExp = intPart.Length - 1 - lead + exp;         // 1 桁目の指数
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        int length = digits.Length;
        var sb = new StringBuilder();
        if (neg) sb.Append('-');
        if (decExp >= lo && decExp <= hi)
        {
            if (length - 1 <= decExp) { sb.Append(digits).Append('0', decExp + 1 - length).Append(".0"); }
            else if (decExp >= 0) { sb.Append(digits, 0, decExp + 1).Append('.').Append(digits, decExp + 1, length - decExp - 1); }
            else { sb.Append("0.").Append('0', -decExp - 1).Append(digits); }
            return sb.ToString();
        }
        sb.Append(digits[0]);
        if (length > 1) sb.Append('.').Append(digits, 1, length - 1);
        sb.Append('e').Append(decExp >= 0 ? '+' : '-').Append(Math.Abs(decExp).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // 文字列 (「"」「\」と制御文字だけを逃がす。\b \f \n \r \t、ほかは \u00XX の小文字)
    public static void Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u00").Append(((int)c).ToString("x2"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}

// DataCore のレコードの JSON (export/json.rs JsonSink と walker.rs walk_record の移植)。
// 整形は serde_json の PrettyFormatter (字下げ 2) を JsonSink が使う形のまま: 値の後の end_object_value を呼ばないので、閉じ括弧の前に改行・字下げが入らない
internal static class DataCoreJson
{
    private sealed class Sink(StringBuilder sb)
    {
        private readonly List<bool> _first = new();
        private int _indent;
        private void Indent() { for (int i = 0; i < _indent; i++) sb.Append("  "); }
        private bool IsFirst => _first.Count == 0 || _first[^1];
        private void NotFirst() { if (_first.Count > 0) _first[^1] = false; }
        private void Key(string name)
        {
            sb.Append(IsFirst ? "\n" : ",\n"); Indent(); NotFirst();
            RustJson.Str(sb, name);
            sb.Append(": ");
        }
        private void Prefix(string? name)
        {
            if (name != null) Key(name);
            else if (_first.Count > 0) { sb.Append(IsFirst ? "\n" : ",\n"); Indent(); NotFirst(); }
        }
        public void BeginObject(string? name) { Prefix(name); _indent++; sb.Append('{'); _first.Add(true); }
        public void EndObject() { _first.RemoveAt(_first.Count - 1); _indent--; sb.Append('}'); }
        public void BeginArray(string name) { Key(name); _indent++; sb.Append('['); _first.Add(true); }
        public void EndArray() { _first.RemoveAt(_first.Count - 1); _indent--; sb.Append(']'); }
        public void Null(string? n) { Prefix(n); sb.Append("null"); }
        public void Bool(string? n, bool v) { Prefix(n); sb.Append(v ? "true" : "false"); }
        public void Int(string? n, long v) { Prefix(n); sb.Append(v.ToString(CultureInfo.InvariantCulture)); }
        public void UInt(string? n, ulong v) { Prefix(n); sb.Append(v.ToString(CultureInfo.InvariantCulture)); }
        public void F32(string? n, float v) { Prefix(n); sb.Append(float.IsFinite(v) ? RustJson.F32(v) : "null"); }
        public void F64(string? n, double v) { Prefix(n); sb.Append(double.IsFinite(v) ? RustJson.F64(v) : "null"); }
        public void Str(string? n, string v) { Prefix(n); RustJson.Str(sb, v); }
        public void Guid(string? n, DataCoreDb.Guid128 g) { Prefix(n); RustJson.Str(sb, g.ToString()); }
    }

    private sealed class Ctx
    {
        public Dictionary<(int, int), int> Weak = new();
        public HashSet<(int, int)> PointedTo = new();
        public int FileNameOffset;
    }

    public static string Write(DataCoreDb db, DataCoreDb.Record record)
    {
        var sb = new StringBuilder();
        WriteTo(db, record, sb);
        return sb.ToString();
    }

    public static void WriteTo(DataCoreDb db, DataCoreDb.Record record, StringBuilder sb)
    {
        var sink = new Sink(sb);
        var ctx = new Ctx { FileNameOffset = record.FileNameOffset };
        if (db.StructHasWeakPointers(record.StructIndex))
        {
            var (o, s) = db.Instance(record.StructIndex, record.InstanceIndex);
            var r = new Rd(db.Data, o, s);
            PrescanStruct(db, record.StructIndex, ref r, ctx);
        }
        ctx.PointedTo = ctx.Weak.Keys.ToHashSet();
        sink.BeginObject(null);
        sink.Str("_RecordName_", db.String2(record.NameOffset));
        sink.Guid("_RecordId_", record.Id);
        if (record.TagOffset != -1) sink.Str("_RecordTag_", db.String2(record.TagOffset));
        sink.BeginObject("_RecordValue_");
        WalkInstance(db, record.StructIndex, record.InstanceIndex, sink, ctx);
        sink.EndObject();
        if (ctx.PointedTo.Count > 0)
        {
            sink.BeginObject("_Pointers_");
            foreach (var (key, id) in ctx.PointedTo.Where(ctx.Weak.ContainsKey).Select(k => (k, ctx.Weak[k])).OrderBy(x => x.Item2))
            {
                sink.BeginObject($"ptr:{id}");
                sink.Str("_Type_", db.StructName(key.Item1));
                var (o, s) = db.Instance(key.Item1, key.Item2);
                var r = new Rd(db.Data, o, s);
                WalkFields(db, key.Item1, ref r, sink, ctx);
                sink.EndObject();
            }
            sink.EndObject();
        }
        sink.EndObject();
    }

    // インスタンスの中を読む (SpanReader と同じく、足りなければ失敗)
    private struct Rd(byte[] d, int start, int size)
    {
        private int _p = start;
        private readonly int _end = start + size;
        private int Take(int n) { if (n > _end - _p) throw new InvalidDataException("DataCore のインスタンスの読み取りが範囲の外です"); var p = _p; _p += n; return p; }
        public void Skip(int n) => Take(n);
        public bool TrySkip(int n) { if (n > _end - _p) return false; _p += n; return true; }
        public bool Bool() => d[Take(1)] != 0;
        public sbyte I8() => (sbyte)d[Take(1)];
        public byte U8() => d[Take(1)];
        public short I16() => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(Take(2)));
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(Take(2)));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(Take(4)));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(Take(4)));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(Take(8)));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(Take(8)));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(Take(4)));
        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(Take(8)));
        public DataCoreDb.Guid128 Guid() => DataCoreDb.Guid128.Read(d.AsSpan(Take(16)));
        public (int S, int I) Ptr() { var p = Take(8); return (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4))); }
        public (int Inst, DataCoreDb.Guid128 Id) Ref() { var p = Take(20); return (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)), DataCoreDb.Guid128.Read(d.AsSpan(p + 4))); }
        public bool TryPtr(out (int S, int I) v) { if (8 > _end - _p) { v = default; return false; } v = Ptr(); return true; }
        public bool TryRef(out (int Inst, DataCoreDb.Guid128 Id) v) { if (20 > _end - _p) { v = default; return false; } v = Ref(); return true; }
        public bool TryI32(out int v) { if (4 > _end - _p) { v = 0; return false; } v = I32(); return true; }
    }

    private static bool IsNullPtr((int S, int I) p) => p.S == -1 && p.I == -1;
    private static bool IsNullRef((int Inst, DataCoreDb.Guid128 Id) r) => r.Inst == 0 && r.Id.IsEmpty;

    // ===== 弱いポインタの下見 (prescan_weak_pointers) =====
    private static void PrescanStruct(DataCoreDb db, int si, ref Rd r, Ctx ctx)
    {
        foreach (var pi in db.AllPropertyIndices(si))
        {
            var p = db.Props[pi];
            if (!DataCoreDb.IsKnownDT(p.DataType) || !DataCoreDb.IsKnownCT(p.ConversionType)) continue;
            var dt = (DataCoreDb.DT)p.DataType;
            if ((DataCoreDb.CT)p.ConversionType == DataCoreDb.CT.Attribute) PrescanAttr(db, dt, p.StructIndex, ref r, ctx);
            else PrescanArray(db, dt, p.StructIndex, ref r, ctx);
        }
    }

    private static void AddWeak(Ctx ctx, (int, int) key) { if (!ctx.Weak.ContainsKey(key)) ctx.Weak[key] = ctx.Weak.Count + 1; }

    private static void PrescanAttr(DataCoreDb db, DataCoreDb.DT dt, int psi, ref Rd r, Ctx ctx)
    {
        switch (dt)
        {
            case DataCoreDb.DT.WeakPointer:
                if (r.TryPtr(out var w) && !IsNullPtr(w)) AddWeak(ctx, w);
                break;
            case DataCoreDb.DT.StrongPointer:
                if (r.TryPtr(out var s) && !IsNullPtr(s)) { var (o, z) = db.Instance(s.S, s.I); var sub = new Rd(db.Data, o, z); PrescanStruct(db, s.S, ref sub, ctx); }
                break;
            case DataCoreDb.DT.Reference:
                if (r.TryRef(out var rf) && !IsNullRef(rf) && db.RecordById(rf.Id) is DataCoreDb.Record t && !db.IsMainRecord(t) && t.FileNameOffset == ctx.FileNameOffset)
                { var (o, z) = db.Instance(t.StructIndex, t.InstanceIndex); var sub = new Rd(db.Data, o, z); PrescanStruct(db, t.StructIndex, ref sub, ctx); }
                break;
            case DataCoreDb.DT.Class:
                PrescanStruct(db, psi, ref r, ctx);
                break;
            default:
                r.TrySkip(DataCoreDb.InlineSize(dt));
                break;
        }
    }

    private static void PrescanArray(DataCoreDb db, DataCoreDb.DT dt, int psi, ref Rd r, Ctx ctx)
    {
        if (!r.TryI32(out var count)) count = 0;
        if (!r.TryI32(out var first)) first = 0;
        for (int i = first; i < first + count; i++)
        {
            switch (dt)
            {
                case DataCoreDb.DT.WeakPointer:
                    { var w = db.Weak(i); if (!IsNullPtr(w)) AddWeak(ctx, w); break; }
                case DataCoreDb.DT.StrongPointer:
                    { var s = db.Strong(i); if (!IsNullPtr(s)) { var (o, z) = db.Instance(s.Item1, s.Item2); var sub = new Rd(db.Data, o, z); PrescanStruct(db, s.Item1, ref sub, ctx); } break; }
                case DataCoreDb.DT.Reference:
                    {
                        var rf = db.GetReference(i);
                        if (!IsNullRef(rf) && db.RecordById(rf.RecordId) is DataCoreDb.Record t && !db.IsMainRecord(t) && t.FileNameOffset == ctx.FileNameOffset)
                        { var (o, z) = db.Instance(t.StructIndex, t.InstanceIndex); var sub = new Rd(db.Data, o, z); PrescanStruct(db, t.StructIndex, ref sub, ctx); }
                        break;
                    }
                case DataCoreDb.DT.Class:
                    { var (o, z) = db.Instance(psi, i); var sub = new Rd(db.Data, o, z); PrescanStruct(db, psi, ref sub, ctx); break; }
            }
        }
    }

    // ===== 書き出し (walk_instance・walk_struct_fields・walk_attribute・walk_array・resolve_reference) =====
    private static void WalkInstance(DataCoreDb db, int si, int ii, Sink sink, Ctx ctx)
    {
        var (o, s) = db.Instance(si, ii);
        var r = new Rd(db.Data, o, s);
        if (ctx.Weak.TryGetValue((si, ii), out var id))
        {
            sink.Str("_Pointer_", $"ptr:{id}");
            ctx.PointedTo.Remove((si, ii));
        }
        sink.Str("_Type_", db.StructName(si));
        WalkFields(db, si, ref r, sink, ctx);
    }

    private static void WalkFields(DataCoreDb db, int si, ref Rd r, Sink sink, Ctx ctx)
    {
        foreach (var pi in db.AllPropertyIndices(si))
        {
            var p = db.Props[pi];
            if (!DataCoreDb.IsKnownDT(p.DataType) || !DataCoreDb.IsKnownCT(p.ConversionType)) throw new InvalidDataException($"DataCore の型 {p.DataType} が分かりません");
            var dt = (DataCoreDb.DT)p.DataType;
            var name = db.String2(p.NameOffset);
            if ((DataCoreDb.CT)p.ConversionType == DataCoreDb.CT.Attribute) WalkAttr(db, dt, p.StructIndex, name, ref r, sink, ctx);
            else WalkArray(db, dt, p.StructIndex, name, ref r, sink, ctx);
        }
    }

    private static void WalkAttr(DataCoreDb db, DataCoreDb.DT dt, int psi, string name, ref Rd r, Sink sink, Ctx ctx)
    {
        switch (dt)
        {
            case DataCoreDb.DT.Boolean: sink.Bool(name, r.Bool()); break;
            case DataCoreDb.DT.SByte: sink.Int(name, r.I8()); break;
            case DataCoreDb.DT.Int16: sink.Int(name, r.I16()); break;
            case DataCoreDb.DT.Int32: sink.Int(name, r.I32()); break;
            case DataCoreDb.DT.Int64: sink.Int(name, r.I64()); break;
            case DataCoreDb.DT.Byte: sink.UInt(name, r.U8()); break;
            case DataCoreDb.DT.UInt16: sink.UInt(name, r.U16()); break;
            case DataCoreDb.DT.UInt32: sink.UInt(name, r.U32()); break;
            case DataCoreDb.DT.UInt64: sink.UInt(name, r.U64()); break;
            case DataCoreDb.DT.Single: sink.F32(name, r.F32()); break;
            case DataCoreDb.DT.Double: sink.F64(name, r.F64()); break;
            case DataCoreDb.DT.String: case DataCoreDb.DT.Locale: case DataCoreDb.DT.EnumChoice: sink.Str(name, db.String1(r.I32())); break;
            case DataCoreDb.DT.Guid: sink.Guid(name, r.Guid()); break;
            case DataCoreDb.DT.Class:
                sink.BeginObject(name);
                sink.Str("_Type_", db.StructName(psi));
                WalkFields(db, psi, ref r, sink, ctx);
                sink.EndObject();
                break;
            case DataCoreDb.DT.StrongPointer:
                {
                    var p = r.Ptr();
                    if (IsNullPtr(p)) sink.Null(name);
                    else { sink.BeginObject(name); WalkInstance(db, p.S, p.I, sink, ctx); sink.EndObject(); }
                    break;
                }
            case DataCoreDb.DT.WeakPointer:
                {
                    var p = r.Ptr();
                    if (IsNullPtr(p)) sink.Null(name);
                    else if (ctx.Weak.TryGetValue(p, out var id)) sink.Str(name, $"_PointsTo_:ptr:{id}");
                    else sink.Null(name);
                    break;
                }
            case DataCoreDb.DT.Reference:
                ResolveReference(db, r.Ref(), name, sink, ctx);
                break;
        }
    }

    private static void WalkArray(DataCoreDb db, DataCoreDb.DT dt, int psi, string name, ref Rd r, Sink sink, Ctx ctx)
    {
        int count = r.I32(), first = r.I32();
        sink.BeginArray(name);
        for (int i = first; i < first + count; i++)
        {
            switch (dt)
            {
                case DataCoreDb.DT.Boolean: sink.Bool(null, db.GetBool(i)); break;
                case DataCoreDb.DT.SByte: sink.Int(null, db.GetInt8(i)); break;
                case DataCoreDb.DT.Int16: sink.Int(null, db.GetInt16(i)); break;
                case DataCoreDb.DT.Int32: sink.Int(null, db.GetInt32(i)); break;
                case DataCoreDb.DT.Int64: sink.Int(null, db.GetInt64(i)); break;
                case DataCoreDb.DT.Byte: sink.UInt(null, db.GetUInt8(i)); break;
                case DataCoreDb.DT.UInt16: sink.UInt(null, db.GetUInt16(i)); break;
                case DataCoreDb.DT.UInt32: sink.UInt(null, db.GetUInt32(i)); break;
                case DataCoreDb.DT.UInt64: sink.UInt(null, db.GetUInt64(i)); break;
                case DataCoreDb.DT.Single: sink.F32(null, db.GetSingle(i)); break;
                case DataCoreDb.DT.Double: sink.F64(null, db.GetDouble(i)); break;
                case DataCoreDb.DT.String: sink.Str(null, db.String1(db.StringIdValue(i))); break;
                case DataCoreDb.DT.Locale: sink.Str(null, db.String1(db.LocaleValue(i))); break;
                case DataCoreDb.DT.EnumChoice: sink.Str(null, db.String1(db.EnumValue(i))); break;
                case DataCoreDb.DT.Guid: sink.Guid(null, db.GetGuid(i)); break;
                case DataCoreDb.DT.Class: sink.BeginObject(null); WalkInstance(db, psi, i, sink, ctx); sink.EndObject(); break;
                case DataCoreDb.DT.StrongPointer:
                    {
                        var p = db.Strong(i);
                        if (IsNullPtr(p)) sink.Null(null);
                        else { sink.BeginObject(null); WalkInstance(db, p.Item1, p.Item2, sink, ctx); sink.EndObject(); }
                        break;
                    }
                case DataCoreDb.DT.WeakPointer:
                    {
                        var p = db.Weak(i);
                        if (IsNullPtr(p)) sink.Null(null);
                        else if (ctx.Weak.TryGetValue(p, out var id)) sink.Str(null, $"_PointsTo_:ptr:{id}");
                        else sink.Null(null);
                        break;
                    }
                case DataCoreDb.DT.Reference:
                    { var rf = db.GetReference(i); ResolveReference(db, (rf.InstanceIndex, rf.RecordId), null, sink, ctx); break; }
            }
        }
        sink.EndArray();
    }

    private static void ResolveReference(DataCoreDb db, (int Inst, DataCoreDb.Guid128 Id) rf, string? name, Sink sink, Ctx ctx)
    {
        if (IsNullRef(rf)) { sink.Null(name); return; }
        if (db.RecordById(rf.Id) is not DataCoreDb.Record t) { sink.Null(name); return; }
        var targetFile = db.String1(t.FileNameOffset);
        var contextFile = db.String1(ctx.FileNameOffset);
        var targetName = db.String2(t.NameOffset);
        if (db.IsMainRecord(t))
        {
            sink.Str(name, ChangeExtension(RelativePath(targetFile, contextFile), "json"));
            return;
        }
        if (t.FileNameOffset == ctx.FileNameOffset)
        {
            sink.BeginObject(name);
            sink.Guid("_RecordId_", t.Id);
            sink.Str("_RecordName_", targetName);
            WalkInstance(db, t.StructIndex, t.InstanceIndex, sink, ctx);
            sink.EndObject();
            return;
        }
        sink.BeginObject(name);
        sink.Str("_RecordPath_", ChangeExtension(RelativePath(targetFile, contextFile), "json"));
        sink.Str("_RecordName_", targetName);
        sink.Guid("_RecordId_", t.Id);
        sink.EndObject();
    }

    public static string ChangeExtension(string path, string ext)
    {
        int lastSlash = path.LastIndexOf('/') + 1;
        int dot = path.LastIndexOf('.');
        if (dot >= lastSlash) path = path[..dot];
        return path + "." + ext;
    }

    public static string RelativePath(string filePath, string contextFile)
    {
        int slashes = contextFile.Count(c => c == '/');
        var sb = new StringBuilder("file://./");
        for (int i = 0; i < slashes; i++) sb.Append("../");
        sb.Append(filePath);
        return sb.ToString();
    }
}

// dcb query・dcb extract (cli/src/dcb.rs) の移植
internal static class DataCoreCli
{
    // dcb query <path> --filter <filter> の標準出力 (行は「\n」)。型が無ければ例外 (starbreaker は終了コード 1)。
    // 当てはまるレコードが無ければ NoRecordsException (starbreaker は「no … records matching filter」で終了コード 1)
    public sealed class NoRecordsException(string message) : Exception(message);

    public static string Query(DataCoreDb db, string path, string? filter)
    {
        var typeName = path.Split('.')[0];
        bool hasProps = path.Length > typeName.Length;
        if (db.StructIndexByName(typeName) is not int si) throw new InvalidOperationException($"unknown record type: {typeName}");
        var records = db.Records.Where(r => r.StructIndex == si && db.IsMainRecord(r) && GlobMatch.MatchesFilter(db.String2(r.NameOffset), filter)).ToList();
        if (records.Count == 0)
            throw new NoRecordsException(filter != null ? $"no {typeName} records matching filter: {filter}" : $"no main records of type {typeName}");
        var sb = new StringBuilder();
        if (!hasProps)
        {
            foreach (var r in records) { DataCoreJson.WriteTo(db, r, sb); sb.Append('\n'); }
            return sb.ToString();
        }
        var compiled = db.CompileRooted(path, AllTypes);
        foreach (var r in records)
        {
            var name = db.String2(r.NameOffset);
            List<object?> values;
            try { values = DataCoreValues.QueryAll(db, compiled, r); }
            catch (Exception e) when (e is DataCoreDb.QueryException or InvalidDataException) { continue; }
            foreach (var v in values)
            {
                switch (v)
                {
                    case null: break;
                    case DataCoreValues.StrValue s: sb.Append(name).Append('\t').Append(s.Value).Append('\n'); break;
                    case DataCoreValues.GuidValue g: sb.Append(name).Append('\t').Append("CigGuid(").Append(g.Id.ToString()).Append(")\n"); break;
                    default:
                        var j = new StringBuilder();
                        if (!DataCoreValues.ToJson(v, j)) break;   // null になるもの
                        sb.Append(name).Append('\t').Append(j).Append('\n');
                        break;
                }
            }
        }
        return sb.ToString();
    }

    public static readonly DataCoreDb.DT[] AllTypes = Enum.GetValues<DataCoreDb.DT>();

    // dcb extract --format json --filter <glob>: 主なレコードのうちファイルのパスが一致するものを <out>/<パスの拡張子を json>。書いたファイルの数を返す
    public static int ExtractJson(DataCoreDb db, string outDir, string? filter, Action<string>? onError = null, CancellationToken ct = default)
    {
        var recs = db.Records.Where(r => db.IsMainRecord(r) && GlobMatch.MatchesFilter(db.String1(r.FileNameOffset), filter)).ToList();
        Directory.CreateDirectory(outDir);
        int n = 0;
        var utf8 = new UTF8Encoding(false);
        Parallel.ForEach(recs, new ParallelOptions { CancellationToken = ct }, r =>
        {
            var file = db.String1(r.FileNameOffset);
            int dot = file.LastIndexOf('.');
            var outName = (dot >= 0 ? file[..dot] : file) + ".json";
            var outPath = Path.Combine(outDir, outName);
            try
            {
                var dir = Path.GetDirectoryName(outPath);
                if (dir != null) Directory.CreateDirectory(dir);
                var json = DataCoreJson.Write(db, r);
                File.WriteAllBytes(outPath, utf8.GetBytes(json));
                Interlocked.Increment(ref n);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { onError?.Invoke($"Error exporting {file}: {e.Message}"); }
        });
        return n;
    }
}

// クエリの値 (query/value.rs) と、dcb query の JSON (value_to_json と serde_json の 1 行の書き方。キーは名前の順)
internal static class DataCoreValues
{
    public sealed record StrValue(string Value);          // String・Enum・Locale (文字列)
    public sealed record GuidValue(DataCoreDb.Guid128 Id);
    public sealed record F32Value(float Value);
    public sealed record F64Value(double Value);
    public sealed record IntValue(long Value);
    public sealed record UIntValue(ulong Value);
    public sealed record BoolValue(bool Value);
    public sealed record ArrValue(List<object?> Items);
    public sealed record ObjValue(string TypeName, List<(string Name, object? Value)> Fields);

    private const int MaxDepth = 24;

    // query_all::<Value> (エラーなら例外。呼ぶ側でそのレコードを飛ばす)
    public static List<object?> QueryAll(DataCoreDb db, DataCoreDb.CompiledPath path, DataCoreDb.Record record)
    {
        if (record.StructIndex != path.RootStruct) throw new DataCoreDb.QueryException("struct mismatch");
        var (o, s) = db.Instance(record.StructIndex, record.InstanceIndex);
        var res = new List<object?>();
        Exec(db, path.Segments, 0, o, s, res);
        return res;
    }

    private static void Exec(DataCoreDb db, List<DataCoreDb.Segment> segs, int idx, int start, int size, List<object?> res)
    {
        if (idx >= segs.Count) return;
        var seg = segs[idx];
        bool leaf = idx == segs.Count - 1;
        var r = new Cur(db.Data, start, size);
        if (seg.Position > 0) r.Skip(db.PropertyByteOffset(seg.ContextStruct, seg.Position));
        if (seg.Conversion == DataCoreDb.CT.Attribute)
        {
            switch (seg.DataType)
            {
                case DataCoreDb.DT.Class:
                    if (leaf) res.Add(Materialize(db, seg.Target!.Value, ref r, 0, new HashSet<DataCoreDb.Guid128>()));
                    else Exec(db, segs, idx + 1, r.Pos, r.End - r.Pos, res);
                    return;
                case DataCoreDb.DT.StrongPointer:
                case DataCoreDb.DT.WeakPointer:
                    {
                        var p = r.Ptr();
                        if (p.S == -1 && p.I == -1) return;
                        if (seg.TypeFilter is int tf && !Inherits(db, p.S, tf)) return;
                        var (io, isz) = db.Instance(p.S, p.I);
                        if (leaf) { var sub = new Cur(db.Data, io, isz); res.Add(Materialize(db, p.S, ref sub, 0, new())); }
                        else Exec(db, segs, idx + 1, io, isz, res);
                        return;
                    }
                case DataCoreDb.DT.Reference:
                    {
                        var rf = r.Ref();
                        if (rf.Inst == 0 && rf.Id.IsEmpty) return;
                        if (db.RecordById(rf.Id) is not DataCoreDb.Record t) return;
                        var (io, isz) = db.Instance(t.StructIndex, t.InstanceIndex);
                        if (leaf) { var sub = new Cur(db.Data, io, isz); res.Add(Materialize(db, t.StructIndex, ref sub, 0, new())); }
                        else Exec(db, segs, idx + 1, io, isz, res);
                        return;
                    }
                default:
                    if (leaf) res.Add(ReadScalar(db, ref r, seg.DataType));
                    return;
            }
        }
        int count = r.I32(), first = r.I32();
        switch (seg.DataType)
        {
            case DataCoreDb.DT.StrongPointer:
            case DataCoreDb.DT.WeakPointer:
                for (int i = 0; i < count; i++)
                {
                    var p = seg.DataType == DataCoreDb.DT.StrongPointer ? db.Strong(first + i) : db.Weak(first + i);
                    if (p.StructIndex == -1 && p.InstanceIndex == -1) continue;
                    if (seg.TypeFilter is int tf && !Inherits(db, p.StructIndex, tf)) continue;
                    var (io, isz) = db.Instance(p.StructIndex, p.InstanceIndex);
                    if (leaf) { var sub = new Cur(db.Data, io, isz); res.Add(Materialize(db, p.StructIndex, ref sub, 0, new())); }
                    else Exec(db, segs, idx + 1, io, isz, res);
                }
                return;
            case DataCoreDb.DT.Class:
                {
                    int tsi = seg.Target!.Value;
                    for (int i = 0; i < count; i++)
                    {
                        var (io, isz) = db.Instance(tsi, first + i);
                        if (leaf) { var sub = new Cur(db.Data, io, isz); res.Add(Materialize(db, tsi, ref sub, 0, new())); }
                        else Exec(db, segs, idx + 1, io, isz, res);
                    }
                    return;
                }
            default:
                if (leaf) for (int i = 0; i < count; i++) res.Add(ReadArray(db, first + i, seg.DataType));
                return;
        }
    }

    private static bool Inherits(DataCoreDb db, int child, int parent)
    {
        int cur = child;
        while (true)
        {
            if (cur == parent) return true;
            var sd = db.Structs[cur];
            if (sd.ParentIndex == -1) return false;
            cur = sd.ParentIndex;
        }
    }

    private struct Cur(byte[] d, int start, int size)
    {
        public int Pos = start;
        public readonly int End = start + size;
        private int Take(int n) { if (n > End - Pos) throw new InvalidDataException("DataCore の読み取りが範囲の外です"); var p = Pos; Pos += n; return p; }
        public void Skip(int n) => Take(n);
        public bool Bool() => d[Take(1)] != 0;
        public sbyte I8() => (sbyte)d[Take(1)];
        public byte U8() => d[Take(1)];
        public short I16() => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(Take(2)));
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(Take(2)));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(Take(4)));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(Take(4)));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(Take(8)));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(Take(8)));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(Take(4)));
        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(Take(8)));
        public DataCoreDb.Guid128 Guid() => DataCoreDb.Guid128.Read(d.AsSpan(Take(16)));
        public (int S, int I) Ptr() { var p = Take(8); return (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4))); }
        public (int Inst, DataCoreDb.Guid128 Id) Ref() { var p = Take(20); return (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)), DataCoreDb.Guid128.Read(d.AsSpan(p + 4))); }
    }

    private static object? ReadScalar(DataCoreDb db, ref Cur r, DataCoreDb.DT dt) => dt switch
    {
        DataCoreDb.DT.Boolean => new BoolValue(r.Bool()),
        DataCoreDb.DT.SByte => new IntValue(r.I8()),
        DataCoreDb.DT.Int16 => new IntValue(r.I16()),
        DataCoreDb.DT.Int32 => new IntValue(r.I32()),
        DataCoreDb.DT.Int64 => new IntValue(r.I64()),
        DataCoreDb.DT.Byte => new UIntValue(r.U8()),
        DataCoreDb.DT.UInt16 => new UIntValue(r.U16()),
        DataCoreDb.DT.UInt32 => new UIntValue(r.U32()),
        DataCoreDb.DT.UInt64 => new UIntValue(r.U64()),
        DataCoreDb.DT.Single => new F32Value(r.F32()),
        DataCoreDb.DT.Double => new F64Value(r.F64()),
        DataCoreDb.DT.String or DataCoreDb.DT.Locale or DataCoreDb.DT.EnumChoice => new StrValue(db.String1(r.I32())),
        DataCoreDb.DT.Guid => new GuidValue(r.Guid()),
        _ => null,
    };

    private static object? ReadArray(DataCoreDb db, int i, DataCoreDb.DT dt) => dt switch
    {
        DataCoreDb.DT.Boolean => new BoolValue(db.GetBool(i)),
        DataCoreDb.DT.SByte => new IntValue(db.GetInt8(i)),
        DataCoreDb.DT.Int16 => new IntValue(db.GetInt16(i)),
        DataCoreDb.DT.Int32 => new IntValue(db.GetInt32(i)),
        DataCoreDb.DT.Int64 => new IntValue(db.GetInt64(i)),
        DataCoreDb.DT.Byte => new UIntValue(db.GetUInt8(i)),
        DataCoreDb.DT.UInt16 => new UIntValue(db.GetUInt16(i)),
        DataCoreDb.DT.UInt32 => new UIntValue(db.GetUInt32(i)),
        DataCoreDb.DT.UInt64 => new UIntValue(db.GetUInt64(i)),
        DataCoreDb.DT.Single => new F32Value(db.GetSingle(i)),
        DataCoreDb.DT.Double => new F64Value(db.GetDouble(i)),
        DataCoreDb.DT.String => new StrValue(db.String1(db.StringIdValue(i))),
        DataCoreDb.DT.Locale => new StrValue(db.String1(db.LocaleValue(i))),
        DataCoreDb.DT.EnumChoice => new StrValue(db.String1(db.EnumValue(i))),
        DataCoreDb.DT.Guid => new GuidValue(db.GetGuid(i)),
        _ => null,
    };

    // materialize_struct_depth (参照はたどる。同じレコードの入れ子は GUID にする。深さ 24 まで)
    private static ObjValue Materialize(DataCoreDb db, int si, ref Cur r, int depth, HashSet<DataCoreDb.Guid128> visited)
    {
        var typeName = db.StructName(si);
        if (depth >= MaxDepth)
        {
            r.Skip((int)db.Structs[si].StructSize);
            return new ObjValue(typeName, new());
        }
        var props = db.AllPropertyIndices(si);
        var fields = new List<(string, object?)>(props.Length);
        foreach (var pi in props)
        {
            var p = db.Props[pi];
            if (!DataCoreDb.IsKnownDT(p.DataType) || !DataCoreDb.IsKnownCT(p.ConversionType)) throw new DataCoreDb.QueryException("unknown type");
            var dt = (DataCoreDb.DT)p.DataType;
            var name = db.String2(p.NameOffset);
            object? v = (DataCoreDb.CT)p.ConversionType != DataCoreDb.CT.Attribute
                ? MatArray(db, dt, p.StructIndex, ref r, depth, visited)
                : MatAttr(db, dt, p.StructIndex, ref r, depth, visited);
            fields.Add((name, v));
        }
        return new ObjValue(typeName, fields);
    }

    private static object? MatRef(DataCoreDb db, (int Inst, DataCoreDb.Guid128 Id) rf, int depth, HashSet<DataCoreDb.Guid128> visited)
    {
        if (rf.Inst == 0 && rf.Id.IsEmpty) return null;
        if (!visited.Add(rf.Id)) return new GuidValue(rf.Id);
        if (depth >= MaxDepth) return new GuidValue(rf.Id);   // starbreaker は visited から外さない
        object? res;
        if (db.RecordById(rf.Id) is DataCoreDb.Record t)
        {
            var (io, isz) = db.Instance(t.StructIndex, t.InstanceIndex);
            var sub = new Cur(db.Data, io, isz);
            res = Materialize(db, t.StructIndex, ref sub, depth + 1, visited);
        }
        else res = null;
        visited.Remove(rf.Id);
        return res;
    }

    private static object? MatAttr(DataCoreDb db, DataCoreDb.DT dt, int psi, ref Cur r, int depth, HashSet<DataCoreDb.Guid128> visited)
    {
        switch (dt)
        {
            case DataCoreDb.DT.Class: return Materialize(db, psi, ref r, depth + 1, visited);
            case DataCoreDb.DT.StrongPointer:
            case DataCoreDb.DT.WeakPointer:
                {
                    var p = r.Ptr();
                    if (p.S == -1 && p.I == -1) return null;
                    var (io, isz) = db.Instance(p.S, p.I);
                    var sub = new Cur(db.Data, io, isz);
                    return Materialize(db, p.S, ref sub, depth + 1, visited);
                }
            case DataCoreDb.DT.Reference: return MatRef(db, r.Ref(), depth, visited);
            default: return ReadScalar(db, ref r, dt);
        }
    }

    private static object? MatArray(DataCoreDb db, DataCoreDb.DT dt, int psi, ref Cur r, int depth, HashSet<DataCoreDb.Guid128> visited)
    {
        int count = r.I32(), first = r.I32();
        var items = new List<object?>(Math.Max(0, count));
        for (int i = 0; i < count; i++)
        {
            int idx = first + i;
            switch (dt)
            {
                case DataCoreDb.DT.Class:
                    { var (io, isz) = db.Instance(psi, idx); var sub = new Cur(db.Data, io, isz); items.Add(Materialize(db, psi, ref sub, depth + 1, visited)); break; }
                case DataCoreDb.DT.StrongPointer:
                case DataCoreDb.DT.WeakPointer:
                    {
                        var p = dt == DataCoreDb.DT.StrongPointer ? db.Strong(idx) : db.Weak(idx);
                        if (p.StructIndex == -1 && p.InstanceIndex == -1) { items.Add(null); break; }
                        var (io, isz) = db.Instance(p.StructIndex, p.InstanceIndex);
                        var sub = new Cur(db.Data, io, isz);
                        items.Add(Materialize(db, p.StructIndex, ref sub, depth + 1, visited));
                        break;
                    }
                case DataCoreDb.DT.Reference:
                    { var rf = db.GetReference(idx); items.Add(MatRef(db, (rf.InstanceIndex, rf.RecordId), depth, visited)); break; }
                default: items.Add(ReadArray(db, idx, dt)); break;
            }
        }
        return new ArrValue(items);
    }

    // value_to_json → serde_json の 1 行の書き方。null になるものは false (配列・オブジェクトの中では null と書く)
    public static bool ToJson(object? v, StringBuilder sb)
    {
        switch (v)
        {
            case null: return false;
            case BoolValue b: sb.Append(b.Value ? "true" : "false"); return true;
            case IntValue i: sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)); return true;
            case UIntValue u: sb.Append(u.Value.ToString(CultureInfo.InvariantCulture)); return true;
            // f32 は f64 にしてから書く (Number::from_f32)。NaN・無限は null
            case F32Value f: if (!float.IsFinite(f.Value)) return false; sb.Append(RustJson.F64(f.Value)); return true;
            case F64Value d: if (!double.IsFinite(d.Value)) return false; sb.Append(RustJson.F64(d.Value)); return true;
            case StrValue s: RustJson.Str(sb, s.Value); return true;
            case GuidValue g: RustJson.Str(sb, $"CigGuid({g.Id})"); return true;
            case ArrValue a:
                sb.Append('[');
                for (int k = 0; k < a.Items.Count; k++) { if (k > 0) sb.Append(','); if (!ToJson(a.Items[k], sb)) sb.Append("null"); }
                sb.Append(']');
                return true;
            case ObjValue o:
                {
                    // serde_json の Map (BTreeMap): キーの順 (バイト順)、同じキーは後の値
                    var map = new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["__type"] = new StrValue(o.TypeName) };
                    foreach (var (k, val) in o.Fields) map[k] = val;
                    sb.Append('{');
                    bool firstK = true;
                    foreach (var (k, val) in map)
                    {
                        if (!firstK) sb.Append(',');
                        firstK = false;
                        RustJson.Str(sb, k);
                        sb.Append(':');
                        if (!ToJson(val, sb)) sb.Append("null");
                    }
                    sb.Append('}');
                    return true;
                }
        }
        return false;
    }
}
