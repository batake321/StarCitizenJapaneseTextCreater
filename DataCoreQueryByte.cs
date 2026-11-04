using System.Buffers.Binary;

namespace StarCitizenJapaneseTextCreater;

// query_single::<u8> (execute_segments_first の、終わりが Byte のもの)。パレットの色 (SocpakGlb) に使う
internal sealed partial class DataCoreDb
{
    public byte? QueryFirstByte(CompiledPath path, Record record)
    {
        if (record.StructIndex != path.RootStruct) throw new QueryException("struct mismatch");
        var (o, s) = Instance(record.StructIndex, record.InstanceIndex);
        return FirstByte(path.Segments, 0, o, s);
    }

    private byte? FirstByte(List<Segment> segs, int idx, int start, int size)
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
                    if (leaf) throw new QueryException("unknown type");
                    return FirstByte(segs, idx + 1, p, end - p);
                case DT.StrongPointer:
                case DT.WeakPointer:
                    {
                        Adv(8);
                        int si = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(p - 8)), ii = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(p - 4));
                        if (si == -1 && ii == -1) return null;
                        if (seg.TypeFilter is int tf && !Inherits(si, tf)) return null;
                        if (leaf) throw new QueryException("unknown type");
                        var (io, isz) = Instance(si, ii);
                        return FirstByte(segs, idx + 1, io, isz);
                    }
                case DT.Reference:
                    {
                        Adv(20);
                        int inst = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(p - 20));
                        var rid = Guid128.Read(Data.AsSpan(p - 16));
                        if (inst == 0 && rid.IsEmpty) return null;
                        if (RecordById(rid) is not Record tr) return null;
                        if (leaf) throw new QueryException("unknown type");
                        var (io, isz) = Instance(tr.StructIndex, tr.InstanceIndex);
                        return FirstByte(segs, idx + 1, io, isz);
                    }
                default:
                    if (!leaf) return null;
                    Adv(1);
                    return Data[p - 1];
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
                    if (leaf) throw new QueryException("unknown type");
                    var (io, isz) = Instance(si, ii);
                    if (FirstByte(segs, idx + 1, io, isz) is byte v) return v;
                }
                return null;
            case DT.Class:
                {
                    int tsi = seg.Target ?? throw new QueryException("missing target");
                    for (int i = 0; i < count; i++)
                    {
                        if (leaf) throw new QueryException("unknown type");
                        var (io, isz) = Instance(tsi, first + i);
                        if (FirstByte(segs, idx + 1, io, isz) is byte v) return v;
                    }
                    return null;
                }
            default:
                if (leaf && count > 0) return GetUInt8(first);
                return null;
        }
    }
}
