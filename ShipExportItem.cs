namespace StarCitizenJapaneseTextCreater;

// 武器・装備などの品 (船以外の EntityClassDefinition) の 3D (2026-11-04、利用者の要望「武器の3D出ないけど？」)。
// 書き出しは船と同じ ShipExport.Export (starbreaker の entity export と同じ流れ。船以外の entity も同じ assemble_glb_with_loadout で書く)。
// 品だけの決まり (Export・ExportJson・DumpHierarchy の item = true。Ctx.Item。船は今までどおり。2026-11-05、利用者の報告で足した):
//   1. CDF の Model File が形を持つとき、それを本体にする (ShipExport.cs の ResolveGeometryFiles・LoadModelBody・AddAttachments。「CQ7 の 3D が、CQ7 マガジンとマズルだけになってる」)
//   2. 主の形が箱の品は、着る形・しまってある形を本体にする (下の ItemShape。「アーマーを選んだのにボックス」)
//   (面を隠す決まりのデカールの見分けを語で見る直し (「Zeus Exploration Backpack は、何も出ない」) は、船・ステーションと同じ決まりにした。SocpakGlb.SubMat.IsDecal)
// ここは、その品に形があるか (Export が「形が無い」で失敗しないか) を前もって調べることと、2 の形の選び方
internal static partial class ShipExport
{
    // name (Export と同じ選び方の record) の形 (Components[SGeometryResourceParams] の Geometry のパス。主の形が箱の品は ItemShape が選んだ形) が DataCore にあり、
    // 参照先の形のファイル (LoadSingleMesh が最初に読むもの。LOD 付きがあればそれ) が Data.p4k にあれば true。
    // 無ければ false (Export は ExportException になる: 例 SHLD_GODI_S04_Bengal_SCItem は形のパスが "m" だけで、starbreaker も同じく失敗する)
    public static bool HasShape(P4kArchive p4k, DataCoreDb db, string name)
    {
        var record = FindRecord(db, name, out _);
        if (record is not { } r) return false;
        var idx = IndexOf(db);
        var geom = ItemShape(p4k, idx, r)?.Geom ?? idx.GeometryPaths(r).Geom;
        if (geom == null) return false;
        return ShapeFileExists(p4k, geom);
    }

    // 形のパス (CDF なら最初に読む部品) の形のファイルが Data.p4k にあるか
    private static bool ShapeFileExists(P4kArchive p4k, string geometryPath)
    {
        try
        {
            var files = ResolveGeometryFiles(p4k, geometryPath, item: true);
            var comp = CompanionPath(p4k, DataCorePathToP4k(files.Parts[0].Path));
            return p4k.Find(comp) >= 0;
        }
        catch (Exception e) when (IsLoadError(e)) { return false; }
    }

    // 品の 3D の本体に、主の形の代わりに使う形と材質のパス。主の形のままなら null。
    // (2026-11-05、利用者の報告「アーマーを選んだのにボックス」「箱が出る」「アーマーの胴腕脚はおかしい」「シャツなどもおかしい」)
    //   アーマーの胴・腕・脚・アンダースーツ・服は、主の形 (Components[SGeometryResourceParams].Geometry.Geometry.Geometry.path) が持ち運び用の箱で、
    //   着る形は同じ部品の Geometry.SubGeometry[] (Tags = Male / Female)、しまってある姿の形はその下の SubGeometry (Tags = inventoryStoredEntity) にある。
    //   当たる品: (a) 主の形のパスが Tags = heldEntity の形のパスと同じで、Male か Female の形が主の形と別にある品。
    //     (b) 主の形のファイルの名前が箱の名前 (crate_・box_ で始まる) で、heldEntity の形が主の形と違う品の形 (箱の名前でない) を指していて、
    //       Male か Female に主の形と別の形 (着る形か、その下のしまってある形) がある品
    //       (実データで 8 件: ORC-mkX (改良) の胴・腕・脚は heldEntity がしまってある形、エッジライダーズ・シューズ 5 色は heldEntity が着る形)。
    //       heldEntity も別の箱の品 (invisible_armor_light_core: Male の形は三角形 1 枚だけの形の無い物) と、heldEntity が主の形と同じで着る形の無い品
    //       (backpack_nodraw・Mannequin_NoDraw_PMA_Shirt など) は当てない (今までどおり主の形)。
    //   出す形: 次の順で最初に使える物 (主の形とパスが違い、形のファイルが Data.p4k にある物):
    //     (1) Male の下の inventoryStoredEntity (2) Female の下の inventoryStoredEntity (3) Male の形 (4) Female の形。どれも使えなければ今までどおり主の形。
    //   材質: 選んだ形の所の材質のパス → 無ければ同じ段 (Male / Female) の材質のパス → 無ければ今までの決まり (主の材質のパス → 形のファイルが指す材質)。
    //   当てない品 (今までどおり主の形): Male / Female の下の inventoryStoredEntity の形が主の形と同じ (しまってある姿が主の形そのもの) で、主の形のファイルの名前が
    //     箱の名前 (crate_・box_ で始まる) でない品。ヘルメット (主の形が .cdf の品そのもの。680 件)・バックパックの一部 (8 件) がこれで、当てると着る形 (.skin) に替わってしまう。
    //     箱の名前の品 (16 件) は、しまってある姿も箱なので (3) (4) の着る形を出す
    internal static (string Geom, string Mtl)? ItemShape(P4kArchive p4k, EntityIndex idx, DataCoreDb.Record r)
    {
        var (g, m) = idx.GeometryPaths(r);
        if (g == null) return null;
        var tree = idx.SubGeometryTree(r);
        if (tree.Count == 0) return null;
        static SubGeomNode? Tagged(List<SubGeomNode> nodes, string tag) => nodes.FirstOrDefault(n => string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase));
        var main = PathKey(g);
        var mainIsBox = HasBoxName(main);
        var held = Tagged(tree, "heldEntity");
        var heldKey = held?.GeometryPath == null ? null : PathKey(held.GeometryPath);
        bool Other(string? p) => p != null && PathKey(p) != main;   // 主の形と違うパス
        var male = Tagged(tree, "Male");
        var female = Tagged(tree, "Female");
        var maleStored = male == null ? null : Tagged(male.Children, "inventoryStoredEntity");
        var femaleStored = female == null ? null : Tagged(female.Children, "inventoryStoredEntity");
        bool worn = Other(male?.GeometryPath) || Other(female?.GeometryPath);
        if (heldKey == main && worn)
        {
            // (a)。しまってある姿が主の形そのもので、箱の名前でない品 (ヘルメットなど) は当てない
            bool storedIsMain = (maleStored?.GeometryPath != null && PathKey(maleStored.GeometryPath) == main) || (femaleStored?.GeometryPath != null && PathKey(femaleStored.GeometryPath) == main);
            if (storedIsMain && !mainIsBox) return null;
        }
        else if (!(mainIsBox && heldKey != null && heldKey != main && !HasBoxName(heldKey)
                   && (worn || Other(maleStored?.GeometryPath) || Other(femaleStored?.GeometryPath)))) return null;   // (b) でもない
        foreach (var (node, gender) in new[] { (maleStored, male), (femaleStored, female), (male, male), (female, female) })
        {
            if (node?.GeometryPath is not string p || !Other(p) || !ShapeFileExists(p4k, p)) continue;
            return (p, node.MaterialPath ?? gender?.MaterialPath ?? m ?? "");
        }
        return null;
    }

    // パスを比べる形にする: 小文字、区切りは /、先頭の data/ は外す
    private static string PathKey(string path)
    {
        var p = path.Replace('\\', '/').Trim().ToLowerInvariant();
        return p.StartsWith("data/", StringComparison.Ordinal) ? p[5..] : p;
    }

    // 持ち運び用の箱のファイルの名前か (実データ: アーマーは crate_armor_*.cgf、服は box_clothing_1_*.cgf)。ItemShape の (a) の「当てない品」の見分けと (b) に使う
    private static bool HasBoxName(string pathKey)
    {
        var file = pathKey[(pathKey.LastIndexOf('/') + 1)..];
        return file.StartsWith("crate_", StringComparison.Ordinal) || file.StartsWith("box_", StringComparison.Ordinal);
    }
}
