using System.IO;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

// ハンガーの中の地図 (2026-11-03、利用者の指示「ハンガーのエレベーターは、マイハンガー (S M L XL 等) で、ハンガーのマップを出してほしい」、利用者の決定「ア」: ハンガーの中の形の地図)。
//   ハンガー (instanced の区域) はステーションの木の外にあり、DataCore で次のようにつながる (2026-11-03 に Game2.dcb で確かめた):
//     ステーションの入口の Entity の EntityClass (InstancedHangarGateway_*) → そのレコード (entities/scitem/doors/hangar/) の SInstancedInteriorGatewayParams.instance
//     → ハンガーのレコード (instancedinterior/hangar/hangar_*.json) の SObjectContainerComponentParams.objectContainer (ハンガーの socpak)・
//       InstancedInteriorComponentParams.sizeId (2 = S・4 = M・5 = L・6 = XL)・name (@ui_interactor_hangar_small など)。
//     場所ごとの既定のハンガーは instancedinterior/instancedinteriorlocations.json の locationInteriors[].defaultHangars (location は星図の場所のレコードのファイル)。
//   ハンガーの地図は、ハンガーの socpak をルートにして、ステーションの地図と同じ流れ (StationMapExports) で作る。ファイルは socpak ごとに 1 つ (場所どうしで使い回す)。
//   どのハンガーを出すか (親の決定 1): 場所の defaultHangars にその大きさがあればそれ。無ければ、その場所の地図の exterior.hangars の入口の class のうち、その大きさのものをたどる。
//     それも無ければ「この場所のハンガーの形はデータにありません」
internal static class StationHangars
{
    // ハンガーの地図の record (StationPlace.Record) の頭と、短い名前 (地図のファイル名) の頭
    public const string RecordPrefix = "Hangar.";
    public const string ShortPrefix = "hangar~";

    // ハンガーの socpak のキー (ObjectContainers の下の相対パスの小文字。StationIndex のキー) → record
    public static string RecordOf(string socpakKey) => RecordPrefix + socpakKey;
    public static bool IsRecord(string record) => record.StartsWith(RecordPrefix, StringComparison.Ordinal);
    public static string SocpakOf(string record) => IsRecord(record) ? record[RecordPrefix.Length..] : "";
    // 短い名前: 頭 + socpak のキーの拡張子を外し「/」を「~」にしたもの (キャッシュの鍵は socpak のパス。親の決定 3)
    public static string ShortOf(string socpakKey)
    {
        var k = socpakKey.EndsWith(".socpak", StringComparison.OrdinalIgnoreCase) ? socpakKey[..^7] : socpakKey;
        return ShortPrefix + k.Replace('/', '~');
    }
    public static bool IsShort(string shortName) => shortName.StartsWith(ShortPrefix, StringComparison.OrdinalIgnoreCase);
    // 取り込みの欄・ログに出す名前「ハンガーの地図（socpak のファイル名）」
    public static string DisplayName(string record) => $"ハンガーの地図（{Path.GetFileNameWithoutExtension(SocpakOf(record))}）";

    // ハンガーの地図のルート: ハンガーの socpak を、変換なし (ハンガーの socpak の枠がそのまま地図の局所の枠) で置いたもの
    public static StationRoot RootOf(string record)
    {
        var key = SocpakOf(record);
        return new StationRoot
        {
            Place = new StationPlace(record, "", StationPlace.TypeHangar, ""),
            Socpak = key,
            EntityName = Path.GetFileNameWithoutExtension(key),
            Path = Array.Empty<string>(),
            Depth = 0,
            StarPos = (0, 0, 0),
            StarRotRaw = (1, 0, 0, 0),
            PreSpin = StationXf.Identity,
            PostSpin = StationXf.Identity,
        };
    }

    // 大きさの区分 (ハンガーの入口の GatewaySize・ハンガーのレコードの sizeId と同じ値)
    public static string SizeOf(int sizeId) => sizeId switch { 2 => "S", 4 => "M", 5 => "L", 6 => "XL", _ => "" };
}

// ハンガーの表 (DataCore から作る。親の決定 2「案 A: 取り込みのときに Game2.dcb から引く」)。Data.p4k の印ごとに 1 回作り、置き場所の stations\hangars\ に覚える
internal sealed class StationHangarCatalog
{
    // ハンガーのレコード 1 つ: File はレコードのファイル名 (拡張子なし・小文字)、Record はレコード名 (EntityClassDefinition. を外したもの)
    public sealed record Hangar(string File, string Record, string Socpak, int SizeId, string NameKey);
    // 選んだハンガー: How は選び方の 1 行 (画面の凡例に出す)
    public sealed record Choice(string Socpak, string Record, string NameKey, string How);

    public string StampKey { get; private set; } = "";
    public Dictionary<string, Hangar> Hangars { get; } = new(StringComparer.OrdinalIgnoreCase);
    // 入口の class (InstancedHangarGateway_*) → ハンガーのレコードのファイル名
    public Dictionary<string, string> Gateways { get; } = new(StringComparer.OrdinalIgnoreCase);
    // 場所 (星図の場所のレコードのファイル名。頭の starmapobject. は外す。小文字) → 既定のハンガーのファイル名 (並びのまま。空のものは入れない)
    public Dictionary<string, List<string>> Defaults { get; } = new(StringComparer.OrdinalIgnoreCase);

    public const string FileName = "hangar_catalog.json";
    public static string FilePath => Path.Combine(StationPlaces.HangarFolder, FileName);
    private const int FileVersion = 1;

    private static StationHangarCatalog? s_current;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static void Log(string msg) => App.AppendStartupLog(App.Config.WorkingDirectory, $"[ハンガーの地図] {msg}");

    // 今の Data.p4k の表 (覚えたものが同じ印なら使う。無ければ starbreaker の dcb extract で作る)。作れなければ例外
    public static async Task<StationHangarCatalog> GetAsync(Ship3DExports.P4kStamp stamp, CancellationToken ct)
    {
        if (s_current is { } c && c.StampKey == stamp.Key) return c;
        await Gate.WaitAsync(ct);
        try
        {
            if (s_current is { } c2 && c2.StampKey == stamp.Key) return c2;
            var fromFile = await Task.Run(() => TryReadFile(stamp.Key), ct);
            if (fromFile != null) return s_current = fromFile;
            var built = await BuildAsync(stamp, ct);
            try { await Task.Run(() => built.WriteFile(), ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"ハンガーの表を保存できませんでした: {ex.Message}"); }
            return s_current = built;
        }
        finally { Gate.Release(); }
    }

    // starbreaker の dcb extract (JSON) で、ハンガーのレコード・場所の表・入口のレコードを作業用のフォルダに書き出して読む (読んだら消す)
    private static async Task<StationHangarCatalog> BuildAsync(Ship3DExports.P4kStamp stamp, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dir = Path.Combine(Ship3DExports.WorkFolder, $"hangar_catalog_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var filter in new[] { "libs/foundry/records/instancedinterior/**", "libs/foundry/records/entities/scitem/doors/hangar/instancedhangargateway*" })
            {
                // (2026-11-04、利用者の決定「starbreaker の処理を自前で持つ」) starbreaker の dcb extract の代わりに、アプリの中の読み取り (DataCoreShared・DataCoreCli) で同じ JSON を書く
                try { await Task.Run(() => DataCoreCli.ExtractJson(DataCoreShared.Get(null, stamp.Path), dir, filter, null, ct), ct); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                { throw new InvalidOperationException($"ゲームデータ (DataCore) からハンガーの表を取り出せませんでした: {ex.Message}", ex); }
            }
            var cat = await Task.Run(() => Parse(dir, stamp.Key), ct);
            Log($"ハンガーの表を DataCore から作りました: ハンガー {cat.Hangars.Count}・入口の種類 {cat.Gateways.Count}・既定のハンガーのある場所 {cat.Defaults.Count}、{sw.Elapsed.TotalSeconds:F1} 秒");
            return cat;
        }
        finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { } }
    }

    // 書き出した JSON を読む
    internal static StationHangarCatalog Parse(string dir, string stampKey)
    {
        var cat = new StationHangarCatalog { StampKey = stampKey };
        var rec = Path.Combine(dir, "libs", "foundry", "records");
        static string Stem(string? fileRef) => fileRef is { Length: > 0 } s ? Path.GetFileNameWithoutExtension(s.Replace('\\', '/').Split('/')[^1]).ToLowerInvariant() : "";
        static IEnumerable<JsonElement> Components(JsonElement root) =>
            root.TryGetProperty("_RecordValue_", out var rv) && rv.TryGetProperty("Components", out var cs) && cs.ValueKind == JsonValueKind.Array ? cs.EnumerateArray() : Enumerable.Empty<JsonElement>();
        static string Type(JsonElement e) => e.TryGetProperty("_Type_", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        static string StrOf(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static string RecordName(JsonElement root) { var n = StrOf(root, "_RecordName_"); var i = n.IndexOf('.'); return i >= 0 ? n[(i + 1)..] : n; }
        var hangarDir = Path.Combine(rec, "instancedinterior", "hangar");
        if (Directory.Exists(hangarDir))
            foreach (var f in Directory.GetFiles(hangarDir, "*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(f));
                string oc = "", nameKey = ""; int size = 0;
                foreach (var c in Components(doc.RootElement))
                {
                    var t = Type(c);
                    if (t == "SObjectContainerComponentParams") oc = StrOf(c, "objectContainer");
                    else if (t == "InstancedInteriorComponentParams")
                    {
                        if (c.TryGetProperty("sizeId", out var sz) && sz.ValueKind == JsonValueKind.Number) size = sz.GetInt32();
                        nameKey = StrOf(c, "name");
                    }
                }
                // socpak は StationIndex のキー (ObjectContainers の下の相対パスの小文字)。socpak の無いレコード (Hangar_Stanton などのまとめのレコード) は入れない
                var key = oc.Replace('\\', '/').ToLowerInvariant();
                const string ocHead = "objectcontainers/";
                if (key.StartsWith("data/", StringComparison.Ordinal)) key = key[5..];
                if (key.StartsWith(ocHead, StringComparison.Ordinal)) key = key[ocHead.Length..];
                if (key.Length == 0) continue;
                var file = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                cat.Hangars[file] = new Hangar(file, RecordName(doc.RootElement), key, size, nameKey);
            }
        var gwDir = Path.Combine(rec, "entities", "scitem", "doors", "hangar");
        if (Directory.Exists(gwDir))
            foreach (var f in Directory.GetFiles(gwDir, "instancedhangargateway*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(f));
                foreach (var c in Components(doc.RootElement))
                    if (Type(c) == "SInstancedInteriorGatewayParams" && Stem(StrOf(c, "instance")) is { Length: > 0 } h)
                        cat.Gateways[RecordName(doc.RootElement)] = h;
            }
        var locFile = Path.Combine(rec, "instancedinterior", "instancedinteriorlocations.json");
        if (File.Exists(locFile))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(locFile));
            if (doc.RootElement.TryGetProperty("_RecordValue_", out var rv) && rv.TryGetProperty("locationInteriors", out var li) && li.ValueKind == JsonValueKind.Array)
                foreach (var l in li.EnumerateArray())
                {
                    var loc = Stem(StrOf(l, "location"));
                    if (loc.StartsWith("starmapobject.", StringComparison.Ordinal)) loc = loc["starmapobject.".Length..];
                    if (loc.Length == 0) continue;
                    var list = new List<string>();
                    if (l.TryGetProperty("defaultHangars", out var dh) && dh.ValueKind == JsonValueKind.Array)
                        foreach (var h in dh.EnumerateArray())
                            if (h.ValueKind == JsonValueKind.String && Stem(h.GetString()) is { Length: > 0 } hs) list.Add(hs);
                    if (list.Count > 0) cat.Defaults[loc] = list;
                }
        }
        if (cat.Hangars.Count == 0) throw new InvalidOperationException("ゲームデータ (DataCore) にハンガーのレコードが見つかりませんでした");
        return cat;
    }

    // どのハンガーを出すか (親の決定 1)。stationShort はステーションの地図の短い名前 (StationPlaces.ShortName)、entrances はその地図の入口 (class と大きさ)。
    // 見つからなければ null (note に理由)。入口の class から選ぶとき、その大きさの class が 2 種類以上あれば、入口の数のいちばん多い class (同じなら名前の順) にし、How にほかの種類の数を書く
    public Choice? Resolve(string stationShort, string size, IEnumerable<(string Class, string Size)> entrances, out string note)
    {
        note = "";
        if (Defaults.TryGetValue(stationShort, out var defs))
            foreach (var file in defs)
                if (Hangars.TryGetValue(file, out var h) && StationHangars.SizeOf(h.SizeId) == size)
                    return new Choice(h.Socpak, h.Record, h.NameKey, $"この場所の既定のハンガー（{h.Record}）を出しています");
        var byClass = entrances.Where(e => e.Size == size && e.Class.Length > 0).GroupBy(e => e.Class, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Class: g.Key, Count: g.Count(), H: Gateways.TryGetValue(g.Key, out var hf) && Hangars.TryGetValue(hf, out var hh) ? hh : null))
            .Where(x => x.H != null).OrderByDescending(x => x.Count).ThenBy(x => x.Class, StringComparer.Ordinal).ToList();
        if (byClass.Count == 0)
        {
            note = $"この場所のハンガー（{size}）の形はデータにありません";
            return null;
        }
        var best = byClass[0];
        var others = byClass.Skip(1).Where(x => !string.Equals(x.H!.Socpak, best.H!.Socpak, StringComparison.Ordinal)).ToList();
        return new Choice(best.H!.Socpak, best.H.Record, best.H.NameKey,
            $"この場所のハンガーの入口（{best.Class}、{best.Count} か所）からたどったハンガー（{best.H.Record}）を出しています" +
            (others.Count > 0 ? $"（ほかに同じ大きさの別の形の入口 {others.Count} 種類: {string.Join("・", others.Select(o => $"{o.Class} {o.Count} か所"))}）" : ""));
    }

    // そのステーションで出すハンガーの socpak を、大きさごとに全部 (まとめて取り込むで使う)
    public IEnumerable<Choice> AllFor(string stationShort, IReadOnlyCollection<(string Class, string Size)> entrances)
    {
        foreach (var size in new[] { "S", "M", "L", "XL" })
            if (Resolve(stationShort, size, entrances, out _) is { } c) yield return c;
    }

    // ===== 覚えたファイル =====

    private void WriteFile()
    {
        Directory.CreateDirectory(StationPlaces.HangarFolder);
        var tmp = FilePath + ".tmp";
        using (var fs = File.Create(tmp))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", FileVersion);
            w.WriteString("stamp", StampKey);
            w.WriteStartArray("hangars");
            foreach (var h in Hangars.Values.OrderBy(h => h.File, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("file", h.File); w.WriteString("record", h.Record); w.WriteString("socpak", h.Socpak); w.WriteNumber("sizeId", h.SizeId); w.WriteString("name", h.NameKey);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("gateways");
            foreach (var (k, v) in Gateways.OrderBy(k => k.Key, StringComparer.Ordinal)) w.WriteString(k, v);
            w.WriteEndObject();
            w.WriteStartObject("defaults");
            foreach (var (k, v) in Defaults.OrderBy(k => k.Key, StringComparer.Ordinal)) { w.WriteStartArray(k); foreach (var s in v) w.WriteStringValue(s); w.WriteEndArray(); }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static StationHangarCatalog? TryReadFile(string stampKey)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            var r = doc.RootElement;
            if (!r.TryGetProperty("version", out var v) || v.GetInt32() != FileVersion) return null;
            if (!r.TryGetProperty("stamp", out var s) || s.GetString() != stampKey) return null;
            var cat = new StationHangarCatalog { StampKey = stampKey };
            foreach (var h in r.GetProperty("hangars").EnumerateArray())
            {
                var file = h.GetProperty("file").GetString() ?? "";
                cat.Hangars[file] = new Hangar(file, h.GetProperty("record").GetString() ?? "", h.GetProperty("socpak").GetString() ?? "", h.GetProperty("sizeId").GetInt32(), h.GetProperty("name").GetString() ?? "");
            }
            foreach (var p in r.GetProperty("gateways").EnumerateObject()) cat.Gateways[p.Name] = p.Value.GetString() ?? "";
            foreach (var p in r.GetProperty("defaults").EnumerateObject()) cat.Defaults[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            return cat.Hangars.Count > 0 ? cat : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }
}
