using System.Buffers.Binary;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

// 船の 3D に入っていなかった区画 (2026-11-05、利用者の報告「床がない。船舶は表示しない部品があったらダメだよね」)。
// 船の書き出しは、開いた船そのものの区画の一覧 (Components[VehicleComponentParams].objectContainers) だけを読んでいたので、次の 2 つが出ていなかった:
//   1. 取り付けられた子の乗り物が自分で持つ区画 (例: DRAK_Ironclad の操縦席のある DRAK_Command_Module の command_module_interior。床・壁・キャノピー)
//   2. 区画の中の TransitManager (エレベーター・リフトの管理) の carriageInterior が指すかごの区画 (例: Ironclad の cargo_lift・cargo_lift_02・rear_lift。台)
// これから取り込む船には初めから入れる (Ctx.Extras。1 は Flatten が子の Payload.Interiors に入れて ShipGlb.AttachChild が子のノードの下に置く。
//   2 は ShipInteriors.Load の carriages。かごを置く階は ShipInteriors.CarriageRefs の説明)。
// 前の版で取り込んだ船 (横の記録の ShipExportVersion が 2 より古い) は、取り込み直さずに、足りない区画だけの GLB をここ (ExportAddon) で作って、
//   ビューアが船のモデルに重ねる (ShipAddons.GetAsync・viewer.html の addonInit)。
// 船の構成の書き出し (DumpHierarchy。照合のハッシュ・見積もり) には入れていない (入れるとハッシュが変わり、次のパッチの照合で、こうした船が全部取り直しになるため)
internal static partial class ShipExport
{
    // 既定のロードアウトの品 (子・孫…) を平らに並べる (取り付け口の情報・SubGeometry は引かない。配る DB の表 ship_addons を作るときに全部の船で使う)
    internal static List<LoadoutNode> LoadoutFlat(EntityIndex idx, DataCoreDb.Record record)
    {
        var db = idx.Db;
        var children = new List<LoadoutNode>();
        var cp = TryCompile(db, record.StructIndex, "Components[SEntityComponentDefaultLoadoutParams]", DataCoreDb.AnyLeaf);
        if (cp != null && TryValues(db, cp, record, true) is { } comps)
            foreach (var comp in comps)
                if (DcValue.ObjField(comp, "loadout") is { } lo) CollectEntries(idx, lo, children, 0);
        var flat = new List<LoadoutNode>();
        void Walk(List<LoadoutNode> l) { foreach (var n in l) { flat.Add(n); Walk(n.Children); } }
        Walk(children);
        return flat;
    }

    // 足りない区画だけの GLB を outPath に書く (船の GLB と同じ座標・同じ向き: いちばん上のノードは同じ CryEngine_Z_up)。書いた区画の名前を返す。
    //   shipGlbPath: 重ねる先の船の GLB (取り込み済みのもの)。子の乗り物の区画は、この GLB の中のその子のノード (名前 = 子の品の名前) の置き方の下に入れる
    //     (船の書き出しが子の部品を置いた所そのもの)。そのノードが無い子の区画は入れない。
    //   色: 船の書き出しと同じ (本体のパレット + 既定のロードアウトの塗装。noPalette なら色付けなし)。塗装の切り替えの表は当たらない (ShipAddons の頭の説明)。
    //   足す区画が 1 つも無ければ ExportException
    public static List<string> ExportAddon(P4kArchive p4k, DataCoreDb db, string name, string shipGlbPath, string outPath, bool noPalette, Action<string>? log = null, CancellationToken ct = default)
    {
        var ctx = new Ctx { P4k = p4k, Db = db, Log = log, Ct = ct, Extras = true };
        var record = FindRecord(db, name, out _) ?? throw new ExportException($"no EntityClassDefinition records matching '{name}'");
        var idx = IndexOf(db);
        var tree = ResolveLoadout(idx, record);
        var (rootPal, _) = PaintOverride(ctx, record, tree, QueryTintPalette(db, record), null);
        ct.ThrowIfCancellationRequested();

        var names = new List<string>();
        // 1. 子の乗り物の区画
        var kids = new List<(string Name, DataCoreDb.Record Record)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(List<LoadoutNode> l)
        {
            foreach (var n in l)
            {
                if (seen.Add(n.EntityName) && ShipInteriors.HasContainers(db, n.Record)) kids.Add((n.EntityName, n.Record));
                Walk(n.Children);
            }
        }
        Walk(tree.Children);
        var extras = new List<(string Name, float[] Matrix, ShipInteriors.Loaded Interiors)>();
        if (kids.Count > 0)
        {
            var world = GlbNodeWorld(shipGlbPath, kids.Select(k => k.Name).ToHashSet(StringComparer.Ordinal));
            foreach (var (kn, kr) in kids)
            {
                ct.ThrowIfCancellationRequested();
                if (!world.TryGetValue(kn, out var m)) { log?.Invoke($"addon: node '{kn}' not found in the ship GLB (skipped)"); continue; }
                var li = ShipInteriors.Load(ctx, kr, carriages: true);
                if (li.Containers.Count == 0) continue;
                extras.Add((kn, m, li));
                names.AddRange(li.Containers.Select(c => $"{kn}:{c.Name}"));
            }
        }
        // 2. 船そのものの区画のかご
        var carr = ShipInteriors.Load(ctx, record, carriages: true, carriagesOnly: true);
        names.AddRange(carr.Containers.Select(c => c.Name));
        if (names.Count == 0) throw new ExportException($"no extra sections: {name}");
        log?.Invoke($"Assembling addon: {names.Count} sections ({string.Join(", ", names)})");
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        ShipGlb.Write(ctx, new ShipGlb.Input { RootMesh = null, RootPalette = rootPal, Interiors = carr, ExtraInteriors = extras, NoPalette = noPalette }, fs);
        return names;
    }

    // GLB の中の、名前が names のどれかのノード (同じ名前は最初のもの) の置き方 (船の座標。いちばん上の CryEngine_Z_up の行列は掛けない。列優先 16)
    private static Dictionary<string, float[]> GlbNodeWorld(string glbPath, HashSet<string> names)
    {
        var res = new Dictionary<string, float[]>(StringComparer.Ordinal);
        byte[] json;
        using (var fs = new FileStream(glbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Span<byte> head = stackalloc byte[20];
            fs.ReadExactly(head);
            if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0x46546C67 || BinaryPrimitives.ReadUInt32LittleEndian(head[16..]) != 0x4E4F534A) throw new InvalidDataException("GLB の頭が違います");
            json = new byte[BinaryPrimitives.ReadUInt32LittleEndian(head[12..])];
            fs.ReadExactly(json);
        }
        int end = json.Length;
        while (end > 0 && json[end - 1] == (byte)' ') end--;
        using var doc = JsonDocument.Parse(json.AsMemory(0, end), new JsonDocumentOptions { MaxDepth = 256 });
        var root = doc.RootElement;
        if (!root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return res;
        int n = nodes.GetArrayLength();
        var parent = new int[n]; Array.Fill(parent, -1);
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        int i = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                foreach (var c in ch.EnumerateArray()) if (c.TryGetInt32(out var ci) && ci >= 0 && ci < n) parent[ci] = i;
            if (node.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String && nm.GetString() is { } s && names.Contains(s)) found.TryAdd(s, i);
            i++;
        }
        // いちばん上のノード (場面の最初のノード。CryEngine_Z_up)
        int top = -1;
        if (root.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array && scenes.GetArrayLength() > 0
            && scenes[0].TryGetProperty("nodes", out var sn) && sn.ValueKind == JsonValueKind.Array && sn.GetArrayLength() > 0 && sn[0].TryGetInt32(out var t0)) top = t0;
        float[] Local(int k)
        {
            var node = nodes[k];
            if (node.TryGetProperty("matrix", out var mt) && mt.ValueKind == JsonValueKind.Array && mt.GetArrayLength() == 16)
            {
                var m = new float[16]; int j = 0;
                foreach (var v in mt.EnumerateArray()) m[j++] = v.GetSingle();
                return m;
            }
            float[] tr = [0, 0, 0], q = [0, 0, 0, 1];
            if (node.TryGetProperty("translation", out var tv) && tv.ValueKind == JsonValueKind.Array && tv.GetArrayLength() == 3) { int j = 0; foreach (var v in tv.EnumerateArray()) tr[j++] = v.GetSingle(); }
            if (node.TryGetProperty("rotation", out var rv) && rv.ValueKind == JsonValueKind.Array && rv.GetArrayLength() == 4) { int j = 0; foreach (var v in rv.EnumerateArray()) q[j++] = v.GetSingle(); }
            return Glam.FromScaleRotationTranslation(1, 1, 1, q[0], q[1], q[2], q[3], tr[0], tr[1], tr[2]);
        }
        foreach (var (name, k0) in found)
        {
            var chain = new List<int>();
            for (int k = k0; k >= 0 && k != top && chain.Count <= n; k = parent[k]) chain.Add(k);
            var m = (float[])Glam.Identity.Clone();
            for (int c = chain.Count - 1; c >= 0; c--) m = Glam.Mul(m, Local(chain[c]));
            res[name] = m;
        }
        return res;
    }
}
