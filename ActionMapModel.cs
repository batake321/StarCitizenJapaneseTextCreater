using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace StarCitizenJapaneseTextCreater;

public class ActionMapData
{
    public List<ActionCategory> Categories { get; set; } = new();
    public Dictionary<string, ActionBinding> AllBindings { get; set; } = new();
}

public class ActionCategory
{
    public string Name { get; set; } = "";
    public string DisplayName => ActionMapNames.GetCategoryName(Name);
    public List<ActionBinding> Actions { get; set; } = new();
}

public class ActionBinding : INotifyPropertyChanged
{
    public string CategoryName { get; set; } = "";
    public string ActionName { get; set; } = "";
    public string DisplayName => ActionMapNames.GetActionName(ActionName);

    private string _keyboard = "";
    private string _mouse = "";
    private string _gamepad = "";
    private string _joystick = "";
    private bool _isLongPress;

    public string Keyboard { get => _keyboard; set { _keyboard = value; OnPropertyChanged(); OnPropertyChanged(nameof(KeyboardDisplay)); } }
    public string Mouse { get => _mouse; set { _mouse = value; OnPropertyChanged(); OnPropertyChanged(nameof(MouseDisplay)); } }
    public string Gamepad { get => _gamepad; set { _gamepad = value; OnPropertyChanged(); OnPropertyChanged(nameof(GamepadDisplay)); } }
    public string Joystick { get => _joystick; set { _joystick = value; OnPropertyChanged(); OnPropertyChanged(nameof(JoystickDisplay)); } }
    public bool IsLongPress { get => _isLongPress; set { _isLongPress = value; OnPropertyChanged(); } }

    public string KeyboardDisplay => InputDisplayHelper.FormatInput(Keyboard);
    public string MouseDisplay => InputDisplayHelper.FormatInput(Mouse);
    public string GamepadDisplay => InputDisplayHelper.FormatInput(Gamepad);
    public string JoystickDisplay => InputDisplayHelper.FormatInput(Joystick);

    public bool HasAnyBinding => !string.IsNullOrEmpty(Keyboard) || !string.IsNullOrEmpty(Mouse) ||
                                  !string.IsNullOrEmpty(Gamepad) || !string.IsNullOrEmpty(Joystick);

    // Default bindings (from defaultProfile.xml) - stored separately for reset
    public string DefaultKeyboard { get; set; } = "";
    public string DefaultMouse { get; set; } = "";
    public string DefaultGamepad { get; set; } = "";
    public string DefaultJoystick { get; set; } = "";

    public bool IsModified => Keyboard != DefaultKeyboard || Mouse != DefaultMouse ||
                              Gamepad != DefaultGamepad || Joystick != DefaultJoystick;
    public bool IsKeyboardModified => Keyboard != DefaultKeyboard;
    public bool IsMouseModified => Mouse != DefaultMouse;
    public bool IsGamepadModified => Gamepad != DefaultGamepad;
    public bool IsJoystickModified => Joystick != DefaultJoystick;
    public string CategoryDisplayName => ActionMapNames.GetCategoryName(CategoryName);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class ActionMapParser
{
    public static ActionMapData LoadFromGame(string gamePath)
    {
        var data = new ActionMapData();

        var defaultProfilePath = Path.Combine(gamePath, "data", "Libs", "Config", "defaultProfile.xml");
        var userOverridePath = Path.Combine(gamePath, "user", "client", "0", "Profiles", "default", "actionmaps.xml");

        // Extract from Data.p4k if not on disk
        if (!File.Exists(defaultProfilePath))
        {
            var p4kPath = Path.Combine(gamePath, "Data.p4k");
            if (File.Exists(p4kPath))
            {
                try
                {
                    Console.WriteLine("defaultProfile.xml を Data.p4k から抽出中...");
                    var extractDir = Path.Combine(Path.GetTempPath(), "SCJPKeybind");
                    defaultProfilePath = Path.Combine(extractDir, "defaultProfile.xml");
                    P4kExtractor.Extract(p4kPath, "Data/Libs/Config/defaultProfile.xml", defaultProfilePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Data.p4k extraction error: {ex.Message}");
                }
            }
        }

        // Load defaults
        if (File.Exists(defaultProfilePath))
        {
            try
            {
                var doc = CryXmlParser.Parse(defaultProfilePath);
                ParseDefaultProfile(doc, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"defaultProfile.xml parse error: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("defaultProfile.xml が見つかりません。");
        }

        // Apply user overrides
        if (File.Exists(userOverridePath))
        {
            try
            {
                var doc = XDocument.Load(userOverridePath);
                ApplyUserOverrides(doc, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"actionmaps.xml parse error: {ex.Message}");
            }
        }

        return data;
    }

    public static ActionMapData LoadFromGameAndSave(string gamePath, string savePath)
    {
        var data = new ActionMapData();
        if (!string.IsNullOrEmpty(gamePath))
        {
            var defaultProfilePath = Path.Combine(gamePath, "data", "Libs", "Config", "defaultProfile.xml");

            if (!File.Exists(defaultProfilePath))
            {
                var p4kPath = Path.Combine(gamePath, "Data.p4k");
                if (File.Exists(p4kPath))
                {
                    try
                    {
                        var extractDir = Path.Combine(Path.GetTempPath(), "SCJPKeybind");
                        defaultProfilePath = Path.Combine(extractDir, "defaultProfile.xml");
                        P4kExtractor.Extract(p4kPath, "Data/Libs/Config/defaultProfile.xml", defaultProfilePath);
                    }
                    catch { }
                }
            }

            if (File.Exists(defaultProfilePath))
            {
                try
                {
                    var doc = CryXmlParser.Parse(defaultProfilePath);
                    ParseDefaultProfile(doc, data);
                }
                catch { }
            }
        }

        // Apply overrides from saved profile
        var saveOverridePath = Path.Combine(savePath, "Profiles", "default", "actionmaps.xml");
        if (File.Exists(saveOverridePath))
        {
            try
            {
                var doc = XDocument.Load(saveOverridePath);
                ApplyUserOverrides(doc, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"saved actionmaps.xml parse error: {ex.Message}");
            }
        }

        return data;
    }

    public static ActionMapData LoadFromFile(string filePath)
    {
        var data = new ActionMapData();
        try
        {
            XDocument doc;
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length >= 7 && System.Text.Encoding.ASCII.GetString(bytes, 0, 7) == "CryXmlB")
                doc = CryXmlParser.Parse(filePath);
            else
                doc = XDocument.Load(filePath);

            var root = doc.Root;
            if (root?.Name.LocalName == "ActionMaps")
                ApplyUserOverrides(doc, data);
            else
                ParseDefaultProfile(doc, data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Load error: {ex.Message}");
        }
        return data;
    }

    private static void ParseDefaultProfile(XDocument doc, ActionMapData data)
    {
        var root = doc.Root;
        if (root == null) return;

        var profile = root.Name.LocalName == "profile" ? root :
                      root.Element("profile") ?? root.Descendants().FirstOrDefault(e => e.Name.LocalName == "profile");
        if (profile == null) profile = root;

        var actionmaps = profile.Elements("actionmap");
        if (!actionmaps.Any())
            actionmaps = root.Descendants().Where(e => e.Name.LocalName == "actionmap");

        foreach (var am in actionmaps)
        {
            var catName = am.Attribute("name")?.Value ?? "";
            if (string.IsNullOrEmpty(catName)) continue;

            var category = data.Categories.FirstOrDefault(c => c.Name == catName);
            if (category == null)
            {
                category = new ActionCategory { Name = catName };
                data.Categories.Add(category);
            }

            foreach (var action in am.Elements("action"))
            {
                var actionName = action.Attribute("name")?.Value ?? "";
                if (string.IsNullOrEmpty(actionName)) continue;

                var binding = new ActionBinding
                {
                    CategoryName = catName,
                    ActionName = actionName,
                };

                // Attribute-based bindings (defaultProfile format)
                var kb = action.Attribute("keyboard")?.Value?.Trim();
                var mo = action.Attribute("mouse")?.Value?.Trim();
                var gp = action.Attribute("gamepad")?.Value?.Trim();
                var js = action.Attribute("joystick")?.Value?.Trim();
                if (!string.IsNullOrEmpty(kb)) { binding.Keyboard = kb; binding.DefaultKeyboard = kb; }
                if (!string.IsNullOrEmpty(mo)) { binding.Mouse = mo; binding.DefaultMouse = mo; }
                if (!string.IsNullOrEmpty(gp)) { binding.Gamepad = gp; binding.DefaultGamepad = gp; }
                if (!string.IsNullOrEmpty(js)) { binding.Joystick = js; binding.DefaultJoystick = js; }

                // Also check rebind child elements
                foreach (var rebind in action.Elements("rebind"))
                {
                    var input = rebind.Attribute("input")?.Value ?? "";
                    if (string.IsNullOrEmpty(input)) continue;

                    ClassifyInput(input, binding, setDefault: true);
                }

                if (!data.AllBindings.ContainsKey($"{catName}.{actionName}"))
                {
                    data.AllBindings[$"{catName}.{actionName}"] = binding;
                    category.Actions.Add(binding);
                }
            }
        }
    }

    private static void ApplyUserOverrides(XDocument doc, ActionMapData data)
    {
        var root = doc.Root;
        if (root == null) return;

        var profiles = root.Element("ActionProfiles") ?? root;
        foreach (var am in profiles.Elements("actionmap"))
        {
            var catName = am.Attribute("name")?.Value ?? "";
            if (string.IsNullOrEmpty(catName)) continue;

            var category = data.Categories.FirstOrDefault(c => c.Name == catName);
            if (category == null)
            {
                category = new ActionCategory { Name = catName };
                data.Categories.Add(category);
            }

            foreach (var action in am.Elements("action"))
            {
                var actionName = action.Attribute("name")?.Value ?? "";
                if (string.IsNullOrEmpty(actionName)) continue;

                var key = $"{catName}.{actionName}";
                if (!data.AllBindings.TryGetValue(key, out var binding))
                {
                    binding = new ActionBinding { CategoryName = catName, ActionName = actionName };
                    data.AllBindings[key] = binding;
                    category.Actions.Add(binding);
                }

                foreach (var rebind in action.Elements("rebind"))
                {
                    var input = rebind.Attribute("input")?.Value ?? "";
                    if (string.IsNullOrEmpty(input)) continue;
                    ClassifyInput(input, binding, setDefault: false);
                }
            }
        }
    }

    private static void ClassifyInput(string input, ActionBinding binding, bool setDefault)
    {
        if (input.StartsWith("kb") || input.StartsWith("key_") ||
            (!input.StartsWith("mo") && !input.StartsWith("gp") && !input.StartsWith("js") &&
             !input.Contains("thumb") && !input.Contains("shoulder") && !input.Contains("trigger")))
        {
            if (input.StartsWith("mo"))
            {
                binding.Mouse = input;
                if (setDefault) binding.DefaultMouse = input;
            }
            else
            {
                binding.Keyboard = input;
                if (setDefault) binding.DefaultKeyboard = input;
            }
        }
        else if (input.StartsWith("mo"))
        {
            binding.Mouse = input;
            if (setDefault) binding.DefaultMouse = input;
        }
        else if (input.StartsWith("gp"))
        {
            binding.Gamepad = input;
            if (setDefault) binding.DefaultGamepad = input;
        }
        else if (input.StartsWith("js"))
        {
            binding.Joystick = input;
            if (setDefault) binding.DefaultJoystick = input;
        }
        else
        {
            binding.Keyboard = input;
            if (setDefault) binding.DefaultKeyboard = input;
        }
    }

    public static void SaveUserOverrides(ActionMapData data, string outputPath)
    {
        var root = new XElement("ActionMaps");
        var profiles = new XElement("ActionProfiles",
            new XAttribute("version", "1"),
            new XAttribute("optionsVersion", "2"),
            new XAttribute("rebindVersion", "2"),
            new XAttribute("profileName", "default"));

        var modifiers = new XElement("modifiers");
        profiles.Add(modifiers);

        foreach (var cat in data.Categories)
        {
            var modifiedActions = cat.Actions.Where(a => a.IsModified).ToList();
            if (modifiedActions.Count == 0) continue;

            var am = new XElement("actionmap", new XAttribute("name", cat.Name));
            foreach (var action in modifiedActions)
            {
                var el = new XElement("action", new XAttribute("name", action.ActionName));
                if (!string.IsNullOrEmpty(action.Keyboard) && action.Keyboard != action.DefaultKeyboard)
                    el.Add(new XElement("rebind", new XAttribute("input", action.Keyboard)));
                if (!string.IsNullOrEmpty(action.Mouse) && action.Mouse != action.DefaultMouse)
                    el.Add(new XElement("rebind", new XAttribute("input", action.Mouse)));
                if (!string.IsNullOrEmpty(action.Gamepad) && action.Gamepad != action.DefaultGamepad)
                    el.Add(new XElement("rebind", new XAttribute("input", action.Gamepad)));
                if (!string.IsNullOrEmpty(action.Joystick) && action.Joystick != action.DefaultJoystick)
                    el.Add(new XElement("rebind", new XAttribute("input", action.Joystick)));
                if (el.HasElements)
                    am.Add(el);
            }
            if (am.HasElements)
                profiles.Add(am);
        }

        root.Add(profiles);
        new XDocument(root).Save(outputPath);
    }
}

public static class InputDisplayHelper
{
    public static string FormatInput(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";

        var parts = input.Split('+');
        var result = new List<string>();
        foreach (var part in parts)
        {
            result.Add(FormatSingleInput(part.Trim()));
        }
        return string.Join(" + ", result);
    }

    private static string FormatSingleInput(string input)
    {
        if (input.StartsWith("kb1_"))
            return FormatKeyboard(input[4..]);

        if (input.StartsWith("mo1_") || input.StartsWith("mo_"))
        {
            var key = input.Contains("_") ? input[(input.IndexOf('_') + 1)..] : input;
            return key switch
            {
                "mouse1" => "Left Click",
                "mouse2" => "Right Click",
                "mouse3" => "Middle Click",
                "mouse4" => "Mouse4",
                "mouse5" => "Mouse5",
                "maxis_x" => "Mouse X",
                "maxis_y" => "Mouse Y",
                "mwheel_up" => "Wheel Up",
                "mwheel_down" => "Wheel Down",
                _ => key
            };
        }

        if (input.StartsWith("gp1_"))
        {
            var key = input[4..];
            return key switch
            {
                "shoulderl" => "LB",
                "shoulderr" => "RB",
                "triggerl_btn" => "LT",
                "triggerr_btn" => "RT",
                "thumbl" => "L Stick Press",
                "thumbr" => "R Stick Press",
                "thumblx" => "L Stick X",
                "thumbly" => "L Stick Y",
                "thumbrx" => "R Stick X",
                "thumbry" => "R Stick Y",
                "dpad_up" => "D-Pad Up",
                "dpad_down" => "D-Pad Down",
                "dpad_left" => "D-Pad Left",
                "dpad_right" => "D-Pad Right",
                "btn_a" or "a" => "A",
                "btn_b" or "b" => "B",
                "btn_x" or "x" => "X",
                "btn_y" or "y" => "Y",
                "start" => "Start",
                "back" => "Back",
                _ => key
            };
        }

        if (input.StartsWith("js"))
            return input;

        return FormatKeyboard(input);
    }

    private static string FormatKeyboard(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "space" => "Space",
            "lshift" => "L-Shift",
            "rshift" => "R-Shift",
            "lctrl" => "L-Ctrl",
            "rctrl" => "R-Ctrl",
            "lalt" => "L-Alt",
            "ralt" => "R-Alt",
            "tab" => "Tab",
            "enter" or "return" => "Enter",
            "escape" or "esc" => "Esc",
            "backspace" => "Backspace",
            "delete" => "Delete",
            "insert" => "Insert",
            "home" => "Home",
            "end" => "End",
            "pgup" or "pageup" => "Page Up",
            "pgdn" or "pagedown" => "Page Down",
            "up" => "Up",
            "down" => "Down",
            "left" => "Left",
            "right" => "Right",
            "np_0" => "Num 0",
            "np_1" => "Num 1",
            "np_2" => "Num 2",
            "np_3" => "Num 3",
            "np_4" => "Num 4",
            "np_5" => "Num 5",
            "np_6" => "Num 6",
            "np_7" => "Num 7",
            "np_8" => "Num 8",
            "np_9" => "Num 9",
            "np_enter" => "Num Enter",
            "np_period" => "Num .",
            "np_add" or "np_plus" => "Num +",
            "np_subtract" or "np_minus" => "Num -",
            "np_multiply" => "Num *",
            "np_divide" => "Num /",
            "numlock" => "Num Lock",
            "capslock" => "Caps Lock",
            "scrolllock" => "Scroll Lock",
            "semicolon" => ";",
            "apostrophe" => "'",
            "comma" => ",",
            "period" => ".",
            "slash" => "/",
            "backslash" => "\\",
            "minus" => "-",
            "equals" => "=",
            "lbracket" => "[",
            "rbracket" => "]",
            "grave" or "tilde" => "`",
            _ => key.Length == 1 ? key.ToUpper() : key
        };
    }
}

public static class ActionMapNames
{
    private static readonly Dictionary<string, string> CategoryNames = new()
    {
        ["spaceship_movement"] = "宇宙船 - 移動",
        ["spaceship_view"] = "宇宙船 - 視点",
        ["spaceship_targeting"] = "宇宙船 - ターゲティング",
        ["spaceship_weapons"] = "宇宙船 - 武器",
        ["spaceship_missiles"] = "宇宙船 - ミサイル",
        ["spaceship_defensive"] = "宇宙船 - 防御",
        ["spaceship_power"] = "宇宙船 - 電力",
        ["spaceship_hud"] = "宇宙船 - HUD",
        ["spaceship_mining"] = "宇宙船 - マイニング",
        ["spaceship_salvage"] = "宇宙船 - サルベージ",
        ["spaceship_general"] = "宇宙船 - 全般",
        ["spaceship_turret"] = "宇宙船 - タレット",
        ["spaceship_radar"] = "宇宙船 - レーダー",
        ["spaceship_auto_weapons"] = "宇宙船 - 自動兵器",
        ["spaceship_docking"] = "宇宙船 - ドッキング",
        ["spaceship_quantum"] = "宇宙船 - クォンタム",
        ["vehicle_movement"] = "地上車両 - 移動",
        ["vehicle_general"] = "地上車両 - 全般",
        ["player"] = "歩行 - 全般",
        ["player_movement"] = "歩行 - 移動",
        ["player_input_fps"] = "歩行 - FPS",
        ["player_choice"] = "プレイヤー選択",
        ["player_emotes"] = "エモート",
        ["player_input_optical_tracking"] = "トラッキング",
        ["prone"] = "伏せ",
        ["zero_gravity_eva"] = "EVA (無重力)",
        ["social"] = "ソーシャル",
        ["inventory"] = "インベントリ",
        ["default"] = "デフォルト",
        ["view"] = "視点",
        ["ui"] = "UI",
        ["visor"] = "バイザー",
        ["lights"] = "ライト",
        ["lights_controller"] = "ライトコントロール",
        ["emote"] = "エモート",
        ["commlink"] = "通信リンク",
        ["seat_general"] = "座席 - 全般",
        ["flycam"] = "フリーカメラ",
        ["hacking"] = "ハッキング",
        ["mining"] = "マイニング (歩行)",
        ["mapui"] = "マップUI",
        ["debug"] = "デバッグ",
        ["character_customizer"] = "キャラクターカスタマイズ",
        ["IFCS_controls"] = "IFCS制御",
        ["incapacitated"] = "行動不能",
        ["server_renderer"] = "サーバーレンダラー",
        ["RemoteRigidEntityController"] = "リモート制御",
    };

    private static readonly Dictionary<string, string> ActionNames = new()
    {
        // Ship movement
        ["v_roll"] = "ロール",
        ["v_pitch"] = "ピッチ",
        ["v_yaw"] = "ヨー",
        ["v_pitch_mouse"] = "ピッチ (マウス)",
        ["v_yaw_mouse"] = "ヨー (マウス)",
        ["v_roll_mouse"] = "ロール (マウス)",
        ["v_pitch_up"] = "ピッチ上",
        ["v_pitch_down"] = "ピッチ下",
        ["v_yaw_left"] = "ヨー左",
        ["v_yaw_right"] = "ヨー右",
        ["v_roll_left"] = "ロール左",
        ["v_roll_right"] = "ロール右",
        ["v_strafe_up"] = "上昇",
        ["v_strafe_down"] = "下降",
        ["v_strafe_left"] = "左移動",
        ["v_strafe_right"] = "右移動",
        ["v_strafe_forward"] = "前進",
        ["v_strafe_back"] = "後退",
        ["v_strafe_vertical"] = "垂直移動",
        ["v_strafe_lateral"] = "横移動",
        ["v_strafe_longitudinal"] = "前後移動",
        ["v_speed_range_up"] = "速度+",
        ["v_speed_range_down"] = "速度-",
        ["v_speed_range_abs"] = "速度 (絶対値)",
        ["v_afterburner"] = "アフターバーナー",
        ["v_brake"] = "ブレーキ",
        ["v_toggle_landing_system"] = "ランディングギア切替",
        ["v_autoland"] = "オートランド",
        ["v_toggle_vtol"] = "VTOL切替",
        ["v_ifcs_toggle_cruise_control"] = "クルーズコントロール切替",
        ["v_ifcs_toggle_esp"] = "ESP切替",
        ["v_ifcs_toggle_gforce_safety"] = "Gフォースセーフティ切替",
        ["v_toggle_relative_mouse_mode"] = "相対マウスモード切替",
        ["v_toggle_yaw_roll_swap"] = "ヨー/ロール入替",
        // Ship quantum
        ["v_toggle_quantum_mode"] = "クォンタムモード切替",
        ["v_activate_quantum_drive"] = "クォンタムドライブ起動",
        // Ship weapons
        ["v_attack1"] = "射撃",
        ["v_attack1_group1"] = "射撃 (グループ1)",
        ["v_attack1_group2"] = "射撃 (グループ2)",
        ["v_attack_all"] = "全武器射撃",
        ["v_weapon_cycle_aimmode"] = "照準モード切替",
        ["v_weapon_toggle_ai"] = "AI武器切替",
        // Ship targeting
        ["v_target_cycle_hostile_fwd"] = "敵ターゲット (次)",
        ["v_target_cycle_hostile_back"] = "敵ターゲット (前)",
        ["v_target_cycle_friendly_fwd"] = "味方ターゲット (次)",
        ["v_target_cycle_friendly_back"] = "味方ターゲット (前)",
        ["v_target_cycle_all_fwd"] = "全ターゲット (次)",
        ["v_target_cycle_all_back"] = "全ターゲット (前)",
        ["v_target_nearest_hostile"] = "最寄り敵ターゲット",
        ["v_target_lock_selected"] = "ターゲットロック",
        ["v_target_unlock_selected"] = "ターゲットロック解除",
        ["v_target_cycle_subitem_fwd"] = "サブアイテム (次)",
        ["v_target_cycle_subitem_back"] = "サブアイテム (前)",
        ["v_target_reticle_focus"] = "レティクルフォーカス",
        // Ship missiles
        ["v_launch_missile"] = "ミサイル発射",
        ["v_lock_missile"] = "ミサイルロック",
        ["v_cycle_missile_fwd"] = "ミサイル (次)",
        ["v_cycle_missile_back"] = "ミサイル (前)",
        // Ship defensive
        ["v_weapon_launch_countermeasure"] = "カウンターメジャー",
        ["v_shield_raise_level_forward"] = "シールド (前面強化)",
        ["v_shield_raise_level_back"] = "シールド (後方強化)",
        ["v_shield_raise_level_left"] = "シールド (左面強化)",
        ["v_shield_raise_level_right"] = "シールド (右面強化)",
        ["v_shield_raise_level_up"] = "シールド (上面強化)",
        ["v_shield_raise_level_down"] = "シールド (下面強化)",
        ["v_shield_reset_level"] = "シールドリセット",
        // Ship power
        ["v_power_toggle"] = "電源切替",
        ["v_power_throttle_up"] = "パワー出力+",
        ["v_power_throttle_down"] = "パワー出力-",
        ["v_power_focus_weapons"] = "パワー→武器",
        ["v_power_focus_shields"] = "パワー→シールド",
        ["v_power_focus_engines"] = "パワー→エンジン",
        ["v_power_reset"] = "パワー配分リセット",
        // Ship HUD
        ["v_toggle_all_doorlocks"] = "ドアロック切替",
        ["v_toggle_all_doors"] = "全ドア切替",
        ["v_open_all_doors"] = "全ドア開",
        ["v_close_all_doors"] = "全ドア閉",
        ["v_lock_all_doors"] = "全ドアロック",
        ["v_unlock_all_doors"] = "全ドアロック解除",
        // Ship general
        ["v_eject"] = "緊急脱出",
        ["v_self_destruct"] = "自爆",
        ["v_emergency_exit"] = "緊急退出",
        ["v_enter"] = "搭乗",
        ["v_exit"] = "降機",
        ["v_horn"] = "ホーン",
        ["v_lights"] = "ライト",
        // Ship mining
        ["v_mining_laser_fire"] = "マイニングレーザー発射",
        ["v_mining_throttle_up"] = "マイニング出力+",
        ["v_mining_throttle_down"] = "マイニング出力-",
        // FPS
        ["attack1"] = "攻撃",
        ["zoom"] = "ズーム / ADS",
        ["reload"] = "リロード",
        ["sprint"] = "スプリント",
        ["walk"] = "歩行",
        ["crouch"] = "しゃがみ",
        ["prone"] = "伏せ",
        ["jump"] = "ジャンプ",
        ["jump_hold"] = "ジャンプ (長押し)",
        ["jump_release"] = "ジャンプ (離す)",
        ["interact"] = "インタラクト",
        ["weapon_melee"] = "近接攻撃",
        ["grenade"] = "グレネード",
        ["holster"] = "武器収納",
        ["weapon_cycle"] = "武器切替",
        ["weapon_stow"] = "武器しまう",
        ["weapon_draw"] = "武器構える",
        ["headlook_toggle"] = "フリールック切替",
        ["moveleft"] = "左移動",
        ["moveright"] = "右移動",
        ["moveforward"] = "前進",
        ["moveback"] = "後退",
        ["rotateyaw"] = "横回転",
        ["rotatepitch"] = "縦回転",
        ["gp_movex"] = "移動X (パッド)",
        ["gp_movey"] = "移動Y (パッド)",
        ["gp_rotateyaw"] = "横回転 (パッド)",
        ["gp_rotatepitch"] = "縦回転 (パッド)",
        ["gp_jump"] = "ジャンプ (パッド)",
        ["gp_crouch"] = "しゃがみ (パッド)",
        ["leanleft"] = "左リーン",
        ["leanright"] = "右リーン",
        ["stabilize"] = "安定化",
        ["inspect"] = "検査",
        // UI/Social
        ["scoreboard"] = "スコアボード",
        ["mobiglas"] = "mobiGlas",
        ["chat"] = "チャット",
        ["toggle_contact"] = "コンタクト切替",
        ["toggle_chat"] = "チャット切替",
        ["starmap"] = "スターマップ",
        ["respawn"] = "リスポーン",
        ["force_respawn"] = "強制リスポーン",
        // Inventory
        ["personal_inventory"] = "パーソナルインベントリ",
    };

    public static string GetCategoryName(string name)
    {
        return CategoryNames.TryGetValue(name, out var display) ? $"{display} ({name})" : name;
    }

    public static string GetActionName(string name)
    {
        return ActionNames.TryGetValue(name, out var display) ? $"{display}" : name;
    }
}
