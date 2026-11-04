using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

// 部品の名前の決まり (床・階段・灯り) と、socpak の .soc の部品の名前の一覧 (2026-11-04、利用者の指示「2D 地図は消して」で、2D の地図の床の判定・書き出しを消し、
// 場所の木 (StationTree) が「外す決まりの Child でも、部分木に床・階段の部品があれば外さない」(StationIndex.SubtreeHasFloorParts) に使う名前の決まりだけを残した)
internal static class StationFloorBuilder
{
    // 升目の大きさ (m。3D の店の入口の判定 StationView3D.Entrances が使う。2D の地図の床の升目と同じ値)
    public const double Cell = 0.5;

    // 部品の名前の語 (小文字・パスと拡張子を除き「_」で分けたもの)
    internal static string[] MeshWords(string mesh) => Words(mesh);

    // 床の板のメッシュ名の規則 (Checkmate の .soc の CGF の名前の一覧から決めた。harness の cm_cgfs.tsv):
    //   「_」で区切った語に floor・floorplate・walkway のどれかがあり、下の除く語が無いもの。または語に plane と metal の両方があるもの
    //   (col_a_ext_adn_plane_metal_1x2m_001_a・1x4m: Checkmate の BlackMarket (rstop_blackmarket_002_rund_001) の床に敷いた金属板。銃の店の前の床がこれだけだった)。
    //   addon は除く語に入れない (util_addon_floorplate_*・col_a_structural_addon_floor_* が床の板。Checkmate の .soc で計 300 か所以上)。
//   gantry は入れない (Checkmate では出入りの小部屋 (spawnc) の中の縦・斜めの部品と、細い飾り (gantry_straight_16x005x025) だった)。
//   Plates で、板の傾きで床・スロープ・縦の板 (除く) に分ける (Plates の説明)
    //   除く語: 床の上・床下の飾り (cable・cables・light・decal・vent・tarp・debris・rack・fixture・terminal・counter・garbage・rib)、
    //   壁・天井 (wall・ceiling・trim)、階段 (stair・stairs。階段は IsStairMesh で別に扱う)、手すり (railing・rail)
    private static readonly HashSet<string> FloorWords = new(StringComparer.Ordinal) { "floor", "floorplate", "walkway" };
    private static readonly HashSet<string> FloorNotWords = new(StringComparer.Ordinal)
    {
        "cable", "cables", "light", "decal", "vent", "tarp", "debris", "rack", "fixture", "terminal", "counter", "garbage", "rib",
        "wall", "ceiling", "trim", "stair", "stairs", "railing", "rail",
    };
    // 階段のメッシュ名の規則: 語に stair・stairs があり、手すり・柱・飾りの語 (rail・railing・decal・wall・structure・strut・divider・platform・cap・capplate・addon・sign・cover) が無いもの
    private static readonly HashSet<string> StairNotWords = new(StringComparer.Ordinal)
    {
        "rail", "railing", "decal", "wall", "structure", "strut", "divider", "platform", "cap", "capplate", "addon", "sign", "cover",
    };

    private static string[] Words(string mesh)
    {
        var s = mesh.ToLowerInvariant();
        var slash = s.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) s = s[(slash + 1)..];
        foreach (var ext in new[] { ".cgfm", ".cgam", ".cgf", ".cga" })
            if (s.EndsWith(ext, StringComparison.Ordinal)) { s = s[..^ext.Length]; break; }
        return s.Split('_', StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool IsFloorMesh(string mesh)
    {
        var w = Words(mesh);
        if (IsCityPlate(w)) return true;
        // (2026-11-01) シャトルの駅の乗り場の床 ind_transit_dock_a・_b・ind_transit_dock_glass_* (Orison の Providence の駅。上面 741.2 m)。語に transit と dock の両方があるもの
        if (w.Contains("transit") && w.Contains("dock") && !w.Any(FloorNotWords.Contains)) return true;
        // (2026-11-02、利用者の決定「A」) 電車のプラットホーム platform_transit_* (1 語目 platform・2 語目 transit。ガラス platform_transit_*_glass_* は除く)。
        // Area18 の電車の乗り場の床 (platform_transit_a・_b)。90 か所の CGF の一覧 (cgfs_all) でこの名前があるのは Area18 だけ
        if (w.Length >= 2 && w[0] == "platform" && w[1] == "transit" && !w.Contains("glass")) return true;
        // (2026-11-01) 階段の踊り場の床 util_stairs_platform_* (語に stairs・stair と platform の両方があり、rail・railing の無いもの)。Checkmate の精錬所のエレベーター
        // (4bbd7b94) の前の床は、この踊り場の板 (-457.98・-457.48 m) だけで、床の規則で拾えず、乗り場がどの床ともつながっていなかった
        // canopy (屋根) は除く (Seraphim の貨物のエレベーターの util_stairs_platform_canopy_cap_4m_c (-753.09 m) が、22 m² の床の階になっていた)
        if (w.Contains("platform") && w.Any(x => x is "stair" or "stairs") && !w.Contains("rail") && !w.Contains("railing") && !w.Contains("canopy")) return true;
        if (IsCorridor(w)) return true;
        // (2026-11-02、親の決定) 足場の通路 structure_gantry_* (1 語目 structure・2 語目 gantry) を歩ける床にする。Orison の Cousin Crow's の店の階 (723.6 m の段) と
        // 727.17 m の床のあいだが structure_gantry_b_16x02x04_b だけで、道が切れていた。縦の部品は Plates の傾きの決まりで除かれる
        if (w.Length >= 2 && w[0] == "structure" && w[1] == "gantry" && !w.Any(FloorNotWords.Contains)) return true;
        return (w.Any(FloorWords.Contains) || w.Contains("plane") && w.Contains("metal")) && !w.Any(FloorNotWords.Contains);
    }

    // (2026-11-01、利用者の決定「A」) 通路の部品: 1 語目が corridor、2 語目が int か commons で、straight・custom・corner のどれかがあり、
    // 灯り・枠・天井・植木鉢・ガラス・飾り (light・frame・ceiling・planter・glass・addon) と、床の除く語 (FloorNotWords) の無いもの。
    // 下りの通路 corridor_int_straight_drop_* も同じ (利用者の言葉「通路扱いでいい」。坂・階段の別の規則は作らず、今の決まりのまま)。
    // New Babbage の Aspire Grand の乗り場の床 (y −353 m で終わる) と階段 stair_a_straight_04x08x04_a (y −347 m から) のあいだ 6 m (z 376 m) は、
    // corridor_commons_custom_b・corridor_int_straight_01x06x04・02x06x04・corridor_int_straight_drop_05x06x04_b だけで、床が途切れていた
    private static readonly HashSet<string> CorridorNotWords = new(StringComparer.Ordinal) { "light", "frame", "ceiling", "planter", "glass", "addon" };
    private static bool IsCorridor(string[] w) =>
        w.Length >= 3 && w[0] == "corridor" && w[1] is "int" or "commons" && w.Any(x => x is "straight" or "custom" or "corner")
        && !w.Any(CorridorNotWords.Contains) && !w.Any(FloorNotWords.Contains);

    // 段の灯り (light_landing_*): 段の縁・段の前の床に、床と同じ高さで埋め込まれた灯り。Orison の Providence の駅の階段は、階段のメッシュが無く、
    // この灯りが 0.75 m おきに並ぶ (737.95 → 741.21 m) だけだった。Seraphim の配送センターの 5 段も、下 (-9.72 m) と上 (-8.47 m) に並ぶ (StepLightPlates)
    public static bool IsStepLightMesh(string mesh)
    {
        var w = Words(mesh);
        return w.Length >= 2 && w[0] == "light" && w[1] == "landing";
    }

    // 床の灯り (light_floor_*): 床に埋め込まれた灯り。Providence の駅の下の乗り場の床 (737.92 m) は床のメッシュが無く、この灯りとベンチだけがあった (FloorLightPlates)
    public static bool IsFloorLightMesh(string mesh)
    {
        var w = Words(mesh);
        return w.Length >= 2 && w[0] == "light" && w[1] == "floor";
    }


    // 書き出しで読むメッシュ (床・階段・段の灯り・床の灯り)
    public static bool IsExportMesh(string mesh) => IsFloorMesh(mesh) || IsStairMesh(mesh) || IsStepLightMesh(mesh) || IsFloorLightMesh(mesh);

    // 都市の床 (Orison で実データから決めたもの): habitation_plate_*・habitation_addon_plate_* (浮かぶ足場の地面) と、
    // plate_trim_*_balcony_* (シャトルの駅の乗り場の縁の床。Cloudview Center の反対回りの乗り場 (-40.9, -129.8, -3.4) の床がこれだった)。
    // understructure (足場の裏の構造) は除く。plate_trim_*_balcony_* の歩ける高さは Plates で乗り場の高さに直す (IsBalcony)
    // (除く語の trim はここでは見ない)。thruster (足場の推進器 590 個)・window (窓) も除く
    // (2026-11-01 に足したもの) Cloudview Center の島の床の板 plate_<大きさ>_* (plate_64x32_a_thick・plate_64_cap_c_thick・plate_64x64_a_surface_a・plate_48_cap_b など。
    // 病院の島の南の床 (上面 -3.67 m) がこれだけだった) と、橋の床のガラス板 plate_trim_bridge_*_glass (厚さ 0.02 m。広場と Crusader Showroom のあいだの橋で、
    // 両端の橋の階段 base_08x06x02_bridge_stairs_b の下の端と同じ -3.42 m)。1 語目が plate で 2 語目が大きさ (数字か 数字x数字) か transition のものを床にし、
    // trim・addon の付くもの (縁・飾り) は除く。皿の plate_empty_1_a・plate_steak_piece_1_a・plate_meal_* (2 語目が数字でない) は入らない
    private static readonly Regex PlateSizeWord = new(@"^\d+(x\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static bool IsCityPlate(string[] w) =>
        w.Contains("plate") && (w.Contains("habitation") || w.Contains("trim") && w.Contains("balcony"))
        && !w.Contains("understructure") && !w.Contains("thruster") && !w.Contains("window")
        || w.Length >= 2 && w[0] == "plate" && (PlateSizeWord.IsMatch(w[1]) || w[1] == "transition") && !w.Contains("trim") && !w.Contains("addon")
        || w.Length >= 3 && w[0] == "plate" && w[1] == "trim" && w.Contains("bridge") && w[^1] == "glass";

    // シャトルの駅の乗り場の縁 (plate_trim_*_balcony_*)。箱の上面・底は歩ける高さではない
    // (Cloudview Center の乗り場の床は z -3.42 m (実測・乗り場の位置 -3.416 m) で、縁の箱は -9.57〜-2.33 m・-3.32〜-2.38 m)
    public static bool IsCityPlateMesh(string mesh) => IsCityPlate(Words(mesh));

    public static bool IsBalcony(string mesh)
    {
        var w = Words(mesh);
        return w.Contains("plate") && w.Contains("trim") && w.Contains("balcony");
    }

    public static bool IsStairMesh(string mesh)
    {
        var w = Words(mesh);
        return w.Any(x => x is "stair" or "stairs") && !w.Any(StairNotWords.Contains);
    }

    private static readonly Regex CgfRegex = new(@"[A-Za-z0-9_/\\.\-]+\.(?:cgf|cga|cgfm|cgam)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // socpak の .soc にある CGF のファイル名 (小文字、パスなし)。開けなければ空
    public static HashSet<string> ScanSocMeshes(StationIndex index, string socpak)
    {
        var res = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var zip = index.OpenSocpak(socpak);
            if (zip == null) return res;
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.EndsWith(".soc", StringComparison.OrdinalIgnoreCase)) continue;
                var t = Encoding.Latin1.GetString(StationIndex.ReadAll(e));
                foreach (Match m in CgfRegex.Matches(t))
                {
                    var n = m.Value.Replace('\\', '/').ToLowerInvariant();
                    res.Add(n[(n.LastIndexOf('/') + 1)..]);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { }
        return res;
    }
}
