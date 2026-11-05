using System.Windows;

namespace StarCitizenJapaneseTextCreater;

// ステーションの 3D (新しい方式) の店の名前と販売品 (2026-11-04、利用者の指示「2D 地図は消して」で、2D の地図の窓 (StationMapPanel) にあった
// 店の名前・販売品の枠の作り方を、そのままここに移した。店は区画の .soc の SCShop の CryGUID と Entity 名で、ShopTree (［詳細］タブと同じ販売データ) の店に結ぶ)
internal static class StationView3DShops
{
    // ゲームの名前の無い店の名前 (2D の地図と同じ文)
    public const string ShopNoName = "（名前がデータに無い店）";

    // 場所 n が record の場所かその下か
    private static bool Under(ShopTreeNode? n, string record) { for (; n != null; n = n.Parent) if (n.RecordName == record) return true; return false; }

    // ShopTree の店 (shop_id = 場所|socpak|SCShop の CryGUID) のうち CryGUID が同じもの。この場所 (かその下) で Entity 名も同じ店を先に選ぶ
    // (shop_id の CryGUID は店ごとに一意ではない。例 SCShop_ShipWeapons_UtilStation の CryGUID がほかの場所の SCShop_lt_a_casaba_small_base_a にも付く)
    public static ShopTreeShop? ShopOf(ShopTree? tree, string record, string guid, string entity)
    {
        if (tree == null) return null;
        var hits = tree.Shops.Where(x =>
        {
            var parts = x.Id.Split('|');
            return parts.Length >= 3 && string.Equals(parts[2], guid, StringComparison.OrdinalIgnoreCase);
        }).ToList();
        bool SameEntity(ShopTreeShop x) => string.Equals(x.EntityName, entity, StringComparison.OrdinalIgnoreCase);
        return hits.FirstOrDefault(x => Under(x.Location, record) && SameEntity(x)) ?? hits.FirstOrDefault(x => Under(x.Location, record))
            ?? hits.FirstOrDefault(SameEntity) ?? hits.FirstOrDefault();
    }

    // 店の名前: ［詳細］タブと同じ ShopTreeShop.Name のうち、franchise の名前か、ゲームの名前・ラベル・販売データの名前だけ。無ければ ""
    public static string ShopName(ShopTree? tree, string record, string guid, string entity)
    {
        if (ShopOf(tree, record, guid, entity) is { Name.Length: > 0 } best
            && (best.HasFranchiseName || best.NameSource is ShopTreeShop.NameFromGame or ShopTreeShop.NameFromLabel or ShopTreeShop.NameFromData))
            return best.Name;
        return "";
    }

    // 名札の名前「店名（種類）」(種類は販売データのある店の種類。名前と同じなら付けない)。名前が無ければ ""
    public static string ShopLabel(ShopTree? tree, string record, string guid, string entity)
    {
        var nm = ShopName(tree, record, guid, entity);
        if (nm.Length == 0) return "";
        var kind = ShopOf(tree, record, guid, entity) is { } sh && (sh.Items.Count > 0 || sh.Commodities.Count > 0) ? sh.Kind : "";
        return kind.Length > 0 && kind != nm ? $"{nm}（{kind}）" : nm;
    }

    // 販売品の枠の知らせ ({type:'sales', title, shops: [{name, note} | {name, tables}]})。品目は「品名・種類・価格 (購入とレンタルが両方あれば 購入・レンタル)」、
    // コモディティは「品名・買値・売値」。同じ行は 1 行に、並びは品名の順。販売データの無い店は「販売データが紐づいていません。」(［詳細］と同じ文)
    public static object Sales(ShopTree? tree, string record, string guid, string entity)
    {
        var label = ShopLabel(tree, record, guid, entity);
        var name = label.Length > 0 ? label : ShopNoName;
        return new { type = "sales", title = $"販売品: {name}", shops = new List<object> { Entry(ShopOf(tree, record, guid, entity), name) } };
    }

    private static object Entry(ShopTreeShop? shop, string name)
    {
        if (shop == null || shop.Items.Count == 0 && shop.Commodities.Count == 0)
            return new { name, note = "販売データが紐づいていません。" };
        var tables = new List<object>();
        if (shop.Items.Count > 0)
        {
            var items = shop.Items.Values.ToList();
            var hasBuy = items.Any(i => i.Buy.HasValue);
            var hasRent = items.Any(i => i.Rents.Count > 0);
            var cols = new List<string> { "品名", "種類" };
            if (hasBuy) cols.Add(hasRent ? "購入" : "価格");
            if (hasRent) cols.Add("レンタル");
            var rows = items.Select(i => (i.Name, i.Type,
                    Buy: i.Buy is { } b ? $"{b:N0} aUEC" : "-",
                    Rent: i.Rents.Count > 0
                        ? string.Join(" / ", i.Rents.Distinct().OrderBy(r => r.Days ?? int.MaxValue).ThenBy(r => r.Price).Select(r => $"{(r.Days is { } d ? $"{d}日 " : "")}{r.Price:N0} aUEC"))
                        : "-"))
                .Distinct().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
                .Select(r =>
                {
                    var row = new List<string> { r.Name, r.Type };
                    if (hasBuy) row.Add(r.Buy);
                    if (hasRent) row.Add(r.Rent);
                    return row;
                }).ToList();
            tables.Add(new { cols, rows, left = 2 });
        }
        if (shop.Commodities.Count > 0)
        {
            static string P(double v) => v > 0 ? $"{v:N0} aUEC" : "-";
            var rows = shop.Commodities.Distinct().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new List<string> { c.Name, P(c.Buy), P(c.Sell) }).ToList();
            tables.Add(new { cols = new List<string> { "品名", "買値", "売値" }, rows, left = 1 });
        }
        return new { name, tables };
    }

    // いま開いている別ウィンドウ (1 つだけ。閉じたら null)
    private static StationView3DWindow? s_window;

    // 場所の 3D を開く入口 (全部ここを通る。2026-11-04、利用者の指示「別ウィンドウひらいてたらそっちに、…そうでなければ map に出して」):
    //   別ウィンドウが開いていれば、そのウィンドウにその場所 (と店の行き先) を出して前に出す。
    //   開いていなければ、［マップ］タブに切り替えてタブの中に出す (MapPanel.ShowPlaceInTab)。［マップ］タブが無いときだけ別ウィンドウで開く。
    // tree があれば店の名前・販売品を渡す (無ければ［マップ］タブが作る)。focusShop: 行き先にする店 (Entity 名)
    public static void Show(string record, string name, ShopTree? tree, string? focusShop = null, Window? owner = null)
    {
        if (MapPanel.ShowPlaceInTab(record, name, tree, focusShop)) return;
        Open(record, name, tree, focusShop, owner);
    }

    // 開いている別ウィンドウに出す。開いていなければ false。
    // here: 出した後に、その 3D で現在位置の所を出す (StationView3DView.RequestHere。［マップ］の現在位置を押したとき)。同じ場所を出していれば出し直さない
    public static bool ShowInOpenWindow(string record, string name, ShopTree? tree, string? focusShop, bool here = false)
    {
        if (s_window is not { } win) return false;
        if (here && focusShop == null && win.View.Record == record)
        {
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
            win.View.RequestHere();
            return true;
        }
        Func<string, string, string>? label = tree == null ? null : (g, e) => ShopLabel(tree, record, g, e);
        Func<string, string, object?>? sales = tree == null ? null : (g, e) => Sales(tree, record, g, e);
        win.ShowPlace(record, name, label, sales, focusShop);
        if (here) win.View.RequestHere();
        return true;
    }

    // 別ウィンドウで開く (［マップ］タブの［別ウィンドウ］から。開いている別ウィンドウがあればそこに出す)。tree があれば店の名前・販売品を渡す。
    // focusShop: 開いたときに行き先にする店 (Entity 名)。無ければ窓の決まり (艦隊マネージャーの端末の前など)
    public static StationView3DWindow Open(string record, string name, ShopTree? tree, string? focusShop = null, Window? owner = null)
    {
        if (ShowInOpenWindow(record, name, tree, focusShop)) return s_window!;
        Func<string, string, string>? label = tree == null ? null : (g, e) => ShopLabel(tree, record, g, e);
        Func<string, string, object?>? sales = tree == null ? null : (g, e) => Sales(tree, record, g, e);
        var win = new StationView3DWindow(record, name, label, sales, focusShop);
        if (owner != null && owner.IsLoaded) win.Owner = owner;
        s_window = win;
        win.Closed += (_, _) => { if (ReferenceEquals(s_window, win)) s_window = null; };
        win.Show();
        return win;
    }
}
