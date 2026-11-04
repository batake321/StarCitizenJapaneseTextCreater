using System.Buffers.Binary;
using System.Text;

namespace StarCitizenJapaneseTextCreater;

// CryXmlB (CryEngine の二進の XML) の読み取り。StarBreaker v0.2.2 (crates/starbreaker-cryxml の cryxml.rs・types.rs) の移植。
//   ・先頭の印「CryXmlB\0」の後のヘッダ (9 個の u32) の後に、節 (28 バイト) → 子の番号 (i32) → 属性 (8 バイト) → 文字列の順に続けて読む
//     (ヘッダの各表の位置の欄は使わない。starbreaker と同じ)
//   ・文字列は 0 で終わる。UTF-8 として読めなければ空 (get_string)
//   ・XML の文字への書き出し (Display) は 2 文字の字下げ、属性の値の 5 つの特殊文字だけを置き換え、子の無い節は「 />」
internal sealed class CryXmlB
{
    public const int NodeSize = 28;
    private readonly byte[] _data;
    private readonly int _nodes, _children, _attrs, _strings;
    private readonly int _nodeCount, _childCount, _attrCount, _stringSize;

    public static bool IsCryXmlB(ReadOnlySpan<byte> data) => data.Length > 8 && data[..8].SequenceEqual("CryXmlB\0"u8);

    private CryXmlB(byte[] data, int nodes, int nodeCount, int children, int childCount, int attrs, int attrCount, int strings, int stringSize)
    {
        _data = data; _nodes = nodes; _nodeCount = nodeCount; _children = children; _childCount = childCount; _attrs = attrs; _attrCount = attrCount; _strings = strings; _stringSize = stringSize;
    }

    // 読めなければ例外 (InvalidDataException)
    public static CryXmlB Parse(byte[] data) => Parse(data, 0, data.Length);
    public static CryXmlB Parse(byte[] data, int start, int length)
    {
        if (length < 8 || !data.AsSpan(start, 8).SequenceEqual("CryXmlB\0"u8)) throw new InvalidDataException("CryXmlB の印がありません");
        int p = start + 8, end = start + length;
        if (p + 36 > end) throw new InvalidDataException("CryXmlB のヘッダが切れています");
        uint nodeCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 8));
        uint attrCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 16));
        uint childCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 24));
        uint stringSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 32));
        p += 36;
        long nodes = p, children = nodes + (long)nodeCount * NodeSize, attrs = children + (long)childCount * 4, strings = attrs + (long)attrCount * 8, stringsEnd = strings + stringSize;
        if (stringsEnd > end) throw new InvalidDataException("CryXmlB が切れています");
        if (end - start > 0 && data.Length > 0 && start != 0)
        {
            // 範囲を写す (位置を 0 から数えるため)
            var copy = data.AsSpan(start, length).ToArray();
            return new CryXmlB(copy, (int)(nodes - start), (int)nodeCount, (int)(children - start), (int)childCount, (int)(attrs - start), (int)attrCount, (int)(strings - start), (int)stringSize);
        }
        return new CryXmlB(data, (int)nodes, (int)nodeCount, (int)children, (int)childCount, (int)attrs, (int)attrCount, (int)strings, (int)stringSize);
    }

    public static CryXmlB? TryParse(byte[] data)
    {
        try { return Parse(data); } catch (InvalidDataException) { return null; }
    }

    public int NodeCount => _nodeCount;
    public int Root => 0;

    private ReadOnlySpan<byte> Node(int i)
    {
        if ((uint)i >= (uint)_nodeCount) throw new InvalidDataException($"CryXmlB の節の番号が範囲の外です ({i})");
        return _data.AsSpan(_nodes + i * NodeSize, NodeSize);
    }

    public string Tag(int node) => GetString(BinaryPrimitives.ReadUInt32LittleEndian(Node(node)));

    public int AttributeCount(int node) => BinaryPrimitives.ReadUInt16LittleEndian(Node(node)[8..]);
    public int ChildCount(int node) => BinaryPrimitives.ReadUInt16LittleEndian(Node(node)[10..]);

    // 属性 (名前, 値) を元の順に
    public IEnumerable<(string Key, string Value)> Attributes(int node)
    {
        var n = Node(node);
        int first = BinaryPrimitives.ReadInt32LittleEndian(n[16..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(n[8..]);
        if (count > 0 && ((uint)first > (uint)_attrCount || first + count > _attrCount)) throw new InvalidDataException("CryXmlB の属性の範囲が外れています");
        for (int k = 0; k < count; k++)
        {
            int a = _attrs + (first + k) * 8;
            yield return (GetString(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(a))), GetString(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(a + 4))));
        }
    }

    // 属性を表にする (同じ名前が 2 つあれば後のもの。HashMap に collect したものと同じ)
    public Dictionary<string, string> AttributeMap(int node)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in Attributes(node)) d[k] = v;
        return d;
    }

    // 直下の子の節の番号を元の順に
    public IEnumerable<int> Children(int node)
    {
        var n = Node(node);
        int first = BinaryPrimitives.ReadInt32LittleEndian(n[20..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(n[10..]);
        if (count > 0 && ((uint)first > (uint)_childCount || first + count > _childCount)) throw new InvalidDataException("CryXmlB の子の範囲が外れています");
        for (int k = 0; k < count; k++) yield return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_children + (first + k) * 4));
    }

    public string GetString(uint offset)
    {
        if (offset > (uint)_stringSize) throw new InvalidDataException("CryXmlB の文字列の位置が範囲の外です");
        var s = _data.AsSpan(_strings + (int)offset, _stringSize - (int)offset);
        int len = s.IndexOf((byte)0);
        if (len < 0) len = s.Length;
        if (len == 0) return "";
        try { return Utf8Strict.GetString(s[..len]); }
        catch (DecoderFallbackException) { return ""; }
    }

    private static readonly UTF8Encoding Utf8Strict = new(false, true);

    // XML の文字にする (fmt::Display と同じ形)
    public string ToXml()
    {
        var sb = new StringBuilder();
        WriteNode(sb, 0, 0);
        return sb.ToString();
    }

    private void WriteNode(StringBuilder sb, int node, int depth)
    {
        var indent = new string(' ', depth * 2);
        var tag = Tag(node);
        sb.Append(indent).Append('<').Append(tag);
        foreach (var (k, v) in Attributes(node)) { sb.Append(' ').Append(k).Append("=\""); EscapeAttr(sb, v); sb.Append('"'); }
        if (ChildCount(node) == 0) { sb.Append(" />\n"); return; }
        sb.Append(">\n");
        foreach (var c in Children(node)) WriteNode(sb, c, depth + 1);
        sb.Append(indent).Append("</").Append(tag).Append(">\n");
    }

    private static void EscapeAttr(StringBuilder sb, string s)
    {
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(ch); break;
            }
        }
    }
}
