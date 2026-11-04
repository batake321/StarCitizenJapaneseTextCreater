namespace StarCitizenJapaneseTextCreater;

// 武器・装備などの品 (船以外の EntityClassDefinition) の 3D (2026-11-04、利用者の要望「武器の3D出ないけど？」)。
// 書き出しは船と同じ ShipExport.Export (starbreaker の entity export と同じ流れ。船以外の entity も同じ assemble_glb_with_loadout で書く)。
// ここは、その品に形があるか (Export が「形が無い」で失敗しないか) を前もって調べるだけ
internal static partial class ShipExport
{
    // name (Export と同じ選び方の record) の形 (Components[SGeometryResourceParams] の Geometry のパス) が DataCore にあり、
    // 参照先の形のファイル (LoadSingleMesh が最初に読むもの。LOD 付きがあればそれ) が Data.p4k にあれば true。
    // 無ければ false (Export は ExportException になる: 例 SHLD_GODI_S04_Bengal_SCItem は形のパスが "m" だけで、starbreaker も同じく失敗する)
    public static bool HasShape(P4kArchive p4k, DataCoreDb db, string name)
    {
        var record = FindRecord(db, name, out _);
        if (record is not { } r) return false;
        var (geom, _) = IndexOf(db).GeometryPaths(r);
        if (geom == null) return false;
        try
        {
            var (parts, _) = ResolveGeometryFiles(p4k, geom);
            var comp = CompanionPath(p4k, DataCorePathToP4k(parts[0].Path));
            return p4k.Find(comp) >= 0;
        }
        catch (Exception e) when (IsLoadError(e)) { return false; }
    }
}
