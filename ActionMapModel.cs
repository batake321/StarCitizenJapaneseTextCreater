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
        ["vehicle_movement"] = "地上車両 - 移動",
        ["vehicle_general"] = "地上車両 - 全般",
        ["player"] = "歩行 - 全般",
        ["player_movement"] = "歩行 - 移動",
        ["player_input_fps"] = "歩行 - FPS",
        ["prone"] = "伏せ",
        ["zero_gravity_eva"] = "EVA (無重力)",
        ["social"] = "ソーシャル",
        ["inventory"] = "インベントリ",
        ["default"] = "デフォルト",
        ["view"] = "視点",
        ["ui"] = "UI",
        ["visor"] = "バイザー",
        ["lights"] = "ライト",
        ["emote"] = "エモート",
        ["commlink"] = "通信リンク",
    };

    private static readonly Dictionary<string, string> ActionNames = new()
    {
        ["v_roll"] = "ロール",
        ["v_pitch"] = "ピッチ",
        ["v_yaw"] = "ヨー",
        ["v_strafe_up"] = "上昇",
        ["v_strafe_down"] = "下降",
        ["v_strafe_left"] = "左移動",
        ["v_strafe_right"] = "右移動",
        ["v_strafe_forward"] = "前進",
        ["v_strafe_back"] = "後退",
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
        ["v_toggle_quantum_mode"] = "クォンタムモード切替",
        ["v_activate_quantum_drive"] = "クォンタムドライブ起動",
        ["v_attack1"] = "射撃",
        ["v_attack1_group2"] = "射撃 (グループ2)",
        ["v_weapon_cycle_aimmode"] = "照準モード切替",
        ["v_target_cycle_hostile_fwd"] = "敵ターゲット (次)",
        ["v_target_cycle_hostile_back"] = "敵ターゲット (前)",
        ["v_target_cycle_friendly_fwd"] = "味方ターゲット (次)",
        ["v_target_cycle_all_fwd"] = "全ターゲット (次)",
        ["v_target_nearest_hostile"] = "最寄り敵ターゲット",
        ["v_target_lock_selected"] = "ターゲットロック",
        ["v_launch_missile"] = "ミサイル発射",
        ["v_weapon_launch_countermeasure"] = "カウンターメジャー",
        ["v_shield_raise_level_forward"] = "シールド (前面強化)",
        ["v_shield_raise_level_back"] = "シールド (後方強化)",
        ["v_shield_reset_level"] = "シールドリセット",
        ["v_power_toggle"] = "電源切替",
        ["v_power_throttle_up"] = "パワー出力+",
        ["v_power_throttle_down"] = "パワー出力-",
        ["attack1"] = "攻撃",
        ["zoom"] = "ズーム / ADS",
        ["reload"] = "リロード",
        ["sprint"] = "スプリント",
        ["crouch"] = "しゃがみ",
        ["prone"] = "伏せ",
        ["jump"] = "ジャンプ",
        ["interact"] = "インタラクト",
        ["weapon_melee"] = "近接攻撃",
        ["grenade"] = "グレネード",
        ["holster"] = "武器収納",
        ["weapon_cycle"] = "武器切替",
        ["headlook_toggle"] = "フリールック切替",
        ["scoreboard"] = "スコアボード",
        ["mobiglas"] = "mobiGlas",
        ["chat"] = "チャット",
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
