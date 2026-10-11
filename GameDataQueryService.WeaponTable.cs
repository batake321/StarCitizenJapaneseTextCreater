using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 武器の表 (［船舶戦闘］［個人戦闘］タブ) の 1 行。値は比べるツールと同じ計算 (FillItemStats / FillItemPrice) のもの。
// Damage = 1発のダメージ、MaxDps / AvgDps = 射撃モードのうち最も高い値、HeavyTorso / HeavyHead = ヘビー相手に胴 / 頭でダウンまでの発数
// (射撃モードのうち最も少ない数)、MinPrice = 店の最安値 (保存データ。色違いのほうが安ければその値)。
// Pellets = 1回の弾数 (散弾は 2 以上。Damage は比べるツールと同じく弾 1 個分)。無い値は null。
// ChargeNote / MixedRateNote = 最大 DPS の注 (注1 チャージ式 / 注2 段ごとに連射速度が違う連続動作)。
// Kind = 種類 (個人武器は比べるツールの種類、船の武器は ShipWeaponKindOf の名前)、Manufacturer = メーカー、
// DamageTypes = ダメージの種類 (個人武器。弾のダメージの種類を「・」でつないだもの)、Ammo = 弾薬/エネルギー (船の武器。判定できなければ "")
public sealed record WeaponTableRow(string Name, int Size, double? Damage, double? MaxDps, double? AvgDps, double? Speed,
    int? Magazine, int? HeavyTorso, int? HeavyHead, double? MinPrice, int? Pellets, bool ChargeNote = false, bool MixedRateNote = false,
    string Kind = "", string Manufacturer = "", string DamageTypes = "", string Ammo = "")
{
    // 表の名前の欄 (注の印を付ける)
    public string DisplayName => Name + (ChargeNote ? " (注1)" : "") + (MixedRateNote ? " (注2)" : "");
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

    // 表の下に出す最大 DPS の注の説明
    public static string DpsNoteChargeText => DpsNote1;
    public static string DpsNoteMixedText => DpsNote2;

    // ===== 船の武器の種類 =====
    // 種類は表示名と record 名から決める (ShipWeaponKindOf)。record 名 (例 BEHR_LaserCannon_S2) は _ で区切った 2 語目から順に見て、
    // 種類の語と同じか、種類の語で終わる (例 javelinballisticcannon → ballisticcannon) 最初の語を使う。gen2・nova・storm など種類でない語は飛ばす。
    // どれにも当たらなければ "other" (その他)。種類の語は実データ (item_index の WeaponGun) にあるもの
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
    public static string ShipWeaponKindOf(string shortName, string? displayName = null)
    {
        var recordKind = RecordShipWeaponKind(shortName);
        var type = NameShipWeaponType(displayName);
        if (type == null) return recordKind;
        if (type == "massdriver") return "massdriver";
        var family = ShipWeaponFamilies.FirstOrDefault(f => recordKind.StartsWith(f, StringComparison.Ordinal));
        if (family == null) return type == "scattergun" ? "scattergun" : recordKind;
        var combined = family + type;
        return ShipWeaponKindTable().Any(k => k.Key == combined) ? combined : recordKind;
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

    // ［個人戦闘］の表: 個人武器の種類 (PersonalWeaponTableCategories) をすべて 1 つの表にまとめる (医療ガンは除く)
    public List<WeaponTableRow> GetPersonalWeaponTable(IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices = null) =>
        PersonalWeaponTableCategories.SelectMany(c => GetWeaponTable(c, uexItemPrices))
            .OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // ［船舶戦闘］の表: 船の武器 (ミサイル・魚雷は別の種類なので入らない) をすべて 1 つの表にする
    public List<WeaponTableRow> GetShipWeaponTable(IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices = null) =>
        GetWeaponTable(CompareCategories.First(c => c.Kind == CompareKind.ShipWeapon && c.ShipWeaponKind == null), uexItemPrices);

    private WeaponTableRow ToWeaponTableRow(CompareEntry e, LocReader loc)
    {
        double? N(string key) => e.Nums.TryGetValue(key, out var v) ? v : null;
        int? I(string key) => e.Nums.TryGetValue(key, out var v) ? (int)Math.Round(v) : null;
        string C(string key) => e.Cells.TryGetValue(key, out var v) ? v : "";
        // 1回の弾数は比べるツールの「1回の弾数」の欄 (射撃モードのうち最も多い数) と同じ値
        int? pellets = int.TryParse(C("1回の弾数"), out var n) ? n : null;
        return new WeaponTableRow(e.DisplayName, e.Size, N("1発のダメージ"), N("最大DPS"), N("平均DPS"), N("弾速"),
            I("装弾数"), I(TtkHeavyTorsoKey), I(TtkHeavyHeadKey), e.Buyable ? e.MinPrice : null, pellets, e.DpsNoteCharge, e.DpsNoteMixed,
            C("種類"), ManufacturerName(e.ManufacturerRef, loc), C("ダメージの種類"), C("弾薬/エネルギー"));
    }

    // 同じ名前の品目を分けるときの種類 (船の武器は ShipWeaponKindOf の名前、個人武器は比べるツールの種類の名前、ほかは無し)
    private static string SameNameKindLabel(CompareKind kind, CompareEntry e)
    {
        if (kind == CompareKind.ShipWeapon)
        {
            var key = ShipWeaponKindOf(e.Short, e.Name);
            return ShipWeaponKindTable().First(k => k.Key == key).Label;
        }
        if (kind == CompareKind.PersonalWeapon)
        {
            var parts = e.Short.Split('_');
            return parts.Length >= 2 ? CompareCategories.FirstOrDefault(c => c.RecordToken != null && c.RecordToken.Equals(parts[1], StringComparison.OrdinalIgnoreCase))?.Label ?? "" : "";
        }
        return "";
    }

    // ===== 種類・弾薬/エネルギー・ダメージの種類 (比べるツールの欄とタブで共用) =====

    // 武器の欄を入れる (FillItemStats から呼ぶ)。
    //   種類: 個人武器は record 名の 2 語目と同じ RecordToken の種類の名前、船の武器は ShipWeaponKindOf の名前
    //   弾薬/エネルギー (船の武器): AmmoKindOf
    //   ダメージの種類 (個人武器): 弾のダメージ (直撃 + 爆発) の種類、弾が無ければビームのダメージの種類
    private static void FillWeaponKindCells(CompareKind kind, CompareEntry e, AmmoInfo? ammo, JsonElement? ammoContainer)
    {
        if (kind == CompareKind.ShipWeapon)
        {
            var key = ShipWeaponKindOf(e.Short, e.Name);
            e.Cells["種類"] = ShipWeaponKindTable().First(k => k.Key == key).Label;
            var a = AmmoKindOf(e.ComponentJson, ammoContainer);
            if (a != null) e.Cells["弾薬/エネルギー"] = a;
        }
        else if (kind == CompareKind.PersonalWeapon)
        {
            var parts = e.Short.Split('_');
            var cat = parts.Length >= 2 ? CompareCategories.FirstOrDefault(c => c.RecordToken != null && c.RecordToken.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) : null;
            if (cat != null) e.Cells["種類"] = cat.Label;
            var types = new List<string>();
            if (ammo != null) types.AddRange(ammo.Direct.Keys.Concat(ammo.Explosion.Keys));
            if (types.Count == 0) types.AddRange(ReadBeamDamage(e.ComponentJson).SelectMany(b => b.Damage.Keys));
            var labels = DamageTypeLabels.Where(t => types.Contains(t.Key)).Select(t => t.Label).ToList();
            if (labels.Count > 0) e.Cells["ダメージの種類"] = string.Join("・", labels);
        }
    }

    // 船の武器が弾を使うか (ゲームデータで判定)。
    //   エネルギー = component_json (SCItemWeaponComponentParams) の weaponRegenConsumerParams がある (撃つたびにエネルギーを使い、時間で回復する) かつ
    //                弾倉 (SAmmoContainerComponentParams) の maxAmmoCount が 0 (または弾倉が無い)
    //   弾薬       = weaponRegenConsumerParams が無い (null) かつ maxAmmoCount が 1 以上
    //   どちらでもない・両方に当たるときは null (判定できない)
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
        if (regen && maxAmmo <= 0) return "エネルギー";
        if (!regen && maxAmmo > 0) return "弾薬";
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
