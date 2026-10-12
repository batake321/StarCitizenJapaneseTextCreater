using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 装備の表 (［船舶装備］［個人装備］タブ) の 1 行。値は比べるツールと同じ計算 (FillItemStats / FillComparePrices) のもの。
// Kind = 種類 (船: パワープラント・シールドなど / 個人: アーマー・服・比べるツールの種類の名前)、Part = 部位 (アーマー・服。それ以外は "")、
// Weight = 重さ (アーマー: ライト / ミディアム / ヘビー / 重さ無し。服・それ以外は "")、Grade / Class = 説明文の Grade / Class (船の部品)、
// Nums = 数値の欄 (比べるツールの Nums。キーは GearTablePanel の列が使う)、Texts = 文字の欄 (比べるツールの Cells)、
// MinPrice = 店の最安値、Shops / ShopsNote = その売り場の一覧と注 (「店の最安値」のツールチップ)、Record = 代表の record 名 (［詳細］タブで開く)
public sealed record GearTableRow(string Name, string Kind, string Part, string Weight, int Size, string Grade, string Class, string Manufacturer,
    IReadOnlyDictionary<string, double> Nums, IReadOnlyDictionary<string, string> Texts, double? MinPrice,
    IReadOnlyList<ShopListing>? Shops = null, string ShopsNote = "", string Record = "")
{
    public double? N(string key) => Nums.TryGetValue(key, out var v) ? v : null;
    public string T(string key) => Texts.TryGetValue(key, out var v) ? v : "";
    public string? PriceTooltip => MinPrice != null ? ShopListing.Tooltip(Shops, ShopsNote) : null;
}

public partial class GameDataQueryService
{
    // ［船舶装備］の種類: 比べるツールの船の部品 (シールド・動力装置・冷却装置・クアンタムドライブ・レーダー)
    public static IReadOnlyList<CompareCategory> ShipGearTableCategories =>
        CompareCategories.Where(c => c.Kind is CompareKind.Shield or CompareKind.PowerPlant or CompareKind.Cooler or CompareKind.QuantumDrive or CompareKind.Radar).ToList();

    // ［個人装備］の種類: 比べるツールの個人装備のうち武器 (［個人戦闘］) 以外。アーマー (全部位)・服 (全部位)・道具・医療用具・武器の付属品 (全種類)・
    // ナイフ・手榴弾・設置型の爆発物。部位ごとの種類 (ヘルメット・アーマー (胴) 等) は「全部位」に含まれるので使わない
    public static IReadOnlyList<CompareCategory> PersonalGearTableCategories =>
        CompareCategories.Where(c => c.Label is "アーマー (全部位)" or "服 (全部位)" or "道具 (マルチツール・トラクタービーム等)" or "医療用具 (メディペン等)"
            or "武器の付属品 (全種類)" or "ナイフ" or "手榴弾" or "設置型の爆発物").ToList();

    // ［船舶装備］の表 (種類ごとに比べるツールと同じく読み、1 つの表にする。並びは種類・サイズ・名前)
    public List<GearTableRow> GetShipGearTable(IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices = null) =>
        ShipGearTableCategories.SelectMany(c => GetGearTable(c, uexItemPrices))
            .OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Size).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // ［個人装備］の表 (同じ品目が 2 つの種類に入ったときは 1 行にする。並びは種類・部位・名前)
    public List<GearTableRow> GetPersonalGearTable(IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices = null) =>
        PersonalGearTableCategories.SelectMany(c => GetGearTable(c, uexItemPrices))
            .GroupBy(r => (r.Name, r.Part)).Select(g => g.First())
            .OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Part, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // 種類 1 つ分の行。色違いは比べるツールと同じく基本品目 1 行にまとめる (GroupVariants の種類)。表が無い古い DB では空
    private List<GearTableRow> GetGearTable(CompareCategory cat, IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices)
    {
        using var loc = new LocReader(_translationsDbPath);
        List<CompareEntry> roots;
        try
        {
            roots = LoadCompareRoots(cat, null, loc, out _, out var noEntries);
            if (noEntries) return new List<GearTableRow>();
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex))
        {
            return new List<GearTableRow>();
        }
        FillComparePrices(roots, uexItemPrices);
        var isArmor = cat.Kind == CompareKind.Armor;
        return roots.Select(e =>
        {
            var part = isArmor ? PartLabels.FirstOrDefault(p => p.ItemType == e.ItemType).Label ?? e.ItemType : "";
            var kind = cat.Kind switch
            {
                CompareKind.Armor => e.ItemType.StartsWith("Char_Clothing_", StringComparison.Ordinal) ? "服" : "アーマー",
                CompareKind.Shield or CompareKind.PowerPlant or CompareKind.Cooler or CompareKind.QuantumDrive or CompareKind.Radar
                    => new ShipPortInfo { ItemType = e.ItemType }.TypeDisplay,
                // 比べるツールの種類の名前の「 (…)」を除いたもの (例 医療用具 (メディペン等) → 医療用具)
                _ => cat.Label.Split(" (")[0],
            };
            // 重さはアーマーだけ (服は空)
            var weight = isArmor && !e.ItemType.StartsWith("Char_Clothing_", StringComparison.Ordinal) ? ArmorWeightLabel(ArmorWeightOf(e.SubType, e.Desc)) : "";
            string D(string key) => e.Desc.TryGetValue(key, out var v) ? v : "";
            return new GearTableRow(e.DisplayName, kind, part, weight, e.Size, D("Grade"), D("Class"), ManufacturerName(e.ManufacturerRef, loc),
                new Dictionary<string, double>(e.Nums), new Dictionary<string, string>(e.Cells), e.Buyable ? e.MinPrice : null,
                e.Buyable ? e.Shops : null, e.ShopsNote, e.Record);
        }).ToList();
    }
}
