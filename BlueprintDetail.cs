using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

// ブループリント (BP) の情報を、ほかの画面の詳細に足す文字にする。読み方・名前の出し方は［BP］タブ (BlueprintPanel) と同じものを使う。
//   ［詳細］の品の詳細 (GameDataQueryService.GetItemDetailText) の末尾の「【ブループリント】」の節 (ItemSectionText)
//   ミッションタブのミッション詳細 (MissionService.FormatDetail) の「■ 報酬の BP」の節 (MissionSectionLines)
internal static class BlueprintDetail
{
    // ［詳細］の品の詳細の「【ブループリント】」の節 (GetItemDetailText の形。DetailHtml が「項目: 値」を表に、「|」の行をそのまま表にする)。
    // できる品が outputRecord (大文字小文字無視) の BP が無い・BP の表が無ければ ""。同じ品に BP が複数あれば全部出す
    public static string ItemSectionText(string workDir, string outputRecord)
    {
        var rows = BlueprintPanel.LoadForOutput(workDir, outputRecord);
        if (rows.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            var (it, stockText, haves) = rows[i];
            if (i > 0) sb.AppendLine();
            sb.AppendLine(rows.Count > 1 ? $"【ブループリント ({i + 1}/{rows.Count})】" : "【ブループリント】");
            if (it.Category.Length > 0) sb.AppendLine($"  分類: {it.Category}");
            sb.AppendLine($"  製作時間: {it.TimeText}");
            if (it.ChooseCount is { } cc) sb.AppendLine($"  材料の選び方: {it.Slots.Count} 枠のうち {cc} 枠選択");

            // 入手: 初期所持・ミッションの報酬 (表示名の数)。どちらも無ければ不明
            var sources = new List<string>();
            if (it.Starter) sources.Add("初期所持");
            if (it.Missions.Count > 0) sources.Add($"契約の報酬 {it.Missions.Count} 件");
            sb.AppendLine("  入手: " + (sources.Count > 0 ? string.Join(" / ", sources) : "不明（ゲームデータに入手経路が無い）"));
            // 在庫: trade_cache.db を読めたときだけ
            if (haves != null && stockText.Length > 0) sb.AppendLine($"  在庫: {stockText}");
            // 所持 (trade_cache.db の my_blueprints): trade_cache.db を読めたときだけ
            if (it.Owned is { } owned) sb.AppendLine($"  所持: {(owned ? "あり" : "なし")}");

            if (it.Slots.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(haves != null ? "  | 枠 | 材料 | 量 | 最低品質 | 手持ち |" : "  | 枠 | 材料 | 量 | 最低品質 |");
                sb.AppendLine(haves != null ? "  |---|---|---|---|---|" : "  |---|---|---|---|");
                for (int k = 0; k < it.Slots.Count; k++)
                {
                    var s = it.Slots[k];
                    var unit = s.ResourceUuid.Length > 0 ? OreService.UnitScu : OreService.UnitPiece;
                    var line = $"  | {Cell(s.SlotName)} | {Cell(s.MaterialName)} | {BlueprintPanel.FormatQuantity(s.Quantity)} {unit} | {(s.MinQuality > 0 ? s.MinQuality.ToString(CultureInfo.InvariantCulture) : "なし")} |";
                    if (haves != null)
                    {
                        // 照合できない品目 (資源でも手掘りの鉱物でもない) は「照合対象外」。在庫の行が無い資源は 0
                        var matchable = s.ResourceUuid.Length > 0 || s.MineralResourceUuid != null;
                        line += matchable ? $" {BlueprintPanel.FormatQuantity(haves[k] ?? 0)} {unit} |" : " 照合対象外 |";
                    }
                    sb.AppendLine(line);
                }
            }
            if (it.Missions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  | 報酬でもらえる契約 |");
                sb.AppendLine("  |---|");
                foreach (var (name, _) in it.Missions) sb.AppendLine($"  | {Cell(name)} |");
            }
        }
        return sb.ToString();
    }

    // 表のセルに入れる文字 (GameDataQueryService.Cell と同じく「|」は「/」に、改行・タブは空白にする)
    private static string Cell(string s) => (s ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

    // BlueprintRewards の 1 件 (BlueprintPanel.RewardObjectRegex) の中の chance の値
    private static readonly Regex ChanceRegex = new("\"chance\"\\s*:\\s*([-+0-9.eE]+)", RegexOptions.Compiled);

    // ミッション詳細の「■ 報酬の BP」の節の行 (先頭の空行を含む)。raw_json に BlueprintRewards の blueprintPool が無い・BP の表が無ければ空。
    // 抽選プールごとに、BlueprintRewards の chance の値をそのまま添え (意味は確かめていないので加工しない)、プールの BP をできる品の名前で並べる
    public static List<string> MissionSectionLines(string workDir, string rawJson)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(rawJson) || !rawJson.Contains("blueprintPool", StringComparison.Ordinal)) return lines;
        // (プールのファイル名, chance の文字) を出てきた順に。同じ組は 1 つ。
        // BlueprintRewards の外の blueprintPool (MissionModifier_TemporaryBlueprint など) は報酬の BP ではないので出さない
        var pools = new List<(string File, string? Chance)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match obj in BlueprintPanel.RewardObjectRegex.Matches(rawJson))
        {
            var chance = ChanceRegex.Match(obj.Value) is { Success: true } c ? c.Groups[1].Value : null;
            foreach (Match m in BlueprintPanel.BlueprintPoolFileRegex.Matches(obj.Value))
            {
                var file = m.Groups[1].Value;
                if (seen.Add(file.ToLowerInvariant() + "\n" + chance)) pools.Add((file, chance));
            }
        }
        if (pools.Count == 0) return lines;

        Dictionary<string, List<string>> names;
        try { names = BlueprintPanel.LoadPoolBlueprintNames(workDir, pools.Select(p => p.File).ToList()); }
        catch (Exception ex)
        {
            lines.Add("");
            lines.Add("■ 報酬の BP");
            lines.Add($"  BP を読み込めなかった ({ex.Message})");
            return lines;
        }
        if (names.Count == 0) return lines;   // BP の表が無い

        lines.Add("");
        lines.Add("■ 報酬の BP");
        foreach (var (file, chance) in pools)
        {
            lines.Add($"  抽選プール {file}" + (chance != null ? $" (chance: {chance})" : ""));
            if (names.TryGetValue(file, out var list) && list.Count > 0)
                foreach (var n in list) lines.Add($"    - {n}");
            else
                lines.Add("    (このプールの BP は BP の表に無い)");
        }
        return lines;
    }
}
