using System.Windows;

namespace StarCitizenJapaneseTextCreater;

// 自作品の材料の枠ごとの品質を入れる窓。OK で Result に 枠 (slot_debug_name) → 品質 (0〜1000) を入れる
// (空の枠は入れない。全部空、または［品質なしにする］なら Result は null = 品質なし)。
// ［装備］の所持コンポーネントの品質 (保存する) と［電力管理］の全部品の品質 (その場だけ) の両方から使う。
// quantity が 2 以上 (［装備］の所持コンポーネントの数量) なら、品質を入れる数 (1〜quantity。既定は全部) を選べる。選んだ数は Count
public partial class ComponentQualityDialog : Window
{
    public Dictionary<string, int>? Result { get; private set; }
    public int Count { get; private set; }

    private readonly List<SlotEntry> _slots;
    private readonly int _quantity;

    public ComponentQualityDialog(string itemName, BlueprintQualityInfo info, IReadOnlyDictionary<string, int>? initial, QualityNames names, int quantity = 1)
    {
        InitializeComponent();
        txtTitle.Text = itemName;
        _quantity = Math.Max(1, quantity);
        Count = _quantity;
        if (_quantity >= 2)
        {
            pnlCount.Visibility = Visibility.Visible;
            txtCount.Text = _quantity.ToString();
            txtCountMax.Text = $"/ {_quantity} 個 (全部より少なければ、その数だけ別の行に分けて品質を入れる)";
        }
        _slots = info.Slots.Select(s => new SlotEntry
        {
            DebugName = s.DebugName,
            Name = names.SlotName(s),
            Effects = string.Join("・", info.PropertiesOf(s.DebugName).Select(names.PropertyName)),
            Text = initial != null && initial.TryGetValue(s.DebugName, out var q) ? q.ToString() : "",
        }).ToList();
        lstSlots.ItemsSource = _slots;
    }

    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (!TryQuality(txtAll.Text, out var q)) { txtError.Text = "品質は 0〜1000 の整数で入れてください"; return; }
        txtError.Text = "";
        foreach (var s in _slots) s.Text = q.ToString();
        lstSlots.ItemsSource = null;
        lstSlots.ItemsSource = _slots;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCount()) return;
        Result = null;
        DialogResult = true;
    }

    // 品質を入れる数 (1〜数量)。数量が 1 なら 1
    private bool TryCount()
    {
        if (_quantity < 2) { Count = 1; return true; }
        if (!int.TryParse((txtCount.Text ?? "").Trim(), out var n) || n < 1 || n > _quantity)
        {
            txtError.Text = $"品質を入れる数は 1〜{_quantity} の整数で入れてください";
            return false;
        }
        Count = n;
        txtError.Text = "";
        return true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCount()) return;
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in _slots)
        {
            if (string.IsNullOrWhiteSpace(s.Text)) continue;
            if (!TryQuality(s.Text, out var q)) { txtError.Text = $"{s.Name}: 品質は 0〜1000 の整数で入れてください"; return; }
            dict[s.DebugName] = q;
        }
        Result = dict.Count > 0 ? dict : null;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryQuality(string? text, out int q) =>
        int.TryParse((text ?? "").Trim(), out q) && q >= 0 && q <= 1000;

    private sealed class SlotEntry
    {
        public string DebugName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Effects { get; set; } = "";
        public string Text { get; set; } = "";
    }
}

// 品質の枠の名前 (crafting_ui_slotname_… の訳) と、変わる性質の名前 (StatName_ + property の訳。
// CraftingGameplayPropertyDef の propertyName は、電力管理で使う性質ではどれも "@StatName_" + record 名 (p4k の craftedproperties で確認))。
// 訳は translations.db (OreService.LoadTranslations)。訳が無ければ英語、それも無ければ枠の debugName / property のまま
public sealed class QualityNames
{
    private readonly Dictionary<string, (string? En, string? Ja)> _loc;

    private QualityNames(Dictionary<string, (string? En, string? Ja)> loc) => _loc = loc;

    // 全部の BP の枠と性質の訳を一度に読む (gamedata_cache.db の blueprint_materials.slot_name_key と blueprint_modifiers.property)。
    // どの部品の窓を開いても訳を引けるように (［電力管理］で候補を替えても)。表が無い・読めなければ訳なし
    public static QualityNames LoadAll(string gamedataDbPath, string translationsDbPath)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(gamedataDbPath))
        {
            try
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={gamedataDbPath};Mode=ReadOnly;Pooling=False");
                conn.Open();
                foreach (var sql in new[]
                {
                    "SELECT DISTINCT slot_name_key FROM blueprint_materials WHERE slot_name_key IS NOT NULL AND slot_name_key != ''",
                    "SELECT DISTINCT property FROM blueprint_modifiers WHERE property IS NOT NULL AND property != ''",
                })
                {
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = sql;
                        using var r = cmd.ExecuteReader();
                        var isProperty = sql.Contains("blueprint_modifiers");
                        while (r.Read()) keys.Add(isProperty ? PropertyKey(r.GetString(0)) : r.GetString(0));
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException) { }   // 表が無い (前の版で構築した DB)
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
        }
        return new QualityNames(OreService.LoadTranslations(translationsDbPath, keys));
    }

    // 訳のキーが translations.db にあるか (訳せない名前を数えるとき、キーが本当に無いものを見分ける)
    public bool HasKey(string key) => _loc.ContainsKey(key);
    public static string PropertyKeyOf(string property) => PropertyKey(property);

    public static QualityNames Load(string translationsDbPath, IEnumerable<BlueprintQualityInfo?> infos)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in infos)
        {
            if (info == null) continue;
            foreach (var s in info.Slots) if (!string.IsNullOrEmpty(s.NameKey)) keys.Add(s.NameKey);
            foreach (var r in info.Ranges) keys.Add(PropertyKey(r.Property));
        }
        return new QualityNames(OreService.LoadTranslations(translationsDbPath, keys));
    }

    private static string PropertyKey(string property) => "StatName_" + property;

    private string? Text(string key) =>
        _loc.TryGetValue(key, out var t) ? (OreService.JapaneseText(t.Ja) ?? OreService.EnglishText(t.En)) : null;

    public string SlotName(QualitySlot s) => (s.NameKey is { Length: > 0 } k ? Text(k) : null) ?? s.DebugName;

    public string PropertyName(string property) => Text(PropertyKey(property)) ?? property;
}
