using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    private List<MissionEntry>? _allCache;
    private Dictionary<string, (string ja, string key)>? _enExactIndex;
    private Dictionary<string, string>? _enPrefix50Index;

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

    // カテゴリはゲームのコントラクトマネージャーの区分と同じにする。ゲームは契約の MissionType レコード
    // (MBE 行の type、CG 行の missionType / missionTypeOverride。抽出時に missions.mission_type へ入れたもの) の
    // LocalisedTypeName で区分を出すので、mission_type → その語 (global.ini の日本語) で分ける。
    // 語は Game2.dcb の MissionType レコードの LocalisedTypeName を japanese_(japan)/global.ini で引いたもの (キーは各行の注記)。
    // 同じ語のレコード (Investigation と Research) は 1 つのカテゴリにまとめる。一覧の並びはこの順
    // (RSI ディスカバリーイベントを先頭に、次にコントラクトマネージャーの案内 Journal_General_ContractManagerTutorial_Content の 7 区分の順)。
    // Wiki だけの行 (WikiMission.*) の種類は Wiki の分類でゲームのデータではないので、Escort (語が @LOC_UNINITIALIZED) と同じく「その他」
    private static readonly (string Category, string[] Types)[] CategoryDefs =
    {
        ("RSIディスカバリーイベント", new[] { "iasi" }),                      // iasi_Journal_Title
        ("アポイントメント", new[] { "appointment" }),                         // mobiglas_ui_Appointment
        ("バウンティハンター", new[] { "bountyhunter" }),                       // mobiglas_ui_BountyHunter
        ("ECN警報", new[] { "ecn" }),                                          // mobiglas_ui_ECN
        ("配達", new[] { "courier" }),                                         // mobiGlas_ui_MissionType_Courier
        ("調査", new[] { "investigation", "research" }),                       // mobiglas_ui_Investigation / mobiGlas_ui_MissionType_Research
        ("メンテナンス", new[] { "maintenance" }),                             // mobiglas_ui_Maintenance
        ("傭兵", new[] { "mercenary" }),                                       // mobiglas_ui_Mercenary
        ("デリバリー", new[] { "delivery" }),                                  // mobiGlas_ui_MissionType_Delivery
        ("輸送", new[] { "hauling" }),                                         // mobiGlas_ui_MissionType_Hauling
        ("輸送 - ローカル", new[] { "hauling_local" }),                        // mobiGlas_ui_MissionType_Hauling_Local
        ("輸送 - 惑星間", new[] { "hauling_planetary" }),                      // mobiGlas_ui_MissionType_Hauling_Planetary
        ("輸送 - 恒星系内", new[] { "hauling_solar" }),                        // mobiGlas_ui_MissionType_Hauling_Solar
        ("輸送 - 星間", new[] { "hauling_interstellar" }),                     // mobiGlas_ui_MissionType_Hauling_Interstellar
        ("優先", new[] { "priority" }),                                        // mobiglas_ui_Priority
        ("リソースドライブ - Crusader", new[] { "priority_crusader" }),        // mobiglas_ui_Priority_Crusader
        ("リソースドライブ - MicroTech", new[] { "priority_microtech" }),      // mobiglas_ui_Priority_MicroTech
        ("リソースドライブ - ArcCorp", new[] { "priority_arccorp" }),          // mobiglas_ui_Priority_ArcCorp
        ("リソースドライブ - Hurston", new[] { "priority_hurston" }),          // mobiglas_ui_Priority_Hurston
        ("優先 - CFP", new[] { "priority_cfp" }),                              // FFFinale_mobiglas_ui_Priority_CFP
        ("優先 - HH", new[] { "priority_hh" }),                                // FFFinale_mobiglas_ui_Priority_HH
        ("サルベージ", new[] { "salvage" }),                                   // mobiglas_ui_Salvage
        ("採掘", new[] { "mining" }),                                          // mobiglas_ui_Mining
        ("船舶採掘", new[] { "shipmining" }),                                  // mobiglas_ui_ShipMining
        ("地上車両マイニング", new[] { "groundmining" }),                      // mobiglas_ui_GroundMining
        ("ハンドマイニング", new[] { "fpsmining" }),                           // mobiglas_ui_FPSMining
        ("回収", new[] { "collection" }),                                      // mobiGlas_ui_MissionType_Collection
        ("捜索", new[] { "search" }),                                          // mobiGlas_ui_MissionType_Search
        ("救助", new[] { "rescue" }),                                          // mobiGlas_ui_MissionType_Rescue
        ("燃料補給中", new[] { "refueling" }),                                 // refueling_ui_Login_Refuel
        ("サービスビーコン", new[] { "servicebeacon" }),                       // mobiGlas_ui_MissionType_ServiceBeacon
        ("レース", new[] { "race" }),                                          // mobiglas_ui_Race
        ("PvPミッション", new[] { "pvpmission" }),                             // mobiglas_ui_PVPMissions
        ("Battaglia", new[] { "battaglia" }),                                  // mobiGlas_ui_MissionType_Battaglia
        ("Wikelo - 乗り物", new[] { "wikelo_ships" }),                         // ContractManager_TempTab_Vehicles
        ("Wikelo - その他のアイテム", new[] { "wikelo_smallitems" }),          // ContractManager_TempTab_Small_Items
        ("サバイバル", new[] { "survival" }),                                  // mobiGlas_ui_MissionType_Survival
        ("キル獲得", new[] { "collector" }),                                   // ea_ui_scoring_KillCollected
        ("ローカル", new[] { "local" }),                                       // chat_command_local
        ("ジョブ", new[] { "job" }),                                           // mobiGlas_ui_MissionType_Job
    };

    // mission_type (CleanType 後) → カテゴリ名
    private static readonly Dictionary<string, string> CategoryByType =
        CategoryDefs.SelectMany(d => d.Types.Select(t => (t, d.Category))).ToDictionary(x => x.t, x => x.Category, StringComparer.Ordinal);

    public int TransDictCount => _transDict?.Count ?? 0;

    // ゲームデータ DB (gamedata_cache.db) のあるフォルダ。詳細の「報酬の BP」を［BP］タブと同じ読み方で出すのに使う (BlueprintDetail)
    private readonly string _workDir;
    // ゲームデータ DB のパス。Wikelo の納品物・入手場所は、この DB を別の接続で開いて読む (裏の処理から呼ぶので _conn を使わない)
    private readonly string _dbPath;

    public MissionService(string dbPath, string? translationDbPath = null)
    {
        _workDir = Path.GetDirectoryName(dbPath) ?? "";
        _dbPath = dbPath;
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

        LoadFactionNames();
    }

    // 勢力の表示名 (record_name → 日本語名、無ければ英語名)。FormatFaction の手書きの対応表に無い勢力に使う。
    // FormatFaction は static (MissionEntry.SuccessRepSummary からも呼ばれる) なので static に持つ
    private static Dictionary<string, string>? _factionNames;

    // gamedata_cache.db の faction_reputation(record_name, display_key) を読み、display_key を
    // 読み込み済みの翻訳 (_transDict → _enDict) で引いて辞書にする。ミッションの読み込み時に1回だけ
    private void LoadFactionNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT record_name, display_key FROM faction_reputation";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0) || r.IsDBNull(1)) continue;
                var rec = r.GetString(0);
                var key = r.GetString(1);
                if (string.IsNullOrEmpty(rec) || string.IsNullOrEmpty(key)) continue;
                string? name = null;
                if (_transDict != null && _transDict.TryGetValue(key, out var ja) && !string.IsNullOrEmpty(ja)) name = ja;
                else if (_enDict != null && _enDict.TryGetValue(key, out var en) && !string.IsNullOrEmpty(en)) name = en;
                if (name != null) map[rec] = name;
            }
        }
        catch { /* faction_reputation が無い古い DB では引かず、今までどおり ID をそのまま出す */ }
        _factionNames = map;

        // 評判の範囲の表示名: 範囲のレコード (SReputationScopeParams) の displayName の語を翻訳で引く (日本語 → 英語)
        var scopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? Lookup(string key)
        {
            if (_transDict != null && _transDict.TryGetValue(key, out var ja) && !string.IsNullOrEmpty(ja)) return ja;
            if (_enDict != null && _enDict.TryGetValue(key, out var en) && !string.IsNullOrEmpty(en)) return en;
            return null;
        }
        foreach (var (rec, key) in ScopeDisplayKeys)
            if (Lookup(key) is { } n) scopes[rec] = n;
        // displayName の語に訳が無い範囲は、代わりの語 (ScopeFallbackKeys)
        foreach (var (rec, key) in ScopeFallbackKeys)
            if (!scopes.ContainsKey(rec) && Lookup(key) is { } n) scopes[rec] = n;
        _scopeNames = scopes;

        var standings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rec, key) in StandingDisplayKeys)
            if (Lookup(key) is { } n) standings[rec] = n;
        _standingNames = standings;
        var stances = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (word, key) in new[] { ("neutral", "mobiGlas_Reputation_Stance_Neutral"), ("ally", "mobiGlas_Reputation_Stance_Ally"), ("hostile", "mobiGlas_Reputation_Stance_Hostile") })
            if (Lookup(key) is { } n) stances[word] = n;
        _stanceWords = stances;
    }

    // 評判の範囲の表示名 (レコード名 → 名前)。FormatScope の手書きの対応より先に使う
    private static Dictionary<string, string>? _scopeNames;

    // 範囲のレコード名 → displayName の語。Data.p4k の dcb query "SReputationScopeParams" の displayName を写したもの
    // (displayName が @LOC_PLACEHOLDER のものは入れない。FormatScope の手書きの対応かレコード名で出す)
    private static readonly (string Record, string Key)[] ScopeDisplayKeys =
    {
        ("reputationscope_emergency", "RepScope_Emergency_Name"),
        ("reputationscope_hauling", "RepScope_TransportGuild_Name"),
        ("reputationscope_wikelo", "RepScope_Barter_Name"),
        ("reputationscope_courier", "RepScope_Courier_Name"),
        ("reputationscope_affinity", "RepScope_MissionGiver_Default_Name"),
        ("reputationscope_technician", "RepScope_Technician_Name"),
        ("reputationscope_hiredmuscle", "RepScope_HiredMuscle_Name"),
        ("reputationscope_bounty", "RepScope_Bounty_Name"),
        ("missionproviderreputationscope_battaglia", "mobiGlas_Reputation_Standing"),
        ("factionreputationscope", "mobiGlas_Reputation_Standing"),
        ("reputationscope_worker", "RepScope_Worker_Name"),
        ("reputationscope_npc_reliability", "mobiGlas_Reputation_ScopeTitle_Reliability"),
        ("reputationscope_assassination", "RepScope_Assassination_Name"),
        ("reputationscope_handyman", "mobiglas_ui_Salvage"),
        ("reputationscope_security", "RepScope_Security_Name"),
        ("reputationscope_salvage", "RepScope_Salvaging_Name"),
        ("reputationscope_theft", "RepScope_Theft_Name"),
        ("reputationscope_maintenance", "RepScope_Maintenance_Name"),
        ("reputationscope_smuggling", "RepScope_HiredMuscle_Name"),
        ("reputationscope_shipcombat", "RepScope_ShipCombat_Name"),
        ("reputationscope_fpscombat", "RepScope_FPSCombat_Name"),
        ("reputationscope_racing_wheeledtimetrial", "Racing_Wheeled_DisplayName"),
        ("reputationscope_racing_gravlevtimetrial", "Racing_Hover_DisplayName"),
        ("reputationscope_racing_shiptimetrial", "Racing_Ship_DisplayName"),
        ("reputationscope_racing_headhunter", "RepScope_Racing_HeadHunters_Name"),
        ("reputationscope_delivery_citizensforpyro", "RepScope_Courier_Name"),
        ("reputationscope_delivery_roughandready", "RepScope_Courier_Name"),
        ("reputationscope_courier_transportguild", "RepScope_Courier_Name"),
        ("reputationscope_bounty_bountyhuntersguild", "RepScope_Bounty_Name"),
        ("reputationscope_worker_roughandready", "RepScope_HiredMuscle_Name"),
        ("reputationscope_handyman_citizensforpyro", "RepScope_HiredMuscle_Name"),
        ("reputationscope_security_mercenaryguild", "RepScope_Security_Name"),
        ("reputationscope_shipcombat_roughandready", "RepScope_ShipCombat_RoughAndReady_Name"),
        ("reputationscope_shipcombat_headhunters", "RepScope_ShipCombat_HeadHunters_Name"),
        ("reputationscope_shipcombat_xenothreat", "RepScope_ShipCombat_XenoThreat_Name"),
        ("reputationscope_fpscombat_citizensforpyro", "mobiglas_ui_Mercenary"),
        ("reputationscope_fpscombat_xenothreat", "mobiglas_ui_Mercenary"),
        ("reputationscope_fpscombat_roughandready", "mobiglas_ui_Mercenary"),
        ("reputationscope_fpscombat_headhunter", "mobiglas_ui_Mercenary"),
    };

    // displayName の語に訳が無い範囲 (global.ini にも translations.db にも無い) の代わりの語。ゲームの訳の言い方に合わせて次から決めた:
    //   艦船での戦闘 (shipcombat と勢力ごとの shipcombat_*): Hints_Ship_Combat_Title の「艦船での戦闘」(Ship Combat)
    //   FPS 戦闘 (fpscombat): 勢力ごとの fpscombat_* の displayName の mobiglas_ui_Mercenary「傭兵」
    //   worker: 勢力ごとの worker_roughandready の displayName の RepScope_HiredMuscle_Name「肉体労働者」
    //   racing_headhunter: mobiglas_ui_Race「レース」
    private static readonly (string Record, string Key)[] ScopeFallbackKeys =
    {
        ("reputationscope_shipcombat", "Hints_Ship_Combat_Title"),
        ("reputationscope_shipcombat_roughandready", "Hints_Ship_Combat_Title"),
        ("reputationscope_shipcombat_headhunters", "Hints_Ship_Combat_Title"),
        ("reputationscope_shipcombat_xenothreat", "Hints_Ship_Combat_Title"),
        ("reputationscope_fpscombat", "mobiglas_ui_Mercenary"),
        ("reputationscope_worker", "RepScope_HiredMuscle_Name"),
        ("reputationscope_racing_headhunter", "mobiglas_ui_Race"),
    };

    // 評判の段階の表示名 (段階のレコード名 → 名前)。FormatStanding の手書きの対応より先に使う
    private static Dictionary<string, string>? _standingNames;
    // 好感度の段階 (reputationstanding_affinity_neutral_020 など。displayName が @LOC_PLACEHOLDER か訳の無い語) に使う、
    // ゲームの立場の語 (mobiGlas_Reputation_Stance_Neutral「中立」・_Ally「同盟」・_Hostile「敵対」)
    private static Dictionary<string, string>? _stanceWords;

    // 段階のレコード名 → displayName の語。Data.p4k の libs/foundry/records/reputation/standings/ の段階のうち、
    // 契約の raw_json から参照されるもの (displayName が @LOC_* のものは入れない) を写したもの
    private static readonly (string Record, string Key)[] StandingDisplayKeys =
    {
        ("reputationstanding_affinity_ally_025", "RepStanding_MissionGiver_Default_Liked_Name"),
        ("reputationstanding_affinity_ally_100", "RepStanding_MissionGiver_Default_Exalted_Name"),
        ("reputationstanding_affinity_neutral_000", "RepStanding_MissionGiver_Default_Neutral_Name"),
        ("reputationstanding_assassination_assosciate", "RepStanding_Assassination_Rank2"),
        ("reputationstanding_assassination_captain", "RepStanding_Assassination_Rank4"),
        ("reputationstanding_assassination_counsel", "RepStanding_Assassination_Rank5"),
        ("reputationstanding_assassination_noteligible", "RepStanding_NotEligible"),
        ("reputationstanding_assassination_outsider", "RepStanding_Assassination_Rank0"),
        ("reputationstanding_assassination_rookie", "RepStanding_Assassination_Rank1"),
        ("reputationstanding_assassination_soldier", "RepStanding_Assassination_Rank3"),
        ("reputationstanding_assassination_underboss", "RepStanding_Assassination_Rank6"),
        ("reputationstanding_bounty_agent", "RepStanding_Bounty_Rank3"),
        ("reputationstanding_bounty_agent_bountyhuntersguild", "RepStanding_Bounty_MidLevel_Name"),
        ("reputationstanding_bounty_applicant", "RepStanding_Bounty_Rank0"),
        ("reputationstanding_bounty_applicant_bountyhuntersguild", "RepStanding_Bounty_Applicant_Name"),
        ("reputationstanding_bounty_junior", "RepStanding_Bounty_Rank2"),
        ("reputationstanding_bounty_junior_bountyhuntersguild", "RepStanding_Bounty_Junior_Name"),
        ("reputationstanding_bounty_masteragent", "RepStanding_Bounty_Rank6"),
        ("reputationstanding_bounty_masteragent_bountyhuntersguild", "RepStanding_Bounty_LegendaryBountyHunter_Name"),
        ("reputationstanding_bounty_probation", "RepStanding_Bounty_Rank1"),
        ("reputationstanding_bounty_probation_bountyhuntersguild", "RepStanding_Bounty_Probation_Name"),
        ("reputationstanding_bounty_senior", "RepStanding_Bounty_Rank4"),
        ("reputationstanding_bounty_senior_bountyhuntersguild", "RepStanding_Bounty_Senior_Name"),
        ("reputationstanding_bounty_veteranagent", "RepStanding_Bounty_Rank5"),
        ("reputationstanding_bounty_veteranagent_bountyhuntersguild", "RepStanding_Bounty_MasterBountyHunter_Name"),
        ("reputationstanding_courier_rank0", "RepStanding_Courier_Rank0"),
        ("reputationstanding_courier_rank1", "RepStanding_Courier_Rank1"),
        ("reputationstanding_courier_rank2", "RepStanding_Courier_Rank2"),
        ("reputationstanding_emergency_noteligible", "RepStanding_NotEligible"),
        ("reputationstanding_factionrep_allied_border", "RepScope_Contractor_Rank0"),
        ("reputationstanding_factionrep_negativerep", "mobiGlas_Reputation_Stance_Hostile"),
        ("reputationstanding_factionrep_rank0", "mobiGlas_Reputation_Stance_Neutral"),
        ("reputationstanding_factionrep_rank1", "RepScope_Contractor_Rank1"),
        ("reputationstanding_factionrep_rank2", "RepScope_Contractor_Rank2"),
        ("reputationstanding_factionrep_rank3", "RepScope_Contractor_Rank3"),
        ("reputationstanding_factionrep_rank4", "RepScope_Contractor_Rank4"),
        ("reputationstanding_factionrep_rank5", "RepScope_Contractor_Rank5"),
        ("reputationstanding_factionrep_rank6", "RepScope_Contractor_Rank6"),
        ("reputationstanding_hauling_rank0", "RepStanding_TransportGuild_Rank0"),
        ("reputationstanding_hauling_rank1", "RepStanding_TransportGuild_Rank1"),
        ("reputationstanding_hauling_rank2", "RepStanding_TransportGuild_Rank2"),
        ("reputationstanding_hauling_rank3", "RepStanding_TransportGuild_Rank3"),
        ("reputationstanding_hauling_rank4", "RepStanding_TransportGuild_Rank4"),
        ("reputationstanding_hauling_rank5", "RepStanding_TransportGuild_Rank5"),
        ("reputationstanding_hauling_rank6", "RepStanding_TransportGuild_Rank6"),
        ("reputationstanding_hiredmuscle_noteligible", "RepStanding_NotEligible"),
        ("reputationstanding_hiredmuscle_rank0", "RepStanding_Rank0"),
        ("reputationstanding_hiredmuscle_rank1", "RepStanding_Rank1"),
        ("reputationstanding_missionproviderrep_battaglia_rank0", "mg_battaglia_RepScope_Rank0"),
        ("reputationstanding_missionproviderrep_battaglia_rank1", "mg_battaglia_RepScope_Rank1"),
        ("reputationstanding_missionproviderrep_battaglia_rank2", "mg_battaglia_RepScope_Rank2"),
        ("reputationstanding_missionproviderrep_battaglia_rank3", "mg_battaglia_RepScope_Rank3"),
        ("reputationstanding_missionproviderrep_battaglia_rank4", "mg_battaglia_RepScope_Rank4"),
        ("reputationstanding_missionproviderrep_battaglia_rank5", "mg_battaglia_RepScope_Rank5"),
        ("reputationstanding_npc_reliability_fireable", "RepStanding_MissionGiver_Default_Disliked_Name"),
        ("reputationstanding_racing_ship_rank0", "RacingRep_Ship_Rank0"),
        ("reputationstanding_racing_ship_rank1", "RacingRep_Ship_Rank1"),
        ("reputationstanding_racing_ship_rank2", "RacingRep_Ship_Rank2"),
        ("reputationstanding_racing_ship_rank3", "RacingRep_Ship_Rank3"),
        ("reputationstanding_racing_ship_rank4", "RacingRep_Ship_Rank4"),
        ("reputationstanding_racing_ship_rank5", "RacingRep_Ship_Rank5"),
        ("reputationstanding_racing_ship_rank6", "RacingRep_Ship_Rank6"),
        ("reputationstanding_racing_ship_rank7", "RacingRep_Ship_Rank7"),
        ("reputationstanding_security_rank0", "RepStanding_Security_Rank0"),
        ("reputationstanding_security_rank1", "RepStanding_Security_Rank1"),
        ("reputationstanding_security_rank2", "RepStanding_Security_Rank2"),
        ("reputationstanding_security_rank3", "RepStanding_Security_Rank3"),
        ("reputationstanding_security_rank4", "RepStanding_Security_Rank4"),
        ("reputationstanding_security_rank5", "RepStanding_Security_Rank5"),
        ("reputationstanding_security_rank6", "RepStanding_Security_Rank6"),
        ("reputationstanding_shipcombat_rank0_headhunters", "RepStanding_Rank0"),
        ("reputationstanding_shipcombat_rank6_headhunters", "RepStanding_Rank6"),
        ("reputationstanding_wikelo_001", "RepStanding_Barter_Rank1_Name"),
        ("reputationstanding_wikelo_002", "RepStanding_Barter_Rank2_Name"),
    };

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
                }
                if (!string.IsNullOrEmpty(en))
                {
                    _enDict[key] = en.Replace("\\n", "\n");
                }
            }
        }
        catch (Exception ex)
        {
            TransLoadError = ex.Message;
        }
    }

    // 組合の欄の「勢力指定なし」: 評判の勢力が元データで指定されていない (factionReputation が null) 項目を持つ契約に絞る
    public const string NoFactionFilter = "勢力指定なし";

    public List<string> GetFactions()
    {
        var factions = new List<string> { "(すべて)", NoFactionFilter };
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
        if (factionFilter == NoFactionFilter)
        {
            // 評判の列・詳細で「(勢力指定なし)」と出る項目 (成功・放棄・失敗の評判で勢力が空) を 1 つでも持つ契約
            result = result.Where(m =>
            {
                ParseRawJson(m);
                return m.SuccessRepRewards.Concat(m.AbandonRepRewards).Concat(m.FailRepRewards)
                    .Any(r => string.IsNullOrEmpty(r.Faction));
            }).ToList();
        }
        else if (!string.IsNullOrEmpty(factionFilter) && factionFilter != "(すべて)")
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
            // ランクが不明 ("") の契約は、どのランクを選んでも残す
            result = result.Where(m => m.WikiRank == rankFilter || string.IsNullOrEmpty(m.WikiRank)).ToList();
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
            var cat = CategoryOf(m);
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

    // record_name (大文字小文字無視) のミッション。records の先頭から探し、最初に見つかったもの (テスト用などで一覧から外したものは見つからない)。
    // ［BP］タブの入手のダブルクリックからミッションタブに出すのに使う
    public MissionEntry? FindByRecord(IEnumerable<string> records)
    {
        var all = LoadAllMissions();
        foreach (var rec in records)
        {
            var m = all.FirstOrDefault(x => string.Equals(x.RecordName, rec, StringComparison.OrdinalIgnoreCase));
            if (m == null) continue;
            ParseRawJson(m);
            return m;
        }
        return null;
    }

    public List<MissionEntry> GetMissions(string category)
    {
        var all = LoadAllMissions();
        var filtered = all.Where(m => CategoryOf(m) == category).ToList();

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

    // ゲームの契約者ランク (global.ini の RepScope_Contractor_Rank0〜6)
    private static readonly string[] RankNames = { "見習い", "ジュニア・コントラクター", "コントラクター", "シニア・コントラクター", "ベテラン・コントラクター", "チーフ・コントラクター", "エリート・コントラクター" };

    private static string GetRankName(int rankIndex) =>
        rankIndex >= 0 && rankIndex < RankNames.Length ? RankNames[rankIndex] : "";

    // ランクの決め方 (前にあるものを優先。どれでも決まらなければ "" = 不明):
    //   (1) ゲームの必要ランク: CG 行は _Contract_.minStanding、MBE 行は reputationRequirements.expression[] の standing
    //   (2) 内部名 (debugName / record_name) を '_' で区切った語の Rank0〜Rank6
    //   (3) 難易度 (Intro / Very Easy → 見習い … Very Hard → ベテラン、Super → チーフ)
    private static int DecideRank(string rawJson, bool isContract, string name, string difficulty)
    {
        var r = RequiredRankFromJson(rawJson, isContract);
        if (r < 0) r = RankFromName(name);
        if (r < 0) r = RankFromDifficulty(difficulty);
        return r;
    }

    // standing のファイル名の末尾が _rankN (N=0〜6) のものだけを見る。比較の向きは問わず、rank 以外の条件 (好感度など) は無視し、
    // rank 条件が複数あればいちばん低い N。無ければ -1
    private static readonly System.Text.RegularExpressions.Regex StandingRankRegex =
        new(@"_rank([0-6])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static int StandingRank(string standing)
    {
        var m = StandingRankRegex.Match(ExtractFileName(standing ?? ""));
        return m.Success ? m.Groups[1].Value[0] - '0' : -1;
    }

    private static int RequiredRankFromJson(string rawJson, bool isContract)
    {
        if (string.IsNullOrEmpty(rawJson)) return -1;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (isContract)
            {
                if (root.TryGetProperty("_Contract_", out var contract) && contract.ValueKind == JsonValueKind.Object)
                    return StandingRank(GetString(contract, "minStanding"));
                return -1;
            }
            if (!root.TryGetProperty("_RecordValue_", out var rv) || rv.ValueKind != JsonValueKind.Object) return -1;
            if (!rv.TryGetProperty("reputationRequirements", out var repReq) || repReq.ValueKind != JsonValueKind.Object) return -1;
            if (!repReq.TryGetProperty("expression", out var expr) || expr.ValueKind != JsonValueKind.Array) return -1;
            int best = -1;
            foreach (var req in expr.EnumerateArray())
            {
                if (req.ValueKind != JsonValueKind.Object) continue;
                var n = StandingRank(GetString(req, "standing"));
                if (n >= 0 && (best < 0 || n < best)) best = n;
            }
            return best;
        }
        catch { return -1; }
    }

    private static readonly System.Text.RegularExpressions.Regex NameRankRegex =
        new(@"^Rank([0-6])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static int RankFromName(string name)
    {
        foreach (var p in (name ?? "").Split('_'))
        {
            var m = NameRankRegex.Match(p);
            if (m.Success) return m.Groups[1].Value[0] - '0';
        }
        return -1;
    }

    private static int RankFromDifficulty(string difficulty) => (difficulty ?? "").ToLowerInvariant() switch
    {
        "intro" => 0,
        "very easy" => 0,
        "easy" => 1,
        "medium" => 2,
        "hard" => 3,
        "very hard" => 4,
        "super" => 5,
        _ => -1,
    };

    private static readonly Dictionary<string, string> DifficultyCodeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        {"vlrt", "Very Easy"}, {"lrt", "Easy"}, {"mrt", "Medium"}, {"hrt", "Hard"}, {"vhrt", "Very Hard"}, {"ert", "Super"},
        {"intro", "Intro"}, {"rank0", "Intro"},
    };

    public List<MissionEntry> Search(string query)
    {
        var all = LoadAllMissions();
        foreach (var m in all)
        {
            // エントリはキャッシュで使い回されるため、前回の検索で付いたヒントを消してから解析する
            m.TranslationHint = "";
            ParseRawJson(m);
        }

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
        if (_allCache != null) return _allCache;
        _allCache = ReadAllMissions(_conn);
        return _allCache;
    }

    // missions 表の全行を MissionEntry にする (LoadAllMissions の中身。conn で読む。結果は覚えない)
    private List<MissionEntry> ReadAllMissions(SqliteConnection conn)
    {
        var list = new List<MissionEntry>();
        using var cmd = conn.CreateCommand();
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

            // ContractGenerator 行は record_name が "ContractGenerator.{契約 id}" なので、GUID は画面に出さず、
            // 表示名・分類には raw_json の名前 (契約 debugName → 生成器 debugName → タイトルキー) を使う。
            // 難易度・場所の推測は抽出側 (GameDataExtractor) と同じく debugName ("_Name_") だけで行う
            var isContract = rn.StartsWith(ContractRecordPrefix);
            string contractDebugName = "", contractDisplayRecord = "";
            if (isContract)
            {
                (contractDebugName, entry.NameBase, contractDisplayRecord) = GameDataExtractor.ReadContractDisplayNames(entry.RawJson);
                entry.ContractId = rn[ContractRecordPrefix.Length..];
            }
            else
            {
                entry.NameBase = rn;
            }
            // テスト用レコードの除外 (MBE / Wiki 行は上で record_name により判定済み。CG 行は debugName で同じ条件を見る)
            if (isContract && (contractDebugName.Contains("_test", StringComparison.OrdinalIgnoreCase)
                || contractDebugName.Contains("ReputationTest", StringComparison.OrdinalIgnoreCase))) continue;

            var inferName = isContract ? contractDebugName : rn;
            // 古い抽出で Vehicle の直後の語 (船の大きさの区分。Vehicle_Medium など) から DB に入った難易度は空として扱う
            if (IsVehicleSizeDifficulty(entry.Difficulty, inferName))
                entry.Difficulty = "";
            if (string.IsNullOrEmpty(entry.Difficulty))
                entry.Difficulty = InferDifficulty(inferName);
            if (string.IsNullOrEmpty(entry.Location))
                entry.Location = InferLocation(inferName);

            entry.DifficultyOrder = DiffOrder(entry.Difficulty);
            entry.FriendlyName = isContract ? contractDisplayRecord : ExtractFriendlyName(rn);
            entry.CleanedName = CleanRecordName(entry.NameBase);

            // WikiFactionJa / WikiRank を設定
            if (!string.IsNullOrEmpty(entry.WikiFaction) && FactionJaMap.TryGetValue(entry.WikiFaction, out var fja))
                entry.WikiFactionJa = fja;
            entry.WikiRank = GetRankName(DecideRank(entry.RawJson, isContract, inferName, entry.Difficulty));

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
        if (entry.Parsed) return;
        entry.Parsed = true;
        // 星系 (StarSystemBitsOf のビット)。raw_json の場所の指定から判定し、判定できなければ finally で場所列と名前から判定する
        int systems = 0;
        try
        {
            if (string.IsNullOrEmpty(entry.RawJson)) return;
            using var doc = JsonDocument.Parse(entry.RawJson);
            if (entry.RecordName.StartsWith(ContractRecordPrefix))
            {
                systems = ContractStarSystems(doc.RootElement);
                ParseContractJson(entry, doc.RootElement);
                return;
            }
            if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv)) return;

            // 星系: localityAvailable (pu_missionlocality/…) と locationMissionAvailable (starmap/pu/system/…)
            systems = StarSystemBitsOf(GetString(rv, "localityAvailable")) | StarSystemBitsOf(GetString(rv, "locationMissionAvailable"));

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
            // 値が null の要素 (例 PU_EliminateSpecific_Caves の reputationRequirements: null) に TryGetProperty すると例外になり、
            // 後ろの評判の報酬などを読まずに終わるので、どれも Object のときだけ読む
            if (rv.TryGetProperty("missionReward", out var reward) && reward.ValueKind == JsonValueKind.Object)
            {
                entry.RewardBase = GetDouble(reward, "reward");
                entry.RewardBonusMax = GetDouble(reward, "max");
                entry.PlusBonuses = GetBool(reward, "plusBonuses");
            }
            entry.BuyInAmount = GetDouble(rv, "missionBuyInAmount");

            // Deadline
            if (rv.TryGetProperty("missionDeadline", out var deadline) && deadline.ValueKind == JsonValueKind.Object)
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
            if (rv.TryGetProperty("reputationPrerequisites", out var repPre) && repPre.ValueKind == JsonValueKind.Object)
            {
                if (repPre.TryGetProperty("wantedLevel", out var wl) && wl.ValueKind == JsonValueKind.Object)
                {
                    entry.WantedLevelMin = GetDouble(wl, "minValue");
                    entry.WantedLevelMax = GetDouble(wl, "maxValue");
                }
            }

            // Reputation requirements
            if (rv.TryGetProperty("reputationRequirements", out var repReq) && repReq.ValueKind == JsonValueKind.Object)
            {
                if (repReq.TryGetProperty("expression", out var expr) && expr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var req in expr.EnumerateArray())
                    {
                        if (req.ValueKind != JsonValueKind.Object) continue;
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
                    if (resultEntry.ValueKind == JsonValueKind.Object && resultEntry.TryGetProperty("reputationAmounts", out var amounts) && amounts.ValueKind == JsonValueKind.Array)
                    {
                        var list = idx == 0 ? entry.SuccessRepRewards
                                 : idx == 2 ? entry.AbandonRepRewards
                                 : idx == 3 ? entry.FailRepRewards
                                 : null;
                        if (list != null)
                        {
                            foreach (var amt in amounts.EnumerateArray())
                            {
                                if (amt.ValueKind != JsonValueKind.Object) continue;
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
        finally
        {
            if (systems == 0) systems = StarSystemBitsOf(entry.Location) | StarSystemBitsOf(entry.NameBase);
            entry.StarSystem = FormatStarSystems(systems);
        }
    }

    // 星系のビット (一覧の「星系」列はこの順で並べる)
    private const int SystemStanton = 1, SystemPyro = 2, SystemNyx = 4;

    // 場所の指定 (ファイルパス・場所列・名前) に含まれる星系名からビットを作る。
    // pu_missionlocality/stanton1, pyro_regions/regiona, stantonandnyx, starmap/pu/system/pyro/…, Stanton (Hurston) など
    private static int StarSystemBitsOf(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int bits = 0;
        if (s.Contains("stanton", StringComparison.OrdinalIgnoreCase)) bits |= SystemStanton;
        if (s.Contains("pyro", StringComparison.OrdinalIgnoreCase)) bits |= SystemPyro;
        if (s.Contains("nyx", StringComparison.OrdinalIgnoreCase)) bits |= SystemNyx;
        return bits;
    }

    private static string FormatStarSystems(int bits)
    {
        var names = new List<string>();
        if ((bits & SystemStanton) != 0) names.Add("Stanton");
        if ((bits & SystemPyro) != 0) names.Add("Pyro");
        if ((bits & SystemNyx) != 0) names.Add("Nyx");
        return string.Join(" / ", names);
    }

    // ContractGenerator 行の星系: _Contract_ と subContracts[] の additionalPrerequisites[] にある localityAvailable / locationAvailable
    private static int ContractStarSystems(JsonElement root)
    {
        if (!root.TryGetProperty("_Contract_", out var contract) || contract.ValueKind != JsonValueKind.Object) return 0;
        int bits = PrerequisiteStarSystems(contract);
        if (contract.TryGetProperty("subContracts", out var subs) && subs.ValueKind == JsonValueKind.Array)
        {
            foreach (var sub in subs.EnumerateArray())
                if (sub.ValueKind == JsonValueKind.Object) bits |= PrerequisiteStarSystems(sub);
        }
        return bits;
    }

    private static int PrerequisiteStarSystems(JsonElement owner)
    {
        if (!owner.TryGetProperty("additionalPrerequisites", out var pres) || pres.ValueKind != JsonValueKind.Array) return 0;
        int bits = 0;
        foreach (var p in pres.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            bits |= StarSystemBitsOf(GetString(p, "localityAvailable")) | StarSystemBitsOf(GetString(p, "locationAvailable"));
        }
        return bits;
    }

    // ContractGenerator 行の record_name の接頭辞 (GameDataExtractor.ContractRecordPrefix と同じ)
    private const string ContractRecordPrefix = GameDataExtractor.ContractRecordPrefix;

    // ContractGenerator 行の raw_json ({"_Resolved_": 補った値, "_Contract_": 契約の JSON}) を読む。
    // 元データにある Title / Description / 依頼主 / Illegal / timeToComplete / notForRelease だけを埋める
    private void ParseContractJson(MissionEntry entry, JsonElement root)
    {
        entry.LawfulnessUnknown = true;
        if (root.TryGetProperty("_Resolved_", out var res) && res.ValueKind == JsonValueKind.Object)
        {
            var titleKey = GetString(res, "title");
            entry.OriginalTitleKey = titleKey;
            if (!string.IsNullOrEmpty(titleKey))
            {
                entry.TitleJa = ResolveJa(titleKey);
                var en = ResolveEn(titleKey);
                if (!IsLocKey(en)) entry.TitleEn = en;
            }

            var descKey = GetString(res, "description");
            if (!string.IsNullOrEmpty(descKey))
            {
                entry.DescriptionJa = ResolveJa(descKey);
                var en = ResolveEn(descKey);
                if (!IsLocKey(en)) entry.DescriptionEn = en;
            }

            var giverKey = GetString(res, "contractor");
            if (!string.IsNullOrEmpty(giverKey))
            {
                var resolved = ResolveJa(giverKey);
                if (!IsLocKey(resolved)) entry.MissionGiverJa = resolved;
                var en = ResolveEn(giverKey);
                if (!IsLocKey(en))
                {
                    entry.MissionGiverEn = en;
                    // mission_giver 列は抽出時に英語 ini で解決した値で、ini が無い環境ではキーのまま入っている。
                    // 詳細表示はこの列を使うので、翻訳 DB の英語で解決できたときはそれで置き換える (解決できなければ列のキーのまま)
                    entry.MissionGiver = en;
                }
            }

            // Illegal (契約 → 生成器 → テンプレート) が false なら合法、true なら非合法。どれにも無ければ「不明」(LawfulnessUnknown のまま)
            if (res.TryGetProperty("illegal", out var il) && (il.ValueKind == JsonValueKind.True || il.ValueKind == JsonValueKind.False))
            {
                entry.IsLawful = il.ValueKind == JsonValueKind.False;
                entry.LawfulnessUnknown = false;
            }

            // パーティ共有 (契約 → 生成器の CanBeShared)。どちらにも無ければ null で、詳細に出さない
            if (res.TryGetProperty("canBeShared", out var cs) && (cs.ValueKind == JsonValueKind.True || cs.ValueKind == JsonValueKind.False))
            {
                entry.ContractCanBeShared = cs.ValueKind == JsonValueKind.True;
                entry.CanBeShared = entry.ContractCanBeShared.Value;
            }
        }

        // 契約または生成器の notForRelease が true なら未リリース扱い
        if (root.TryGetProperty("_Generator_", out var gen) && gen.ValueKind == JsonValueKind.Object && GetBool(gen, "notForRelease"))
            entry.NotForRelease = true;

        if (root.TryGetProperty("_Contract_", out var contract) && contract.ValueKind == JsonValueKind.Object)
        {
            if (GetBool(contract, "notForRelease")) entry.NotForRelease = true;
            // 受注条件の評判: additionalPrerequisites[] の ContractPrerequisite_Reputation (勢力・範囲・minStanding〜maxStanding。
            // exclude が真なら、その段階の間にいると受けられない)。subContracts の中は読まない (報酬と同じ)
            if (contract.TryGetProperty("additionalPrerequisites", out var pres) && pres.ValueKind == JsonValueKind.Array)
            {
                foreach (var pre in pres.EnumerateArray())
                {
                    if (pre.ValueKind != JsonValueKind.Object || GetString(pre, "_Type_") != "ContractPrerequisite_Reputation") continue;
                    entry.RepRequirements.Add(new ReputationRequirement
                    {
                        Faction = ExtractFileName(GetString(pre, "factionReputation")),
                        Scope = ExtractFileName(GetString(pre, "scope")),
                        Standing = ExtractFileName(GetString(pre, "minStanding")),
                        MaxStanding = ExtractFileName(GetString(pre, "maxStanding")),
                        Exclude = GetBool(pre, "exclude"),
                        IsRange = true,
                    });
                }
            }
            // contractResults.timeToComplete は分単位
            if (contract.TryGetProperty("contractResults", out var results) && results.ValueKind == JsonValueKind.Object)
            {
                entry.CompletionTimeMinutes = GetDouble(results, "timeToComplete");

                // 評判の報酬: contractResults.contractResults[] の ContractResult_LegacyReputation (量のレコードあり) と
                // ContractResult_CalculatedReputation (量のレコードが無く、ゲームが契約の内容から計算する。勢力と範囲だけを持つ)。
                // missionResults (bool 5 個) の並びは MBE 行の missionResultReputationRewards と同じとみなす (成功 = 0, 放棄 = 2, 失敗 = 3)。subContracts の中は読まない
                if (results.TryGetProperty("contractResults", out var resultList) && resultList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cr in resultList.EnumerateArray())
                    {
                        if (cr.ValueKind != JsonValueKind.Object) continue;
                        var crType = GetString(cr, "_Type_");
                        JsonElement amt;
                        bool calculated;
                        if (crType == "ContractResult_LegacyReputation")
                        {
                            if (!cr.TryGetProperty("contractResultReputationAmounts", out amt) || amt.ValueKind != JsonValueKind.Object) continue;
                            calculated = false;
                        }
                        else if (crType == "ContractResult_CalculatedReputation") { amt = cr; calculated = true; }
                        else continue;
                        if (!cr.TryGetProperty("missionResults", out var flags) || flags.ValueKind != JsonValueKind.Array) continue;
                        int idx = 0;
                        foreach (var flag in flags.EnumerateArray())
                        {
                            var list = flag.ValueKind != JsonValueKind.True ? null
                                     : idx == 0 ? entry.SuccessRepRewards
                                     : idx == 2 ? entry.AbandonRepRewards
                                     : idx == 3 ? entry.FailRepRewards
                                     : null;
                            list?.Add(new ReputationRewardInfo
                            {
                                Faction = ExtractFileName(GetString(amt, "factionReputation")),
                                Scope = ExtractFileName(GetString(amt, "reputationScope")),
                                Amount = calculated ? "" : ExtractFileName(GetString(amt, "reward")),
                                Calculated = calculated,
                            });
                            idx++;
                        }
                    }
                }
            }
        }
    }

    // ミッションの属するカテゴリ名 (GetMissions / GetCategories と同じ分け方)。BP タブからミッションを開くときにカテゴリを選ぶのに使う
    public static string CategoryOf(MissionEntry m) => Classify(m.RecordName, m.MissionType);

    private static string Classify(string recordName, string missionType)
    {
        // Wiki だけの行の種類は Wiki の分類 (ゲームのデータではない) なので区分を決めない
        if ((recordName ?? "").StartsWith("WikiMission.", StringComparison.Ordinal)) return "その他";
        return CategoryByType.TryGetValue(CleanType(missionType ?? ""), out var cat) ? cat : "その他";
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

    // 難易度の画面表示 (内部の値は英語のまま。並べ替え・ランクの決め方・検索の照合は英語の値で行う)。
    // ゲームの訳に契約の難易度の語が無いので日本語を当てる。Super は「超高難度」。知らない値はそのまま
    internal static string DifficultyJa(string difficulty) => (difficulty ?? "").ToLowerInvariant() switch
    {
        "intro" => "入門",
        "very easy" => "とても易しい",
        "easy" => "易しい",
        "medium" => "普通",
        "hard" => "難しい",
        "very hard" => "とても難しい",
        "super" => "超高難度",
        _ => difficulty ?? "",
    };

    // DB の難易度が、内部名の Vehicle の直後の語 (空白を除いて大文字小文字無視で同じ) から来たもの:
    // 内部名から推測し直すと難易度が空になり、かつ DB の値が Vehicle の直後の語と同じとき
    private static bool IsVehicleSizeDifficulty(string difficulty, string name)
    {
        if (string.IsNullOrEmpty(difficulty) || string.IsNullOrEmpty(name)) return false;
        if (InferDifficulty(name).Length > 0) return false;
        var d = difficulty.Replace(" ", "");
        var parts = name.Split('_');
        for (var i = 1; i < parts.Length; i++)
            if (parts[i - 1].Equals("Vehicle", StringComparison.OrdinalIgnoreCase)
                && parts[i].Equals(d, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string InferDifficulty(string rn)
    {
        var parts = rn.Split('_');
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            // Vehicle の直後の語 (TheCollector_Vehicle_Small_Fortune / _Medium_ / _Large_ / _Ground_ / _Super_ など) は
            // 船の大きさの区分なので難易度にしない
            if (i > 0 && parts[i - 1].Equals("Vehicle", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Equals("Intro", StringComparison.OrdinalIgnoreCase)) return "Intro";
            if (p.Equals("Super", StringComparison.OrdinalIgnoreCase)) return "Super";
            if (p.Equals("VEasy", StringComparison.OrdinalIgnoreCase) || p.Equals("VeryEasy", StringComparison.OrdinalIgnoreCase)) return "Very Easy";
            if (p.Equals("Easy", StringComparison.OrdinalIgnoreCase)) return "Easy";
            if (p.Equals("Medium", StringComparison.OrdinalIgnoreCase)) return "Medium";
            if (p.Equals("Hard", StringComparison.OrdinalIgnoreCase)) return "Hard";
            if (p.Equals("VHard", StringComparison.OrdinalIgnoreCase) || p.Equals("VeryHard", StringComparison.OrdinalIgnoreCase)) return "Very Hard";
            // 略号 (VE / E / M / H / VH) と LRT 系 (DifficultyCodeMap)
            if (p.Equals("VE", StringComparison.OrdinalIgnoreCase)) return "Very Easy";
            if (p.Equals("E", StringComparison.OrdinalIgnoreCase)) return "Easy";
            if (p.Equals("M", StringComparison.OrdinalIgnoreCase)) return "Medium";
            if (p.Equals("H", StringComparison.OrdinalIgnoreCase)) return "Hard";
            if (p.Equals("VH", StringComparison.OrdinalIgnoreCase)) return "Very Hard";
            // Rank0 はランクの手順 (2) で別に扱うので、難易度の推定には使わない
            if (p.Equals("Rank0", StringComparison.OrdinalIgnoreCase)) continue;
            if (DifficultyCodeMap.TryGetValue(p, out var code)) return code;
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

        lines.Add("══════ 契約詳細 ══════\n");

        lines.Add("■ 基本情報");
        lines.Add($"  レコード名: {m.FriendlyName}");
        if (!string.IsNullOrEmpty(m.ContractId))
            lines.Add($"  契約 ID: {m.ContractId}");
        if (!string.IsNullOrEmpty(m.TitleJa) && !IsLocKey(m.TitleJa))
            lines.Add($"  タイトル (日本語): {m.TitleJa}");
        var englishTitle = !string.IsNullOrEmpty(m.TitleEn) ? m.TitleEn : m.Title;
        if (!IsLocKey(englishTitle) && !string.IsNullOrEmpty(englishTitle))
            lines.Add($"  タイトル (英語): {englishTitle}");
        if (!IsLocKey(m.TitleHud) && !string.IsNullOrEmpty(m.TitleHud) && m.TitleHud != m.Title)
            lines.Add($"  HUDタイトル: {m.TitleHud}");
        if (!string.IsNullOrEmpty(m.Difficulty))
            lines.Add($"  難易度: {DifficultyJa(m.Difficulty)}");
        if (!string.IsNullOrEmpty(m.MissionGiverJa))
            lines.Add($"  依頼者: {m.MissionGiverJa}" + (!IsLocKey(m.MissionGiver) && m.MissionGiver != m.MissionGiverJa ? $" ({m.MissionGiver})" : ""));
        else if (!IsLocKey(m.MissionGiver) && !string.IsNullOrEmpty(m.MissionGiver))
            lines.Add($"  依頼者: {m.MissionGiver}");
        if (!string.IsNullOrEmpty(m.MissionGiverRecord))
            lines.Add($"  依頼元: {FormatGiverRecord(m.MissionGiverRecord)}");
        if (!string.IsNullOrEmpty(m.Location))
            lines.Add($"  場所: {m.Location}");
        lines.Add($"  合法性: {(m.LawfulnessUnknown ? "不明" : m.IsLawful ? "合法" : "非合法")}");
        if (!string.IsNullOrEmpty(m.TranslationHint))
            lines.Add($"  ※ 検索一致タイトル: {m.TranslationHint}");
        if (!string.IsNullOrEmpty(m.WikiTitle))
            lines.Add($"  Wiki タイトル: {m.WikiTitle}");
        if (!string.IsNullOrEmpty(m.WikiFaction))
        {
            var factionDisplay = !string.IsNullOrEmpty(m.WikiFactionJa) ? $"{m.WikiFactionJa} ({m.WikiFaction})" : m.WikiFaction;
            lines.Add($"  ファクション: {factionDisplay}");
        }
        // ランクが決まらない契約は「不明」(ランクで絞っても一覧に残る)
        lines.Add($"  ランク: {(string.IsNullOrEmpty(m.WikiRank) ? "不明" : m.WikiRank)}");
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

        // raw_json の BlueprintRewards (blueprintPool) があれば、抽選プールごとのもらえる BP (BP の表が無ければ出さない)
        lines.AddRange(BlueprintDetail.MissionSectionLines(_workDir, m.RawJson));

        if (m.RepRequirements.Count > 0)
        {
            lines.Add("");
            lines.Add("■ レピュテーション要件");
            foreach (var req in m.RepRequirements)
            {
                var faction = FormatFaction(req.Faction);
                var scope = FormatScope(req.Scope);
                var standing = FormatStanding(req.Standing);
                if (!req.IsRange)
                {
                    lines.Add($"  {faction} [{scope}]: {standing} {FormatComparison(req.Comparison)}");
                    continue;
                }
                // CG 行の受注条件 (段階の範囲)。上限がその範囲のいちばん上の段階 (rank6。例 factionrep_rank6・shipcombat_rank6_headhunters) なら「以上」だけ
                var top = string.IsNullOrEmpty(req.MaxStanding) || TopRankRegex.IsMatch(req.MaxStanding);
                var range = top ? $"{standing} 以上" : $"{standing} 以上 {FormatStanding(req.MaxStanding)} 以下";
                lines.Add(req.Exclude ? $"  {faction} [{scope}]: {range}のときは受けられない" : $"  {faction} [{scope}]: {range}");
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
            lines.Add("■ 解放条件 (前提契約)");
            foreach (var rm in m.RequiredMissions)
                lines.Add($"  - {FormatMissionName(rm)}");
        }

        if (m.SuccessRepRewards.Count > 0)
        {
            lines.Add("");
            lines.Add("■ レピュテーション報酬 (成功時)");
            foreach (var rr in m.SuccessRepRewards)
            {
                var amount = FormatRepAmount(rr);
                lines.Add($"  {FormatFaction(rr.Faction)} [{FormatScope(rr.Scope)}]: {amount}");
            }
        }

        if (m.FailRepRewards.Count > 0)
        {
            lines.Add("");
            lines.Add("■ レピュテーション変動 (失敗時)");
            foreach (var rr in m.FailRepRewards)
            {
                var amount = FormatRepAmount(rr);
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
        lines.Add("■ 契約設定");
        if (m.CompletionTimeMinutes > 0)
            lines.Add($"  制限時間: {m.CompletionTimeMinutes:0}分");
        if (m.MaxPlayers > 0)
            lines.Add($"  最大プレイヤー数: {m.MaxPlayers}");
        else if (m.MaxPlayers == -1)
            lines.Add("  最大プレイヤー数: 無制限");
        if (m.MaxInstances > 0)
            lines.Add($"  最大インスタンス数: {m.MaxInstances}");
        // ContractGenerator 行は元データに無い 3 項目を出さない (パーティ共有だけは CanBeShared があるときに出す)
        var isContract = m.RecordName.StartsWith(ContractRecordPrefix);
        if (!isContract)
            lines.Add($"  パーティ共有: {(m.CanBeShared ? "可" : "不可")}");
        else if (m.ContractCanBeShared.HasValue)
            lines.Add($"  パーティ共有: {(m.ContractCanBeShared.Value ? "可" : "不可")}");
        if (m.OnceOnly)
            lines.Add("  一度きり: はい");
        if (!isContract)
        {
            lines.Add($"  犯罪者化で失敗: {(m.FailIfCriminal ? "はい" : "いいえ")}");
            lines.Add($"  収監で失敗: {(m.FailIfPrison ? "はい" : "いいえ")}");
        }
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
        var engDesc = !string.IsNullOrEmpty(m.DescriptionEn) ? m.DescriptionEn : m.Description;
        if (!IsLocKey(engDesc) && !string.IsNullOrEmpty(engDesc))
        {
            lines.Add("");
            lines.Add("■ 説明 (英語)");
            foreach (var dl in engDesc.Replace("\\n", "\n").Split('\n'))
                lines.Add($"  {dl.TrimEnd()}");
        }

        return string.Join("\n", lines);
    }

    // 名前 (一覧の名前の列の日本語名と英語名の組) とカテゴリが同じ契約を 1 行にまとめる。行の順は各行の 1 件目が出てきた順、行の中も元の順
    public static List<MissionGroup> GroupByName(IEnumerable<MissionEntry> missions)
    {
        var rows = new List<MissionGroup>();
        var byKey = new Dictionary<(string, string, string), List<MissionEntry>>();
        var order = new List<List<MissionEntry>>();
        foreach (var m in missions)
        {
            var key = (m.ListNameJa, m.ListNameEn, m.Category);
            if (!byKey.TryGetValue(key, out var list))
            {
                list = new List<MissionEntry>();
                byKey[key] = list;
                order.Add(list);
            }
            list.Add(m);
        }
        foreach (var list in order) rows.Add(new MissionGroup(list));
        return rows;
    }

    // まとめた行の「■ 違い（n 件）」の節。契約ごとに 1 行で、詳細 (FormatDetail) の項目のうち契約の間で値が違うものだけを出す。
    // 1 件の行・全部同じなら ""。レコード名は全件に共通の前後 ('.' '_' の区切りまで) を除いた違いの部分だけを出す
    // representative: 詳細欄の上に全文を出している契約 (BP タブから開いたときなど)。その契約を 1 件目 (代表) にして比べる
    public string FormatGroupDifferences(MissionGroup group, MissionEntry? representative = null)
    {
        if (group.Members.Count < 2) return "";
        var members = representative != null && group.Members.Contains(representative)
            ? new List<MissionEntry> { representative }.Concat(group.Members.Where(m => !ReferenceEquals(m, representative))).ToList()
            : group.Members.ToList();
        var itemsPer = members.Select(m => ParseDetailItems(FormatDetail(m))).ToList();

        // 項目の順: 1 件目から順に初めて出てきた順
        var keys = new List<(string Section, string Label)>();
        var keySet = new HashSet<(string, string)>();
        foreach (var items in itemsPer)
            foreach (var it in items)
                if (keySet.Add((it.Section, it.Label))) keys.Add((it.Section, it.Label));

        var diffKeys = new List<(string Section, string Label, bool Whole)>();
        foreach (var k in keys)
        {
            // 契約 ID は契約ごとに必ず違うので一覧に出さない (契約ごとの詳細では見られる)
            if (k.Section == "■ 基本情報" && k.Label == "契約 ID") continue;
            var values = itemsPer.Select(items => items.FirstOrDefault(i => i.Section == k.Section && i.Label == k.Label)).ToList();
            var distinct = values.Select(v => v.Value).Distinct(StringComparer.Ordinal).Count();
            if (distinct > 1)
            {
                var whole = values.First(v => v.Value != null).Whole;
                diffKeys.Add((k.Section, k.Label, whole));
            }
        }
        if (diffKeys.Count == 0) return "";

        // 同じ見出しが別の節にもあるときは節の名前を前に付ける
        var labelCount = diffKeys.GroupBy(k => k.Label).ToDictionary(g => g.Key, g => g.Count());
        string DisplayLabel((string Section, string Label, bool Whole) k) =>
            k.Whole ? k.Section.TrimStart('■', ' ')
            : labelCount[k.Label] > 1 ? $"{k.Section.TrimStart('■', ' ')} {k.Label}" : k.Label;

        // 項目ごとに各契約の表示の値 (null はその契約ではこの項目を出さない)
        var display = new List<string?[]>();
        foreach (var k in diffKeys)
        {
            var raw = itemsPer.Select(items => items.FirstOrDefault(i => i.Section == k.Section && i.Label == k.Label).Value).ToArray();
            string?[] shown;
            if (k.Section == "■ 基本情報" && k.Label == "レコード名")
                shown = RecordNameDifferences(raw).Select(v => string.IsNullOrEmpty(v) ? "なし" : v).ToArray();
            else if (k.Whole)
            {
                // 節まるごとの項目は 1 段目の行だけ (BP の節ならプール名) を使う。1 段目だけでは違いが消えるときは全部の行。
                // 詳細欄の上に全文が出ている代表の契約 (1 件目) と比べ、各契約で代表に無い行だけを「 / 」でつないで出す。
                // 代表と行が全部同じ契約 (代表自身も) はこの項目を出さない。代表にある節が無い契約は「なし」。
                // 代表に無い行は無いが、代表の行が欠けている契約は「代表の文のうち n 行が無い」
                var top = raw.Select(v => v == null ? null : SplitWhole(v, topOnly: true).ToList()).ToArray();
                var full = raw.Select(v => v == null ? null : SplitWhole(v, topOnly: false).ToList()).ToArray();
                int DistinctCount(List<string>?[] a) => a.Select(l => l == null ? null : string.Join("\n", l)).Distinct(StringComparer.Ordinal).Count();
                var use = DistinctCount(top) == DistinctCount(full) ? top : full;
                var rep = new HashSet<string>(use[0] ?? new List<string>(), StringComparer.Ordinal);
                shown = use.Select(l =>
                {
                    if (l == null) return use[0] == null ? null : "なし";
                    var mine = new HashSet<string>(l, StringComparer.Ordinal);
                    var extra = l.Where(s => !rep.Contains(s)).Distinct(StringComparer.Ordinal).ToList();
                    if (extra.Count > 0) return string.Join(" / ", extra);
                    var missing = rep.Count(s => !mine.Contains(s));
                    return missing > 0 ? $"代表の文のうち {missing} 行が無い" : null;
                }).ToArray();
            }
            else shown = raw.Select(v => string.IsNullOrEmpty(v) ? "なし" : v).ToArray();
            display.Add(shown);
        }

        var lines = new List<string> { "", $"■ 違い（{members.Count} 件）" };
        for (int i = 0; i < members.Count; i++)
        {
            var parts = diffKeys.Select((k, j) => display[j][i] == null ? null : $"{DisplayLabel(k)}: {display[j][i]}")
                                .Where(p => p != null).ToList();
            // 1 件目は詳細欄の上に全文が出ている代表の契約
            var head = i == 0 ? "(代表) " : "";
            var body = parts.Count > 0 ? string.Join("、", parts) : i == 0 ? "上の詳細の契約" : "代表と同じ";
            lines.Add($"  {i + 1}. {head}{body}");
        }
        return "\n" + string.Join("\n", lines);
    }

    // FormatDetail の文を項目に分ける。「■ 」の行が節の見出し。
    // 説明・タイトルバリアント・解放条件・報酬の BP の節と、字下げの深い行がある節は節まるごとで 1 項目 (Label は "")。
    // それ以外の節は 1 行 1 項目で、「見出し: 値」の行は最初の ": " で分け、": " の無い行は行そのものを見出しにして値を "あり" にする
    private static List<(string Section, string Label, string? Value, bool Whole)> ParseDetailItems(string detail)
    {
        var result = new List<(string, string, string?, bool)>();
        var sections = new List<(string Head, List<string> Lines)>();
        foreach (var line in detail.Split('\n'))
        {
            if (line.StartsWith("■ ")) { sections.Add((line.TrimEnd(), new List<string>())); continue; }
            if (sections.Count == 0) continue;   // 「══════ 契約詳細 ══════」
            sections[^1].Lines.Add(line);
        }
        foreach (var (head, rawLines) in sections)
        {
            // 節と節の間の空行 (最後の空行) は節の中身に入れない
            var lines = rawLines.ToList();
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 0) continue;
            var whole = head.StartsWith("■ 説明") || head.StartsWith("■ タイトルバリアント") || head.StartsWith("■ 解放条件")
                     || head.StartsWith("■ 報酬の BP") || lines.Any(l => l.StartsWith("    "));
            if (whole) { result.Add((head, "", string.Join("\n", lines), true)); continue; }
            // 同じ節に同じ見出しが 2 回以上出たら、2 回目からは「見出し (2)」のように番号を付けて別の項目にする
            var seen = new Dictionary<string, int>();
            foreach (var l in lines)
            {
                var t = l.Trim();
                if (t.Length == 0) continue;
                var idx = t.IndexOf(": ", StringComparison.Ordinal);
                var (label, value) = idx > 0 ? (t[..idx], t[(idx + 2)..]) : (t, "あり");
                var n = seen.TryGetValue(label, out var c) ? c + 1 : 1;
                seen[label] = n;
                result.Add((head, n > 1 ? $"{label} ({n})" : label, value, false));
            }
        }
        return result;
    }

    // 節まるごとの項目の行 (前後の空白を除き、空の行は除く)。topOnly なら 1 段目 (字下げ 2) の行だけ
    private static IEnumerable<string> SplitWhole(string value, bool topOnly) =>
        value.Split('\n')
             .Where(l => !topOnly || !l.StartsWith("    "))
             .Select(l => l.Trim())
             .Where(l => l.Length > 0);

    // レコード名の違いの部分: 全件 (値のあるもの) に共通の先頭と末尾を、'.' '_' の区切りの位置まで戻して除いたもの
    private static string?[] RecordNameDifferences(string?[] names)
    {
        var present = names.Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
        if (present.Count < 2) return names;
        var minLen = present.Min(n => n.Length);
        int pre = 0;
        while (pre < minLen && present.All(n => n[pre] == present[0][pre])) pre++;
        // 共通の先頭は区切りの直後まで戻す (全部が区切りで終わっていればそのまま)
        while (pre > 0 && present[0][pre - 1] != '.' && present[0][pre - 1] != '_') pre--;
        int suf = 0;
        while (suf < minLen - pre && present.All(n => n[n.Length - 1 - suf] == present[0][present[0].Length - 1 - suf])) suf++;
        while (suf > 0 && present[0][present[0].Length - suf] != '.' && present[0][present[0].Length - suf] != '_') suf--;
        return names.Select(n =>
        {
            if (string.IsNullOrEmpty(n)) return n;
            var mid = n.Substring(pre, n.Length - pre - suf);
            return mid.Length > 0 ? mid : n;
        }).ToArray();
    }

    private static bool IsLocKey(string s) =>
        string.IsNullOrEmpty(s) || s.StartsWith("@") || s.StartsWith("LOC_") ||
        s.Contains("LOC_UNINITIALIZED") || s.Contains("LOC_EMPTY") || s.Contains("procedural_text_null") ||
        s.Contains("UNINITIALIZED");

    private static bool ContainsJapanese(string s) =>
        !string.IsNullOrEmpty(s) && s.Any(c => c >= '　' && c <= '鿿' || c >= '＀' && c <= '￯');


    private static string FormatFaction(string f)
    {
        // 元データの factionReputation が null (組合のファイル名も無い) のもの。例 PU_Space_Escort_Surveyor_Battaglia_Intro
        if (string.IsNullOrEmpty(f)) return "(勢力指定なし)";
        var original = f;
        if (f.StartsWith("factionreputation_")) f = f["factionreputation_".Length..];
        string FromDb()
        {
            var names = _factionNames;
            if (names != null && !string.IsNullOrEmpty(original))
            {
                if (names.TryGetValue(original, out var n)) return n;
                if (names.TryGetValue("factionreputation_" + f, out n)) return n;
            }
            return f;
        }
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
            _ => FromDb(),
        };
    }

    private static string FormatScope(string s)
    {
        if (_scopeNames != null && !string.IsNullOrEmpty(s) && _scopeNames.TryGetValue(s, out var gameName)) return gameName;
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

    private static readonly System.Text.RegularExpressions.Regex AffinityStandingRegex =
        new(@"^reputationstanding_affinity_(neutral|ally|hostile)_(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex TopRankRegex =
        new(@"_rank6(_|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string FormatStanding(string s)
    {
        // 段階のレコードの displayName をゲームの訳で (StandingDisplayKeys)
        if (_standingNames != null && !string.IsNullOrEmpty(s) && _standingNames.TryGetValue(s, out var gameName)) return gameName;
        // 好感度の段階 (displayName が仮の文字・訳の無い語): 立場の語と値 (例 affinity_neutral_020 → 中立 20)
        var am = AffinityStandingRegex.Match(s ?? "");
        if (am.Success && _stanceWords != null && _stanceWords.TryGetValue(am.Groups[1].Value, out var stance))
            return $"{stance} {int.Parse(am.Groups[2].Value)}";
        s ??= "";
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

    // 評判の量の文。量がゲームの計算で決まるもの (ContractResult_CalculatedReputation) は「+ (量は計算で決まる)」
    private static string FormatRepAmount(ReputationRewardInfo r) => r.Calculated ? "+ (量は計算で決まる)" : FormatRepAmount(r.Amount);

    private static string FormatRepAmount(string a)
    {
        if (a.StartsWith("reputationrewardamount_")) a = a["reputationrewardamount_".Length..];
        // ContractGenerator 行には綴り違いのファイル名 (repuationrewardamount_positive_200 など) もある
        else if (a.StartsWith("repuationrewardamount_")) a = a["repuationrewardamount_".Length..];
        // Wikelo の契約の評判 (wikelo_10 など) は数値に + を付けて出す
        if (a.StartsWith("wikelo_")) return $"+{a["wikelo_".Length..]}";
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

    // 英語原文 → 日本語訳 の逆引き索引。_enDict の線形走査を置き換える。
    // _enDict の列挙順で最初に見つかったものを採用し、走査版と同じ結果になるようにする。
    // 索引は初めて使うときに作る。裏の処理 (納品物の報酬の索引で一覧を別に読む経路) と UI 側から同時に呼ばれても、
    // 作りかけの索引を読まない・二重に作らないように、作成は lock の中で行い、出来上がった 2 つを一度に入れる (入れた後は読むだけ)
    private readonly object _enIndexLock = new();

    private (Dictionary<string, (string ja, string key)> Exact, Dictionary<string, string> Prefix50) EnsureEnglishIndexes()
    {
        lock (_enIndexLock)
        {
            if (_enExactIndex == null || _enPrefix50Index == null) BuildEnglishIndexes();
            return (_enExactIndex!, _enPrefix50Index!);
        }
    }

    // lock (_enIndexLock) の中から呼ぶ (EnsureEnglishIndexes)
    private void BuildEnglishIndexes()
    {
        var exact = new Dictionary<string, (string ja, string key)>(StringComparer.OrdinalIgnoreCase);
        var prefix50 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_enDict != null && _transDict != null)
        {
            foreach (var (key, enVal) in _enDict)
            {
                if (key.StartsWith("@")) continue;
                if (string.IsNullOrEmpty(enVal)) continue;
                if (!_transDict.TryGetValue(key, out var ja) || string.IsNullOrEmpty(ja) || !ContainsJapanese(ja)) continue;

                if (!exact.ContainsKey(enVal))
                    exact[enVal] = (ja, key);

                if (enVal.Length >= 50)
                {
                    var p = enVal[..50];
                    if (!prefix50.ContainsKey(p))
                        prefix50[p] = ja;
                }
            }
        }
        _enPrefix50Index = prefix50;
        _enExactIndex = exact;
    }

    private (string ja, string key) ResolveJaByEnglishWithKey(string english)
    {
        if (_enDict == null || _transDict == null || string.IsNullOrEmpty(english)) return ("", "");
        var (exact, _) = EnsureEnglishIndexes();
        return exact.TryGetValue(english, out var hit) ? hit : ("", "");
    }

    private string ResolveJaByEnglish(string english)
    {
        if (_enDict == null || _transDict == null || string.IsNullOrEmpty(english)) return "";
        var (exact, prefix50) = EnsureEnglishIndexes();
        if (exact.TryGetValue(english, out var hit)) return hit.ja;

        // 長文の場合: 先頭50文字で前方一致
        if (english.Length > 50 && prefix50.TryGetValue(english[..50], out var ja))
            return ja;

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
        // キーが見つからなければ末尾に ",P" を付けたキーも試す (",P" 付きでしか存在しないキーがあるため)
        var val = LookupLoc(raw, dict) ?? LookupLoc(raw + ",P", dict);
        if (val == null) return "";
        return val.Replace("\\n", "\n");
    }

    // そのまま / @ 付き / @ 無し の順で引く (見つからなければ null)
    private static string? LookupLoc(string raw, Dictionary<string, string> dict)
    {
        if (dict.TryGetValue(raw, out var v1) && !string.IsNullOrEmpty(v1)) return v1;
        if (!raw.StartsWith("@") && dict.TryGetValue("@" + raw, out var v2) && !string.IsNullOrEmpty(v2)) return v2;
        if (raw.StartsWith("@") && dict.TryGetValue(raw[1..], out var v3) && !string.IsNullOrEmpty(v3)) return v3;
        return null;
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

    // ── Wikelo の契約の納品物 ──

    // 納品物 1 件 (契約タブの納品物の表の 1 行)
    public sealed class DeliverableItem
    {
        // 表の名前: 英語名 (ゲームと同じ)。取れなければ日本語名、それも無ければエンティティのファイル名・資源の record の短い名前
        public string Name { get; init; } = "";
        // 表の数: 品は「n」(min と max が違い、どちらも 0 でなければ「min〜max」)、資源は「n SCU」
        public string Amount { get; init; } = "";
        public bool IsResource { get; init; }
        // エンティティのファイル名 (.json を除いたもの) / 資源の record 名の "ResourceType." を除いたもの
        public string ShortName { get; init; } = "";
        // item_index の record_name (品が item_index に無ければ "") / 資源は resource_types の record_name (無ければ "")
        public string Record { get; init; } = "";
        // item_index の uuid (同じ record の行すべて。店の売り場を引く)
        public List<string> Uuids { get; init; } = new();
        // 英語名 (取れなければ "")。UEX・交易の価格を英語名で引く
        public string NameEn { get; init; } = "";
        // 英語名が取れず日本語名で代用した
        public bool NameIsJapanese { get; init; }
        // 品・資源の表 (item_index / resource_types) に無い
        public bool NotInIndex { get; init; }
    }

    // Wikelo の契約か: 分類が wikelo_* (Wikelo - 乗り物 / Wikelo - その他のアイテム)、または raw_json にテンプレートの納品物 (_TemplateHauling_。
    // Wikelo のテンプレートを使う契約にだけ入れる) がある (導入の契約 TheCollector_Intro / TheCollector_Lunch は分類が collection)
    public static bool IsWikeloContract(MissionEntry m) =>
        CleanType(m.MissionType ?? "").StartsWith("wikelo_", StringComparison.Ordinal)
        || (m.RawJson ?? "").Contains("\"_TemplateHauling_\"", StringComparison.Ordinal);

    // 納品の注文 1 件 (契約側 haulingOrderContent[] / テンプレート側 haulingOrders[] のどちらか)
    private readonly record struct HaulingPick(bool IsResource, string Ref, double Min, double Max);

    // Wikelo の契約の納品物。Wikelo の契約でない・納品物が無ければ空。
    //   契約側: _Contract_.paramOverrides.propertyOverrides[] の value.haulingOrderContent[] (missionVariableName ごと。HaulingOverride など)
    //   テンプレート側: raw_json の "_TemplateHauling_" (インデックス構築で入れる。GameDataExtractor.BuildTemplateHaulingJson) の orders[]
    //   テンプレート側がある: orders[] を順に読み、HaulingOrder_EntityClass / _Resource はその品、HaulingOrder_Property は
    //     haulingOrdersProperty が指す property の missionVariableName (propertyVariables。引けなければ HaulingOverride) の契約側の品を入れる。
    //     テンプレートに HaulingOrder_Property が無ければ、契約側に HaulingOverride があっても使わない
    //     (ゲームの処理はテンプレートの haulingOrders を使い、上書きは Property の所でだけ取り込む作りと推定)
    //   テンプレート側が無い (構築し直す前の DB): 契約側の HaulingOverride の品
    public List<DeliverableItem> GetDeliverables(MissionEntry m)
    {
        var result = new List<DeliverableItem>();
        var picks = ReadDeliverablePicks(m);
        if (picks.Count == 0) return result;

        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        foreach (var p in picks)
            result.Add(p.IsResource ? ResolveResourceDeliverable(conn, p) : ResolveEntityDeliverable(conn, p));
        return result;
    }

    // 構築 (GameDataExtractor.ExtractDeliverableSourcesAsync) が入手場所を調べる品を決めるのに使う: missions の 1 行の納品物
    // (資源か, 品はエンティティのファイル名・資源は ResourceType の record 名)。一覧と同じく test の契約は除き (LoadAllMissions と同じ条件)、
    // 納品物は GetDeliverables と同じ読み方 (ReadDeliverablePicks)
    internal static List<(bool IsResource, string Ref)> DeliverableRefsForBuild(string recordName, string missionType, string rawJson)
    {
        if (recordName.Contains("_test", StringComparison.OrdinalIgnoreCase)) return new();
        if (recordName.Contains("ReputationTest", StringComparison.OrdinalIgnoreCase)) return new();
        var m = new MissionEntry { RecordName = recordName, MissionType = missionType, RawJson = rawJson };
        return ReadDeliverablePicks(m).Select(p => (p.IsResource, p.Ref)).ToList();
    }

    // GetDeliverables の納品物の読み方 (上の説明)。Wikelo の契約でない・読めない・納品物が無ければ空
    private static List<HaulingPick> ReadDeliverablePicks(MissionEntry m)
    {
        var picks = new List<HaulingPick>();
        if (!IsWikeloContract(m) || string.IsNullOrEmpty(m.RawJson)) return picks;
        try
        {
            using var doc = JsonDocument.Parse(m.RawJson);
            var root = doc.RootElement;
            // 契約側の上書き: missionVariableName → haulingOrderContent[] の品 (同じ名前は先のもの)
            var overrides = new Dictionary<string, List<HaulingPick>>(StringComparer.Ordinal);
            if (root.TryGetProperty("_Contract_", out var contract) && contract.ValueKind == JsonValueKind.Object
                && contract.TryGetProperty("paramOverrides", out var po) && po.ValueKind == JsonValueKind.Object
                && po.TryGetProperty("propertyOverrides", out var props) && props.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in props.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var name = GetString(p, "missionVariableName");
                    if (name.Length == 0 || overrides.ContainsKey(name)) continue;
                    if (!p.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Object) continue;
                    if (!v.TryGetProperty("haulingOrderContent", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                    var list = new List<HaulingPick>();
                    foreach (var c in content.EnumerateArray())
                        if (ReadHaulingPick(c) is { } hp) list.Add(hp);
                    overrides[name] = list;
                }
            }

            if (root.TryGetProperty("_TemplateHauling_", out var th) && th.ValueKind == JsonValueKind.Object
                && th.TryGetProperty("orders", out var orders) && orders.ValueKind == JsonValueKind.Array)
            {
                var vars = th.TryGetProperty("propertyVariables", out var pv) && pv.ValueKind == JsonValueKind.Object ? pv : default;
                foreach (var o in orders.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Object) continue;
                    if (GetString(o, "_Type_") == "HaulingOrder_Property")
                    {
                        var ptr = GetString(o, "haulingOrdersProperty");
                        if (ptr.StartsWith("_PointsTo_:", StringComparison.Ordinal)) ptr = ptr["_PointsTo_:".Length..];
                        var varName = vars.ValueKind == JsonValueKind.Object ? GetString(vars, ptr) : "";
                        if (varName.Length == 0) varName = "HaulingOverride";
                        if (overrides.TryGetValue(varName, out var list)) picks.AddRange(list);
                        continue;
                    }
                    if (ReadHaulingPick(o) is { } hp) picks.Add(hp);
                }
            }
            else if (overrides.TryGetValue("HaulingOverride", out var list))
            {
                picks.AddRange(list);
            }
        }
        catch (JsonException) { return new List<HaulingPick>(); }
        return picks;
    }

    // 注文 1 件を読む。_Type_ が …_EntityClass なら entityClass のファイル名と minAmount / maxAmount、
    // …_Resource なら resource の _RecordName_ と minSCU / maxSCU。それ以外 (HaulingOrder_Property など) は null
    private static HaulingPick? ReadHaulingPick(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        var type = GetString(o, "_Type_");
        if (type.EndsWith("_EntityClass", StringComparison.Ordinal))
        {
            var file = ExtractFileName(GetString(o, "entityClass"));
            if (file.Length == 0) return null;
            return new HaulingPick(false, file, GetDouble(o, "minAmount"), GetDouble(o, "maxAmount"));
        }
        if (type.EndsWith("_Resource", StringComparison.Ordinal))
        {
            if (!o.TryGetProperty("resource", out var res) || res.ValueKind != JsonValueKind.Object) return null;
            var rec = GetString(res, "_RecordName_");
            if (rec.Length == 0) return null;
            return new HaulingPick(true, rec, GetDouble(o, "minSCU"), GetDouble(o, "maxSCU"));
        }
        return null;
    }

    // 数の文字: min と max が違い、どちらも 0 でなければ「min〜max」、ほかは min
    private static string DeliverableAmount(double min, double max, string unit)
    {
        static string F(double v) => v.ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture);
        var text = min != max && min != 0 && max != 0 ? $"{F(min)}〜{F(max)}" : F(min);
        return unit.Length > 0 ? $"{text} {unit}" : text;
    }

    // 英語名として使える文字か (空・ローカライズキーのまま・仮の文字でない)
    private static bool UsableName(string s) =>
        !string.IsNullOrWhiteSpace(s) && !IsLocKey(s) && !s.StartsWith("<=", StringComparison.Ordinal)
        && !s.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase);

    // 品: item_index の record_name = "EntityClassDefinition." + ファイル名 (大文字小文字無視。［詳細］の GetItemDetailText と同じ照合)。
    // 英語名は name_key を翻訳 DB の英語で引く。引けなければ日本語名 (name_ja → name)
    private DeliverableItem ResolveEntityDeliverable(SqliteConnection conn, HaulingPick p)
    {
        string record = "", nameKey = "", nameJa = "", name = "";
        var uuids = new List<string>();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT uuid, record_name, COALESCE(name, ''), COALESCE(name_ja, ''), COALESCE(name_key, '') FROM item_index WHERE record_name = @rn COLLATE NOCASE ORDER BY rowid";
            cmd.Parameters.AddWithValue("@rn", "EntityClassDefinition." + p.Ref);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                uuids.Add(r.GetString(0));
                if (record.Length > 0) continue;
                record = r.GetString(1); name = r.GetString(2); nameJa = r.GetString(3); nameKey = r.GetString(4);
            }
        }
        catch (SqliteException) { }
        var amount = DeliverableAmount(p.Min, p.Max, "");
        if (record.Length == 0)
            return new DeliverableItem { Name = p.Ref, Amount = amount, ShortName = p.Ref, NotInIndex = true };
        var en = nameKey.Length > 0 ? ResolveEn(nameKey).Trim() : "";
        if (UsableName(en))
            return new DeliverableItem { Name = en, NameEn = en, Amount = amount, ShortName = p.Ref, Record = record, Uuids = uuids };
        var ja = UsableName(nameJa) ? nameJa.Trim() : UsableName(name) ? name.Trim() : "";
        return new DeliverableItem
        {
            Name = ja.Length > 0 ? ja : p.Ref, NameIsJapanese = ja.Length > 0, Amount = amount, ShortName = p.Ref, Record = record, Uuids = uuids,
        };
    }

    // 資源: resource_types の record_name (例 ResourceType.Quantanium)。英語名は name_en、無ければ name_key を翻訳 DB の英語で引く。
    // 引けなければ name_key の日本語
    private DeliverableItem ResolveResourceDeliverable(SqliteConnection conn, HaulingPick p)
    {
        var shortName = p.Ref.StartsWith("ResourceType.", StringComparison.Ordinal) ? p.Ref["ResourceType.".Length..] : p.Ref;
        string record = "", nameKey = "", nameEn = "";
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT record_name, COALESCE(name_key, ''), COALESCE(name_en, '') FROM resource_types WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", p.Ref);
            using var r = cmd.ExecuteReader();
            if (r.Read()) { record = r.GetString(0); nameKey = r.GetString(1); nameEn = r.GetString(2); }
        }
        catch (SqliteException) { }
        var amount = DeliverableAmount(p.Min, p.Max, "SCU");
        if (record.Length == 0)
            return new DeliverableItem { Name = shortName, Amount = amount, IsResource = true, ShortName = shortName, NotInIndex = true };
        var en = UsableName(nameEn) ? nameEn.Trim() : nameKey.Length > 0 ? ResolveEn(nameKey).Trim() : "";
        if (UsableName(en))
            return new DeliverableItem { Name = en, NameEn = en, Amount = amount, IsResource = true, ShortName = shortName, Record = record };
        var ja = nameKey.Length > 0 ? ResolveJa(nameKey).Trim() : "";
        return new DeliverableItem
        {
            Name = UsableName(ja) ? ja : shortName, NameIsJapanese = UsableName(ja), Amount = amount, IsResource = true, ShortName = shortName, Record = record,
        };
    }

    // 契約の報酬の品: エンティティのファイル名 (小文字) → その品を報酬に出す契約 (一覧と同じく test の契約は除く)。初めて使うときに作る
    private Dictionary<string, List<MissionEntry>>? _rewardMissionsByEntity;
    private readonly object _rewardIndexLock = new();

    // 報酬の品を読む: _Contract_.contractResults.contractResults[] の ContractResult_Item (entityClass) と
    // ContractResult_ItemsWeighting の itemAwardStructure[].awards[] の ItemAwardEntityClass (entityClass)。
    // どちらも missionResults の先頭 (成功) が true のものだけ (subContracts の中は読まない。評判の報酬と同じ)
    private Dictionary<string, List<MissionEntry>> RewardMissionsByEntity()
    {
        lock (_rewardIndexLock)
        {
            if (_rewardMissionsByEntity != null) return _rewardMissionsByEntity;
            var map = new Dictionary<string, List<MissionEntry>>(StringComparer.Ordinal);
            void Add(string entityClass, MissionEntry m)
            {
                var file = ExtractFileName(entityClass).ToLowerInvariant();
                if (file.Length == 0) return;
                if (!map.TryGetValue(file, out var list)) map[file] = list = new List<MissionEntry>();
                if (!list.Contains(m)) list.Add(m);
            }
            static bool OnSuccess(JsonElement cr) =>
                cr.TryGetProperty("missionResults", out var flags) && flags.ValueKind == JsonValueKind.Array
                && flags.GetArrayLength() > 0 && flags[0].ValueKind == JsonValueKind.True;
            // 裏の処理から呼ぶので _conn は使わない。読み込み済みの一覧があればそれ、無ければ別の接続で読む (一覧としては覚えない)
            var all = _allCache;
            if (all == null)
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Pooling=False");
                conn.Open();
                all = ReadAllMissions(conn);
                foreach (var e in all) ParseRawJson(e);   // 一覧の名前 (ListNameJa / ListNameEn) を埋める
            }
            foreach (var m in all)
            {
                if (string.IsNullOrEmpty(m.RawJson) || !m.RawJson.Contains("ContractResult_Item", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(m.RawJson);
                    if (!doc.RootElement.TryGetProperty("_Contract_", out var contract) || contract.ValueKind != JsonValueKind.Object) continue;
                    if (!contract.TryGetProperty("contractResults", out var results) || results.ValueKind != JsonValueKind.Object) continue;
                    if (!results.TryGetProperty("contractResults", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                    foreach (var cr in list.EnumerateArray())
                    {
                        if (cr.ValueKind != JsonValueKind.Object || !OnSuccess(cr)) continue;
                        var type = GetString(cr, "_Type_");
                        if (type == "ContractResult_Item") Add(GetString(cr, "entityClass"), m);
                        else if (type == "ContractResult_ItemsWeighting"
                                 && cr.TryGetProperty("itemAwardStructure", out var structure) && structure.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var w in structure.EnumerateArray())
                            {
                                if (w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("awards", out var awards) || awards.ValueKind != JsonValueKind.Array) continue;
                                foreach (var a in awards.EnumerateArray())
                                    if (a.ValueKind == JsonValueKind.Object && GetString(a, "_Type_") == "ItemAwardEntityClass")
                                        Add(GetString(a, "entityClass"), m);
                            }
                        }
                    }
                }
                catch (JsonException) { }
            }
            return _rewardMissionsByEntity = map;
        }
    }

    // 納品物の入手場所の文字 (契約タブの納品物の表の行を選んだときに出す):
    //   品: 店で購入 (item_shop_prices の購入。星系 / 場所 / 店名 / 値段)・UEX (equipment_cache.db の uex_item_prices を英語名で引く)・
    //       ブループリント (BlueprintDetail.ItemSectionText。材料・その BP を報酬に出す契約)・他の契約の報酬 (RewardMissionsByEntity)
    //   資源: 交易の購入場所 (trade_cache.db の trade_prices の is_current = 1 で price_buy > 0 の行。ターミナル / 星系 / 値段)
    //   どれも無ければ「入手場所のデータなし」
    public string FormatDeliverableSources(DeliverableItem d)
    {
        var lines = new List<string>();
        lines.Add($"■ {d.Name} の入手場所");
        if (d.NotInIndex) lines.Add(d.IsResource ? "  (資源の表に無い)" : "  (品目の表に無い)");
        var any = false;
        if (!d.IsResource)
        {
            // 店で購入 (cstone / UEX の保存データ)
            var hasShopBuys = false;
            if (d.Uuids.Count > 0)
            {
                try
                {
                    using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Pooling=False");
                    conn.Open();
                    var map = ShopPriceStore.LoadMany(conn, d.Uuids);
                    var buys = d.Uuids.Where(map.ContainsKey).SelectMany(u => map[u])
                        .Where(r => r.Kind == ShopPriceStore.KindBuy && r.Price.HasValue)
                        .Select(r => new ShopListing(r.Location, r.Price!.Value, r.Source)).ToList();
                    if (buys.Count > 0)
                    {
                        any = true;
                        hasShopBuys = true;
                        lines.Add("");
                        lines.Add("【店で購入】 星系 / 場所 / 店名 / 値段 (cstone / UEX の保存データ)");
                        foreach (var s in buys.GroupBy(s => s.Location).Select(g => g.MinBy(s => s.Price)!).OrderBy(s => s.Price).ThenBy(s => s.Location, StringComparer.OrdinalIgnoreCase))
                            lines.Add("  " + s.Line());
                    }
                }
                catch (SqliteException) { }
            }

            // UEX (items_prices の保存データ。英語名で照合)。［詳細］タブ (GameDataQueryService.ShopText) と同じく、店の購入が無いときだけ
            if (!hasShopBuys && d.NameEn.Length > 0)
            {
                var uex = LoadUexItemPrices(d.NameEn);
                if (uex.Count > 0)
                {
                    any = true;
                    lines.Add("");
                    lines.Add("【UEX】 星系 / 場所 / 店名 / 値段");
                    foreach (var s in uex.GroupBy(s => s.Location).Select(g => g.MinBy(s => s.Price)!).OrderBy(s => s.Price).ThenBy(s => s.Location, StringComparer.OrdinalIgnoreCase))
                        lines.Add("  " + s.Line());
                }
            }

            // ブループリント (材料・その BP を報酬に出す契約)。表の形 (「| 枠 | 材料 | … |」) は文字の欄で読める行に直す (PlainTableLines)
            if (d.Record.Length > 0)
            {
                string bp;
                try { bp = BlueprintDetail.ItemSectionText(_workDir, d.Record); }
                catch (Exception ex) { bp = $"【ブループリント】\n  BP を読み込めなかった ({ex.Message})"; }
                if (bp.Length > 0)
                {
                    any = true;
                    lines.Add("");
                    lines.AddRange(PlainTableLines(bp.Replace("\r", "").TrimEnd('\n').Split('\n')));
                }
            }

            // 他の契約の報酬
            if (RewardMissionsByEntity().TryGetValue(d.ShortName.ToLowerInvariant(), out var rewardMissions) && rewardMissions.Count > 0)
            {
                var names = rewardMissions.Select(MissionNameForList).Where(n => n.Length > 0)
                    .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                if (names.Count > 0)
                {
                    any = true;
                    lines.Add("");
                    lines.Add($"【他の契約の報酬】 {names.Count} 件");
                    foreach (var n in names) lines.Add($"  - {n}");
                }
            }
        }
        else if (d.NameEn.Length > 0)
        {
            var buys = LoadTradeBuyPrices(d.NameEn);
            if (buys.Count > 0)
            {
                any = true;
                lines.Add("");
                lines.Add("【資源の購入場所】 ターミナル / 星系 / 値段 (交易データ)");
                foreach (var (terminal, system, price) in buys)
                    lines.Add($"  {terminal} / {(system.Length > 0 ? system : "-")} / {price:N0} aUEC/SCU");
            }
        }
        // ゲームデータからの入手場所 (インデックス構築で作る deliverable_sources。場所が 1 つでもあれば「データなし」にしない)
        var (gameLines, gameHasPlace) = GameDataSourceLines(d);
        if (gameHasPlace) any = true;
        lines.AddRange(gameLines);
        if (!any)
        {
            lines.Add("");
            lines.Add("入手場所のデータなし");
        }
        return string.Join("\n", lines);
    }

    // ゲームデータからの入手場所の節 (GameDataExtractor.ExtractDeliverableSourcesAsync が作る deliverable_sources)。節の順と見出し
    private static readonly (string Section, string Header)[] GameDataSourceSections =
    [
        (GameDataExtractor.DeliverableSourceSections.Mining, "【ゲームデータ: 採掘・収穫】"),
        (GameDataExtractor.DeliverableSourceSections.Creature, "【ゲームデータ: 生き物 (惑星の生態)】"),
        (GameDataExtractor.DeliverableSourceSections.Placed, "【ゲームデータ: 配置】"),
        (GameDataExtractor.DeliverableSourceSections.Holder, "【ゲームデータ: この品を持つ生き物・NPC の配置】"),
        (GameDataExtractor.DeliverableSourceSections.Spawn, "【ゲームデータ: 出現 (タグで選ぶ出現の設定)】（推定）"),
        (GameDataExtractor.DeliverableSourceSections.LootListed, "【ゲームデータ: ルート (表に品が載っている)】"),
        (GameDataExtractor.DeliverableSourceSections.Loot, "【ゲームデータ: ルート (タグの評価)】（推定）"),
    ];

    // 品・資源のゲームデータからの入手場所の行と、場所 (惑星・月など・近い場所) が 1 つでもあったか。表が無い (構築し直す前の DB) なら空。
    // 節ごとに「惑星・月など」(英語名)・「名前の引けない socpak」・「近い場所」(英語名)・「場所まで辿れないもの」(件数と最初の 5 件) を出す
    private (List<string> Lines, bool HasPlace) GameDataSourceLines(DeliverableItem d)
    {
        var lines = new List<string>();
        var hasPlace = false;
        var rows = new List<(string Section, string PlaceType, string Name)>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT section, place_type, name FROM deliverable_sources WHERE target_kind = @k AND target IN (@t1, @t2)";
            cmd.Parameters.AddWithValue("@k", d.IsResource ? "resource" : "entity");
            var shortLower = d.ShortName.ToLowerInvariant();
            cmd.Parameters.AddWithValue("@t1", shortLower);
            cmd.Parameters.AddWithValue("@t2", d.IsResource ? "resourcetype." + shortLower : shortLower);
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        catch (SqliteException) { return (lines, false); }
        static string Names(IEnumerable<string> names) =>
            string.Join(", ", names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        foreach (var (section, header) in GameDataSourceSections)
        {
            var sec = rows.Where(x => x.Section == section).ToList();
            if (sec.Count == 0) continue;
            lines.Add("");
            lines.Add(header);
            var planets = sec.Where(x => x.PlaceType == "planet").Select(x => x.Name).ToList();
            var raw = sec.Where(x => x.PlaceType == "planet_raw").Select(x => x.Name).ToList();
            var near = sec.Where(x => x.PlaceType == "near").Select(x => x.Name).ToList();
            var unlocated = sec.Where(x => x.PlaceType == "unlocated").Select(x => x.Name).Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (planets.Count + raw.Count + near.Count > 0) hasPlace = true;
            if (planets.Count > 0) lines.Add($"  惑星・月など: {Names(planets)}");
            if (raw.Count > 0) lines.Add($"  名前の引けない socpak: {Names(raw)}");
            if (near.Count > 0) lines.Add($"  近い場所: {Names(near)}");
            if (unlocated.Count > 0)
            {
                lines.Add($"  場所まで辿れないもの: {unlocated.Count} 件");
                foreach (var u in unlocated.Take(5)) lines.Add($"    - {u}");
                if (unlocated.Count > 5) lines.Add($"    ほか {unlocated.Count - 5} 件");
            }
        }
        return (lines, hasPlace);
    }

    // 「|」の表の行 (見出しの行・区切りの行 |---|・中身の行) を、文字の欄で読める行にする。表でない行はそのまま。中身は削らない:
    //   列が 2 つ以上: 中身の行ごとに「  - 見出し1: 値1 / 見出し2: 値2 / …」 (例「  - 枠: … / 材料: Copper / 量: 0.5 SCU / 最低品質: なし」)
    //   列が 1 つ   : 見出しを「  見出し:」、中身の行を「    - 値」
    private static List<string> PlainTableLines(IReadOnlyList<string> src)
    {
        static bool IsRow(string l) { var t = l.Trim(); return t.Length >= 2 && t.StartsWith('|') && t.EndsWith('|'); }
        static bool IsSeparator(string l) => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*\|(?:\s*:?-+:?\s*\|)+\s*$");
        static List<string> Cells(string l) { var t = l.Trim(); return t[1..^1].Split('|').Select(c => c.Trim()).ToList(); }
        var result = new List<string>();
        for (int i = 0; i < src.Count; i++)
        {
            var line = src[i];
            if (!IsRow(line) || i + 1 >= src.Count || !IsSeparator(src[i + 1])) { result.Add(line); continue; }
            var indent = line[..(line.Length - line.TrimStart().Length)];
            var heads = Cells(line);
            i++;   // 区切りの行
            if (heads.Count == 1) result.Add($"{indent}{heads[0]}:");
            while (i + 1 < src.Count && IsRow(src[i + 1]) && !IsSeparator(src[i + 1]))
            {
                i++;
                var cells = Cells(src[i]);
                if (heads.Count == 1) { result.Add($"{indent}  - {string.Join(" / ", cells)}"); continue; }
                var parts = cells.Select((c, k) => k < heads.Count ? $"{heads[k]}: {c}" : c);
                result.Add($"{indent}- {string.Join(" / ", parts)}");
            }
        }
        return result;
    }

    // 契約の一覧の名前と同じ「日本語名 (英語名)」(日本語が無い・英語と同じなら英語名だけ)
    private static string MissionNameForList(MissionEntry m)
    {
        var ja = m.ListNameJa;
        var en = m.ListNameEn;
        if (!string.IsNullOrEmpty(ja) && !string.IsNullOrEmpty(en) && !ja.Equals(en, StringComparison.OrdinalIgnoreCase)) return $"{ja} ({en})";
        return !string.IsNullOrEmpty(en) ? en : ja ?? "";
    }

    // equipment_cache.db の uex_item_prices のうち、品目名が nameEn と同じもの (GameDataQueryService.NormalizeItemName で照合)。表が無ければ空
    private List<ShopListing> LoadUexItemPrices(string nameEn)
    {
        var result = new List<ShopListing>();
        var path = Path.Combine(_workDir, "equipment_cache.db");
        if (!File.Exists(path)) return result;
        var key = GameDataQueryService.NormalizeItemName(nameEn);
        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT item_name, location, price FROM uex_item_prices";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0) || GameDataQueryService.NormalizeItemName(r.GetString(0)) != key) continue;
                result.Add(new ShopListing(r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? 0 : r.GetDouble(2), ShopListing.SourceUexItemsPrices));
            }
        }
        catch (SqliteException) { }
        return result;
    }

    // trade_cache.db の trade_prices の今の値 (is_current = 1) で、品名が nameEn (大文字小文字無視) で price_buy > 0 の行 (安い順)。表が無ければ空
    private List<(string Terminal, string StarSystem, double Price)> LoadTradeBuyPrices(string nameEn)
    {
        var result = new List<(string, string, double)>();
        var path = Path.Combine(_workDir, "trade_cache.db");
        if (!File.Exists(path)) return result;
        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(terminal, ''), COALESCE(star_system, ''), price_buy FROM trade_prices " +
                              "WHERE is_current = 1 AND price_buy > 0 AND commodity_name = @n COLLATE NOCASE ORDER BY price_buy, terminal";
            cmd.Parameters.AddWithValue("@n", nameEn.Trim());
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add((r.GetString(0), r.GetString(1), r.GetDouble(2)));
        }
        catch (SqliteException) { }
        return result;
    }

    public void Dispose()
    {
        _conn?.Close();
        _conn?.Dispose();
    }

    public class MissionCategory : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public override string ToString() => $"{Name} ({Count})";

        // ミッション一覧で選んだ契約のカテゴリ (左の一覧で水色にする)。ListBox の選択とは別
        private bool _isHighlighted;
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set
            {
                if (_isHighlighted == value) return;
                _isHighlighted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class MissionEntry
    {
        public string RecordName { get; set; } = "";
        // 分類・難易度・場所の推測と表示名に使う名前 (MBE / Wiki 行は record_name、ContractGenerator 行は契約 debugName)
        public string NameBase { get; set; } = "";
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
        // ミッションタブの一覧に出す名前 (強調タグを消し、~mission(…) を〔〕にしたもの)。検索や分類には使わない
        public string ListNameJa => MissionDisplayText.ToPlain(DisplayNameJa);
        public string ListNameEn => MissionDisplayText.ToPlain(DisplayNameEn);
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
        // ContractGenerator 行のパーティ共有 (契約 → 生成器の CanBeShared。どちらにも無ければ null)
        public bool? ContractCanBeShared { get; set; }
        // ContractGenerator 行で Illegal が契約・生成器・テンプレートのどこにも無い (合法性「不明」)。MBE / Wiki 行は常に false
        public bool LawfulnessUnknown { get; set; }
        // ContractGenerator 行の契約 id (record_name の GUID 部分)。MBE / Wiki 行は ""
        public string ContractId { get; set; } = "";
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
        public bool Parsed { get; set; }


        public List<string> RequiredMissions { get; set; } = new();
        public List<ReputationRequirement> RepRequirements { get; set; } = new();
        public List<ReputationRewardInfo> SuccessRepRewards { get; set; } = new();
        public List<ReputationRewardInfo> AbandonRepRewards { get; set; } = new();
        public List<ReputationRewardInfo> FailRepRewards { get; set; } = new();
        public List<SpawnGroupInfo> EnemySpawns { get; set; } = new();

        // 一覧の「星系」列 (Stanton / Pyro / Nyx。複数なら " / " でつなぐ。分からなければ "")。ParseRawJson で決める
        public string StarSystem { get; set; } = "";
        // 一覧の「カテゴリ」列
        public string Category => CategoryOf(this);
        // 一覧の「評判」列: 成功時の評判 ("組織 量" を ", " でつなぐ。無ければ "")
        // 成功時の評判の一覧の文。範囲が「評判」(factionreputationscope。ゲームの評判の画面のいちばん上の値) 以外の項目は、
        // 1 種類でもいつも「 (範囲)」を付ける (肉体労働者・暗殺などだけが上がる契約を、評判が上がる契約と見分けられるように)。
        // 範囲が 2 種類以上ある契約は、評判の項目にも「 (範囲)」を付ける。組合・範囲・量が全部同じ項目は 1 つにまとめる
        public string SuccessRepSummary
        {
            get
            {
                var several = SuccessRepRewards.Select(r => r.Scope).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
                bool WithScope(ReputationRewardInfo r) => !string.IsNullOrEmpty(r.Scope)
                    && (several || !r.Scope.Equals("factionreputationscope", StringComparison.OrdinalIgnoreCase));
                return string.Join(", ", SuccessRepRewards
                    .Select(r => $"{FormatFaction(r.Faction)} {FormatRepAmount(r)}".TrimEnd() + (WithScope(r) ? $" ({FormatScope(r.Scope)})" : ""))
                    .Distinct(StringComparer.Ordinal));
            }
        }
        // 成功時に評判が上がる (量が + のものが 1 つでもある)。一覧の行を薄い緑にする
        public bool GivesReputation => SuccessRepRewards.Any(r => FormatRepAmount(r).StartsWith("+"));

        public string DisplayReward =>
            RewardBase > 0 ? $"{RewardBase:N0}"
            : RewardMax > 0 ? $"{RewardMax:N0}"
            : RewardMin > 0 ? $"{RewardMin:N0}"
            : "-";
        // 一覧の「報酬」列の並べ替えに使う数 (DisplayReward と同じ値。"-" は 0)
        public double RewardSortValue =>
            RewardBase > 0 ? RewardBase
            : RewardMax > 0 ? RewardMax
            : RewardMin > 0 ? RewardMin
            : 0;
    }

    // 契約タブの一覧の 1 行: 名前 (日本語名と英語名の組) とカテゴリが同じ契約をまとめたもの (GroupByName)。
    // 各列は、まとめた契約で値が全部同じならその値、違えば違う値を重複を除いて「 / 」でつないだもの (空の値は「なし」)
    public class MissionGroup
    {
        public MissionGroup(List<MissionEntry> members)
        {
            Members = members;
            var rep = members[0];
            var suffix = members.Count > 1 ? $" ×{members.Count}" : "";
            // 件数は一覧で上に出る名前に付ける (日本語名があれば日本語名、無ければ英語名)
            ListNameJa = rep.ListNameJa.Length > 0 ? rep.ListNameJa + suffix : "";
            ListNameEn = rep.ListNameJa.Length > 0 ? rep.ListNameEn : rep.ListNameEn + suffix;
            CleanedName = rep.CleanedName;
            Category = rep.Category;
            StarSystem = JoinStarSystems();
            SuccessRepSummary = JoinReputation();
            Difficulty = Join(m => m.Difficulty);
            DifficultyJa = Join(m => MissionService.DifficultyJa(m.Difficulty));
            RankText = JoinRank();
            RankSortValue = members.Min(m => RankOrder(m.WikiRank));
            _difficultySortValue = members.Min(m => string.Equals(m.Difficulty, "Super", StringComparison.OrdinalIgnoreCase) ? 6 : m.DifficultyOrder);
            DisplayReward = Join(m => m.DisplayReward);
            Location = Join(m => m.Location);
            RewardSortValue = members.Min(m => m.RewardSortValue);
            GivesReputation = members.Any(m => m.GivesReputation);
        }

        // 値が違うときは、各値を「 / 」で区切った部分ごとに重複を除いて、出てきた順につなぐ (空の値は「なし」)
        private string Join(Func<MissionEntry, string> get)
        {
            var values = Members.Select(get).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count == 1) return values[0];
            var parts = values
                .SelectMany(v => string.IsNullOrEmpty(v) ? new[] { "なし" } : v.Split(" / "))
                .Distinct(StringComparer.Ordinal);
            return string.Join(" / ", parts);
        }

        // ランクの列: Join と同じく、全部同じならその値、違えば重複を除いて出てきた順に「 / 」でつなぐ。
        // ランクが決まらない契約 (空) は「不明」と出す (1 件の行でも、まとめた行に混ざるときも)
        private string JoinRank()
        {
            var parts = Members.Select(m => string.IsNullOrEmpty(m.WikiRank) ? "不明" : m.WikiRank).Distinct(StringComparer.Ordinal);
            return string.Join(" / ", parts);
        }

        // ランクの列の並べ替えに使う数 (契約者ランクの順。不明は最後)
        private static int RankOrder(string rank)
        {
            var i = Array.IndexOf(RankNames, rank ?? "");
            return i >= 0 ? i : 99;
        }

        // 評判の列: 値が違うときは、各値を「, 」で区切った部分ごとに重複を除いて、出てきた順に「, 」でつなぐ (空の値は「なし」)
        private string JoinReputation()
        {
            var values = Members.Select(m => m.SuccessRepSummary).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count == 1) return values[0];
            var parts = values
                .SelectMany(v => string.IsNullOrEmpty(v) ? new[] { "なし" } : v.Split(", "))
                .Distinct(StringComparer.Ordinal);
            return string.Join(", ", parts);
        }

        // 星系の列: まとめた契約の星系を合わせて、1 件の行と同じ Stanton, Pyro, Nyx の順 (FormatStarSystems) で出す。
        // 星系の分からない契約 (空) が混ざるときは最後に「なし」を付ける
        private string JoinStarSystems()
        {
            var values = Members.Select(m => m.StarSystem).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count == 1) return values[0];
            var bits = values.Aggregate(0, (b, v) => b | StarSystemBitsOf(v));
            var text = FormatStarSystems(bits);
            if (values.Any(string.IsNullOrEmpty)) text = text.Length > 0 ? text + " / なし" : "なし";
            return text;
        }

        public List<MissionEntry> Members { get; }
        // 代表の契約 (まとめた中の 1 件目)
        public MissionEntry Representative => Members[0];
        public string ListNameJa { get; }
        public string ListNameEn { get; }
        // 名前の列の並べ替え (代表の契約の CleanedName)
        public string CleanedName { get; }
        public string Category { get; }
        public string StarSystem { get; }
        public string SuccessRepSummary { get; }
        // 難易度 (英語の値。難易度の列の並べ替えに使う)
        public string Difficulty { get; }
        // 難易度の列に出す日本語 (DifficultyJa)
        public string DifficultyJa { get; }
        // 難易度の列の並べ替え (易しい順。まとめた中でいちばん易しいもの。DiffOrder と同じ順で、不明・Super は後ろ)
        public int DifficultySortValue => _difficultySortValue;
        private readonly int _difficultySortValue;
        // ランクの列に出す文 (JoinRank)
        public string RankText { get; }
        // ランクの列の並べ替え (まとめた中でいちばん低いランク。全部不明なら 99)
        public int RankSortValue { get; }
        public string DisplayReward { get; }
        public string Location { get; }
        // 報酬の列の並べ替え (まとめた中の最小の値)
        public double RewardSortValue { get; }
        // まとめた契約のどれか 1 件でも成功時に評判が上がれば薄い緑
        public bool GivesReputation { get; }
    }

    public class ReputationRequirement
    {
        public string Faction { get; set; } = "";
        public string Scope { get; set; } = "";
        public string Comparison { get; set; } = "";
        public string Standing { get; set; } = "";
        // CG 行の受注条件 (ContractPrerequisite_Reputation): Standing〜MaxStanding の範囲。Exclude なら、その範囲にいると受けられない
        public string MaxStanding { get; set; } = "";
        public bool Exclude { get; set; }
        public bool IsRange { get; set; }
    }

    public class ReputationRewardInfo
    {
        public string Faction { get; set; } = "";
        public string Scope { get; set; } = "";
        public string Amount { get; set; } = "";
        // 量のレコードが無く、ゲームが契約の内容から計算する (ContractResult_CalculatedReputation)
        public bool Calculated { get; set; }
    }

    public class SpawnGroupInfo
    {
        public string GroupName { get; set; } = "";
        public int ConcurrentAmount { get; set; }
        public double Weight { get; set; }
    }
}

// ミッションタブの表示用に、ゲームの書式タグを読める形にする (DB の値・ini・チャット検索の出力には使わない)。
//  - <EM数字> / <EM> とその閉じタグ (</EM4> のほか、翻訳に混じる </Em4> </EMCA> </EMdo> のような崩れた閉じタグも閉じとみなす) はタグだけ消す。
//    <EM4> の中はゲームのミッション画面で強調表示される部分なので、Parse では強調 (emphasis = true) として返す。
//    閉じタグが無いまま行が終わったら、その行の終わりで強調を終える。
//  - ~mission(A|B) / ~mission(A) は、何が入るかを〔〕で示す (MissionDisplayText.ReplaceMissionTokens)。
public static class MissionDisplayText
{
    private static readonly System.Text.RegularExpressions.Regex TagRegex =
        new(@"<(/?)EM([A-Za-z0-9]*)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex MissionTokenRegex =
        new(@"~mission\(([^)]*)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // A (先頭部分。末尾の数字は外して引き、訳の後ろに付け直す。大文字小文字は区別しない) の訳。
    // Location / Ship / Contractor / Destination / Item は利用者の指定。ほかは翻訳データの英語・日本語訳で使われ方を確認したもの
    private static readonly Dictionary<string, string> PrimaryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Location"] = "場所",
        ["Ship"] = "船",
        ["Contractor"] = "依頼主",
        ["Destination"] = "目的地",
        ["Item"] = "品目",
        ["TargetName"] = "対象者名",      // "NAME: ~mission(TargetName)" (公式訳 "氏名:")、行方不明者の名前にも使われる
        ["System"] = "星系",              // "SYSTEM: ~mission(System)" (公式訳 "システム:")
        ["ApprovalCode"] = "承認コード",  // "APPROVAL CODE = ~mission(ApprovalCode)" (公式訳 "承認コード =")
        ["Pickup"] = "集荷場所",          // "Package #… from ~mission(Pickup1|Address)" (公式訳 "…より")
        ["Dropoff"] = "配達先",           // "Package #… to ~mission(Dropoff1|Address)" (公式訳 "… へ")
        ["Creature"] = "生物",            // "an uptick in the ~mission(Creature) population"
        ["ScripAmount"] = "スクリップ額", // "a bonus of ~mission(ScripAmount) MG Scrip"
        ["Title"] = "タイトル",           // タイトルの文字列が "~mission(Title)" だけのもの
    };

    // B 以降の訳。Address は場所の書き方の指定なので表示しない (null)。ここに無いものが 1 つでもあれば全体を英語のまま〔〕に入れる
    private static readonly Dictionary<string, string?> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Address"] = null,
        ["SignOff"] = "署名",
        ["SerialNumber"] = "シリアル番号",
        ["First"] = "名",                 // "locate ~mission(TargetName|First)"
        ["Last"] = "姓",                  // "the status of ~mission(TargetName|Last)"
    };

    // ~mission(…) を〔〕の表示に置き換える
    public static string ReplaceMissionTokens(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("~mission(", StringComparison.OrdinalIgnoreCase) < 0) return text;
        return MissionTokenRegex.Replace(text, m => "〔" + DescribeToken(m.Groups[1].Value) + "〕");
    }

    // "Location|Address" → "場所"、"Contractor|SignOff" → "依頼主の署名"、"Item1|SerialNumber" → "品目1のシリアル番号"。
    // 分からない部分があれば、Address を外した元の英語のまま ("Contractor|Timed" → "Contractor|Timed"、"Danger" → "Danger")
    public static string DescribeToken(string inner)
    {
        var parts = inner.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (parts.Count == 0) return inner;
        var kept = new List<string> { parts[0] };
        var modifiers = new List<string>();
        bool known = true;
        foreach (var part in parts.Skip(1))
        {
            if (ModifierNames.TryGetValue(part, out var ja))
            {
                if (ja == null) continue;
                modifiers.Add(ja);
            }
            else known = false;
            kept.Add(part);
        }

        var head = parts[0];
        var digits = new string(head.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        var baseName = head[..(head.Length - digits.Length)];
        if (!known || !PrimaryNames.TryGetValue(baseName, out var primary))
            return string.Join("|", kept);
        // 対象者名の名・姓は "対象者の名" / "対象者の姓" と書く
        if (baseName.Equals("TargetName", StringComparison.OrdinalIgnoreCase) && modifiers.Count == 1 && (modifiers[0] == "名" || modifiers[0] == "姓"))
            return "対象者" + digits + "の" + modifiers[0];
        var result = primary + digits;
        foreach (var mod in modifiers) result += "の" + mod;
        return result;
    }

    // 一覧の名前用: 強調タグを消し、~mission(…) を〔〕にした文字列
    public static string ToPlain(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return string.Concat(Parse(text).Select(x => x.text));
    }

    // 詳細表示用: (文字列, 強調するか) の並びにする。強調は <EM4> の中だけ (ほかの EM の色は未確認なのでタグを消すだけ)
    public static List<(string text, bool emphasis)> Parse(string text)
    {
        var result = new List<(string text, bool emphasis)>();
        if (string.IsNullOrEmpty(text)) return result;

        bool emphasis = false;
        int pos = 0;
        void Emit(string chunk)
        {
            if (chunk.Length == 0) return;
            // 閉じタグが無いまま改行に来たら、そこで強調を終える
            while (emphasis && chunk.IndexOf('\n') is var nl && nl >= 0)
            {
                Add(chunk[..nl], true);
                emphasis = false;
                chunk = chunk[nl..];
            }
            Add(chunk, emphasis);
        }
        void Add(string chunk, bool emph)
        {
            chunk = ReplaceMissionTokens(chunk);
            if (chunk.Length == 0) return;
            if (result.Count > 0 && result[^1].emphasis == emph)
                result[^1] = (result[^1].text + chunk, emph);
            else
                result.Add((chunk, emph));
        }

        foreach (System.Text.RegularExpressions.Match m in TagRegex.Matches(text))
        {
            var isClose = m.Groups[1].Value == "/";
            var suffix = m.Groups[2].Value;
            // 開きタグは <EM> と <EM数字> だけ。閉じタグは </EM…> をすべて閉じとみなす
            if (!isClose && suffix.Length > 0 && !suffix.All(char.IsDigit)) continue;
            Emit(text[pos..m.Index]);
            pos = m.Index + m.Length;
            emphasis = !isClose && suffix == "4";
        }
        Emit(text[pos..]);
        return result;
    }
}
