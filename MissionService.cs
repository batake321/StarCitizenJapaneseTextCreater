using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public class MissionService : IDisposable
{
    private readonly SqliteConnection _conn;
    private Dictionary<string, string>? _transDict;
    private Dictionary<string, string>? _enDict;
    private readonly bool _hasWikiColumns;

    private static readonly string[] StripPrefixes =
    {
        "Bounty_PVE_Group_", "Bounty_PVE_", "Bounty_FPS_",
        "PVP_Bounty_", "PVP_",
        "CertificationMission_PVE_", "CertificationMission_PVP_",
        "Delivery_DC_", "Delivery_Local_", "Delivery_DistributionCentr_", "Delivery_DistributionCenters_", "Delivery_",
        "EliminateAll_", "EliminateSpecific_", "EliminateBoss_",
        "Multi-EliminateAll_", "Multi-EliminateBoss_", "Multi-EliminateSpecific_",
        "MultiElimAllAndBoss_",
        "RemoveClaimJumpers_", "FPS_CAVE_", "FPS_",
        "Assassination_SyncedAssassination_", "Assassination_",
        "Defend_", "Infiltrate_", "Inhabited_Derelict_",
        "Derelict_EliminateAll_", "Derelict_EliminateSpecific_", "Derelict_Recovery_", "Derelict_DataDownload_", "Derelict_",
        "MissingPerson_", "Covalex_Investigation_",
        "SalvageContractor_", "Scavenge_",
        "ServiceBeacon_CombatAssistance_", "ServiceBeacon_Refuel_", "ServiceBeacon_",
        "Recover_", "Recovery_", "RetrieveConsignment_", "Collect_Covalex_", "Collect_",
        "Cave_Recovery_", "Cave_",
        "Mining_", "Patrol_",
        "Space_Escort_", "Escort_",
        "BlockadeRunner_", "TowShip_",
        "NTLockdown_FleetDestroy_", "NTLockdown_FleetFind_", "NTLockdown_Delivery_", "NTLockdown_",
        "Station_WasteDisposal_", "Station_",
        "Steal_", "Theft_", "TripMinesTheft_",
        "DestroyNarcotics_", "Destroy_",
        "Deploy_", "HackPrevention_", "CommArrayHack_", "CommArrayRepair_",
        "HijackedShip_", "ShipHijacked_",
        "Jumptown2_", "Wanted5_",
        "ECN_", "ScrambleRace_",
        "PROCYON_", "ResourceRush_", "GhostHollow_",
        "ForceDepletion_", "PirateRaids_",
        "StealEvidence_", "ClearCrimestat_",
        "Intro_",
    };

    private static readonly (string Category, Func<string, string, bool> Match)[] CategoryDefs =
    {
        ("バウンティハンター", (rn, t) => t.Contains("bountyhunter") && !rn.Contains("PVP")),
        ("PVPミッション",     (rn, t) => rn.Contains("PVP") || rn.Contains("CertificationMission_PVP")),
        ("暗殺",             (rn, t) => rn.Contains("Assassination")),
        ("窃盗・強奪",        (rn, t) => rn.Contains("Steal") || rn.Contains("Theft") || rn.Contains("TripMines")),
        ("ハッキング",        (rn, t) => rn.Contains("CommArrayHack") || rn.Contains("HackPrevention")),
        ("麻薬",             (rn, t) => rn.Contains("Drug") || rn.Contains("Narcotics") || rn.Contains("DestroyNarcotics")),
        ("傭兵",             (rn, t) => t.Contains("mercenary")),
        ("回収",             (rn, t) => rn.Contains("Recover") || rn.Contains("Recovery") || rn.Contains("RetrieveConsignment") || rn.Contains("Collect_")),
        ("調査",             (rn, t) => t.Contains("investigation") || rn.Contains("MissingPerson") || t.Contains("search") || t.Contains("research")),
        ("サルベージ",        (rn, t) => t.Contains("salvage") || rn.Contains("Salvage") || rn.Contains("Scavenge")),
        ("配達",             (rn, t) => rn.Contains("Delivery_Local")),
        ("輸送ー惑星間",      (rn, t) => rn.Contains("Delivery_DC") || rn.Contains("Delivery_DistributionCentr")),
        ("デリバリー",        (rn, t) => t.Contains("delivery")),
        ("輸送ー恒星系内",    (rn, t) => rn.Contains("Escort") || rn.Contains("BlockadeRunner") || t.Contains("priority")),
        ("輸送",             (rn, t) => rn.Contains("TowShip") || rn.Contains("NTLockdown_Delivery")),
        ("ハンドマイニング",   (rn, t) => t.Contains("mining") || rn.Contains("Mining_")),
        ("燃料補給",          (rn, t) => t.Contains("servicebeacon") || rn.Contains("ServiceBeacon") || t.Contains("maintenance")),
        ("ECN・緊急通信",     (rn, t) => t.Contains("ecn") || rn.Contains("ECN_")),
        ("レース",           (rn, t) => t.Contains("race") || rn.Contains("Race")),
    };

    public int TransDictCount => _transDict?.Count ?? 0;

    public MissionService(string dbPath, string? translationDbPath = null)
    {
        _conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        _conn.Open();

        try
        {
            using var chk = _conn.CreateCommand();
            chk.CommandText = "SELECT sql FROM sqlite_master WHERE name='missions'";
            var schema = chk.ExecuteScalar() as string ?? "";
            _hasWikiColumns = schema.Contains("wiki_title", StringComparison.OrdinalIgnoreCase);
        }
        catch { _hasWikiColumns = false; }

        if (!string.IsNullOrEmpty(translationDbPath) && File.Exists(translationDbPath))
            LoadTranslations(translationDbPath);
    }

    public string? TransLoadError { get; private set; }

    private void LoadTranslations(string dbPath)
    {
        _transDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _enDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, english, japanese FROM translations WHERE (japanese IS NOT NULL AND japanese != '') OR (english IS NOT NULL AND english != '')";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(0);
                var en = r.IsDBNull(1) ? "" : r.GetString(1);
                var ja = r.IsDBNull(2) ? "" : r.GetString(2);
                if (!string.IsNullOrEmpty(ja))
                {
                    _transDict[key] = ja.Replace("\\n", "\n");
                    if (!key.StartsWith("@")) _transDict["@" + key] = ja.Replace("\\n", "\n");
                }
                if (!string.IsNullOrEmpty(en))
                {
                    _enDict[key] = en.Replace("\\n", "\n");
                    if (!key.StartsWith("@")) _enDict["@" + key] = en.Replace("\\n", "\n");
                }
            }
        }
        catch (Exception ex)
        {
            TransLoadError = ex.Message;
        }
    }

    public List<string> GetFactions()
    {
        var factions = new List<string> { "(すべて)" };
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = _hasWikiColumns
            ? "SELECT DISTINCT wiki_faction FROM missions WHERE wiki_faction IS NOT NULL AND wiki_faction != '' ORDER BY wiki_faction"
            : "SELECT DISTINCT mission_giver FROM missions WHERE mission_giver != '' AND mission_giver NOT LIKE '%LOC%' ORDER BY mission_giver";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var f = r.GetString(0);
            var ja = FactionJaMap.TryGetValue(f, out var j) ? j : "";
            factions.Add(!string.IsNullOrEmpty(ja) ? $"{ja} ({f})" : f);
        }
        return factions;
    }

    public List<string> GetRanks()
    {
        var ranks = new List<string> { "(すべて)" };
        foreach (var r in RankNames)
            ranks.Add(r);
        return ranks;
    }

    public List<MissionEntry> FilterByFactionAndRank(List<MissionEntry> missions, string? factionFilter, string? rankFilter)
    {
        var result = missions;
        if (!string.IsNullOrEmpty(factionFilter) && factionFilter != "(すべて)")
        {
            var factionEn = factionFilter;
            var parenIdx = factionFilter.IndexOf('(');
            if (parenIdx > 0) factionEn = factionFilter[(parenIdx + 1)..].TrimEnd(')').Trim();

            result = result.Where(m =>
                m.WikiFaction.Equals(factionEn, StringComparison.OrdinalIgnoreCase) ||
                m.MissionGiver.Contains(factionEn.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ||
                m.FriendlyName.Contains(factionEn.Split(' ')[0], StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }
        if (!string.IsNullOrEmpty(rankFilter) && rankFilter != "(すべて)")
        {
            result = result.Where(m => m.WikiRank == rankFilter).ToList();
        }
        return result;
    }

    public List<MissionCategory> GetCategories()
    {
        var all = LoadAllMissions();
        var counts = new Dictionary<string, int>();
        foreach (var cat in CategoryDefs)
            counts[cat.Category] = 0;
        counts["その他"] = 0;

        foreach (var m in all)
        {
            var cat = Classify(m.RecordName, m.MissionType);
            if (counts.ContainsKey(cat)) counts[cat]++;
            else counts["その他"]++;
        }

        var result = new List<MissionCategory>();
        foreach (var cat in CategoryDefs)
            if (counts[cat.Category] > 0)
                result.Add(new MissionCategory { Name = cat.Category, Count = counts[cat.Category] });
        if (counts["その他"] > 0)
            result.Add(new MissionCategory { Name = "その他", Count = counts["その他"] });
        return result;
    }

    public List<MissionEntry> GetMissions(string category)
    {
        var all = LoadAllMissions();
        var filtered = all.Where(m => Classify(m.RecordName, m.MissionType) == category).ToList();

        foreach (var m in filtered)
            ParseRawJson(m);

        filtered.Sort((a, b) =>
        {
            var d = a.DifficultyOrder.CompareTo(b.DifficultyOrder);
            if (d != 0) return d;
            return a.RewardMax.CompareTo(b.RewardMax);
        });

        return filtered;
    }

    private static readonly Dictionary<string, string> FactionJaMap = new(StringComparer.OrdinalIgnoreCase)
    {
        {"Headhunters", "ヘッドハンターズ"}, {"Bounty Hunters Guild", "バウンティーハンターズ・ギルド"},
        {"Northrock Service Group", "ノースロック"}, {"Adagio Holdings", "アダージョ・ホールディングス"},
        {"Bit Zeros", "ビットゼロズ"}, {"Dead Saints", "デッドセインツ"},
        {"InterSec Defense Solutions", "インターセック"}, {"United Wayfarers Club", "ユナイテッド・ウェイファーラーズ"},
        {"Red Wind Linehaul", "レッドウィンド"}, {"Shubin Interstellar", "シュビン・インターステラー"},
        {"Covalex", "コバレックス"}, {"Crusader Industries", "クルセイダー"},
        {"Hurston Dynamics", "ハーストン"}, {"microTech", "マイクロテック"},
        {"ArcCorp", "アークコープ"}, {"Vaughn", "ヴォーン"},
        {"Ruto", "ルート"}, {"Citizens For Prosperity", "CFP"},
        {"Rayari Incorporated", "レイアリ"}, {"Tar Pits", "タールピッツ"},
        {"Eckhart Security", "エッカート・セキュリティ"}, {"Wildstar Racing", "ワイルドスター"},
        {"Foxwell Enforcement", "フォックスウェル"},
    };

    private static readonly string[] RankNames = { "中立", "ジュニア・コントラクター", "コントラクター", "シニア・コントラクター", "エキスパート", "エリート・コントラクター" };

    private static string GetRankName(int rankIndex) =>
        rankIndex >= 0 && rankIndex < RankNames.Length ? RankNames[rankIndex] : "";

    private static readonly Dictionary<string, string> DifficultyCodeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        {"vlrt", "Very Easy"}, {"lrt", "Easy"}, {"mrt", "Medium"}, {"hrt", "Hard"}, {"vhrt", "Very Hard"}, {"ert", "Super"},
        {"intro", "Intro"}, {"rank0", "Intro"},
    };

    public List<MissionEntry> Search(string query)
    {
        var all = LoadAllMissions();
        foreach (var m in all)
            ParseRawJson(m);

        var matchers = BuildMatchers(query);
        var filtered = all.Where(m =>
        {
            var fields = new[] { m.CleanedName, m.TitleEn, m.TitleJa, m.DisplayNameJa,
                m.DescriptionEn, m.DescriptionJa, m.MissionGiverEn, m.MissionGiverJa,
                m.MissionGiver, m.FriendlyName, m.Title, m.WikiTitle,
                m.WikiFaction, m.WikiFactionJa, m.WikiRank };
            return matchers.All(matcher => fields.Any(f => matcher(f)));
        }).ToList();

        if (filtered.Count == 0)
        {
            var transHits = SearchTranslations(query);
            if (transHits.Count > 0)
            {
                var matchedMissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, en, ja) in transHits)
                {
                    var parts = key.Split('_');
                    var faction = parts[0];
                    string? difficulty = null;
                    foreach (var p in parts)
                        if (DifficultyCodeMap.TryGetValue(p, out var d)) { difficulty = d; break; }

                    foreach (var m in all)
                    {
                        if (!m.FriendlyName.Contains(faction, StringComparison.OrdinalIgnoreCase)) continue;
                        if (difficulty != null && !string.IsNullOrEmpty(m.Difficulty) &&
                            !m.Difficulty.Equals(difficulty, StringComparison.OrdinalIgnoreCase)) continue;
                        matchedMissions.Add(m.RecordName);
                        if (string.IsNullOrEmpty(m.TranslationHint))
                            m.TranslationHint = $"{ja} ({en})";
                    }
                }
                filtered = all.Where(m => matchedMissions.Contains(m.RecordName)).ToList();
            }
        }

        filtered.Sort((a, b) =>
        {
            var d = a.DifficultyOrder.CompareTo(b.DifficultyOrder);
            if (d != 0) return d;
            return a.RewardMax.CompareTo(b.RewardMax);
        });

        return filtered;
    }

    public List<(string key, string en, string ja)> SearchTranslations(string query)
    {
        var results = new List<(string key, string en, string ja)>();
        if (_enDict == null) return results;

        var matcher = BuildSingleMatcher(query);
        foreach (var (key, enVal) in _enDict)
        {
            if (key.StartsWith("@")) continue;
            if (!key.Contains("title", StringComparison.OrdinalIgnoreCase)) continue;
            if (matcher(enVal))
            {
                var ja = _transDict != null && _transDict.TryGetValue(key, out var jaVal) ? jaVal : "";
                results.Add((key, enVal, ja));
            }
        }
        if (_transDict != null)
        {
            foreach (var (key, jaVal) in _transDict)
            {
                if (key.StartsWith("@")) continue;
                if (!key.Contains("title", StringComparison.OrdinalIgnoreCase)) continue;
                if (results.Any(r => r.key.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
                if (matcher(jaVal))
                {
                    var en = _enDict.TryGetValue(key, out var enVal) ? enVal : "";
                    results.Add((key, en, jaVal));
                }
            }
        }
        return results;
    }

    private static List<Func<string, bool>> BuildMatchers(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
            return [BuildSingleMatcher(query)];
        return words.Select(BuildSingleMatcher).ToList();
    }

    private static Func<string, bool> BuildSingleMatcher(string pattern)
    {
        bool startsWithStar = pattern.StartsWith('*');
        bool endsWithStar = pattern.EndsWith('*');
        var core = pattern.Trim('*');
        if (string.IsNullOrEmpty(core))
            return _ => true;

        var coreAlt = core.Contains('_') ? core.Replace('_', ' ') : core.Replace(' ', '_');

        if (startsWithStar && endsWithStar)
            return s => !string.IsNullOrEmpty(s) && (s.Contains(core, StringComparison.OrdinalIgnoreCase) || s.Contains(coreAlt, StringComparison.OrdinalIgnoreCase));
        if (startsWithStar)
            return s => !string.IsNullOrEmpty(s) && (s.EndsWith(core, StringComparison.OrdinalIgnoreCase) || s.EndsWith(coreAlt, StringComparison.OrdinalIgnoreCase));
        if (endsWithStar)
            return s => !string.IsNullOrEmpty(s) && (s.StartsWith(core, StringComparison.OrdinalIgnoreCase) || s.StartsWith(coreAlt, StringComparison.OrdinalIgnoreCase));
        return s => !string.IsNullOrEmpty(s) && (s.Contains(core, StringComparison.OrdinalIgnoreCase) || s.Contains(coreAlt, StringComparison.OrdinalIgnoreCase));
    }

    private List<MissionEntry> LoadAllMissions()
    {
        var list = new List<MissionEntry>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = _hasWikiColumns
            ? "SELECT record_name, title, title_hud, mission_type, difficulty, mission_giver, location_label, description, reward_min, reward_max, required_reputation, lawfulness_type, jurisdiction, time_limit, raw_json, wiki_title, wiki_faction, wiki_reward, wiki_legality, wiki_enemy_min, wiki_enemy_max, wiki_duration_min FROM missions"
            : "SELECT record_name, title, title_hud, mission_type, difficulty, mission_giver, location_label, description, reward_min, reward_max, required_reputation, lawfulness_type, jurisdiction, time_limit, raw_json FROM missions";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rn = r.GetString(0);
            if (rn.Contains("_test", StringComparison.OrdinalIgnoreCase)) continue;
            if (rn.Contains("ReputationTest", StringComparison.OrdinalIgnoreCase)) continue;

            var entry = new MissionEntry
            {
                RecordName = rn,
                Title = SafeStr(r, 1),
                TitleHud = SafeStr(r, 2),
                MissionType = SafeStr(r, 3),
                Difficulty = SafeStr(r, 4),
                MissionGiver = SafeStr(r, 5),
                Location = SafeStr(r, 6),
                Description = SafeStr(r, 7),
                RewardMin = r.IsDBNull(8) ? 0 : r.GetDouble(8),
                RewardMax = r.IsDBNull(9) ? 0 : r.GetDouble(9),
                RequiredReputation = SafeStr(r, 10),
                LawfulnessType = SafeStr(r, 11),
                Jurisdiction = SafeStr(r, 12),
                TimeLimit = SafeStr(r, 13),
                RawJson = SafeStr(r, 14),
                WikiTitle = _hasWikiColumns ? SafeStr(r, 15) : "",
                WikiFaction = _hasWikiColumns ? SafeStr(r, 16) : "",
                WikiReward = _hasWikiColumns && !r.IsDBNull(17) ? r.GetDouble(17) : 0,
                WikiLegality = _hasWikiColumns ? SafeStr(r, 18) : "",
                WikiEnemyMin = _hasWikiColumns && !r.IsDBNull(19) ? r.GetInt32(19) : 0,
                WikiEnemyMax = _hasWikiColumns && !r.IsDBNull(20) ? r.GetInt32(20) : 0,
                WikiDuration = _hasWikiColumns && !r.IsDBNull(21) ? r.GetDouble(21) : 0,
            };

            if (string.IsNullOrEmpty(entry.Difficulty))
                entry.Difficulty = InferDifficulty(rn);
            if (string.IsNullOrEmpty(entry.Location))
                entry.Location = InferLocation(rn);

            entry.DifficultyOrder = DiffOrder(entry.Difficulty);
            entry.FriendlyName = ExtractFriendlyName(rn);
            entry.CleanedName = CleanRecordName(rn);

            // WikiFactionJa / WikiRank を設定
            if (!string.IsNullOrEmpty(entry.WikiFaction) && FactionJaMap.TryGetValue(entry.WikiFaction, out var fja))
                entry.WikiFactionJa = fja;
            if (!string.IsNullOrEmpty(entry.Difficulty))
                entry.WikiRank = GetRankName(DiffOrder(entry.Difficulty) switch { 0 => 0, 1 => 0, 2 => 1, 3 => 2, 4 => 3, 5 => 4, _ => -1 });

            // Wiki ミッション: タイトル・説明から日本語を解決
            if (rn.StartsWith("WikiMission.") && !string.IsNullOrEmpty(entry.Title))
            {
                entry.TitleEn = entry.Title;
                var (titleJa, titleKey) = ResolveJaByEnglishWithKey(entry.Title);
                entry.TitleJa = titleJa;
                if (!string.IsNullOrEmpty(entry.Description))
                    entry.DescriptionEn = entry.Description;
                if (!string.IsNullOrEmpty(titleKey))
                {
                    var descKey = titleKey.Replace("_title_", "_desc_");
                    var descJa = ResolveJa(descKey);
                    if (!string.IsNullOrEmpty(descJa)) entry.DescriptionJa = descJa;
                }
                if (string.IsNullOrEmpty(entry.DescriptionJa) && !string.IsNullOrEmpty(entry.Description))
                    entry.DescriptionJa = ResolveJaByEnglish(entry.Description);
                if (!string.IsNullOrEmpty(entry.MissionGiver))
                    entry.MissionGiverEn = entry.MissionGiver;
            }

            list.Add(entry);
        }
        return list;
    }

    private void ParseRawJson(MissionEntry entry)
    {
        if (string.IsNullOrEmpty(entry.RawJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(entry.RawJson);
            if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv)) return;

            // Resolve English/Japanese title from original @key
            var titleKey = GetString(rv, "title");
            entry.OriginalTitleKey = titleKey;
            if (!string.IsNullOrEmpty(titleKey))
            {
                entry.TitleJa = ResolveJa(titleKey);
                var en = ResolveEn(titleKey);
                if (!IsLocKey(en)) entry.TitleEn = en;
            }

            // Resolve English/Japanese description
            var descKey = GetString(rv, "description");
            if (!string.IsNullOrEmpty(descKey))
            {
                entry.DescriptionJa = ResolveJa(descKey);
                var en = ResolveEn(descKey);
                if (!IsLocKey(en)) entry.DescriptionEn = en;
            }

            // Resolve Japanese mission giver
            var giverKey = GetString(rv, "missionGiver");
            if (!string.IsNullOrEmpty(giverKey))
            {
                var resolved = ResolveJa(giverKey);
                if (!IsLocKey(resolved)) entry.MissionGiverJa = resolved;
                var en = ResolveEn(giverKey);
                if (!IsLocKey(en)) entry.MissionGiverEn = en;
            }

            // Reward details
            if (rv.TryGetProperty("missionReward", out var reward))
            {
                entry.RewardBase = GetDouble(reward, "reward");
                entry.RewardBonusMax = GetDouble(reward, "max");
                entry.PlusBonuses = GetBool(reward, "plusBonuses");
            }
            entry.BuyInAmount = GetDouble(rv, "missionBuyInAmount");

            // Deadline
            if (rv.TryGetProperty("missionDeadline", out var deadline))
            {
                entry.CompletionTimeMinutes = GetDouble(deadline, "missionCompletionTime");
                entry.AutoEnd = GetBool(deadline, "missionAutoEnd");
            }

            // Player settings
            entry.MaxInstances = GetInt(rv, "maxInstances");
            entry.MaxPlayers = GetInt(rv, "maxPlayersPerInstance");
            entry.CanBeShared = GetBool(rv, "canBeShared");
            entry.OnceOnly = GetBool(rv, "onceOnly");
            entry.IsLawful = GetBool(rv, "lawfulMission");
            entry.FailIfCriminal = GetBool(rv, "failIfBecameCriminal");
            entry.FailIfPrison = GetBool(rv, "failIfSentToPrison");
            entry.RespawnTime = GetDouble(rv, "respawnTime");
            entry.CooldownTime = GetDouble(rv, "abandonedCooldownTime");
            entry.NotForRelease = GetBool(rv, "notForRelease");

            // Wanted level prerequisites
            if (rv.TryGetProperty("reputationPrerequisites", out var repPre))
            {
                if (repPre.TryGetProperty("wantedLevel", out var wl))
                {
                    entry.WantedLevelMin = GetDouble(wl, "minValue");
                    entry.WantedLevelMax = GetDouble(wl, "maxValue");
                }
            }

            // Reputation requirements
            if (rv.TryGetProperty("reputationRequirements", out var repReq))
            {
                if (repReq.TryGetProperty("expression", out var expr) && expr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var req in expr.EnumerateArray())
                    {
                        entry.RepRequirements.Add(new ReputationRequirement
                        {
                            Faction = ExtractFileName(GetString(req, "factionReputation")),
                            Scope = ExtractFileName(GetString(req, "reputationScope")),
                            Comparison = GetString(req, "comparison"),
                            Standing = ExtractFileName(GetString(req, "standing")),
                        });
                    }
                }
            }

            // Required missions (prerequisites)
            if (rv.TryGetProperty("requiredMissions", out var reqMissions) && reqMissions.ValueKind == JsonValueKind.Array)
            {
                foreach (var rm in reqMissions.EnumerateArray())
                {
                    var path = rm.GetString() ?? "";
                    if (!string.IsNullOrEmpty(path))
                        entry.RequiredMissions.Add(ExtractFileName(path));
                }
            }

            // Mission giver record
            var giverRec = GetString(rv, "missionGiverRecord");
            if (!string.IsNullOrEmpty(giverRec))
                entry.MissionGiverRecord = ExtractFileName(giverRec);

            // Reputation rewards (success = index 0, abandon = index 2, fail = index 3)
            if (rv.TryGetProperty("missionResultReputationRewards", out var repRewards) && repRewards.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var resultEntry in repRewards.EnumerateArray())
                {
                    if (resultEntry.TryGetProperty("reputationAmounts", out var amounts) && amounts.ValueKind == JsonValueKind.Array)
                    {
                        var list = idx == 0 ? entry.SuccessRepRewards
                                 : idx == 2 ? entry.AbandonRepRewards
                                 : idx == 3 ? entry.FailRepRewards
                                 : null;
                        if (list != null)
                        {
                            foreach (var amt in amounts.EnumerateArray())
                            {
                                list.Add(new ReputationRewardInfo
                                {
                                    Faction = ExtractFileName(GetString(amt, "factionReputation")),
                                    Scope = ExtractFileName(GetString(amt, "reputationScope")),
                                    Amount = ExtractFileName(GetString(amt, "reward")),
                                });
                            }
                        }
                    }
                    idx++;
                }
            }

            // Enemy spawn descriptions
            if (rv.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
            {
                foreach (var prop in props.EnumerateArray())
                {
                    var varName = GetString(prop, "missionVariableName");
                    if (varName != "BP_SpawnDescriptions") continue;
                    if (!prop.TryGetProperty("value", out var val)) continue;
                    if (!val.TryGetProperty("spawnDescriptions", out var descs)) continue;

                    foreach (var desc in descs.EnumerateArray())
                    {
                        var groupName = GetString(desc, "Name");
                        if (!desc.TryGetProperty("ships", out var ships)) continue;

                        foreach (var shipOpt in ships.EnumerateArray())
                        {
                            if (!shipOpt.TryGetProperty("options", out var opts)) continue;
                            foreach (var opt in opts.EnumerateArray())
                            {
                                entry.EnemySpawns.Add(new SpawnGroupInfo
                                {
                                    GroupName = groupName,
                                    ConcurrentAmount = GetInt(opt, "concurrentAmount"),
                                    Weight = GetDouble(opt, "weight", 1.0),
                                });
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static string Classify(string recordName, string missionType)
    {
        var rn = recordName ?? "";
        var t = CleanType(missionType ?? "");
        foreach (var (cat, match) in CategoryDefs)
        {
            if (match(rn, t))
                return cat;
        }
        return "その他";
    }

    private static string CleanType(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (!raw.Contains('/')) return raw.ToLowerInvariant();
        var last = raw.Split('/').Last();
        if (last.EndsWith(".json")) last = last[..^5];
        if (last.StartsWith("missiontype.")) last = last[12..];
        return last.ToLowerInvariant();
    }

    private static string ExtractFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var idx = path.LastIndexOf('/');
        var name = idx >= 0 ? path[(idx + 1)..] : path;
        if (name.EndsWith(".json")) name = name[..^5];
        return name;
    }

    private static string ExtractFriendlyName(string recordName)
    {
        if (!recordName.StartsWith("MissionBrokerEntry.")) return recordName;
        return recordName["MissionBrokerEntry.".Length..];
    }

    private static string CleanRecordName(string rn)
    {
        if (rn.StartsWith("MissionBrokerEntry.")) rn = rn["MissionBrokerEntry.".Length..];
        if (rn.StartsWith("PU_")) rn = rn[3..];
        foreach (var p in StripPrefixes)
            if (rn.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            { rn = rn[p.Length..]; break; }
        return rn.Replace('_', ' ').Trim();
    }

    internal static bool IsLocKeyStatic(string s) => IsLocKey(s);

    private static int DiffOrder(string difficulty) => (difficulty ?? "").ToLowerInvariant() switch
    {
        "intro" => 0,
        "very easy" => 1,
        "easy" => 2,
        "medium" => 3,
        "hard" => 4,
        "very hard" => 5,
        _ => 99,
    };

    private static string InferDifficulty(string rn)
    {
        foreach (var p in rn.Split('_'))
        {
            if (p.Equals("Intro", StringComparison.OrdinalIgnoreCase)) return "Intro";
            if (p.Equals("VEasy", StringComparison.OrdinalIgnoreCase) || p.Equals("VeryEasy", StringComparison.OrdinalIgnoreCase)) return "Very Easy";
            if (p.Equals("Easy", StringComparison.OrdinalIgnoreCase)) return "Easy";
            if (p.Equals("Medium", StringComparison.OrdinalIgnoreCase)) return "Medium";
            if (p.Equals("Hard", StringComparison.OrdinalIgnoreCase)) return "Hard";
            if (p.Equals("VHard", StringComparison.OrdinalIgnoreCase) || p.Equals("VeryHard", StringComparison.OrdinalIgnoreCase)) return "Very Hard";
        }
        return "";
    }

    private static string InferLocation(string rn)
    {
        if (rn.Contains("Stanton1", StringComparison.OrdinalIgnoreCase)) return "Stanton (Hurston)";
        if (rn.Contains("Stanton2", StringComparison.OrdinalIgnoreCase)) return "Stanton (Crusader)";
        if (rn.Contains("Stanton3", StringComparison.OrdinalIgnoreCase)) return "Stanton (ArcCorp)";
        if (rn.Contains("Stanton4", StringComparison.OrdinalIgnoreCase)) return "Stanton (microTech)";
        if (rn.Contains("Pyro", StringComparison.OrdinalIgnoreCase)) return "Pyro";
        if (rn.Contains("Nyx", StringComparison.OrdinalIgnoreCase)) return "Nyx";
        return "";
    }

    public string FormatDetail(MissionEntry m)
    {
        var lines = new List<string>();

        lines.Add("══════ ミッション詳細 ══════\n");

        lines.Add("■ 基本情報");
        lines.Add($"  レコード名: {m.FriendlyName}");
        if (!string.IsNullOrEmpty(m.TitleJa) && !IsLocKey(m.TitleJa))
            lines.Add($"  タイトル (日本語): {m.TitleJa}");
        var englishTitle = !string.IsNullOrEmpty(m.TitleEn) ? m.TitleEn : m.Title;
        if (!IsLocKey(englishTitle) && !string.IsNullOrEmpty(englishTitle))
            lines.Add($"  タイトル (英語): {englishTitle}");
        if (!IsLocKey(m.TitleHud) && !string.IsNullOrEmpty(m.TitleHud) && m.TitleHud != m.Title)
            lines.Add($"  HUDタイトル: {m.TitleHud}");
        if (!string.IsNullOrEmpty(m.Difficulty))
            lines.Add($"  難易度: {m.Difficulty}");
        if (!string.IsNullOrEmpty(m.MissionGiverJa))
            lines.Add($"  依頼者: {m.MissionGiverJa}" + (!IsLocKey(m.MissionGiver) && m.MissionGiver != m.MissionGiverJa ? $" ({m.MissionGiver})" : ""));
        else if (!IsLocKey(m.MissionGiver) && !string.IsNullOrEmpty(m.MissionGiver))
            lines.Add($"  依頼者: {m.MissionGiver}");
        if (!string.IsNullOrEmpty(m.MissionGiverRecord))
            lines.Add($"  依頼元: {FormatGiverRecord(m.MissionGiverRecord)}");
        if (!string.IsNullOrEmpty(m.Location))
            lines.Add($"  場所: {m.Location}");
        lines.Add($"  合法性: {(m.IsLawful ? "合法" : "非合法")}");
        if (!string.IsNullOrEmpty(m.TranslationHint))
            lines.Add($"  ※ 検索一致タイトル: {m.TranslationHint}");
        if (!string.IsNullOrEmpty(m.WikiTitle))
            lines.Add($"  Wiki タイトル: {m.WikiTitle}");
        if (!string.IsNullOrEmpty(m.WikiFaction))
        {
            var factionDisplay = !string.IsNullOrEmpty(m.WikiFactionJa) ? $"{m.WikiFactionJa} ({m.WikiFaction})" : m.WikiFaction;
            lines.Add($"  ファクション: {factionDisplay}");
        }
        if (!string.IsNullOrEmpty(m.WikiRank))
            lines.Add($"  ランク: {m.WikiRank}");
        if (!string.IsNullOrEmpty(m.WikiLegality))
            lines.Add($"  合法性 (Wiki): {m.WikiLegality}");
        if (m.WikiEnemyMin > 0 || m.WikiEnemyMax > 0)
            lines.Add($"  敵数 (Wiki): {m.WikiEnemyMin}-{m.WikiEnemyMax}");
        if (m.WikiDuration > 0)
            lines.Add($"  所要時間 (Wiki): {m.WikiDuration:0}分");
        if (m.NotForRelease)
            lines.Add("  ※ 未リリース (開発中)");

        var titleVariants = FindTitleVariants(m.Title);
        if (titleVariants.Count > 0)
        {
            lines.Add("");
            lines.Add("■ タイトルバリアント (ランタイム選択)");
            foreach (var (key, en, ja) in titleVariants)
            {
                var display = !string.IsNullOrEmpty(ja) ? $"{ja} ({en})" : en;
                lines.Add($"  - {display}");
            }
        }

        lines.Add("");
        lines.Add("■ 報酬");
        if (m.RewardBase > 0)
            lines.Add($"  基本報酬: {m.RewardBase:N0} aUEC");
        else if (m.RewardMax > 0 || m.RewardMin > 0)
        {
            if (m.RewardMin > 0 && m.RewardMax > 0 && m.RewardMin != m.RewardMax)
                lines.Add($"  報酬: {m.RewardMin:N0} - {m.RewardMax:N0} aUEC");
            else
                lines.Add($"  報酬: {Math.Max(m.RewardMin, m.RewardMax):N0} aUEC");
        }
        if (m.RewardBonusMax > 0)
            lines.Add($"  最大ボーナス: {m.RewardBonusMax:N0} aUEC");
        if (m.PlusBonuses)
            lines.Add("  レピュテーションボーナスあり");
        if (m.BuyInAmount > 0)
            lines.Add($"  参加費: {m.BuyInAmount:N0} aUEC");

        if (m.RepRequirements.Count > 0)
        {
            lines.Add("");
            lines.Add("■ レピュテーション要件");
            foreach (var req in m.RepRequirements)
            {
                var faction = FormatFaction(req.Faction);
                var scope = FormatScope(req.Scope);
                var standing = FormatStanding(req.Standing);
                lines.Add($"  {faction} [{scope}]: {standing} {FormatComparison(req.Comparison)}");
            }
        }

        if (m.WantedLevelMax > 0 && m.WantedLevelMax < 5)
        {
            lines.Add("");
            lines.Add("■ 犯罪レベル制限");
            lines.Add($"  許容犯罪レベル: {m.WantedLevelMin:0} - {m.WantedLevelMax:0}");
        }

        if (m.RequiredMissions.Count > 0)
        {
            lines.Add("");
            lines.Add("■ 解放条件 (前提ミッション)");
            foreach (var rm in m.RequiredMissions)
                lines.Add($"  - {FormatMissionName(rm)}");
        }

        if (m.SuccessRepRewards.Count > 0)
        {
            lines.Add("");
            lines.Add("■ レピュテーション報酬 (成功時)");
            foreach (var rr in m.SuccessRepRewards)
            {
                var amount = FormatRepAmount(rr.Amount);
                lines.Add($"  {FormatFaction(rr.Faction)} [{FormatScope(rr.Scope)}]: {amount}");
            }
        }

        if (m.FailRepRewards.Count > 0)
        {
            lines.Add("");
            lines.Add("■ レピュテーション変動 (失敗時)");
            foreach (var rr in m.FailRepRewards)
            {
                var amount = FormatRepAmount(rr.Amount);
                lines.Add($"  {FormatFaction(rr.Faction)} [{FormatScope(rr.Scope)}]: {amount}");
            }
        }

        if (m.EnemySpawns.Count > 0)
        {
            lines.Add("");
            lines.Add("■ 敵の構成");
            var groups = m.EnemySpawns.GroupBy(s => s.GroupName);
            foreach (var g in groups)
            {
                var groupLabel = g.Key switch
                {
                    "Target" => "ターゲット",
                    "Reinforcement" => "増援",
                    _ => g.Key
                };
                var spawns = g.ToList();
                if (spawns.Count == 1)
                {
                    lines.Add($"  {groupLabel}: {spawns[0].ConcurrentAmount}機");
                }
                else
                {
                    var parts = spawns.Select(s => $"{s.ConcurrentAmount}機 (確率{s.Weight:P0})");
                    lines.Add($"  {groupLabel}: {string.Join(" / ", parts)}");
                }
            }
        }

        lines.Add("");
        lines.Add("■ ミッション設定");
        if (m.CompletionTimeMinutes > 0)
            lines.Add($"  制限時間: {m.CompletionTimeMinutes:0}分");
        if (m.MaxPlayers > 0)
            lines.Add($"  最大プレイヤー数: {m.MaxPlayers}");
        else if (m.MaxPlayers == -1)
            lines.Add("  最大プレイヤー数: 無制限");
        if (m.MaxInstances > 0)
            lines.Add($"  最大インスタンス数: {m.MaxInstances}");
        lines.Add($"  パーティ共有: {(m.CanBeShared ? "可" : "不可")}");
        if (m.OnceOnly)
            lines.Add("  一度きり: はい");
        lines.Add($"  犯罪者化で失敗: {(m.FailIfCriminal ? "はい" : "いいえ")}");
        lines.Add($"  収監で失敗: {(m.FailIfPrison ? "はい" : "いいえ")}");
        if (m.RespawnTime > 0)
            lines.Add($"  リスポーン間隔: {m.RespawnTime:0}分");
        if (m.CooldownTime > 0)
            lines.Add($"  放棄後クールダウン: {m.CooldownTime:0}分");

        if (!string.IsNullOrEmpty(m.DescriptionJa) && !IsLocKey(m.DescriptionJa))
        {
            lines.Add("");
            lines.Add("■ 説明 (日本語)");
            foreach (var dl in m.DescriptionJa.Split('\n'))
                lines.Add($"  {dl.TrimEnd()}");
        }
        if (!IsLocKey(m.Description) && !string.IsNullOrEmpty(m.Description))
        {
            lines.Add("");
            lines.Add("■ 説明 (英語)");
            foreach (var dl in m.Description.Replace("\\n", "\n").Split('\n'))
                lines.Add($"  {dl.TrimEnd()}");
        }

        return string.Join("\n", lines);
    }

    private static bool IsLocKey(string s) =>
        string.IsNullOrEmpty(s) || s.StartsWith("@") || s.StartsWith("LOC_") ||
        s.Contains("LOC_UNINITIALIZED") || s.Contains("LOC_EMPTY") || s.Contains("procedural_text_null") ||
        s.Contains("UNINITIALIZED");

    private static bool ContainsJapanese(string s) =>
        !string.IsNullOrEmpty(s) && s.Any(c => c >= '　' && c <= '鿿' || c >= '＀' && c <= '￯');


    private static string FormatFaction(string f)
    {
        if (f.StartsWith("factionreputation_")) f = f["factionreputation_".Length..];
        return f switch
        {
            "microtech" => "microTech",
            "hurston" => "Hurston",
            "crusader" => "Crusader",
            "arccorp" => "ArcCorp",
            "lawful_bountyhuntersguild" => "Bounty Hunters Guild",
            "lawful_mercenariesguild" => "Mercenaries Guild",
            "unlawful_headhuntersguild" => "Headhunters Guild",
            "unlawful_wildstar" => "WildStar",
            "pyro" => "Pyro",
            _ => f,
        };
    }

    private static string FormatScope(string s)
    {
        if (s.StartsWith("reputationscope_")) s = s["reputationscope_".Length..];
        return s switch
        {
            "affinity" => "友好度",
            "bounty" => "賞金稼ぎ",
            "bounty_bountyhuntersguild" => "賞金稼ぎギルド",
            "mercenary" => "傭兵",
            "mercenary_mercenariesguild" => "傭兵ギルド",
            "delivery" => "配達",
            "salvage" => "サルベージ",
            _ => s,
        };
    }

    private static string FormatStanding(string s)
    {
        if (s.StartsWith("reputationstanding_")) s = s["reputationstanding_".Length..];
        var parts = s.Split('_');
        if (parts.Length >= 2)
            return parts.Last() switch
            {
                "applicant" => "Applicant (志望者)",
                "contractor" => "Contractor (請負人)",
                "journeyman" => "Journeyman (中堅)",
                "professional" => "Professional (専門家)",
                "expert" => "Expert (熟練者)",
                "master" => "Master (達人)",
                _ => parts.Last(),
            };
        return s;
    }

    private static string FormatComparison(string c) => c switch
    {
        "GreaterThan" => "以上",
        "LessThan" => "未満",
        "EqualTo" => "と等しい",
        _ => c,
    };

    private static string FormatRepAmount(string a)
    {
        if (a.StartsWith("reputationrewardamount_")) a = a["reputationrewardamount_".Length..];
        if (a == "zero") return "±0";
        var positive = a.StartsWith("positive_");
        var negative = a.StartsWith("negative_");
        var size = a.Split('_').Last().ToUpperInvariant();
        var sign = positive ? "+" : negative ? "-" : "";
        return $"{sign}{size}";
    }

    private static string FormatMissionName(string name)
    {
        if (name.StartsWith("pu_")) name = name[3..];
        return name.Replace("_", " ").Trim();
    }

    private static string FormatGiverRecord(string name)
    {
        return name.Replace("_", " ").Trim();
    }

    private List<(string key, string en, string ja)> FindTitleVariants(string titleKey)
    {
        var results = new List<(string key, string en, string ja)>();
        if (string.IsNullOrEmpty(titleKey) || _enDict == null) return results;
        var prefix = titleKey.TrimEnd("0123456789".ToCharArray()).TrimEnd('_');
        if (prefix.Length < 5) return results;
        foreach (var (key, enVal) in _enDict)
        {
            if (key.StartsWith("@")) continue;
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!key.Contains("title", StringComparison.OrdinalIgnoreCase)) continue;
            var ja = _transDict != null && _transDict.TryGetValue(key, out var jaVal) ? jaVal : "";
            results.Add((key, enVal, ja));
        }
        return results;
    }

    private string ResolveJa(string raw)
    {
        if (_transDict == null) return "";
        var val = ResolveLoc(raw, _transDict);
        if (!string.IsNullOrEmpty(val) && ContainsJapanese(val)) return val;
        return "";
    }

    private (string ja, string key) ResolveJaByEnglishWithKey(string english)
    {
        if (_enDict == null || _transDict == null || string.IsNullOrEmpty(english)) return ("", "");
        foreach (var (key, enVal) in _enDict)
        {
            if (key.StartsWith("@")) continue;
            if (!enVal.Equals(english, StringComparison.OrdinalIgnoreCase)) continue;
            if (_transDict.TryGetValue(key, out var ja) && !string.IsNullOrEmpty(ja) && ContainsJapanese(ja))
                return (ja, key);
        }
        return ("", "");
    }

    private string ResolveJaByEnglish(string english)
    {
        if (_enDict == null || _transDict == null || string.IsNullOrEmpty(english)) return "";
        foreach (var (key, enVal) in _enDict)
        {
            if (key.StartsWith("@")) continue;
            if (!enVal.Equals(english, StringComparison.OrdinalIgnoreCase)) continue;
            if (_transDict.TryGetValue(key, out var ja) && !string.IsNullOrEmpty(ja) && ContainsJapanese(ja))
                return ja;
        }
        // 長文の場合: 先頭50文字で前方一致
        if (english.Length > 50)
        {
            var prefix = english[..50];
            foreach (var (key, enVal) in _enDict)
            {
                if (key.StartsWith("@")) continue;
                if (!enVal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (_transDict.TryGetValue(key, out var ja) && !string.IsNullOrEmpty(ja) && ContainsJapanese(ja))
                    return ja;
            }
        }
        return "";
    }

    private string ResolveEn(string raw)
    {
        if (_enDict == null) return "";
        return ResolveLoc(raw, _enDict);
    }

    private static string ResolveLoc(string raw, Dictionary<string, string> dict)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string? val = null;
        if (dict.TryGetValue(raw, out var v1) && !string.IsNullOrEmpty(v1)) val = v1;
        else if (!raw.StartsWith("@") && dict.TryGetValue("@" + raw, out var v2) && !string.IsNullOrEmpty(v2)) val = v2;
        else if (raw.StartsWith("@") && dict.TryGetValue(raw[1..], out var v3) && !string.IsNullOrEmpty(v3)) val = v3;
        if (val == null) return "";
        return val.Replace("\\n", "\n");
    }

    private static string SafeStr(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? "" : r.GetString(i);

    private static string GetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    private static double GetDouble(JsonElement el, string prop, double fallback = 0)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return fallback;
    }

    private static int GetInt(JsonElement el, string prop, int fallback = 0)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        return fallback;
    }

    private static bool GetBool(JsonElement el, string prop, bool fallback = false)
    {
        if (el.TryGetProperty(prop, out var v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        return fallback;
    }

    public void Dispose()
    {
        _conn?.Close();
        _conn?.Dispose();
    }

    public class MissionCategory
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public override string ToString() => $"{Name} ({Count})";
    }

    public class MissionEntry
    {
        public string RecordName { get; set; } = "";
        public string FriendlyName { get; set; } = "";
        public string CleanedName { get; set; } = "";
        public string TitleJa { get; set; } = "";
        public string DescriptionJa { get; set; } = "";
        public string OriginalTitleKey { get; set; } = "";
        public string MissionGiverJa { get; set; } = "";
        public string DisplayNameJa
        {
            get
            {
                var ja = !string.IsNullOrEmpty(TitleJa) && !IsLocKeyStatic(TitleJa) && ContainsJapanese(TitleJa) ? TitleJa : "";
                if (NotForRelease && !string.IsNullOrEmpty(ja)) return $"{ja} (開発中)";
                if (NotForRelease && string.IsNullOrEmpty(ja)) return "(開発中)";
                return ja;
            }
        }
        public string TitleEn { get; set; } = "";
        public string DescriptionEn { get; set; } = "";
        public string MissionGiverEn { get; set; } = "";
        public string DisplayNameEn
        {
            get
            {
                var en = !string.IsNullOrEmpty(WikiTitle) ? WikiTitle
                       : !string.IsNullOrEmpty(TitleEn) ? TitleEn : CleanedName;
                return !string.IsNullOrEmpty(TitleJa) && !IsLocKeyStatic(TitleJa) ? $"({en})" : en;
            }
        }
        public string Title { get; set; } = "";
        public string TitleHud { get; set; } = "";
        public string MissionType { get; set; } = "";
        public string Difficulty { get; set; } = "";
        public int DifficultyOrder { get; set; }
        public string MissionGiver { get; set; } = "";
        public string Location { get; set; } = "";
        public string Description { get; set; } = "";
        public double RewardMin { get; set; }
        public double RewardMax { get; set; }
        public string RequiredReputation { get; set; } = "";
        public string LawfulnessType { get; set; } = "";
        public string Jurisdiction { get; set; } = "";
        public string TimeLimit { get; set; } = "";
        public string RawJson { get; set; } = "";

        // Parsed from raw_json
        public double RewardBase { get; set; }
        public double RewardBonusMax { get; set; }
        public bool PlusBonuses { get; set; }
        public double BuyInAmount { get; set; }
        public double CompletionTimeMinutes { get; set; }
        public bool AutoEnd { get; set; }
        public int MaxInstances { get; set; }
        public int MaxPlayers { get; set; }
        public bool CanBeShared { get; set; }
        public bool OnceOnly { get; set; }
        public bool IsLawful { get; set; }
        public bool FailIfCriminal { get; set; }
        public bool FailIfPrison { get; set; }
        public bool NotForRelease { get; set; }
        public double RespawnTime { get; set; }
        public double CooldownTime { get; set; }
        public double WantedLevelMin { get; set; }
        public double WantedLevelMax { get; set; }
        public string MissionGiverRecord { get; set; } = "";
        public string TranslationHint { get; set; } = "";
        public string WikiTitle { get; set; } = "";
        public string WikiFaction { get; set; } = "";
        public string WikiFactionJa { get; set; } = "";
        public string WikiRank { get; set; } = "";
        public double WikiReward { get; set; }
        public string WikiLegality { get; set; } = "";
        public int WikiEnemyMin { get; set; }
        public int WikiEnemyMax { get; set; }
        public double WikiDuration { get; set; }


        public List<string> RequiredMissions { get; set; } = new();
        public List<ReputationRequirement> RepRequirements { get; set; } = new();
        public List<ReputationRewardInfo> SuccessRepRewards { get; set; } = new();
        public List<ReputationRewardInfo> AbandonRepRewards { get; set; } = new();
        public List<ReputationRewardInfo> FailRepRewards { get; set; } = new();
        public List<SpawnGroupInfo> EnemySpawns { get; set; } = new();

        public string DisplayReward =>
            RewardBase > 0 ? $"{RewardBase:N0}"
            : RewardMax > 0 ? $"{RewardMax:N0}"
            : RewardMin > 0 ? $"{RewardMin:N0}"
            : "-";
    }

    public class ReputationRequirement
    {
        public string Faction { get; set; } = "";
        public string Scope { get; set; } = "";
        public string Comparison { get; set; } = "";
        public string Standing { get; set; } = "";
    }

    public class ReputationRewardInfo
    {
        public string Faction { get; set; } = "";
        public string Scope { get; set; } = "";
        public string Amount { get; set; } = "";
    }

    public class SpawnGroupInfo
    {
        public string GroupName { get; set; } = "";
        public int ConcurrentAmount { get; set; }
        public double Weight { get; set; }
    }
}
