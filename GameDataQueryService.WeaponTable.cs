using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 武器の表 (［船舶戦闘］［個人戦闘］タブ) の 1 行。値は比べるツールと同じ計算 (FillItemStats / FillItemPrice) のもの。
// Damage = 1発のダメージ、MaxDps / AvgDps = 射撃モードのうち最も高い値、HeavyTorso / HeavyHead = ヘビー相手に胴 / 頭でダウンまでの発数
// (射撃モードのうち最も少ない数。LightTorso / LightHead / MediumTorso / MediumHead はライト / ミディアム相手の同じ値)、MinPrice = 店の最安値 (保存データ。色違いのほうが安ければその値)。
// Pellets = 1回の弾数 (散弾は 2 以上。Damage は比べるツールと同じく弾 1 個分)。無い値は null。
// ChargeNote / MixedRateNote = バースト DPS の注 (注1 チャージ式 / 注2 段ごとに連射速度が違う連続動作)。
// Kind = 種類 (個人武器は比べるツールの種類、船の武器は ShipWeaponKindOf の名前)、Manufacturer = メーカー、
// DamageTypes = ダメージの種類 (弾のダメージの種類を「・」でつないだもの。弾が無ければビームのダメージの種類)、Ammo = 弾 (船の武器。弾を使う / 使わない。判定できなければ "")
// Tracking = 追尾する信号 (ミサイル・魚雷。赤外線 (IR) / 電磁 (EM) / 断面積 (CS)。それ以外は "")
// Shops / ShopsNote = 店の最安値の売り場の一覧とその注 (CompareEntry.Shops / ShopsNote。「店の最安値」のセルのツールチップ)
// Record = 代表の record 名 (［詳細］タブで開くのに使う)
public sealed record WeaponTableRow(string Name, int Size, double? Damage, double? MaxDps, double? AvgDps, double? Speed,
    int? Magazine, int? HeavyTorso, int? HeavyHead, double? MinPrice, int? Pellets, bool ChargeNote = false, bool MixedRateNote = false,
    string Kind = "", string Manufacturer = "", string DamageTypes = "", string Ammo = "",
    int? LightTorso = null, int? LightHead = null, int? MediumTorso = null, int? MediumHead = null, string Tracking = "",
    IReadOnlyList<ShopListing>? Shops = null, string ShopsNote = "", string Record = "")
{
    // Damage/Alpha = 1 回撃ったときの合計ダメージ = 1発のダメージ × 1回の弾数 (弾数が無ければ 1 発として。1発のダメージが無ければ null)
    public double? Alpha => Damage is { } d ? GameDataQueryService.WeaponAlpha(d, Pellets ?? 1) : null;

    // 「店の最安値」のセルのツールチップ (星系 / 場所 / 店名 / 値段 を安い順に。売り場が無ければ null)
    public string? PriceTooltip => MinPrice != null ? ShopListing.Tooltip(Shops, ShopsNote) : null;

    // 表の名前の欄 (注の印を付ける)
    public string DisplayName => Name + (ChargeNote ? " (注1)" : "") + (MixedRateNote ? " (注2)" : "");

    // アーマー ('H' ヘビー / 'M' ミディアム / 'L' ライト) の相手を胴 / 頭に当ててダウンまでの発数
    public int? DownShots(char armor, bool head) => armor switch
    {
        'L' => head ? LightHead : LightTorso,
        'M' => head ? MediumHead : MediumTorso,
        _ => head ? HeavyHead : HeavyTorso,
    };
}

public partial class GameDataQueryService
{
    // 読み取り専用の接続で開く (武器の表のタブ用)。DB に書き込まない (表を作る DDL・重複の削除を走らせない)。
    // インデックス構築中・VACUUM 中でも書き込みのロックを取らない。表が無い古い DB では読み取りのメソッドが空・false を返す
    public static GameDataQueryService OpenReadOnly(string gamedataCacheDbPath) => new(gamedataCacheDbPath, readOnly: true);

    private GameDataQueryService(string gamedataCacheDbPath, bool readOnly)
    {
        DbPath = gamedataCacheDbPath;
        _translationsDbPath = Path.Combine(Path.GetDirectoryName(gamedataCacheDbPath) ?? "", "translations.db");
        _conn = new SqliteConnection($"Data Source={gamedataCacheDbPath};Mode=ReadOnly");
        _conn.Open();
    }

    // ［個人戦闘］の種類: 比べるツールの個人武器の種類のうち、record 名で分ける種類 (医療ガンはダメージが無いので除く)
    public static IReadOnlyList<CompareCategory> PersonalWeaponTableCategories =>
        CompareCategories.Where(c => c.Kind == CompareKind.PersonalWeapon && c.RecordToken != null && c.RecordToken != "medgun").ToList();

    // ［船舶戦闘］の種類: 比べるツールの船の武器の種類 (「船の武器」全体と、種類ごと)
    public static IReadOnlyList<CompareCategory> ShipWeaponTableCategories =>
        CompareCategories.Where(c => c.Kind == CompareKind.ShipWeapon).ToList();

    // 表の下に出すバースト DPS の注の説明
    public static string DpsNoteChargeText => DpsNote1;
    public static string DpsNoteMixedText => DpsNote2;

    // ===== 船の武器の種類 =====
    // 種類は表示名と record 名から決める (ShipWeaponKindOf)。record 名 (例 BEHR_LaserCannon_S2) は _ で区切った 2 語目から順に見て、
    // 種類の語と同じか、種類の語で終わる (例 javelinballisticcannon → ballisticcannon) 最初の語を使う。gen2・nova・storm など種類でない語は飛ばす。
    // record 名から種類が決まらないときは、ダメージの種類と表示名・説明文から決める (表に無い種類は英語の名前のまま。ShipWeaponKindOf)。
    // 種類の語は実データ (item_index の WeaponGun) にあるもの
    // 静的フィールドを使わない (CompareCategories の初期化から呼ぶため)
    private static (string Key, string Label, string[] Aliases)[] ShipWeaponKindTable() =>
    [
        ("lasercannon", "レーザーキャノン", ["laser cannon", "レーザーキャノン"]),
        ("laserrepeater", "レーザーリピーター", ["laser repeater", "レーザーリピーター"]),
        ("lasergatling", "レーザーガトリング", ["laser gatling", "レーザーガトリング"]),
        ("laserscattergun", "レーザースキャッターガン", ["laser scattergun", "レーザースキャッターガン"]),
        ("laserbeam", "レーザービーム", ["laser beam", "レーザービーム"]),
        ("ballisticcannon", "バリスティックキャノン", ["ballistic cannon", "バリスティックキャノン"]),
        ("ballisticrepeater", "バリスティックリピーター", ["ballistic repeater", "バリスティックリピーター"]),
        ("ballisticgatling", "バリスティックガトリング", ["ballistic gatling", "バリスティックガトリング"]),
        ("ballisticscattergun", "バリスティックスキャッターガン", ["ballistic scattergun", "バリスティックスキャッターガン"]),
        // 2 語目が scattergun だけの record (弾の種類の語が無い)。別名は付けない (ほかのスキャッターガンを聞かれたときに当たらないように)
        ("scattergun", "スキャッターガン", []),
        ("massdriver", "マスドライバー", ["mass driver", "マスドライバー"]),
        ("distortioncannon", "ディストーションキャノン", ["distortion cannon", "ディストーションキャノン"]),
        ("distortionrepeater", "ディストーションリピーター", ["distortion repeater", "ディストーションリピーター"]),
        ("distortionscattergun", "ディストーションスキャッターガン", ["distortion scattergun", "ディストーションスキャッターガン"]),
        ("neutroncannon", "ニュートロンキャノン", ["neutron cannon", "ニュートロンキャノン"]),
        ("neutronrepeater", "ニュートロンリピーター", ["neutron repeater", "ニュートロンリピーター"]),
        ("plasmacannon", "プラズマキャノン", ["plasma cannon", "プラズマキャノン"]),
        ("tachyoncannon", "タキオンキャノン", ["tachyon cannon", "タキオンキャノン"]),
        ("tachyonbeam", "タキオンビーム", ["tachyon beam", "タキオンビーム"]),
        ("other", "その他", []),
    ];

    // 比べるツールの種類 (「船の武器 (レーザーキャノン)」など)。
    // タキオンビームは record 名の語としては残す (系統の判定に使う) が、表示名で分けると該当が 0 件になるので種類は作らない
    // (データに出てきたときも、表と「船の武器」全体には「タキオンビーム」の種類名で出る)
    // (CompareCategories の初期化から呼ぶので、静的フィールドではなくここに書く)
    private static IEnumerable<CompareCategory> ShipWeaponKindCategories() =>
        ShipWeaponKindTable().Where(k => k.Key != "tachyonbeam")
            .Select(k => new CompareCategory($"船の武器 ({k.Label})", CompareKind.ShipWeapon, k.Aliases, [ShipGunFilter],
                UexCategoryIds: [32], ShipWeaponKind: k.Key));

    // 船の武器の種類のキー。種類 (リピーター・キャノンなど) は表示名 (英語名) の語で決め、系統 (レーザー・バリスティックなど) は record 名の語から取る:
    //   1. record 名 (EntityClassDefinition. を除いたもの) の語から種類を決める (RecordShipWeaponKind)
    //   2. 表示名に種類の語 (Mass Driver / Scattergun / Repeater / Gatling / Cannon / Beam) があれば、record 名の系統 + その種類にする
    //      (例 VNCL_LaserCannon_S1「'WEAK' Repeater」→ レーザーリピーター)。Mass Driver は系統の無い種類
    //   3. record 名から系統が決まらないときは、系統の無い種類があるもの (スキャッターガン) だけ表示名の種類にし、
    //      ほかは record 名の種類のまま (例 BEHR_MassDriver_S12「M9A Cannon」はマスドライバー、トラクタービームはその他)。
    //      系統 + 種類の組み合わせが表に無いときも record 名の種類のまま
    //   表示名が無い (名前未設定) ・種類の語が無いときは record 名の種類
    //   4. record 名から種類が決まらない (record 名の語に種類の語が無い) ときは、データから決める (damageTypes を渡したときだけ。
    //      渡さなければ前のとおり、マスドライバー・スキャッターガン以外は "other"):
    //      - ダメージ (弾・ビーム) が無いもの (例 トラクタービーム) は "other" (その他)
    //      - 系統は record 名の語の頭 (例 XXXX_LaserRailgun → laser) から取り、取れなければダメージの種類で補う:
    //        物理があればバリスティック、無くてディストーションがあればディストーション。
    //        エネルギーだけ (レーザー・ニュートロン・プラズマ・タキオンの区別が付かない) は系統なし
    //      - 表示名に種類の語があれば、系統 + その種類 (表に無ければ英語の名前。例 "Ballistic Beam"、系統なしなら "Beam")。
    //        スキャッターガンで系統なしは前のとおり "scattergun"
    //      - 表示名に種類の語が無ければ、表示名の語のうち説明文の Item Type か displayType (typeWords) にもある最後の語を種類の語にする
    //        (例 表示名 "Foo Railgun Edition" と Item Type "Laser Railgun" → "Railgun")。系統があれば前に付ける (例 "Ballistic Railgun")。
    //        当たる語が無ければ "other" (その他)
    //   表に無い種類のキーは英語の名前そのもの (表のキーは小文字の語だけなので重ならない)。表示名は ShipWeaponKindLabel で引く
    public static string ShipWeaponKindOf(string shortName, string? displayName = null,
        Func<IReadOnlyCollection<string>>? damageTypes = null, Func<IReadOnlyCollection<string>>? typeWords = null)
    {
        var recordKind = RecordShipWeaponKind(shortName);
        var type = NameShipWeaponType(displayName);
        if (recordKind != "other")
        {
            if (type == null) return recordKind;
            if (type == "massdriver") return "massdriver";
            var recordFamily = ShipWeaponFamilies.FirstOrDefault(f => recordKind.StartsWith(f, StringComparison.Ordinal));
            if (recordFamily == null) return type == "scattergun" ? "scattergun" : recordKind;
            var recordCombined = recordFamily + type;
            return ShipWeaponKindTable().Any(k => k.Key == recordCombined) ? recordCombined : recordKind;
        }
        // record 名から種類が決まらない
        if (type == "massdriver") return "massdriver";
        if (damageTypes == null) return type == "scattergun" ? "scattergun" : "other";
        var damage = damageTypes();
        if (damage.Count == 0) return "other";
        // 系統: record 名の語 (2 語目から) が系統の語で始まればそれ (例 XXXX_LaserRailgun → laser)、無ければダメージの種類で補う
        var family = shortName.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                         .Select(w => ShipWeaponFamilies.FirstOrDefault(f => w.StartsWith(f, StringComparison.Ordinal))).FirstOrDefault(f => f != null)
                     ?? (damage.Contains("Physical") ? "ballistic" : damage.Contains("Distortion") ? "distortion" : null);
        if (type != null)
        {
            if (family == null) return type == "scattergun" ? "scattergun" : ShipWeaponTypeEnglish(type);
            var combined = family + type;
            return ShipWeaponKindTable().Any(k => k.Key == combined) ? combined : ShipWeaponFamilyEnglish(family) + " " + ShipWeaponTypeEnglish(type);
        }
        var word = PickShipWeaponTypeWord(displayName, typeWords?.Invoke());
        if (word == null) return "other";
        return family == null ? word : ShipWeaponFamilyEnglish(family) + " " + word;
    }

    // 船の武器の種類のキーの表示名 (表にあれば日本語の名前、無ければキー = 英語の名前のまま)
    public static string ShipWeaponKindLabel(string key) =>
        ShipWeaponKindTable().FirstOrDefault(k => k.Key == key).Label ?? key;

    // 系統・種類のキーの英語 (表に無い種類の名前に使う。例 ballistic → Ballistic、gatling → Gatling)
    private static string ShipWeaponFamilyEnglish(string family) => char.ToUpperInvariant(family[0]) + family[1..];
    private static string ShipWeaponTypeEnglish(string type)
    {
        var word = ShipWeaponNameTypes.FirstOrDefault(t => t.Type == type).Word ?? type;
        return string.Join(" ", word.Split(' ').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));
    }

    // 表示名の語 (英字の並び) のうち、candidates (説明文の Item Type・displayType の語) にもある最後の語。系統の語は除く。無ければ null
    private static string? PickShipWeaponTypeWord(string? displayName, IReadOnlyCollection<string>? candidates)
    {
        if (string.IsNullOrWhiteSpace(displayName) || candidates == null || candidates.Count == 0) return null;
        var set = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        var words = System.Text.RegularExpressions.Regex.Matches(displayName, "[A-Za-z]+").Select(m => m.Value).ToList();
        for (var i = words.Count - 1; i >= 0; i--)
        {
            var w = words[i];
            if (set.Contains(w) && !ShipWeaponFamilies.Contains(w.ToLowerInvariant())) return char.ToUpperInvariant(w[0]) + w[1..];
        }
        return null;
    }

    // 船の武器の種類のキー (ShipWeaponKindOf に、ダメージの種類と説明文・displayType の語をデータから渡す。どちらも要るときだけ読む)
    private string ShipWeaponKindKeyOf(string shortName, string? displayName, string componentJson, string rawJson, LocReader loc) =>
        ShipWeaponKindOf(shortName, displayName,
            () => WeaponDamageTypes(ReadWeaponAmmo(componentJson, rawJson), componentJson),
            () => ShipWeaponTypeWords(rawJson, loc));

    // 武器のダメージの種類 (弾の直撃 + 爆発。弾が無ければビーム) のキー (Physical / Energy など)
    private static List<string> WeaponDamageTypes(AmmoInfo? ammo, string componentJson)
    {
        var types = new List<string>();
        if (ammo != null) types.AddRange(ammo.Direct.Keys.Concat(ammo.Explosion.Keys));
        if (types.Count == 0) types.AddRange(ReadBeamDamage(componentJson).SelectMany(b => b.Damage.Keys));
        return types.Distinct().ToList();
    }

    // 説明文の頭の「Item Type:」(英語) と SCItemPurchasableParams.displayType (翻訳 DB の英語) の語
    private static List<string> ShipWeaponTypeWords(string rawJson, LocReader loc)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(rawJson)) return words;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var cs) || cs.ValueKind != JsonValueKind.Array) return words;
            foreach (var c in cs.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("_Type_", out var t)) continue;
                string? text = null;
                if (t.GetString() == "SAttachableComponentParams" && c.TryGetProperty("AttachDef", out var ad) && ad.TryGetProperty("Localization", out var lz)
                    && lz.TryGetProperty("Description", out var dk) && dk.ValueKind == JsonValueKind.String && (dk.GetString() ?? "").StartsWith('@'))
                {
                    var desc = loc.English(dk.GetString()![1..]);
                    if (desc != null && ParseDescHeader(desc).TryGetValue("Item Type", out var it)) text = it;
                }
                else if (t.GetString() == "SCItemPurchasableParams" && c.TryGetProperty("displayType", out var dt) && dt.ValueKind == JsonValueKind.String
                         && (dt.GetString() ?? "").StartsWith('@'))
                    text = loc.English(dt.GetString()![1..]);
                if (text != null) words.AddRange(System.Text.RegularExpressions.Regex.Matches(text, "[A-Za-z]+").Select(m => m.Value));
            }
        }
        catch (JsonException) { }
        return words;
    }

    // 系統 (record 名の種類の語の頭)
    private static readonly string[] ShipWeaponFamilies = ["laser", "ballistic", "distortion", "neutron", "plasma", "tachyon"];

    // 表示名の種類の語 (語として一致したもの。上から順に見る)
    private static readonly (string Word, string Type)[] ShipWeaponNameTypes =
        [("mass driver", "massdriver"), ("scattergun", "scattergun"), ("repeater", "repeater"), ("gatling", "gatling"), ("cannon", "cannon"), ("beam", "beam")];

    private static string? NameShipWeaponType(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        var n = displayName.ToLowerInvariant();
        foreach (var (word, type) in ShipWeaponNameTypes)
            if (System.Text.RegularExpressions.Regex.IsMatch(n, @"\b" + System.Text.RegularExpressions.Regex.Escape(word) + @"\b")) return type;
        return null;
    }

    // record 名の語から決める種類: 2 語目から順に見て、種類の語と同じか、種類の語で終わる最初の語 (gen2 などは飛ばす)。無ければ "other"
    private static string RecordShipWeaponKind(string shortName)
    {
        var kinds = ShipWeaponKindTable().Where(k => k.Key != "other").Select(k => k.Key).OrderByDescending(k => k.Length).ToList();
        var words = shortName.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        foreach (var w in words.Skip(1))
        {
            var hit = kinds.FirstOrDefault(k => w == k || w.EndsWith(k, StringComparison.Ordinal));
            if (hit != null) return hit;
        }
        return "other";
    }

    // インデックスが構築されているか (item_index に行がある)。表・列が無い古い DB だけ false にし、
    // それ以外の例外 (構築中のロック・壊れた DB など) は呼び出し側へ出す (「未構築」と誤って案内しないため)
    public bool HasItemIndex()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM item_index)";
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return false; }
    }

    private static bool IsMissingTableOrColumn(SqliteException ex) =>
        ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase);

    // 種類ごとの武器の表。比べるツールと同じく色違いを基本品目 1 行にまとめる。店は保存データ (item_shop_prices) を使い、
    // 無い品目は uexItemPrices (UEX の items_prices。比べるツールと同じ) を使う (null なら保存データだけ)。医療ガン (薬剤を注入するもの) は除く。
    // 表が無い古い DB (item_index はあるが items 等が無い) では空を返す
    public List<WeaponTableRow> GetWeaponTable(CompareCategory cat, IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices = null)
    {
        using var loc = new LocReader(_translationsDbPath);
        List<CompareEntry> roots;
        try
        {
            roots = LoadCompareRoots(cat, null, loc, out _, out var noEntries);
            if (noEntries) return new List<WeaponTableRow>();
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex))
        {
            return new List<WeaponTableRow>();
        }
        FillComparePrices(roots, uexItemPrices);
        return roots.Where(e => !IsHealingDevice(e)).Select(e => ToWeaponTableRow(e, loc))
            .OrderBy(r => r.Size).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ［個人戦闘］の表: 個人武器の種類 (PersonalWeaponTableCategories) をすべて 1 つの表にまとめる (医療ガンは除く)。
    // どの種類 (RecordToken) にも当たらない武器 (新しい種類) も、「個人武器 (全種類)」から足す (PersonalWeaponCategoryOf が null のもの。
    // 同じ名前の複製に種類の決まるものがあれば足さない。医療ガンは除く)
    public List<WeaponTableRow> GetPersonalWeaponTable(IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices = null) =>
        PersonalWeaponTableCategories.SelectMany(c => GetWeaponTable(c, uexItemPrices))
            .Concat(GetOtherPersonalWeaponRows(uexItemPrices))
            .OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // どの種類 (RecordToken) にも当たらない個人武器の行
    private List<WeaponTableRow> GetOtherPersonalWeaponRows(IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices)
    {
        var allCat = CompareCategories.First(c => c.Kind == CompareKind.PersonalWeapon && c.RecordToken == null);
        using var loc = new LocReader(_translationsDbPath);
        List<CompareEntry> roots;
        try
        {
            // 種類 (RecordToken) の決まる品目は種類ごとの表で読んだので、性能を入れる前に外す (性能を二重に読まない)
            roots = LoadCompareRoots(allCat, null, loc, out _, out var noEntries, e => PersonalWeaponCategoryOf(e) == null);
            if (noEntries) return new List<WeaponTableRow>();
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex))
        {
            return new List<WeaponTableRow>();
        }
        roots = roots.Where(e => !IsHealingDevice(e)).ToList();
        FillComparePrices(roots, uexItemPrices);
        return roots.Select(e => ToWeaponTableRow(e, loc)).ToList();
    }

    // ［船舶戦闘］の表: 船の武器と、ミサイル・魚雷 (比べるツールの「ミサイル・魚雷」の種類。ロケット・ロケットポッドを含む) を 1 つの表にする。
    // 並びは船の武器の後にミサイル・魚雷 (どちらもサイズ・名前の順)
    public List<WeaponTableRow> GetShipWeaponTable(IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices = null) =>
        GetWeaponTable(CompareCategories.First(c => c.Kind == CompareKind.ShipWeapon && c.ShipWeaponKind == null), uexItemPrices)
            .Concat(GetShipMissileRows(uexItemPrices))
            .ToList();

    // ［船舶戦闘］の店の最安値を UEX の items_prices から引くカテゴリ (船の武器の種類 + ミサイル・魚雷)
    public static int[] ShipWeaponTableUexIds =>
        ShipWeaponTableCategories.Append(MissileCategory).SelectMany(c => c.UexCategoryIds ?? []).Distinct().ToArray();

    // 比べるツールの「ミサイル・魚雷」の種類 (item_index: Missile と WeaponGun/Rocket)
    private static CompareCategory MissileCategory =>
        CompareCategories.First(c => c.Filters.Contains("Missile") && c.Filters.Contains("WeaponGun/Rocket"));

    // ［船舶戦闘］のミサイル・魚雷の行。同じ名前の複製は比べるツールと同じく 1 行にまとめる。店は GetWeaponTable と同じ。
    //   ロケットポッド (WeaponGun/Rocket): 船の武器と同じ計算 (LoadCompareRoots が FillItemStats の ShipWeapon で入れる。弾のダメージ・連射速度・DPS・弾速)。種類は「ロケットポッド」
    //   ミサイル・魚雷・ロケット (Missile): ToMissileRow (raw_json の SCItemMissileParams)
    private List<WeaponTableRow> GetShipMissileRows(IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices)
    {
        using var loc = new LocReader(_translationsDbPath);
        List<CompareEntry> roots;
        try
        {
            roots = LoadCompareRoots(MissileCategory, null, loc, out _, out var noEntries);
            if (noEntries) return new List<WeaponTableRow>();
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex))
        {
            return new List<WeaponTableRow>();
        }
        FillComparePrices(roots, uexItemPrices);
        var rows = new List<WeaponTableRow>();
        foreach (var e in roots)
        {
            // ロケットポッドの値は LoadCompareRoots が船の武器と同じ計算で入れている
            if (string.Equals(e.ItemType, "WeaponGun", StringComparison.OrdinalIgnoreCase))
                rows.Add(ToWeaponTableRow(e, loc) with { Kind = "ロケットポッド" });
            else rows.Add(ToMissileRow(e, loc));
        }
        return rows.OrderBy(r => r.Size).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ミサイルの種類 (item_index.sub_type = ゲームデータの AttachDef.SubType) の名前
    private static string MissileKindLabel(string subType) => subType switch
    {
        "Torpedo" => "魚雷",
        "Rocket" => "ロケット",
        "GroundVehicleMissile" => "地上車両用ミサイル",
        _ => "ミサイル",
    };

    // ミサイル・魚雷・ロケット 1 件の行。値は FillItemStats (FillMissileCells) が入れたもの:
    //   1発のダメージ (= Damage/Alpha。1 回に 1 発)、ダメージの種類、弾速、追尾。
    // 連射速度・DPS・弾・装弾数は無い (空)
    private WeaponTableRow ToMissileRow(CompareEntry e, LocReader loc)
    {
        double? N(string key) => e.Nums.TryGetValue(key, out var v) ? v : null;
        string C(string key) => e.Cells.TryGetValue(key, out var v) ? v : "";
        var damage = N("1発のダメージ");
        return new WeaponTableRow(e.DisplayName, e.Size, damage, null, null, N("弾速"), null, null, null, e.Buyable ? e.MinPrice : null, damage != null ? 1 : null,
            Kind: MissileKindLabel(e.SubType), Manufacturer: ManufacturerName(e.ManufacturerRef, loc), DamageTypes: C("ダメージの種類"), Tracking: C("追尾"),
            Shops: e.Buyable ? e.Shops : null, ShopsNote: e.ShopsNote, Record: e.Record);
    }

    private WeaponTableRow ToWeaponTableRow(CompareEntry e, LocReader loc)
    {
        double? N(string key) => e.Nums.TryGetValue(key, out var v) ? v : null;
        int? I(string key) => e.Nums.TryGetValue(key, out var v) ? (int)Math.Round(v) : null;
        string C(string key) => e.Cells.TryGetValue(key, out var v) ? v : "";
        // 1回の弾数は比べるツールの「1回の弾数」の欄 (射撃モードのうち最も多い数) と同じ値
        int? pellets = int.TryParse(C("1回の弾数"), out var n) ? n : null;
        return new WeaponTableRow(e.DisplayName, e.Size, N("1発のダメージ"), N("バーストDPS"), N("平均DPS"), N("弾速"),
            I("装弾数"), I(TtkHeavyTorsoKey), I(TtkHeavyHeadKey), e.Buyable ? e.MinPrice : null, pellets, e.DpsNoteCharge, e.DpsNoteMixed,
            C("種類"), ManufacturerName(e.ManufacturerRef, loc), C("ダメージの種類"), C("弾"),
            I(TtkDownKey("ライト", false)), I(TtkDownKey("ライト", true)), I(TtkDownKey("ミディアム", false)), I(TtkDownKey("ミディアム", true)),
            Shops: e.Buyable ? e.Shops : null, ShopsNote: e.ShopsNote, Record: e.Record);
    }


    // ===== 種類・弾・ダメージの種類 (比べるツールの欄とタブで共用) =====

    // 武器の欄を入れる (FillItemStats から呼ぶ)。
    //   種類: 個人武器は record 名の 2 語目と同じ RecordToken の種類の名前、船の武器は ShipWeaponKindOf の名前
    //   弾 (船の武器): AmmoKindOf (弾を使う / 使わない)
    //   ダメージの種類 (個人武器・船の武器): 弾のダメージ (直撃 + 爆発) の種類、弾が無ければビームのダメージの種類
    private static void FillWeaponKindCells(CompareKind kind, CompareEntry e, AmmoInfo? ammo, JsonElement? ammoContainer, LocReader loc)
    {
        if (kind == CompareKind.ShipWeapon)
        {
            // 読み込みのときに決めた種類 (読み込みを通らない品目はここで決める。名前が無い品目は表示名なしで決める)
            e.ShipWeaponKindKey ??= ShipWeaponKindOf(e.Short, e.Name.Length > 0 ? e.Name : null,
                () => WeaponDamageTypes(ammo, e.ComponentJson), () => ShipWeaponTypeWords(e.RawJson, loc));
            e.Cells["種類"] = ShipWeaponKindLabel(e.ShipWeaponKindKey);
            var a = AmmoKindOf(e.ComponentJson, ammoContainer);
            if (a != null) e.Cells["弾"] = a;
        }
        else if (kind == CompareKind.PersonalWeapon)
        {
            // record 名の 2 語目が種類 (RecordToken) に当たれば、その種類の名前。当たらない武器 (新しい種類) は、
            // 同じ名前でまとめた複製に種類の決まるものがあればその種類 (新しい種類を作らない。例 BR-2 Shotgun の別 record)、
            // 無ければ説明文の Item Type (英語。翻訳 DB の displayType に同じ英語があればその日本語)
            var cat = PersonalWeaponCategoryOf(e);
            if (cat != null) e.Cells["種類"] = cat.Label;
            else if (e.Desc.TryGetValue("Item Type", out var itemType) && itemType.Length > 0)
                e.Cells["種類"] = loc.DisplayTypeJapanese(itemType) ?? itemType;
        }
        if (kind is CompareKind.ShipWeapon or CompareKind.PersonalWeapon)
        {
            var types = WeaponDamageTypes(ammo, e.ComponentJson);
            var labels = DamageTypeLabels.Where(t => types.Contains(t.Key)).Select(t => t.Label).ToList();
            if (labels.Count > 0) e.Cells["ダメージの種類"] = string.Join("・", labels);
        }
    }

    // 個人武器の種類 (RecordToken)。品目と、同じ名前でまとめた複製のどれかの record 名の 2 語目が当たる種類。無ければ null
    private static CompareCategory? PersonalWeaponCategoryOf(CompareEntry e)
    {
        foreach (var m in e.Dups.Prepend(e))
        {
            var parts = m.Short.Split('_');
            if (parts.Length < 2) continue;
            var cat = CompareCategories.FirstOrDefault(c => c.RecordToken != null && c.RecordToken.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
            if (cat != null) return cat;
        }
        return null;
    }

    // 船の武器が弾を使うか (ゲームデータで判定)。ダメージの種類 (エネルギー等) とは別のもの
    //   使う     = 弾倉 (SAmmoContainerComponentParams) の maxAmmoCount が 1 以上 (weaponRegenConsumerParams があっても使うとする。
    //              例 HRST_LaserBeam_Tiburon は両方ある)
    //   使わない = 弾倉が無い (maxAmmoCount が 0) で、component_json (SCItemWeaponComponentParams) の weaponRegenConsumerParams がある
    //              (撃つたびにエネルギーを使い、時間で回復する)
    //   どちらでもない (weaponRegenConsumerParams も弾倉も無い) ときは null (判定できない)
    internal static string? AmmoKindOf(string componentJson, JsonElement? ammoContainer)
    {
        if (string.IsNullOrEmpty(componentJson)) return null;
        bool regen;
        try
        {
            using var doc = JsonDocument.Parse(componentJson);
            regen = doc.RootElement.TryGetProperty("weaponRegenConsumerParams", out var r) && r.ValueKind == JsonValueKind.Object;
        }
        catch { return null; }
        var maxAmmo = ammoContainer is { } ac && TryNum(ac, "maxAmmoCount", out var m) ? m : 0;
        if (maxAmmo > 0) return "使う";
        if (regen) return "使わない";
        return null;
    }

    // メーカーの名前。参照先のファイル (例 .../scitemmanufacturer.behr.json) の最後の語 (behr) をコードとし、翻訳 DB の manufacturer_Name<コード>
    // (例 Behring) を引く。引けなければ _ の前の語でも引き、それでも無ければコードを大文字で出す
    private static string ManufacturerName(string manufacturerRef, LocReader loc)
    {
        if (string.IsNullOrWhiteSpace(manufacturerRef)) return "";
        var stem = Path.GetFileNameWithoutExtension(manufacturerRef.Replace('\\', '/'));
        var code = stem.Contains('.') ? stem[(stem.LastIndexOf('.') + 1)..] : stem;
        if (code.Length == 0) return "";
        foreach (var c in new[] { code, code.Split('_')[0] }.Distinct())
        {
            var name = loc.English("manufacturer_Name" + c.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return code.ToUpperInvariant();
    }
}
