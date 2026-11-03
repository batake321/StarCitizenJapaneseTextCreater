namespace StarCitizenJapaneseTextCreater;

// 過去に配った同梱の部品の表 (station_mesh_kinds.json) のハッシュ (StationMeshCache.BundledKindsHash と同じ: ファイルのバイトの SHA-256 の 16 進大文字) と、
// その表の確定の none (読まない部品) の名前 (2026-11-02、親の指示)。版 1 の保存 (none の一覧が無い) の KindsHash がここにあれば、その表の none を
// 「保存したときの none」とみなし、版 2 と同じく「保存したときの none ⊆ 今の none」なら使う (StationMeshCache.KindsUsable)。
// 中身は git の履歴の表 (git show <commit>:station_mesh_kinds.json) から作った。改行が LF のときと CRLF のときのハッシュを両方入れる
// (利用者の v2.5.2 の保存の KindsHash は LF のときのハッシュ A5102BEB… だった)。ここに無いハッシュの版 1 の保存は使わない
internal static class StationMeshKindsKnown
{
    public static readonly IReadOnlyDictionary<string, string[]> NoneByHash = Build();

    private static Dictionary<string, string[]> Build()
    {
        var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        // 670ac8a (v2.5.x 途中): none 151 件
        var n670ac8a = new[]
        {
            "cargo_rack_1_a", "cargo_rack_cap_bottom_1_a", "cargo_rack_cap_middle_1_a", "cargo_rack_cap_top_1_a",
            "cargo_rack_end_1_a", "cargo_rack_floor_1_a", "cargo_rack_slot_1_a", "crate_016x008x008_a",
            "crate_01x005x005_a", "crate_01x005x005_a_open", "crate_01x005x005_b", "crate_armor_1_005x005x005",
            "crate_armor_clothing_1_005x005x005", "crate_armor_core_1_005x005x005", "crate_armor_helmet_1_005x005x005", "crate_armor_legs_1_005x005x005",
            "crate_armour_0075x015x0075_1_a", "crate_armour_1_tarp_a", "crate_basket_1_a", "crate_basket_1_b",
            "crate_basket_1_c", "crate_dry_1_00625x00625x00625_a", "crate_dry_1_0125x00625x00625_a", "crate_dry_1_0125x0125x00625_a",
            "crate_dry_1_0125x0125x0125_a", "crate_dry_1_0125x0125x0125_b", "crate_dry_1_0125x0125x0125_c", "crate_dry_1_0125x0125x0125_stack_a",
            "crate_dry_1_0125x0125x0125_stack_b", "crate_dry_1_0125x0125x0125_stack_wind_a", "crate_dry_1_0125x0125x0125_stack_wind_b", "crate_dry_1_015x0075x0075_a",
            "crate_dry_1_01x005x005_a", "crate_dry_1_cloth_a", "crate_dry_1_stack_01", "crate_dry_1_stack_02",
            "crate_dry_1_stack_03", "crate_dry_2_0125x0125x0125_a", "crate_dry_2_0125x0125x0125_b", "crate_dry_8_a",
            "crate_liquid_1_a", "crate_metal_1_0125x0635x0625_a", "crate_metal_1_0250x0125x0625_a", "crate_metal_1_0325x0125x0125_a",
            "crate_metal_1_open_0125x0635x0625_a", "crate_metal_1_open_0250x0125x0625_a", "crate_metal_2_01x005x0025_a", "crate_metal_2_0625x0625x0625_a",
            "crate_metal_2_open_01x005x0025_a", "crate_metal_3_015x0075x0075_a", "crate_metal_3_01x005x0037_a", "crate_metal_3_open_015x0075x0075_a",
            "crate_metal_4_010x010x0075_stackable_a", "crate_metal_4_0125x0125x0125_a", "crate_metal_4_0125x0125x0125_a_yellow", "crate_metal_loot_1_small_a",
            "crate_metal_loot_1_small_b", "crate_mining_1_01235x0125x0125_a", "crate_multipurpose_1_0125x0125x0125_a", "crate_multipurpose_1_0125x025x0125_a",
            "crate_multipurpose_1_025x05x025_a", "crate_pirate_1_0125x0125x0125_a", "crate_pirate_1_closed_a", "crate_pirate_cluster_01",
            "crate_pirate_cluster_02", "crate_pirate_cluster_03", "crate_plastic_1_005x01x005_a", "crate_slaver_1_0075x015x0075_a",
            "crate_slaver_1_open_0075x015x0075_a", "crate_small_b", "crate_stack_1_0125x0125x0125_a", "crate_stack_1_015x015x0075_a",
            "crate_stack_1_03x025x0125_a", "crate_torpedo_1_03x0075x0075_a", "crate_torpedo_1_03x0075x0075_lid_a", "crate_torpedo_1_03x0075x0075_open_a",
            "crate_weapon_1_005x0025x0025_a", "crate_weapon_1_005x0025x0025_b", "crate_weapon_1_015x0075x0075_a", "crate_weapon_1_015x0075x0075_b",
            "crate_weapon_1_01x005x0025_a", "crate_weapon_1_01x005x0025_b", "crate_weapon_1_01x005x00375_a", "crate_weapon_1_01x005x00375_b",
            "crate_weapon_1_01x005x005_a", "crate_weapon_2_015x0075x0075_a", "crate_weapon_3_01x005x0025_a", "crate_weapon_3_01x005x00375_a",
            "crate_weapon_3_075x045x00375_a", "crate_weapon_3_075x045x00375_b", "crate_weapon_3_075x045x00375_c", "crate_weapon_3_open_01x005x0025_a",
            "crate_weapon_3_open_01x005x00375_a", "crate_weapon_4_015x0075x0075_a", "crate_weapon_4_open_015x0075x0075_a", "improvised_box_plastic_1_a",
            "shipping_container", "shipping_container_1_025x025x025_a", "shipping_container_1_025x025x025_b", "shipping_container_1_025x025x025_c",
            "shipping_container_1_025x05x025_a", "shipping_container_1_025x05x025_b", "shipping_container_1_025x05x025_c", "shipping_container_1_025x05x025_open_a",
            "shipping_container_blue", "shipping_container_cluster_01", "shipping_container_cluster_02", "shipping_container_cluster_03",
            "shipping_container_cluster_04", "shipping_container_grey", "shipping_container_red", "shipping_container_silver",
            "shipping_container_special_cluster_01", "shipping_container_special_cluster_02", "shipping_container_special_cluster_03", "shipping_container_special_cluster_04",
            "shipping_container_special_cluster_05", "shipping_container_special_cluster_11", "shipping_container_yellow", "storage_container_1_025x05x025_a",
            "storage_container_1_025x075x025_a", "storage_container_1_025x075x025_d", "storage_container_1_025x10x025_a", "storage_container_stack_1_a",
            "storage_container_stack_1_b", "storage_container_stack_2_a", "storage_container_stack_2_b", "storage_container_stack_3_a",
            "storage_container_stack_3_b", "storage_container_stack_4_a", "storage_container_stack_4_b", "storage_container_stack_5_a",
            "storage_container_stack_5_b", "storage_rack_1_02x0575x03_a", "storage_rack_1_02x0575x05_a", "storage_rack_1_02x0575x06_a",
            "storage_rack_1_02x0575x08_a", "storage_rack_1_addon_a", "storage_rack_1_shelve_a", "storage_rack_2_end_a",
            "storage_rack_2_middle_a", "storage_rack_3_middle_a", "storage_rack_4_end_a", "storage_rack_4_middle_a",
            "storage_rack_5_end_a", "storage_rack_5_middle_a", "storage_rack_shelf_01_a", "storage_rack_shelf_02_a",
            "storage_rack_shelf_02_b", "storage_rack_shelf_03_a", "storage_rack_shelf_03_b",
        };
        d["738F7B3675A818D4488A759694C27D5286A428E167980433773C315AEEE3B9E6"] = n670ac8a;   // 改行 LF
        d["8EF7C4F0948C41A7BBD09C2293BC1614162D7CBE87637F608E6E9B2BFF5F63B9"] = n670ac8a;   // 改行 CRLF
        // 64dcc76 (v2.5.1。efc0cd9・28394ec・e292a4f (v2.5.2) も同じ中身): none 242 件
        var n64dcc76 = new[]
        {
            "cargo_rack_1_a", "cargo_rack_cap_bottom_1_a", "cargo_rack_cap_middle_1_a", "cargo_rack_cap_top_1_a",
            "cargo_rack_end_1_a", "cargo_rack_floor_1_a", "cargo_rack_slot_1_a", "core_b_med_a_mid_256m_a",
            "core_b_med_a_mid_96m_a", "core_b_med_a_mid_cap_64m_a", "core_b_med_a_mid_cap_sckt_64m_a", "core_b_med_a_mid_cap_sckt_64m_b",
            "core_b_med_a_side_128m_a", "core_b_med_a_side_256m_a", "core_b_med_a_side_256m_b", "core_b_med_a_side_256m_b_cap",
            "core_b_med_a_side_256m_c", "core_b_med_a_side_256m_d", "core_b_med_a_side_256m_e", "core_b_med_a_side_256m_hangar_side_a",
            "core_b_med_a_side_256m_hangar_top_a", "core_b_med_a_side_addn_pad_a", "core_b_med_a_side_cap_64m_a", "core_b_med_a_side_cap_64m_a_mirror",
            "core_b_med_a_side_cap_64m_c", "core_b_med_a_side_cap_64m_c_mirror", "core_b_med_a_side_sckt_a", "core_b_med_a_top_256m_a",
            "core_b_med_a_top_cap_64m_a", "core_b_med_a_top_cap_64m_a_mirror", "core_b_med_b_side_addn_pad_a", "core_b_sml_a_side_a",
            "core_b_sml_a_top_a", "core_b_sml_a_top_a_rund_a", "core_b_sml_a_top_name_display", "core_b_sml_a_top_name_display_b",
            "core_b_sml_wall_a_hngr_a", "core_b_sml_wall_a_hngr_a_cap_a", "core_b_sml_wall_c_36_a", "core_b_sml_wall_c_cap_a",
            "core_b_sml_wall_c_cap_addon_a", "core_b_sml_wall_c_pad_bot_a", "core_b_sml_wall_c_pad_bot_b_addn_a", "core_c_ring_lrg_a_45_a",
            "core_c_ring_lrg_a_45_a_innr_a", "core_c_ring_lrg_a_45_a_innr_b", "core_c_ring_lrg_a_45_a_innr_hngr_sml_a", "core_c_ring_lrg_a_45_a_innr_hngr_sml_a_addn_a",
            "core_c_ring_lrg_a_45_a_innr_hngr_sml_a_cnpy_a", "core_c_ring_lrg_a_45_a_lwr_a", "core_c_ring_lrg_a_45_a_lwr_a_addn_a", "core_c_ring_lrg_a_45_a_lwr_a_addn_b",
            "core_c_ring_lrg_a_45_a_uppr_ring_a", "core_c_ring_lrg_a_45_b", "core_c_ring_lrg_a_45_c", "core_c_ring_lrg_a_45_c_addn_a",
            "core_c_ring_lrg_a_45_c_addn_b", "core_c_ring_lrg_a_45_innr_addn_a", "core_c_ring_lrg_a_45_innr_addn_a_cap_a", "core_c_ring_lrg_a_45_innr_addn_a_window_a",
            "core_c_ring_lrg_a_45_innr_plate_a", "core_c_ring_lrg_a_45_name_display", "core_c_ring_lrg_a_addn_a", "core_c_ring_lrg_a_addn_btm_a",
            "core_c_ring_lrg_a_addn_outr_a", "core_c_ring_lrg_a_addn_top_a", "core_c_ring_lrg_a_addon_window", "core_c_ring_lrg_a_arm_cnntr_a",
            "core_c_ring_lrg_a_arm_cnntr_b", "core_c_ring_lrg_a_cap_a", "core_c_ring_lrg_a_cap_b", "core_c_ring_lrg_a_cover_a",
            "core_c_ring_lrg_a_hngr_a", "core_c_ring_lrg_a_med_hngr_cap_a", "core_c_ring_lrg_a_med_hngr_frame_a", "core_c_ring_lrg_a_outr_cap_a",
            "crate_016x008x008_a", "crate_01x005x005_a", "crate_01x005x005_a_open", "crate_01x005x005_b",
            "crate_armor_1_005x005x005", "crate_armor_clothing_1_005x005x005", "crate_armor_core_1_005x005x005", "crate_armor_helmet_1_005x005x005",
            "crate_armor_legs_1_005x005x005", "crate_armour_0075x015x0075_1_a", "crate_armour_1_tarp_a", "crate_basket_1_a",
            "crate_basket_1_b", "crate_basket_1_c", "crate_dry_1_00625x00625x00625_a", "crate_dry_1_0125x00625x00625_a",
            "crate_dry_1_0125x0125x00625_a", "crate_dry_1_0125x0125x0125_a", "crate_dry_1_0125x0125x0125_b", "crate_dry_1_0125x0125x0125_c",
            "crate_dry_1_0125x0125x0125_stack_a", "crate_dry_1_0125x0125x0125_stack_b", "crate_dry_1_0125x0125x0125_stack_wind_a", "crate_dry_1_0125x0125x0125_stack_wind_b",
            "crate_dry_1_015x0075x0075_a", "crate_dry_1_01x005x005_a", "crate_dry_1_cloth_a", "crate_dry_1_stack_01",
            "crate_dry_1_stack_02", "crate_dry_1_stack_03", "crate_dry_2_0125x0125x0125_a", "crate_dry_2_0125x0125x0125_b",
            "crate_dry_8_a", "crate_liquid_1_a", "crate_metal_1_0125x0635x0625_a", "crate_metal_1_0250x0125x0625_a",
            "crate_metal_1_0325x0125x0125_a", "crate_metal_1_open_0125x0635x0625_a", "crate_metal_1_open_0250x0125x0625_a", "crate_metal_2_01x005x0025_a",
            "crate_metal_2_0625x0625x0625_a", "crate_metal_2_open_01x005x0025_a", "crate_metal_3_015x0075x0075_a", "crate_metal_3_01x005x0037_a",
            "crate_metal_3_open_015x0075x0075_a", "crate_metal_4_010x010x0075_stackable_a", "crate_metal_4_0125x0125x0125_a", "crate_metal_4_0125x0125x0125_a_yellow",
            "crate_metal_loot_1_small_a", "crate_metal_loot_1_small_b", "crate_mining_1_01235x0125x0125_a", "crate_multipurpose_1_0125x0125x0125_a",
            "crate_multipurpose_1_0125x025x0125_a", "crate_multipurpose_1_025x05x025_a", "crate_pirate_1_0125x0125x0125_a", "crate_pirate_1_closed_a",
            "crate_pirate_cluster_01", "crate_pirate_cluster_02", "crate_pirate_cluster_03", "crate_plastic_1_005x01x005_a",
            "crate_slaver_1_0075x015x0075_a", "crate_slaver_1_open_0075x015x0075_a", "crate_small_b", "crate_stack_1_0125x0125x0125_a",
            "crate_stack_1_015x015x0075_a", "crate_stack_1_03x025x0125_a", "crate_torpedo_1_03x0075x0075_a", "crate_torpedo_1_03x0075x0075_lid_a",
            "crate_torpedo_1_03x0075x0075_open_a", "crate_weapon_1_005x0025x0025_a", "crate_weapon_1_005x0025x0025_b", "crate_weapon_1_015x0075x0075_a",
            "crate_weapon_1_015x0075x0075_b", "crate_weapon_1_01x005x0025_a", "crate_weapon_1_01x005x0025_b", "crate_weapon_1_01x005x00375_a",
            "crate_weapon_1_01x005x00375_b", "crate_weapon_1_01x005x005_a", "crate_weapon_2_015x0075x0075_a", "crate_weapon_3_01x005x0025_a",
            "crate_weapon_3_01x005x00375_a", "crate_weapon_3_075x045x00375_a", "crate_weapon_3_075x045x00375_b", "crate_weapon_3_075x045x00375_c",
            "crate_weapon_3_open_01x005x0025_a", "crate_weapon_3_open_01x005x00375_a", "crate_weapon_4_015x0075x0075_a", "crate_weapon_4_open_015x0075x0075_a",
            "improvised_box_plastic_1_a", "light_beam_city_glow_a", "sand_pile_single_centre_16m_a", "sand_pile_single_centre_1x002_a",
            "sand_pile_single_centre_23m_a", "sand_pile_single_centre_2x003_a", "sand_pile_single_centre_7x10m_a", "sand_pile_single_centre_8m_a",
            "sand_pile_single_centre_8m_b", "sand_pile_single_cornerin_4x4m_a", "sand_pile_single_edge_2m_a", "sand_pile_single_edge_4m_a",
            "sand_pile_single_edge_4m_b", "sand_pile_single_edge_8m_a", "sand_pile_single_round_flat_fs", "sand_pile_single_round_shallow_fs",
            "sand_pile_single_straight_fs", "sand_piles_02", "sand_piles_03", "shipping_container",
            "shipping_container_1_025x025x025_a", "shipping_container_1_025x025x025_b", "shipping_container_1_025x025x025_c", "shipping_container_1_025x05x025_a",
            "shipping_container_1_025x05x025_b", "shipping_container_1_025x05x025_c", "shipping_container_1_025x05x025_open_a", "shipping_container_blue",
            "shipping_container_cluster_01", "shipping_container_cluster_02", "shipping_container_cluster_03", "shipping_container_cluster_04",
            "shipping_container_grey", "shipping_container_red", "shipping_container_silver", "shipping_container_special_cluster_01",
            "shipping_container_special_cluster_02", "shipping_container_special_cluster_03", "shipping_container_special_cluster_04", "shipping_container_special_cluster_05",
            "shipping_container_special_cluster_11", "shipping_container_yellow", "storage_container_1_025x05x025_a", "storage_container_1_025x075x025_a",
            "storage_container_1_025x075x025_d", "storage_container_1_025x10x025_a", "storage_container_stack_1_a", "storage_container_stack_1_b",
            "storage_container_stack_2_a", "storage_container_stack_2_b", "storage_container_stack_3_a", "storage_container_stack_3_b",
            "storage_container_stack_4_a", "storage_container_stack_4_b", "storage_container_stack_5_a", "storage_container_stack_5_b",
            "storage_rack_1_02x0575x03_a", "storage_rack_1_02x0575x05_a", "storage_rack_1_02x0575x06_a", "storage_rack_1_02x0575x08_a",
            "storage_rack_1_addon_a", "storage_rack_1_shelve_a", "storage_rack_2_end_a", "storage_rack_2_middle_a",
            "storage_rack_3_middle_a", "storage_rack_4_end_a", "storage_rack_4_middle_a", "storage_rack_5_end_a",
            "storage_rack_5_middle_a", "storage_rack_shelf_01_a", "storage_rack_shelf_02_a", "storage_rack_shelf_02_b",
            "storage_rack_shelf_03_a", "storage_rack_shelf_03_b", "util_stairs_a_start_capplate_a", "util_stairs_a_start_capplate_c",
            "util_stairs_start_capplate_a", "util_stairs_structure_4m_b",
        };
        d["A5102BEBBE5EF1812A575B178022DCA643BA89D5BDDCD30AC4C910437FCDCB7C"] = n64dcc76;   // 改行 LF
        d["E338A4A5AB21F466706758A4ADE4E8C62DF158E408685D1B1002BCE010BF0B56"] = n64dcc76;   // 改行 CRLF
        return d;
    }
}
