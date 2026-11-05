using System.Globalization;
using System.Numerics;
using System.Text;

namespace StarCitizenJapaneseTextCreater;

// `entity export <NAME> <out> --dump-hierarchy` の自前の版 (2026-11-04)。移植元 pipeline.rs dump_hierarchy・dump_nmc_nodes。
// 文字は Rust の {:?} (str の Debug)、数は {:.N} (正確な値を偶数丸め) と同じ書き方にする。
// props の並びは Rust では HashMap の並び (出すたびに変わる) なので、ここでは読んだ順。照合のハッシュ (Ship3DEstimator.CanonicalHash) はキーを並べ替えるので影響しない
internal static partial class ShipExport
{
    // item: Export の item と同じ (船でない品)。品で主の形の代わりに別の形を本体にするとき (ItemShape) は、root の geometry と root_nmc をその形のものにする
    // (照合の元ファイルの一覧に、実際に読む形が入るように。船は今までどおり)
    public static (string Json, string ResolvedName) DumpHierarchy(P4kArchive p4k, DataCoreDb db, string name, CancellationToken ct = default, bool item = false)
    {
        var ctx = new Ctx { P4k = p4k, Db = db, Ct = ct, Item = item };
        var record = FindRecord(db, name, out _) ?? throw new ExportException($"no EntityClassDefinition records matching '{name}'");
        var idx = IndexOf(db);
        var tree = ResolveLoadout(idx, record);
        var o = new StringBuilder("{\n");
        var gc = TryCompile(db, record.StructIndex, "Components[SGeometryResourceParams].Geometry.Geometry.Geometry.path", DataCoreDb.StringLeaf);
        var rootGeom = (gc == null ? null : TryString(db, gc, record)) ?? "";
        var mc = TryCompile(db, record.StructIndex, "Components[SGeometryResourceParams].Geometry.Geometry.Material.path", DataCoreDb.StringLeaf);
        var rootMtl = (mc == null ? null : TryString(db, mc, record)) ?? "";
        if (item && ItemShape(p4k, idx, record) is { } shape) (rootGeom, rootMtl) = shape;
        o.Append("  \"root\": {\n    \"entity\": ").Append(Dbg(tree.EntityName)).Append(",\n    \"geometry\": ").Append(Dbg(rootGeom)).Append("\n  },\n");
        DumpNmc(o, "root_nmc", p4k, rootGeom, rootMtl);
        var invisible = InvisiblePorts(ctx, record);
        o.Append("  \"loadout\": [\n");
        DumpLoadout(o, p4k, tree.Children, tree.EntityName, 0, invisible, ct);
        o.Append("  ],\n");
        var interiors = ShipInteriors.Load(ctx, record);
        o.Append("  \"interiors\": [\n");
        foreach (var c in interiors.Containers)
        {
            o.Append("    {\n      \"container\": ").Append(Dbg(c.Name)).Append(",\n      \"meshes\": [\n");
            foreach (var (ix, t) in c.Placements)
            {
                var e = interiors.Cgfs[ix];
                float sx = MathF.Sqrt(t[0] * t[0] + t[1] * t[1] + t[2] * t[2]);
                float sy = MathF.Sqrt(t[4] * t[4] + t[5] * t[5] + t[6] * t[6]);
                float sz = MathF.Sqrt(t[8] * t[8] + t[9] * t[9] + t[10] * t[10]);
                o.Append("        {\"cgf\": ").Append(Dbg(e.Path)).Append(", \"pos\": [").Append(Fixed(t[12], 2)).Append(", ").Append(Fixed(t[13], 2)).Append(", ").Append(Fixed(t[14], 2))
                 .Append("], \"scale\": [").Append(Fixed(sx, 3)).Append(", ").Append(Fixed(sy, 3)).Append(", ").Append(Fixed(sz, 3)).Append(']');
                if (e.Mtl != null) o.Append(", \"material\": ").Append(Dbg(e.Mtl));
                o.Append("},\n");
            }
            o.Append("      ],\n      \"lights\": ").Append(c.Lights.Count.ToString(CultureInfo.InvariantCulture)).Append("\n    },\n");
        }
        o.Append("  ]\n}\n");
        return (o.ToString(), db.String2(record.NameOffset));
    }

    private static void DumpNmc(StringBuilder o, string key, P4kArchive p4k, string geom, string mtl)
    {
        if (geom.Length == 0) return;
        var (nmc, _) = NmcAndMaterial(p4k, geom, mtl);
        if (nmc == null) return;
        o.Append("  ").Append(Dbg(key)[0..]).Append(": [\n");
        foreach (var n in nmc)
        {
            o.Append("    {\"node\": ").Append(Dbg(n.Name)).Append(", \"type\": ").Append(n.GeometryType.ToString(CultureInfo.InvariantCulture));
            var b = n.BoneToWorld;
            bool id = MathF.Abs(b[0] - 1.0f) < 0.001f && MathF.Abs(b[5] - 1.0f) < 0.001f && MathF.Abs(b[10] - 1.0f) < 0.001f
                && MathF.Abs(b[3]) < 0.001f && MathF.Abs(b[7]) < 0.001f && MathF.Abs(b[11]) < 0.001f
                && MathF.Abs(b[1]) < 0.001f && MathF.Abs(b[2]) < 0.001f && MathF.Abs(b[4]) < 0.001f && MathF.Abs(b[6]) < 0.001f
                && MathF.Abs(b[8]) < 0.001f && MathF.Abs(b[9]) < 0.001f;
            if (!id)
            {
                o.Append(", \"bone_to_world\": [");
                for (int r = 0; r < 3; r++)
                {
                    if (r > 0) o.Append(',');
                    o.Append('[').Append(Fixed(b[r * 4], 4)).Append(',').Append(Fixed(b[r * 4 + 1], 4)).Append(',').Append(Fixed(b[r * 4 + 2], 4)).Append(',').Append(Fixed(b[r * 4 + 3], 4)).Append(']');
                }
                o.Append(']');
            }
            if (n.Props.Count > 0) o.Append(", \"props\": {").Append(string.Join(", ", n.Props.Select(p => Dbg(p.Key) + ": " + Dbg(p.Value)))).Append('}');
            o.Append("},\n");
        }
        o.Append("  ],\n");
    }

    private static void DumpLoadout(StringBuilder o, P4kArchive p4k, List<LoadoutNode> nodes, string parent, int depth, HashSet<string> invisible, CancellationToken ct)
    {
        var ind = new string(' ', 4 * (depth + 1));
        foreach (var n in nodes)
        {
            ct.ThrowIfCancellationRequested();
            var geom = n.GeometryPath ?? "";
            o.Append(ind).Append("{\n");
            o.Append(ind).Append("  \"entity\": ").Append(Dbg(n.EntityName)).Append(",\n");
            o.Append(ind).Append("  \"port\": ").Append(Dbg(n.ItemPortName)).Append(",\n");
            o.Append(ind).Append("  \"parent\": ").Append(Dbg(parent)).Append(",\n");
            if (invisible.Contains(n.ItemPortName)) o.Append(ind).Append("  \"invisible\": true,\n");
            o.Append(ind).Append("  \"geometry\": ").Append(Dbg(geom));
            if (geom.Length > 0)
            {
                var (nmc, _) = NmcAndMaterial(p4k, geom, n.MaterialPath ?? "");
                if (nmc != null)
                {
                    var withProps = nmc.Where(x => x.Props.Count > 0).ToList();
                    if (withProps.Count > 0)
                    {
                        o.Append(",\n").Append(ind).Append("  \"nmc_properties\": [\n");
                        foreach (var x in withProps)
                            o.Append(ind).Append("    {\"node\": ").Append(Dbg(x.Name)).Append(", \"props\": {").Append(string.Join(", ", x.Props.Select(p => Dbg(p.Key) + ": " + Dbg(p.Value)))).Append("}},\n");
                        o.Append(ind).Append("  ]");
                    }
                }
            }
            if (n.Children.Count > 0)
            {
                o.Append(",\n").Append(ind).Append("  \"children\": [\n");
                DumpLoadout(o, p4k, n.Children, n.EntityName, depth + 2, invisible, ct);
                o.Append(ind).Append("  ]\n");
            }
            else o.Append('\n');
            o.Append(ind).Append("},\n");
        }
    }

    // Rust の str の Debug ("…"。\t \r \n \\ \" \0 を逃がし、他の制御文字は \u{XX})
    internal static string Dbg(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (ch < 0x20 || ch == 0x7F) sb.Append("\\u{").Append(((int)ch).ToString("x", CultureInfo.InvariantCulture)).Append('}');
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // Rust の {:.N} (f32 の正確な値を小数 N 桁に。ちょうど半分は偶数へ。負の 0 は「-」を付ける)
    internal static string Fixed(float f, int prec)
    {
        if (float.IsNaN(f)) return "NaN";
        if (float.IsInfinity(f)) return f > 0 ? "inf" : "-inf";
        double d = f;
        bool neg = double.IsNegative(d);
        long bits = BitConverter.DoubleToInt64Bits(Math.Abs(d));
        int exp = (int)((bits >> 52) & 0x7FF);
        long man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else man |= 1L << 52;
        exp -= 1075;   // 値 = man × 2^exp
        // 値 × 10^prec を割り算で整数と余りに
        BigInteger num = man * BigInteger.Pow(10, prec), den = BigInteger.One;
        if (exp >= 0) num <<= exp; else den <<= -exp;
        var q = BigInteger.DivRem(num, den, out var rem);
        var twice = rem * 2;
        if (twice > den || (twice == den && !q.IsEven)) q += 1;
        var digits = q.ToString(CultureInfo.InvariantCulture);
        if (prec > 0)
        {
            if (digits.Length <= prec) digits = new string('0', prec - digits.Length + 1) + digits;
            digits = digits[..^prec] + "." + digits[^prec..];
        }
        return (neg ? "-" : "") + digits;
    }
}
