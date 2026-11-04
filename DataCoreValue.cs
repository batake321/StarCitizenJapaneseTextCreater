using System.Buffers.Binary;

namespace StarCitizenJapaneseTextCreater;

// DataCore の値の木 (query::<Value>・query_no_references) (2026-11-04、船の 3D の読み取りを自前にするため)。
// StarBreaker v0.2.2 (crates/starbreaker-datacore の query/execute.rs の execute_segments・materialize_*、query/value.rs) の移植。
//   ・References は既定では先の record を開いて Object にする (record_id 付き)。同じ record を辿っている途中なら Guid にする (visited)。
//     no_refs では Reference は Guid のまま
//   ・深さ 24 (MAX_MATERIALIZE_DEPTH) に届いたら、その struct は中を読まず大きさ分だけ進めて空の Object にする
//   ・読み取りが範囲の外なら例外 (InvalidDataException。Rust の QueryError と同じく、呼ぶ側で「結果なし」に扱う)
internal abstract record DcValue
{
    public sealed record Null : DcValue { public static readonly Null I = new(); }
    public sealed record Bool(bool V) : DcValue;
    public sealed record Int8(sbyte V) : DcValue;
    public sealed record Int16(short V) : DcValue;
    public sealed record Int32(int V) : DcValue;
    public sealed record Int64(long V) : DcValue;
    public sealed record UInt8(byte V) : DcValue;
    public sealed record UInt16(ushort V) : DcValue;
    public sealed record UInt32(uint V) : DcValue;
    public sealed record UInt64(ulong V) : DcValue;
    public sealed record Float(float V) : DcValue;
    public sealed record Double(double V) : DcValue;
    public sealed record Str(string V) : DcValue;      // String
    public sealed record Enum(string V) : DcValue;
    public sealed record Locale(string V) : DcValue;
    public sealed record Guid(DataCoreDb.Guid128 V) : DcValue;
    public sealed record Arr(List<DcValue> Items) : DcValue;
    public sealed record Obj(string TypeName, List<(string Name, DcValue Value)> Fields, DataCoreDb.Guid128? RecordId) : DcValue;

    // loadout.rs get_object_field: 名前が一致する最初の欄のうち Object のもの (同じ名前が続けば、Object である最初のもの)
    public static Obj? ObjField(DcValue v, string name)
    {
        if (v is not Obj o) return null;
        foreach (var (k, x) in o.Fields) if (k == name && x is Obj ox) return ox;
        return null;
    }
    // get_string_field (String だけ。Enum・Locale は当たらない)
    public static string? StrField(DcValue v, string name)
    {
        if (v is not Obj o) return null;
        foreach (var (k, x) in o.Fields) if (k == name && x is Str s) return s.V;
        return null;
    }
    public static List<DcValue>? ArrField(DcValue v, string name)
    {
        if (v is not Obj o) return null;
        foreach (var (k, x) in o.Fields) if (k == name && x is Arr a) return a.Items;
        return null;
    }
    public static bool? BoolField(DcValue v, string name)
    {
        if (v is not Obj o) return null;
        foreach (var (k, x) in o.Fields) if (k == name && x is Bool b) return b.V;
        return null;
    }
    // get_f32_field: Float・Double (f32 に)・Int32 (f32 に) の最初のもの
    public static float? F32Field(DcValue v, string name)
    {
        if (v is not Obj o) return null;
        foreach (var (k, x) in o.Fields)
            if (k == name)
                switch (x)
                {
                    case Float f: return f.V;
                    case Double d: return (float)d.V;
                    case Int32 i: return i.V;
                }
        return null;
    }
    // get_value_u8 (UInt8 だけ)
    public static byte? U8Field(DcValue v, string name)
    {
        if (v is not Obj o) return null;
        foreach (var (k, x) in o.Fields) if (k == name && x is UInt8 b) return b.V;
        return null;
    }
    public static DataCoreDb.Guid128? GuidField(DcValue v, string name)
    {
        if (v is not Obj o) return null;
        foreach (var (k, x) in o.Fields) if (k == name && x is Guid g) return g.V;
        return null;
    }
    // get_value_field (pipeline.rs): 名前が一致する最初の欄 (型を問わない)
    public static DcValue? Field(DcValue v, string name)
    {
        if (v is not Obj o) return null;
        foreach (var (k, x) in o.Fields) if (k == name) return x;
        return null;
    }
}

internal sealed partial class DataCoreDb
{
    public static readonly DT[] AnyLeaf = [DT.Boolean, DT.SByte, DT.Int16, DT.Int32, DT.Int64, DT.Byte, DT.UInt16, DT.UInt32, DT.UInt64, DT.String, DT.Single, DT.Double, DT.Locale, DT.Guid, DT.EnumChoice, DT.Class, DT.StrongPointer, DT.WeakPointer, DT.Reference];

    private const int MaxMaterializeDepth = 24;

    // query::<Value> (skipReferences=false) と query_no_references (true)
    public List<DcValue> QueryValues(CompiledPath path, Record record, bool skipReferences = false)
    {
        if (record.StructIndex != path.RootStruct) throw new QueryException("struct mismatch");
        var (o, s) = Instance(record.StructIndex, record.InstanceIndex);
        var res = new List<DcValue>();
        var r = new DcReader(Data, o, s);
        ExecValues(path.Segments, 0, ref r, res, skipReferences);
        return res;
    }

    private struct DcReader
    {
        private readonly byte[] _d;
        public int P;
        private readonly int _end;
        public DcReader(byte[] d, int start, int size) { _d = d; P = start; _end = start + size; }
        public int Take(int n)
        {
            if (n < 0 || n > _end - P) throw new InvalidDataException("DataCore の読み取りが範囲の外です");
            int at = P; P += n; return at;
        }
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(Take(4)));
    }

    private bool TypeMatches(int actual, int? filter) => filter is not int f || Inherits(actual, f);

    private (int StructIndex, int InstanceIndex) ReadPointer(ref DcReader r)
    {
        int at = r.Take(8);
        return (BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(at)), BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(at + 4)));
    }
    private (int Inst, Guid128 Id) ReadReference(ref DcReader r)
    {
        int at = r.Take(20);
        return (BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(at)), Guid128.Read(Data.AsSpan(at + 4)));
    }
    private DcReader Sub(int si, int ii) { var (o, s) = Instance(si, ii); return new DcReader(Data, o, s); }

    private DcValue Mat(int si, ref DcReader r, bool skipRefs) => MaterializeStruct(si, ref r, 0, new HashSet<Guid128>(), skipRefs);

    private void ExecValues(List<Segment> segs, int idx, ref DcReader r, List<DcValue> res, bool skipRefs)
    {
        if (idx >= segs.Count) return;
        var seg = segs[idx];
        bool leaf = idx == segs.Count - 1;
        if (seg.Position > 0) r.Take(PropertyByteOffset(seg.ContextStruct, seg.Position));
        if (seg.Conversion == CT.Attribute)
        {
            switch (seg.DataType)
            {
                case DT.Class:
                    if (leaf) res.Add(Mat(seg.Target ?? throw new QueryException("missing target"), ref r, skipRefs));
                    else ExecValues(segs, idx + 1, ref r, res, skipRefs);
                    return;
                case DT.StrongPointer:
                case DT.WeakPointer:
                    {
                        var (si, ii) = ReadPointer(ref r);
                        if (si == -1 && ii == -1) return;
                        if (!TypeMatches(si, seg.TypeFilter)) return;
                        var sub = Sub(si, ii);
                        if (leaf) res.Add(Mat(si, ref sub, skipRefs));
                        else ExecValues(segs, idx + 1, ref sub, res, skipRefs);
                        return;
                    }
                case DT.Reference:
                    {
                        var (inst, rid) = ReadReference(ref r);
                        if (inst == 0 && rid.IsEmpty) return;
                        if (RecordById(rid) is not Record tr) return;
                        var sub = Sub(tr.StructIndex, tr.InstanceIndex);
                        if (leaf) res.Add(Mat(tr.StructIndex, ref sub, skipRefs));
                        else ExecValues(segs, idx + 1, ref sub, res, skipRefs);
                        return;
                    }
                default:
                    if (leaf) res.Add(ReadPrimitive(ref r, seg.DataType));
                    return;
            }
        }
        int count = r.I32(), first = r.I32();
        switch (seg.DataType)
        {
            case DT.StrongPointer:
            case DT.WeakPointer:
                for (int i = 0; i < count; i++)
                {
                    var (si, ii) = seg.DataType == DT.StrongPointer ? Strong(first + i) : Weak(first + i);
                    if (si == -1 && ii == -1) continue;
                    if (!TypeMatches(si, seg.TypeFilter)) continue;
                    var sub = Sub(si, ii);
                    if (leaf) res.Add(Mat(si, ref sub, skipRefs));
                    else ExecValues(segs, idx + 1, ref sub, res, skipRefs);
                }
                return;
            case DT.Class:
                {
                    int tsi = seg.Target ?? throw new QueryException("missing target");
                    for (int i = 0; i < count; i++)
                    {
                        var sub = Sub(tsi, first + i);
                        if (leaf) res.Add(Mat(tsi, ref sub, skipRefs));
                        else ExecValues(segs, idx + 1, ref sub, res, skipRefs);
                    }
                    return;
                }
            default:
                if (leaf) for (int i = 0; i < count; i++) res.Add(ArrayPrimitive(first + i, seg.DataType));
                return;
        }
    }

    private DcValue MaterializeStruct(int si, ref DcReader r, int depth, HashSet<Guid128> visited, bool skipRefs)
    {
        var typeName = StructName(si);
        if (depth >= MaxMaterializeDepth)
        {
            r.Take((int)Structs[si].StructSize);
            return new DcValue.Obj(typeName, new(), null);
        }
        var props = AllPropertyIndices(si);
        var fields = new List<(string, DcValue)>(props.Length);
        foreach (var pi in props)
        {
            var prop = Props[pi];
            var name = PropName(pi);
            if (!IsKnownDT(prop.DataType) || !IsKnownCT(prop.ConversionType)) throw new QueryException("unknown type");
            var dt = (DT)prop.DataType;
            var v = (CT)prop.ConversionType != CT.Attribute
                ? MaterializeArray(dt, prop.StructIndex, ref r, depth, visited, skipRefs)
                : MaterializeAttribute(dt, prop.StructIndex, ref r, depth, visited, skipRefs);
            fields.Add((name, v));
        }
        return new DcValue.Obj(typeName, fields, null);
    }

    private DcValue MaterializeAttribute(DT dt, int propStruct, ref DcReader r, int depth, HashSet<Guid128> visited, bool skipRefs)
    {
        switch (dt)
        {
            case DT.Class: return MaterializeStruct(propStruct, ref r, depth + 1, visited, skipRefs);
            case DT.StrongPointer:
            case DT.WeakPointer:
                {
                    var (si, ii) = ReadPointer(ref r);
                    if (si == -1 && ii == -1) return DcValue.Null.I;
                    var sub = Sub(si, ii);
                    return MaterializeStruct(si, ref sub, depth + 1, visited, skipRefs);
                }
            case DT.Reference:
                {
                    var (inst, rid) = ReadReference(ref r);
                    if (inst == 0 && rid.IsEmpty) return DcValue.Null.I;
                    if (skipRefs) return new DcValue.Guid(rid);
                    if (!visited.Add(rid)) return new DcValue.Guid(rid);
                    if (depth >= MaxMaterializeDepth) return new DcValue.Guid(rid);   // (Rust も visited から外さない)
                    DcValue result;
                    if (RecordById(rid) is Record tr)
                    {
                        var sub = Sub(tr.StructIndex, tr.InstanceIndex);
                        var v = MaterializeStruct(tr.StructIndex, ref sub, depth + 1, visited, false);
                        result = v is DcValue.Obj o ? o with { RecordId = rid } : v;
                    }
                    else result = DcValue.Null.I;
                    visited.Remove(rid);
                    return result;
                }
            default: return ReadPrimitive(ref r, dt);
        }
    }

    private DcValue MaterializeArray(DT dt, int propStruct, ref DcReader r, int depth, HashSet<Guid128> visited, bool skipRefs)
    {
        int count = r.I32(), first = r.I32();
        var items = new List<DcValue>(Math.Max(0, Math.Min(count, 4096)));
        for (int i = 0; i < count; i++)
        {
            int idx = first + i;
            DcValue v;
            switch (dt)
            {
                case DT.Class:
                    {
                        var sub = Sub(propStruct, first + i);
                        v = MaterializeStruct(propStruct, ref sub, depth + 1, visited, skipRefs);
                        break;
                    }
                case DT.StrongPointer:
                case DT.WeakPointer:
                    {
                        var (si, ii) = dt == DT.StrongPointer ? Strong(idx) : Weak(idx);
                        if (si == -1 && ii == -1) v = DcValue.Null.I;
                        else { var sub = Sub(si, ii); v = MaterializeStruct(si, ref sub, depth + 1, visited, skipRefs); }
                        break;
                    }
                case DT.Reference:
                    {
                        var (inst, rid) = GetReference(idx);
                        if (inst == 0 && rid.IsEmpty) v = DcValue.Null.I;
                        else if (skipRefs) v = new DcValue.Guid(rid);
                        else if (!visited.Add(rid)) v = new DcValue.Guid(rid);
                        else if (depth >= MaxMaterializeDepth) v = new DcValue.Guid(rid);
                        else
                        {
                            if (RecordById(rid) is Record tr)
                            {
                                var sub = Sub(tr.StructIndex, tr.InstanceIndex);
                                var x = MaterializeStruct(tr.StructIndex, ref sub, depth + 1, visited, false);
                                v = x is DcValue.Obj o ? o with { RecordId = rid } : x;
                            }
                            else v = DcValue.Null.I;
                            visited.Remove(rid);
                        }
                        break;
                    }
                default: v = ArrayPrimitive(idx, dt); break;
            }
            items.Add(v);
        }
        return new DcValue.Arr(items);
    }

    private DcValue ReadPrimitive(ref DcReader r, DT dt)
    {
        switch (dt)
        {
            case DT.Boolean: return new DcValue.Bool(Data[r.Take(1)] != 0);
            case DT.SByte: return new DcValue.Int8((sbyte)Data[r.Take(1)]);
            case DT.Int16: return new DcValue.Int16(BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan(r.Take(2))));
            case DT.Int32: return new DcValue.Int32(BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(r.Take(4))));
            case DT.Int64: return new DcValue.Int64(BinaryPrimitives.ReadInt64LittleEndian(Data.AsSpan(r.Take(8))));
            case DT.Byte: return new DcValue.UInt8(Data[r.Take(1)]);
            case DT.UInt16: return new DcValue.UInt16(BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(r.Take(2))));
            case DT.UInt32: return new DcValue.UInt32(BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(r.Take(4))));
            case DT.UInt64: return new DcValue.UInt64(BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(r.Take(8))));
            case DT.Single: return new DcValue.Float(BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan(r.Take(4))));
            case DT.Double: return new DcValue.Double(BinaryPrimitives.ReadDoubleLittleEndian(Data.AsSpan(r.Take(8))));
            case DT.String: return new DcValue.Str(String1(BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(r.Take(4)))));
            case DT.Locale: return new DcValue.Locale(String1(BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(r.Take(4)))));
            case DT.EnumChoice: return new DcValue.Enum(String1(BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(r.Take(4)))));
            case DT.Guid: return new DcValue.Guid(Guid128.Read(Data.AsSpan(r.Take(16))));
            default: throw new QueryException("unknown type");
        }
    }

    private DcValue ArrayPrimitive(int idx, DT dt) => dt switch
    {
        DT.Boolean => new DcValue.Bool(GetBool(idx)),
        DT.SByte => new DcValue.Int8(GetInt8(idx)),
        DT.Int16 => new DcValue.Int16(GetInt16(idx)),
        DT.Int32 => new DcValue.Int32(GetInt32(idx)),
        DT.Int64 => new DcValue.Int64(GetInt64(idx)),
        DT.Byte => new DcValue.UInt8(GetUInt8(idx)),
        DT.UInt16 => new DcValue.UInt16(GetUInt16(idx)),
        DT.UInt32 => new DcValue.UInt32(GetUInt32(idx)),
        DT.UInt64 => new DcValue.UInt64(GetUInt64(idx)),
        DT.Single => new DcValue.Float(GetSingle(idx)),
        DT.Double => new DcValue.Double(GetDouble(idx)),
        DT.String => new DcValue.Str(String1(StringIdValue(idx))),
        DT.Locale => new DcValue.Locale(String1(LocaleValue(idx))),
        DT.EnumChoice => new DcValue.Enum(String1(EnumValue(idx))),
        DT.Guid => new DcValue.Guid(GetGuid(idx)),
        _ => throw new QueryException("unknown type"),
    };
}
