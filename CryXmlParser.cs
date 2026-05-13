using System.Text;
using System.Xml.Linq;

namespace StarCitizenJapaneseTextCreater;

public static class CryXmlParser
{
    public static XDocument Parse(string filePath)
    {
        var data = File.ReadAllBytes(filePath);

        if (data.Length < 8 || Encoding.ASCII.GetString(data, 0, 7) != "CryXmlB")
            return XDocument.Load(filePath);

        return ParseBinary(data);
    }

    private static XDocument ParseBinary(byte[] data)
    {
        int offset = 8;

        var fileHeader = ReadFileHeader(data, ref offset);

        var nodes = new CryNode[fileHeader.NodeCount];
        for (int i = 0; i < fileHeader.NodeCount; i++)
            nodes[i] = ReadNode(data, ref offset);

        var attrs = new CryAttribute[fileHeader.AttrCount];
        for (int i = 0; i < fileHeader.AttrCount; i++)
            attrs[i] = ReadAttribute(data, ref offset);

        var childIndices = new int[fileHeader.ChildCount];
        for (int i = 0; i < fileHeader.ChildCount; i++)
        {
            childIndices[i] = BitConverter.ToInt32(data, offset);
            offset += 4;
        }

        var strings = new Dictionary<int, string>();
        int strStart = offset;
        int strEnd = strStart + fileHeader.StringSize;
        int pos = strStart;
        while (pos < strEnd)
        {
            int relOffset = pos - strStart;
            int end = pos;
            while (end < strEnd && data[end] != 0) end++;
            strings[relOffset] = Encoding.UTF8.GetString(data, pos, end - pos);
            pos = end + 1;
        }

        string GetStr(int off) => strings.TryGetValue(off, out var s) ? s : "";

        XElement BuildElement(int nodeIdx)
        {
            var node = nodes[nodeIdx];
            var tag = GetStr(node.TagStringOffset);
            if (string.IsNullOrEmpty(tag)) tag = "Unknown";
            var el = new XElement(SanitizeXmlName(tag));

            var content = GetStr(node.ContentStringOffset);
            if (!string.IsNullOrEmpty(content))
                el.Value = content;

            for (int i = 0; i < node.AttrCount; i++)
            {
                var attr = attrs[node.FirstAttrIndex + i];
                var key = GetStr(attr.KeyStringOffset);
                var val = GetStr(attr.ValueStringOffset);
                if (!string.IsNullOrEmpty(key))
                {
                    try { el.SetAttributeValue(SanitizeXmlName(key), val); }
                    catch { }
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
            {
                var childIdx = childIndices[node.FirstChildIndex + i];
                el.Add(BuildElement(childIdx));
            }

            return el;
        }

        var root = BuildElement(0);
        return new XDocument(root);
    }

    private static string SanitizeXmlName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_";
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i == 0 && !char.IsLetter(c) && c != '_')
                sb.Append('_');
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.Length > 0 ? sb.ToString() : "_";
    }

    private static FileHeader ReadFileHeader(byte[] data, ref int offset)
    {
        var h = new FileHeader
        {
            FileSize = BitConverter.ToInt32(data, offset),
            NodeTableOffset = BitConverter.ToInt32(data, offset + 4),
            NodeCount = BitConverter.ToInt32(data, offset + 8),
            AttrTableOffset = BitConverter.ToInt32(data, offset + 12),
            AttrSize = BitConverter.ToInt32(data, offset + 16),
            ChildTableOffset = BitConverter.ToInt32(data, offset + 20),
            ChildCount = BitConverter.ToInt32(data, offset + 24),
            StringTableOffset = BitConverter.ToInt32(data, offset + 28),
            StringSize = BitConverter.ToInt32(data, offset + 32),
        };
        h.AttrCount = h.AttrSize / 8;
        offset = 8 + h.NodeTableOffset;
        return h;
    }

    private static CryNode ReadNode(byte[] data, ref int offset)
    {
        var n = new CryNode
        {
            TagStringOffset = BitConverter.ToInt32(data, offset),
            ContentStringOffset = BitConverter.ToInt32(data, offset + 4),
            AttrCount = BitConverter.ToUInt16(data, offset + 8),
            ChildCount = BitConverter.ToUInt16(data, offset + 10),
            ParentIndex = BitConverter.ToInt32(data, offset + 12),
            FirstAttrIndex = BitConverter.ToInt32(data, offset + 16),
            FirstChildIndex = BitConverter.ToInt32(data, offset + 20),
        };
        offset += 28;
        return n;
    }

    private static CryAttribute ReadAttribute(byte[] data, ref int offset)
    {
        var a = new CryAttribute
        {
            KeyStringOffset = BitConverter.ToInt32(data, offset),
            ValueStringOffset = BitConverter.ToInt32(data, offset + 4),
        };
        offset += 8;
        return a;
    }

    private struct FileHeader
    {
        public int FileSize, NodeTableOffset, NodeCount;
        public int AttrTableOffset, AttrSize, AttrCount;
        public int ChildTableOffset, ChildCount;
        public int StringTableOffset, StringSize;
    }

    private struct CryNode
    {
        public int TagStringOffset, ContentStringOffset;
        public ushort AttrCount, ChildCount;
        public int ParentIndex, FirstAttrIndex, FirstChildIndex;
    }

    private struct CryAttribute
    {
        public int KeyStringOffset, ValueStringOffset;
    }
}
