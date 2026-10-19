using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace StarCitizenJapaneseTextCreater;

public class ActionMapData
{
    public List<ActionCategory> Categories { get; set; } = new();
    public Dictionary<string, ActionBinding> AllBindings { get; set; } = new();
    public Dictionary<string, string> Localization { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // actionmaps.xml の <options type="joystick" instance="N" Product="..."> (読み込んだ保存データのもの)
    public List<JoystickDeviceOption> JoystickOptions { get; set; } = new();

    // HOTAS の左右の表示 (アプリ側だけ。actionmaps.xml は変えない): true = js1 が左手・js2 が右手、false = js1 が右手・js2 が左手。
    //   機器の名前からは決めない。利用者の選択 (エディターが機器の定義の指紋ごとに覚える) で決め、選んでいないときは true。
    //   js1/js2 の入れ替えをすると、物理スティックの左右が変わらないよう反転する
    public bool Js1IsLeft { get; set; } = true;

    // 利用者が左右を選んだか (記録から読んだ、または今回選んだ)。選んでいなければ、エディターを開いたときに聞く
    public bool StickSidesChosen { get; set; }

    // 「js1/js2 の入れ替え」を奇数回行い、まだ保存していない (保存時に options の instance 1 と 2 を交換する)
    public bool JoystickInstancesSwapPending { get; set; }

    // 保存データの actionmaps.xml の機器の定義 (options type="joystick" の instance 1 / 2 ごとの見分け。ActionMapParser.GetJoystickDefinitionParts)。
    //   ファイルに今書いてある向き (まだ保存していない js1/js2 の入れ替えは含まない)。options が無ければ "-"
    public string JoystickDefinition1 { get; set; } = "-";
    public string JoystickDefinition2 { get; set; } = "-";

    // 機器の定義の指紋 (HOTAS の左右の選択・手で選んだ js 番号の記録の鍵)。ファイルに今書いてある向きのもの
    public string FileJoystickFingerprint => ActionMapParser.MakeJoystickFingerprint(JoystickDefinition1, JoystickDefinition2);

    // js1/js2 の入れ替えを actionmaps.xml に保存した後に呼ぶ (ファイルの instance 1 と 2 が入れ替わったので、定義も入れ替える)
    public void SwapJoystickDefinitionsAfterSave() =>
        (JoystickDefinition1, JoystickDefinition2) = (JoystickDefinition2, JoystickDefinition1);

    // js 番号 (1/2) に対応する options (instance が同じ番号のもの)
    public JoystickDeviceOption? GetJoystickOptionForJs(int js) =>
        JoystickOptions.FirstOrDefault(o => o.Instance == js);

    // js 番号の Product 名 (GUID を除いたもの)。無ければ ""
    public string GetJsProductName(int js) => GetJoystickOptionForJs(js)?.Name ?? "";

    // 見出し用 "js1: VKBsim Gladiator EVO L" (Product が無ければ "js1")
    public string GetJsHeading(int js)
    {
        var name = GetJsProductName(js);
        return string.IsNullOrEmpty(name) ? $"js{js}" : $"js{js}: {name}";
    }

    // 左側 (左手) に表示する js 番号 (1 or 2)。利用者の選択 (Js1IsLeft) で決める
    public int LeftJs => Js1IsLeft ? 1 : 2;

    // "L" / "R" (HOTAS L / HOTAS R の表記用)
    public string GetJsSideLabel(int js) => js == LeftJs ? "L" : "R";

    // 物理スティック (VID/PID) → js 番号。instance 1 / 2 のうち VID/PID が一致するものがちょうど 1 つのときだけ決まる
    public bool TryGetJsForDevice(ushort vendorId, ushort productId, out int js)
    {
        js = 0;
        var matches = JoystickOptions
            .Where(o => (o.Instance == 1 || o.Instance == 2) && o.HasVidPid && o.VendorId == vendorId && o.ProductId == productId)
            .ToList();
        if (matches.Count != 1) return false;
        js = matches[0].Instance;
        return true;
    }

    // 手で選んだ js 番号 (options から決められない機器のもの): 機器のキー (GetDeviceKey) → instance (1 / 2)。
    //   options の instance と同じ扱い: js 番号 = instance、js1/js2 の入れ替えで 1↔2 を交換する
    public Dictionary<string, int> ManualJoystickInstances { get; } = new(StringComparer.OrdinalIgnoreCase);

    // 手で選んだ js 番号が変わったとき (呼び出し元が保存フォルダごとに覚える)
    public event Action? ManualJoystickInstancesChanged;

    // 機器のキー: Windows の機器 ID (NonRoamableId) があればそれ、無ければ "VID:PID" (16 進 4 桁)
    public static string GetDeviceKey(string? deviceId, ushort vendorId, ushort productId) =>
        !string.IsNullOrEmpty(deviceId) ? deviceId : $"{vendorId:X4}:{productId:X4}";

    // 押した機器の js 番号。options から決まればそれ (manual = false)、決まらなければ手で選んだ番号 (manual = true)。どちらも無ければ false
    public bool TryResolveJsForDevice(ushort vendorId, ushort productId, string? deviceId, out int js, out bool manual)
    {
        manual = false;
        if (TryGetJsForDevice(vendorId, productId, out js)) return true;
        if (ManualJoystickInstances.TryGetValue(GetDeviceKey(deviceId, vendorId, productId), out var instance) && (instance == 1 || instance == 2))
        {
            js = instance;
            manual = true;
            return true;
        }
        js = 0;
        return false;
    }

    // 「この機器は js◯」を手で選ぶ (その機器の分だけを入れる。ほかの機器の記録は変えない)
    public void SetManualJsForDevice(ushort vendorId, ushort productId, string? deviceId, int js)
    {
        if (js != 1 && js != 2) return;
        var key = GetDeviceKey(deviceId, vendorId, productId);
        ManualJoystickInstances[key] = js;
        ManualJoystickInstancesChanged?.Invoke();
    }

    // 「js1/js2 の入れ替え」: 全 action の Joystick1 と Joystick2 の値を js1_↔js2_ を付け替えて交換し、
    //   js1 / js2 の activationMode も交換する (割り当てに付けたまま移す)。
    //   既定の割り当て (DefaultJoystick1 / 2) も同じく交換する (［既定値に戻す］は入れ替えた後の既定を入れる)。
    //   ゲームの既定 (GameDefaultJoystick1 / 2。保存で rebind を書くかを決める値) は交換しない (ゲームは defaultProfile の既定を番号どおりに使うため)。
    //   options の instance 1 / 2 の情報と、手で選んだ js 番号もメモリ上で交換する (ファイルの options は保存時に交換)。
    //   HOTAS の左右の表示 (Js1IsLeft) も反転する (物理スティックの左右は変わらないので、js 番号の左右を入れ替える)。
    //   2 回行うと元に戻る
    public void SwapJoystick1And2Bindings()
    {
        foreach (var b in Categories.SelectMany(c => c.Actions))
        {
            var j1 = b.Joystick1;
            var j2 = b.Joystick2;
            b.Joystick1 = SwapJsPrefix(j2);
            b.Joystick2 = SwapJsPrefix(j1);
            (b.Joystick1ActivationMode, b.Joystick2ActivationMode) = (b.Joystick2ActivationMode, b.Joystick1ActivationMode);
            b.SwapJoystickDefaults();
        }
        foreach (var o in JoystickOptions)
        {
            if (o.Instance == 1) o.Instance = 2;
            else if (o.Instance == 2) o.Instance = 1;
        }
        foreach (var key in ManualJoystickInstances.Keys.ToList())
        {
            var inst = ManualJoystickInstances[key];
            if (inst == 1 || inst == 2) ManualJoystickInstances[key] = 3 - inst;
        }
        Js1IsLeft = !Js1IsLeft;
        JoystickInstancesSwapPending = !JoystickInstancesSwapPending;
        if (ManualJoystickInstances.Count > 0) ManualJoystickInstancesChanged?.Invoke();
    }

    // 保存フォルダの actionmaps.xml が「js1/js2 の入れ替え」を保存したものであるとき (エディターが記録から決める)、読み込み直後に呼ぶ:
    //   既定の割り当て (DefaultJoystick1 / 2) と既定のモード (js1 / js2 の activationMode) だけを左右入れ替える
    //   (今の割り当て・options・手で選んだ js 番号・保存待ちの印は変えない。ゲームの既定 GameDefaultJoystick1 / 2 も変えない)
    public void SwapJoystickDefaultsForSavedSwap()
    {
        foreach (var b in Categories.SelectMany(c => c.Actions))
        {
            b.SwapJoystickDefaults();
            (b.Joystick1ActivationMode, b.Joystick2ActivationMode) = (b.Joystick2ActivationMode, b.Joystick1ActivationMode);
        }
    }

    internal static string SwapJsPrefix(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var parts = value.Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            var lead = p.Length - p.TrimStart().Length;
            var body = p[lead..];
            if (body.StartsWith("js1_", StringComparison.OrdinalIgnoreCase)) parts[i] = p[..lead] + "js2_" + body[4..];
            else if (body.StartsWith("js2_", StringComparison.OrdinalIgnoreCase)) parts[i] = p[..lead] + "js1_" + body[4..];
        }
        return string.Join("+", parts);
    }
}

// actionmaps.xml の <options type="joystick"> 1 つ分
public class JoystickDeviceOption
{
    public int Instance { get; set; }
    public string Product { get; set; } = "";   // 属性値そのまま (例 " VKBsim Gladiator EVO L    {0201231D-...}")
    public bool HasVidPid { get; set; }
    public ushort VendorId { get; set; }
    public ushort ProductId { get; set; }

    // 機器の見分け (元の js1 の機器の記録に使う): Product の GUID から VID/PID が取れれば "VID:PID" (16 進 4 桁)、
    //   取れなければ GUID そのもの (大文字)、GUID も無ければ null
    public string? DeviceIdentity
    {
        get
        {
            if (HasVidPid) return $"{VendorId:X4}:{ProductId:X4}";
            var m = System.Text.RegularExpressions.Regex.Match(Product ?? "", @"\{[^}]*\}");
            return m.Success ? m.Value.ToUpperInvariant() : null;
        }
    }

    // GUID を除いて前後の空白を落とした名前 (例 "VKBsim Gladiator EVO L")
    public string Name => System.Text.RegularExpressions.Regex.Replace(Product ?? "", @"\{[^}]*\}", "").Trim();
}

public class ActionCategory
{
    public string Name { get; set; } = "";
    public string UILabel { get; set; } = "";
    public ActionMapData? Owner { get; set; }
    public string DisplayName
    {
        get
        {
            var loc = ResolveLocLabel(UILabel, Owner?.Localization);
            if (!string.IsNullOrEmpty(loc)) return loc;
            return ActionMapNames.GetCategoryName(Name);
        }
    }
    public List<ActionBinding> Actions { get; set; } = new();

    internal static string ResolveLocLabel(string uiLabel, Dictionary<string, string>? localization)
    {
        if (string.IsNullOrEmpty(uiLabel) || localization == null || localization.Count == 0) return "";
        var key = uiLabel.TrimStart('@');
        return localization.TryGetValue(key, out var val) ? val : "";
    }
}

public class ActionBinding : INotifyPropertyChanged
{
    public string CategoryName { get; set; } = "";
    public string ActionName { get; set; } = "";
    public string UILabel { get; set; } = "";
    public ActionMapData? Owner { get; set; }
    public string DisplayName
    {
        get
        {
            var loc = ActionCategory.ResolveLocLabel(UILabel, Owner?.Localization);
            if (!string.IsNullOrEmpty(loc)) return loc;
            return ActionMapNames.GetActionName(ActionName);
        }
    }

    private string _keyboard = "";
    private string _mouse = "";
    private string _gamepad = "";
    private string _joystick1 = "";
    private string _joystick2 = "";
    private bool _isLongPress;

    public string ActivationMode { get; set; } = "";
    public string KeyboardActivationMode { get; set; } = "";
    public string MouseActivationMode { get; set; } = "";
    public string GamepadActivationMode { get; set; } = "";
    public string Joystick1ActivationMode { get; set; } = "";
    public string Joystick2ActivationMode { get; set; } = "";

    public string EffectiveKeyboardActivationMode =>
        !string.IsNullOrEmpty(KeyboardActivationMode) ? KeyboardActivationMode : ActivationMode;
    public string EffectiveMouseActivationMode =>
        !string.IsNullOrEmpty(MouseActivationMode) ? MouseActivationMode : ActivationMode;
    public string EffectiveGamepadActivationMode =>
        !string.IsNullOrEmpty(GamepadActivationMode) ? GamepadActivationMode : ActivationMode;
    public string EffectiveJoystick1ActivationMode =>
        !string.IsNullOrEmpty(Joystick1ActivationMode) ? Joystick1ActivationMode : ActivationMode;
    public string EffectiveJoystick2ActivationMode =>
        !string.IsNullOrEmpty(Joystick2ActivationMode) ? Joystick2ActivationMode : ActivationMode;

    // 値は代入時にゲームの形 (機器接頭辞を先頭に 1 回: "js1_rctrl+button3"、"gp1_shoulderl+a"、"kb1_lshift+w") に直す
    public string Keyboard { get => _keyboard; set { _keyboard = InputCodeHelper.ToGameForm(value, "kb1_"); OnPropertyChanged(); OnPropertyChanged(nameof(KeyboardDisplay)); OnPropertyChanged(nameof(KeyboardDisplayJa)); } }
    public string Mouse { get => _mouse; set { _mouse = InputCodeHelper.ToGameForm(value, "mo1_"); OnPropertyChanged(); OnPropertyChanged(nameof(MouseDisplay)); OnPropertyChanged(nameof(MouseDisplayJa)); } }
    public string Gamepad { get => _gamepad; set { _gamepad = InputCodeHelper.ToGameForm(value, "gp1_"); OnPropertyChanged(); OnPropertyChanged(nameof(GamepadDisplay)); OnPropertyChanged(nameof(GamepadDisplayJa)); } }
    public string Joystick1 { get => _joystick1; set { _joystick1 = InputCodeHelper.ToGameForm(value, "js1_"); OnPropertyChanged(); OnPropertyChanged(nameof(Joystick1Display)); OnPropertyChanged(nameof(Joystick1DisplayJa)); OnPropertyChanged(nameof(HotasDisplay)); } }
    public string Joystick2 { get => _joystick2; set { _joystick2 = InputCodeHelper.ToGameForm(value, "js2_"); OnPropertyChanged(); OnPropertyChanged(nameof(Joystick2Display)); OnPropertyChanged(nameof(Joystick2DisplayJa)); OnPropertyChanged(nameof(HotasDisplay)); } }
    public bool IsLongPress { get => _isLongPress; set { _isLongPress = value; OnPropertyChanged(); } }

    // ［機能別］タブで、スティック・ゲームパッドで押した入力がこの機能に割り当ててあるとき true (行をオレンジの枠で目立たせる。画面だけ)
    private bool _isPressHighlighted;
    public bool IsPressHighlighted { get => _isPressHighlighted; set { if (_isPressHighlighted == value) return; _isPressHighlighted = value; OnPropertyChanged(); } }

    // Legacy alias (kept for searching/text matching from external services like ChatService).
    public string Joystick => string.IsNullOrEmpty(_joystick1) ? _joystick2 : _joystick1;
    public string JoystickDisplay => HotasDisplay;

    // 従来の表記 (チャット (ChatService) が使う。変更前のまま)
    public string KeyboardDisplay => InputDisplayHelper.FormatInput(Keyboard);
    public string MouseDisplay => InputDisplayHelper.FormatInput(Mouse);
    public string GamepadDisplay => InputDisplayHelper.FormatInput(Gamepad);
    public string Joystick1Display => InputDisplayHelper.FormatInput(Joystick1);
    public string Joystick2Display => InputDisplayHelper.FormatInput(Joystick2);

    // 画面用の日本語表記
    public string KeyboardDisplayJa => InputDisplayHelper.FormatInputJa(Keyboard);
    public string MouseDisplayJa => InputDisplayHelper.FormatInputJa(Mouse);
    public string GamepadDisplayJa => InputDisplayHelper.FormatInputJa(Gamepad);
    public string Joystick1DisplayJa => InputDisplayHelper.FormatInputJa(Joystick1);
    public string Joystick2DisplayJa => InputDisplayHelper.FormatInputJa(Joystick2);
    // 画面用 (機能別タブの HOTAS 列・印刷・検索)。"R (js2): ...\nL (js1): ..." の形で日本語表記。
    //   R/L は利用者の選択 (Owner.LeftJs) に従う。Owner が無ければ選んでいないときと同じ js1 = L
    public string HotasDisplay
    {
        get
        {
            int leftJs = Owner?.LeftJs ?? 1;
            int rightJs = 3 - leftJs;
            var r = rightJs == 1 ? Joystick1DisplayJa : Joystick2DisplayJa;
            var l = leftJs == 1 ? Joystick1DisplayJa : Joystick2DisplayJa;
            var rTag = $"R (js{rightJs})";
            var lTag = $"L (js{leftJs})";
            if (!string.IsNullOrEmpty(r) && !string.IsNullOrEmpty(l)) return $"{rTag}: {r}\n{lTag}: {l}";
            if (!string.IsNullOrEmpty(r)) return $"{rTag}: {r}";
            if (!string.IsNullOrEmpty(l)) return $"{lTag}: {l}";
            return "";
        }
    }

    // 割り当て解除 ("js1_ " など) は割り当て無しとして数える
    public bool HasAnyBinding => InputCodeHelper.IsAssigned(Keyboard) || InputCodeHelper.IsAssigned(Mouse) ||
                                  InputCodeHelper.IsAssigned(Gamepad) || InputCodeHelper.IsAssigned(Joystick1) || InputCodeHelper.IsAssigned(Joystick2);

    // Default bindings (from defaultProfile.xml) - stored separately for reset
    //   既定値もゲームの形に直して持つ (「既定値に戻す」で書かれる値もゲームの形になる)
    private string _defaultKeyboard = "", _defaultMouse = "", _defaultGamepad = "", _defaultJoystick1 = "", _defaultJoystick2 = "";
    public string DefaultKeyboard { get => _defaultKeyboard; set => _defaultKeyboard = InputCodeHelper.ToGameForm(value, "kb1_"); }
    public string DefaultMouse { get => _defaultMouse; set => _defaultMouse = InputCodeHelper.ToGameForm(value, "mo1_"); }
    public string DefaultGamepad { get => _defaultGamepad; set => _defaultGamepad = InputCodeHelper.ToGameForm(value, "gp1_"); }
    //   DefaultJoystick1 / 2 に代入すると、ゲームの既定 (GameDefaultJoystick1 / 2) にも同じ値が入る (読み込み時)
    public string DefaultJoystick1 { get => _defaultJoystick1; set => _defaultJoystick1 = _gameDefaultJoystick1 = InputCodeHelper.ToGameForm(value, "js1_"); }
    public string DefaultJoystick2 { get => _defaultJoystick2; set => _defaultJoystick2 = _gameDefaultJoystick2 = InputCodeHelper.ToGameForm(value, "js2_"); }

    // ゲームの既定 (defaultProfile のまま。「js1/js2 の入れ替え」でも変えない)。
    //   ゲームは rebind の無い機能に defaultProfile の既定を js 番号どおりに使うので、保存で rebind を書くかはこの値と比べて決める
    private string _gameDefaultJoystick1 = "", _gameDefaultJoystick2 = "";
    public string GameDefaultJoystick1 => _gameDefaultJoystick1;
    public string GameDefaultJoystick2 => _gameDefaultJoystick2;

    // 「js1/js2 の入れ替え」: 既定の割り当て (DefaultJoystick1 / 2) を js1_↔js2_ を付け替えて交換する (ゲームの既定は変えない)
    internal void SwapJoystickDefaults()
    {
        var d1 = _defaultJoystick1;
        var d2 = _defaultJoystick2;
        _defaultJoystick1 = ActionMapData.SwapJsPrefix(d2);
        _defaultJoystick2 = ActionMapData.SwapJsPrefix(d1);
    }

    // 保存で rebind を書く必要があるか (今の値がゲームの既定と違う機器がある)。
    //   js1 / js2 は入れ替えた既定ではなくゲームの既定と比べる
    internal bool DiffersFromGameDefault =>
        Keyboard != DefaultKeyboard || Mouse != DefaultMouse || Gamepad != DefaultGamepad ||
        Joystick1 != GameDefaultJoystick1 || Joystick2 != GameDefaultJoystick2;

    public bool IsModified => Keyboard != DefaultKeyboard || Mouse != DefaultMouse ||
                              Gamepad != DefaultGamepad ||
                              Joystick1 != DefaultJoystick1 || Joystick2 != DefaultJoystick2;
    public bool IsKeyboardModified => Keyboard != DefaultKeyboard;
    public bool IsMouseModified => Mouse != DefaultMouse;
    public bool IsGamepadModified => Gamepad != DefaultGamepad;
    public bool IsJoystick1Modified => Joystick1 != DefaultJoystick1;
    public bool IsJoystick2Modified => Joystick2 != DefaultJoystick2;
    public bool IsHotasModified => IsJoystick1Modified || IsJoystick2Modified;
    public string CategoryDisplayName
    {
        get
        {
            if (Owner != null)
            {
                var cat = Owner.Categories.FirstOrDefault(c => c.Name == CategoryName);
                if (cat != null)
                {
                    var loc = ActionCategory.ResolveLocLabel(cat.UILabel, Owner.Localization);
                    if (!string.IsNullOrEmpty(loc)) return loc;
                }
            }
            return ActionMapNames.GetCategoryName(CategoryName);
        }
    }

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

        // Load Japanese localization for display names
        if (!string.IsNullOrEmpty(gamePath))
        {
            var jaIniPath = Path.Combine(gamePath, "data", "Localization", "japanese_(japan)", "global.ini");
            if (!File.Exists(jaIniPath))
            {
                var p4kPath = Path.Combine(gamePath, "Data.p4k");
                if (File.Exists(p4kPath))
                {
                    try
                    {
                        var extractDir = Path.Combine(Path.GetTempPath(), "SCJPKeybind");
                        jaIniPath = Path.Combine(extractDir, "japanese_global.ini");
                        if (!File.Exists(jaIniPath))
                            P4kExtractor.Extract(p4kPath, "Data/Localization/japanese_(japan)/global.ini", jaIniPath);
                    }
                    catch { }
                }
            }
            if (File.Exists(jaIniPath))
            {
                try { data.Localization = GlobalIniParser.Parse(jaIniPath); }
                catch { }
            }
        }

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
                ParseJoystickOptions(doc, data);
                (data.JoystickDefinition1, data.JoystickDefinition2) = GetJoystickDefinitionParts(doc);
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
                var catLabel = am.Attribute("UILabel")?.Value ?? am.Attribute("UICategory")?.Value ?? "";
                category = new ActionCategory { Name = catName, UILabel = catLabel, Owner = data };
                data.Categories.Add(category);
            }

            foreach (var action in am.Elements("action"))
            {
                var actionName = action.Attribute("name")?.Value ?? "";
                if (string.IsNullOrEmpty(actionName)) continue;

                var uiLabel = action.Attribute("UILabel")?.Value ?? "";
                var binding = new ActionBinding
                {
                    CategoryName = catName,
                    ActionName = actionName,
                    UILabel = uiLabel,
                    Owner = data,
                };

                // Activation mode
                var actMode = action.Attribute("activationMode")?.Value?.Trim()
                           ?? action.Attribute("ActivationMode")?.Value?.Trim()
                           ?? action.Attribute("activationmode")?.Value?.Trim()
                           ?? "";
                binding.ActivationMode = actMode;

                // Attribute-based bindings (defaultProfile format)
                var kb = NormalizeInputPrefix(action.Attribute("keyboard")?.Value?.Trim(), "kb1_");
                var mo = NormalizeInputPrefix(action.Attribute("mouse")?.Value?.Trim(), "mo1_");
                var gp = NormalizeInputPrefix(action.Attribute("gamepad")?.Value?.Trim(), "gp1_");
                var js = action.Attribute("joystick")?.Value?.Trim();
                if (!string.IsNullOrEmpty(kb)) { binding.Keyboard = kb; binding.DefaultKeyboard = kb; }
                if (!string.IsNullOrEmpty(mo)) { binding.Mouse = mo; binding.DefaultMouse = mo; }
                if (!string.IsNullOrEmpty(gp)) { binding.Gamepad = gp; binding.DefaultGamepad = gp; }
                if (!string.IsNullOrEmpty(js))
                {
                    var jsDev = InputCodeHelper.GetMainDevicePrefix(js);
                    if (jsDev == "js2_") { binding.Joystick2 = js; binding.DefaultJoystick2 = js; }
                    else if (!InputCodeHelper.IsJoystick3OrLater(jsDev)) { binding.Joystick1 = js; binding.DefaultJoystick1 = js; }
                    // js3 以降はアプリでは扱わない (どの欄にも入れない)
                }

                // Per-device child elements with their own activationMode and input
                foreach (var deviceEl in action.Elements())
                {
                    var elName = deviceEl.Name.LocalName.ToLower();
                    var devActMode = deviceEl.Attribute("activationMode")?.Value?.Trim() ?? "";
                    var devInput = deviceEl.Attribute("input")?.Value?.Trim() ?? "";
                    var devOnHold = deviceEl.Attribute("onHold")?.Value?.Trim() ?? "";

                    switch (elName)
                    {
                        case "keyboard":
                            if (!string.IsNullOrEmpty(devActMode)) binding.KeyboardActivationMode = devActMode;
                            if (!string.IsNullOrEmpty(devInput)) { var v = NormalizeInputPrefix(devInput, "kb1_"); binding.Keyboard = v; binding.DefaultKeyboard = v; }
                            break;
                        case "mouse":
                            if (!string.IsNullOrEmpty(devActMode)) binding.MouseActivationMode = devActMode;
                            if (!string.IsNullOrEmpty(devInput)) { var v = NormalizeInputPrefix(devInput, "mo1_"); binding.Mouse = v; binding.DefaultMouse = v; }
                            break;
                        case "gamepad":
                            if (!string.IsNullOrEmpty(devActMode)) binding.GamepadActivationMode = devActMode;
                            if (!string.IsNullOrEmpty(devInput)) { var v = NormalizeInputPrefix(devInput, "gp1_"); binding.Gamepad = v; binding.DefaultGamepad = v; }
                            if (devOnHold == "1" && string.IsNullOrEmpty(devActMode)) binding.GamepadActivationMode = "hold";
                            break;
                        case "joystick":
                            if (!string.IsNullOrEmpty(devInput))
                            {
                                var devJs = InputCodeHelper.GetMainDevicePrefix(devInput);
                                if (devJs == "js2_")
                                {
                                    if (!string.IsNullOrEmpty(devActMode)) binding.Joystick2ActivationMode = devActMode;
                                    binding.Joystick2 = devInput; binding.DefaultJoystick2 = devInput;
                                }
                                else if (InputCodeHelper.IsJoystick3OrLater(devJs))
                                {
                                    // js3 以降はアプリでは扱わない
                                }
                                else
                                {
                                    if (!string.IsNullOrEmpty(devActMode)) binding.Joystick1ActivationMode = devActMode;
                                    binding.Joystick1 = devInput; binding.DefaultJoystick1 = devInput;
                                }
                            }
                            else if (!string.IsNullOrEmpty(devActMode))
                            {
                                binding.Joystick1ActivationMode = devActMode;
                            }
                            break;
                    }
                }

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
                category = new ActionCategory { Name = catName, Owner = data };
                data.Categories.Add(category);
            }

            foreach (var action in am.Elements("action"))
            {
                var actionName = action.Attribute("name")?.Value ?? "";
                if (string.IsNullOrEmpty(actionName)) continue;

                var key = $"{catName}.{actionName}";
                if (!data.AllBindings.TryGetValue(key, out var binding))
                {
                    binding = new ActionBinding { CategoryName = catName, ActionName = actionName, Owner = data };
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

    private static string NormalizeInputPrefix(string? input, string requiredPrefix)
    {
        if (string.IsNullOrEmpty(input) || input.Trim() == " ") return "";
        var parts = input.Split('+');
        var result = new List<string>();
        foreach (var part in parts)
        {
            var p = part.Trim();
            if (string.IsNullOrEmpty(p)) continue;
            // Already has any device prefix → keep as-is
            if (p.StartsWith("kb1_") || p.StartsWith("mo1_") || p.StartsWith("gp1_") ||
                p.StartsWith("js1_") || p.StartsWith("js2_"))
            {
                result.Add(p);
            }
            else
            {
                result.Add(requiredPrefix + p);
            }
        }
        return result.Count > 0 ? string.Join("+", result) : "";
    }

    private static void ClassifyInput(string input, ActionBinding binding, bool setDefault)
    {
        // 本体入力の機器接頭辞で振り分ける (以前アプリが保存した "kb1_rctrl+js1_button30" も js1 の欄へ)。
        //   値は代入時にゲームの形に直る。js3 以降はアプリでは扱わない (どの欄にも入れない)
        var dev = InputCodeHelper.GetMainDevicePrefix(input);
        if (dev != null)
        {
            switch (dev)
            {
                case "kb1_": binding.Keyboard = input; if (setDefault) binding.DefaultKeyboard = input; return;
                case "mo1_": binding.Mouse = input; if (setDefault) binding.DefaultMouse = input; return;
                case "gp1_": binding.Gamepad = input; if (setDefault) binding.DefaultGamepad = input; return;
                case "js1_": binding.Joystick1 = input; if (setDefault) binding.DefaultJoystick1 = input; return;
                case "js2_": binding.Joystick2 = input; if (setDefault) binding.DefaultJoystick2 = input; return;
            }
            if (InputCodeHelper.IsJoystick3OrLater(dev)) return;
            // それ以外の接頭辞 (kb2_ など) は従来の判定へ
        }

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
        else if (input.StartsWith("js2_"))
        {
            binding.Joystick2 = input;
            if (setDefault) binding.DefaultJoystick2 = input;
        }
        else if (input.StartsWith("js"))
        {
            binding.Joystick1 = input;
            if (setDefault) binding.DefaultJoystick1 = input;
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
            // js1 / js2 は入れ替えた既定ではなくゲームの既定と比べる (DiffersFromGameDefault)
            var modifiedActions = cat.Actions.Where(a => a.DiffersFromGameDefault).ToList();
            if (modifiedActions.Count == 0) continue;

            var am = new XElement("actionmap", new XAttribute("name", cat.Name));
            foreach (var action in modifiedActions)
            {
                var el = new XElement("action", new XAttribute("name", action.ActionName));
                // キーボード・マウス・パッド: 既定と違えば書く。値が空なら割り当て解除としてゲームの書式 ("kb1_ " など) で書く
                if (action.Keyboard != action.DefaultKeyboard)
                    el.Add(new XElement("rebind", new XAttribute("input", string.IsNullOrWhiteSpace(action.Keyboard) ? "kb1_ " : action.Keyboard)));
                if (action.Mouse != action.DefaultMouse)
                    el.Add(new XElement("rebind", new XAttribute("input", string.IsNullOrWhiteSpace(action.Mouse) ? "mo1_ " : action.Mouse)));
                if (action.Gamepad != action.DefaultGamepad)
                    el.Add(new XElement("rebind", new XAttribute("input", string.IsNullOrWhiteSpace(action.Gamepad) ? "gp1_ " : action.Gamepad)));
                if (action.Joystick1 != action.GameDefaultJoystick1)
                    el.Add(new XElement("rebind", new XAttribute("input", string.IsNullOrWhiteSpace(action.Joystick1) ? "js1_ " : action.Joystick1)));
                if (action.Joystick2 != action.GameDefaultJoystick2)
                    el.Add(new XElement("rebind", new XAttribute("input", string.IsNullOrWhiteSpace(action.Joystick2) ? "js2_ " : action.Joystick2)));
                if (el.HasElements)
                    am.Add(el);
            }
            if (am.HasElements)
                profiles.Add(am);
        }

        root.Add(profiles);
        new XDocument(root).Save(outputPath);
    }

    // ===== 機器の定義の指紋 =====
    //   options type="joystick" の instance 1 / 2 ごとに、その instance の options 要素 (文書の順) を 1 つずつ、機器を見分ける値だけにして ";" でつなぐ:
    //     Product に GUID ({...}) があれば "g:" + GUID (大文字)
    //     GUID が無ければ "p:" + Product の属性の文字 (前後の空白を除き、途中の連続する空白を 1 つにまとめたもの)
    //     Product も無ければ "-" (見分けられない)
    //   軸の設定などの子要素や、ほかの属性は入れない (ゲームが軸の設定を書き換えても指紋が変わらないように)。
    //   その instance の要素が無い、またはどの要素も見分けられないときは "-"。
    //   instance 属性を使わないので、js1/js2 の入れ替えを保存したファイルの定義は、元の定義の 1 と 2 を入れ替えたものになる
    internal static (string Def1, string Def2) GetJoystickDefinitionParts(XDocument doc)
    {
        var root = doc.Root;
        if (root == null) return ("-", "-");
        var profiles = root.Element("ActionProfiles") ?? root;
        var opts = profiles.Elements("options").Where(o => (string?)o.Attribute("type") == "joystick").ToList();
        string Part(string instance)
        {
            var tokens = opts.Where(o => ((string?)o.Attribute("instance"))?.Trim() == instance)
                .Select(o =>
                {
                    // 前後の空白を除き、途中の連続する空白を 1 つにまとめる
                    var product = System.Text.RegularExpressions.Regex.Replace(((string?)o.Attribute("Product"))?.Trim() ?? "", @"\s+", " ");
                    var m = System.Text.RegularExpressions.Regex.Match(product, @"\{[^}]*\}");
                    if (m.Success) return "g:" + m.Value.ToUpperInvariant();
                    return product.Length > 0 ? "p:" + product : "-";
                })
                .ToList();
            return tokens.Count == 0 || tokens.All(t => t == "-") ? "-" : string.Join(";", tokens);
        }
        return (Part("1"), Part("2"));
    }

    // 定義 1 / 2 から指紋を作る ("fp:" + SHA-256 の 16 進。記録の JSON の鍵に使う)
    public static string MakeJoystickFingerprint(string def1, string def2)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"1={def1}|2={def2}"));
        return "fp:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // 保存フォルダの actionmaps.xml から指紋を作る (古い形の記録の移し替え用)。ファイルが無い・読めなければ null
    public static string? ComputeJoystickFingerprintForSave(string savePath)
    {
        try
        {
            var path = Path.Combine(savePath, "Profiles", "default", "actionmaps.xml");
            if (!File.Exists(path)) return null;
            var (d1, d2) = GetJoystickDefinitionParts(XDocument.Load(path));
            return MakeJoystickFingerprint(d1, d2);
        }
        catch { return null; }
    }

    // <options type="joystick" instance="N" Product="..."> を読む
    private static void ParseJoystickOptions(XDocument doc, ActionMapData data)
    {
        data.JoystickOptions.Clear();
        var root = doc.Root;
        if (root == null) return;
        var profiles = root.Element("ActionProfiles") ?? root;
        foreach (var opt in profiles.Elements("options"))
        {
            if ((string?)opt.Attribute("type") != "joystick") continue;
            if (!int.TryParse((string?)opt.Attribute("instance"), out var instance)) continue;
            var product = (string?)opt.Attribute("Product") ?? "";
            var o = new JoystickDeviceOption { Instance = instance, Product = product };
            if (GameControllerInput.TryParseProductVidPid(product, out var vid, out var pid))
            {
                o.HasVidPid = true;
                o.VendorId = vid;
                o.ProductId = pid;
            }
            data.JoystickOptions.Add(o);
        }
    }

    // メモリ上の値 → ファイルに書く input 値。
    //   空 = 割り当て解除 → ゲームの書式 "<接頭辞> " (例 "js1_ ")
    //   機器接頭辞が無い値 (defaultProfile 由来の "button1" など) → 先頭に接頭辞を付ける
    //   別の機器の接頭辞の値 → null (書かない)
    private static string? ToFileInputValue(string value, string slotPrefix)
    {
        if (string.IsNullOrWhiteSpace(value)) return slotPrefix + " ";
        var dev = InputCodeHelper.GetMainDevicePrefix(value);
        if (dev == null) return slotPrefix + value;
        if (dev != slotPrefix) return null;
        return value;
    }

    // 既存の actionmaps.xml を読み、<options>・<modifiers> などはそのまま残して、各 action の rebind だけを書き換える。
    //   各 action・各機器 (kb1/mo1/gp1/js1/js2) について:
    //     既存の rebind (input の本体入力の機器接頭辞が同じもの。複数あれば最後のもの) があれば input 属性だけ新しい値にする
    //     (複数あったときは、書き換えた最後の 1 本より前の同じ機器の rebind を消す)
    //     (js3 以降の rebind はどの欄とも一致しないので触らない)
    //     無ければ、値が既定値 (js1 / js2 はゲームの既定) と違うときだけ rebind を追加する (actionmap / action 要素が無ければ作る)
    //   swapJoystickInstances = true なら <options type="joystick"> の instance="1" と "2" を交換する
    public static void SaveMergedOverrides(ActionMapData data, string path, bool swapJoystickInstances)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidDataException("actionmaps.xml にルート要素がありません。");
        var profiles = root.Element("ActionProfiles") ?? root;

        // js1/js2 の入れ替え: メモリ上の値と同じく、ファイルの rebind の input も js1_↔js2_ を付け替える
        //   (activationMode などの属性を、その割り当てに付けたまま移すため)
        //   <modifiers> の中の要素の input も同じ規則で付け替える (中身が空なら何もしない)
        if (swapJoystickInstances)
        {
            foreach (var rb in profiles.Elements("actionmap").Elements("action").Elements("rebind"))
            {
                var input = (string?)rb.Attribute("input");
                if (input != null) rb.SetAttributeValue("input", ActionMapData.SwapJsPrefix(input));
            }
            foreach (var mod in profiles.Elements("modifiers").Descendants())
            {
                var input = (string?)mod.Attribute("input");
                if (input != null) mod.SetAttributeValue("input", ActionMapData.SwapJsPrefix(input));
            }
        }

        foreach (var cat in data.Categories)
        {
            XElement? amEl = null;
            bool amLooked = false;
            foreach (var action in cat.Actions)
            {
                if (!amLooked)
                {
                    amEl = profiles.Elements("actionmap").FirstOrDefault(e => (string?)e.Attribute("name") == cat.Name);
                    amLooked = true;
                }
                var actEl = amEl?.Elements("action").FirstOrDefault(e => (string?)e.Attribute("name") == action.ActionName);

                var slots = new (string Prefix, string Value, string Default)[]
                {
                    ("kb1_", action.Keyboard, action.DefaultKeyboard),
                    ("mo1_", action.Mouse, action.DefaultMouse),
                    ("gp1_", action.Gamepad, action.DefaultGamepad),
                    // js1 / js2 は入れ替えた既定ではなくゲームの既定と比べる (ゲームは rebind の無い機能に defaultProfile の既定を番号どおりに使う)
                    ("js1_", action.Joystick1, action.GameDefaultJoystick1),
                    ("js2_", action.Joystick2, action.GameDefaultJoystick2),
                };

                foreach (var (prefix, value, def) in slots)
                {
                    // 同じ機器の rebind が 2 本以上あれば、最後の 1 本を書き換え、それより前の rebind は消す
                    var sameDevice = actEl?.Elements("rebind")
                        .Where(r => InputCodeHelper.GetMainDevicePrefix((string?)r.Attribute("input") ?? "") == prefix)
                        .ToList();
                    var existing = sameDevice?.LastOrDefault();
                    if (sameDevice != null && existing != null)
                    {
                        var fv = ToFileInputValue(value ?? "", prefix);
                        if (fv == null) continue;
                        existing.SetAttributeValue("input", fv);
                        foreach (var old in sameDevice.Take(sameDevice.Count - 1))
                            old.Remove();
                    }
                    else
                    {
                        if ((value ?? "") == (def ?? "")) continue;
                        var fv = ToFileInputValue(value ?? "", prefix);
                        if (fv == null) continue;
                        if (amEl == null)
                        {
                            amEl = new XElement("actionmap", new XAttribute("name", cat.Name));
                            profiles.Add(amEl);
                        }
                        if (actEl == null)
                        {
                            actEl = new XElement("action", new XAttribute("name", action.ActionName));
                            amEl.Add(actEl);
                        }
                        actEl.Add(new XElement("rebind", new XAttribute("input", fv)));
                    }
                }
            }
        }

        if (swapJoystickInstances)
        {
            var jsOptions = profiles.Elements("options").Where(o => (string?)o.Attribute("type") == "joystick").ToList();
            var inst1 = jsOptions.Where(o => (string?)o.Attribute("instance") == "1").ToList();
            var inst2 = jsOptions.Where(o => (string?)o.Attribute("instance") == "2").ToList();
            foreach (var o in inst1) o.SetAttributeValue("instance", "2");
            foreach (var o in inst2) o.SetAttributeValue("instance", "1");
        }

        // 元ファイルの書式 (宣言なし・BOM なし・CRLF・1 字下げ) に寄せて書く。一時ファイルに書いてから置き換える
        var settings = new System.Xml.XmlWriterSettings
        {
            OmitXmlDeclaration = doc.Declaration == null,
            Indent = true,
            IndentChars = " ",
            NewLineChars = "\r\n",
            Encoding = new System.Text.UTF8Encoding(false),
        };
        var tmp = path + ".tmp";
        try
        {
            using var ms = new MemoryStream();
            using (var w = System.Xml.XmlWriter.Create(ms, settings))
                doc.Save(w);
            var written = new System.Text.UTF8Encoding(false).GetString(ms.ToArray());
            // XmlWriter は自己終了タグを ' />' で書く。ゲームの書いたファイルは属性のある要素では '"/>'、
            //   属性の無い要素では ' />' (例 <modifiers />) なので、属性の後の ' />' だけを '/>' に直す
            var text = System.Text.RegularExpressions.Regex.Replace(written, "\" />", "\"/>");
            File.WriteAllText(tmp, text + "\r\n", new System.Text.UTF8Encoding(false));   // 元ファイルと同じく末尾に改行
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
        }
    }
}

public static class ActivationModeHelper
{
    public static string GetDisplayName(string mode) => mode.ToLowerInvariant() switch
    {
        "tap" => "タップ",
        "press" => "押下中",
        "hold" => "長押し",
        "hold_no_retrigger" => "長押し(1回)",
        "hold_toggle" => "長押しトグル",
        "smart_toggle" => "スマートトグル",
        "double_tap" => "二度押し",
        "double_tap_nonblocking" => "二度押し(非ブロック)",
        "delayed_press" => "遅延押し",
        "delayed_press_quicker" => "遅延押し(短)",
        "delayed_press_medium" => "遅延押し(中)",
        "delayed_press_long" => "遅延押し(長)",
        "delayed_hold" => "遅延長押し",
        "delayed_hold_long" => "遅延長押し(長)",
        "delayed_hold_no_retrigger" => "遅延長押し(1回)",
        _ => string.IsNullOrEmpty(mode) ? "" : mode,
    };

    public static string GetCategory(string mode) => mode.ToLowerInvariant() switch
    {
        "tap" or "" => "tap",
        "press" => "press",
        "hold" or "hold_no_retrigger" or "hold_toggle" or "smart_toggle" => "hold",
        "double_tap" or "double_tap_nonblocking" => "double_tap",
        _ when mode.Contains("hold") => "hold",
        _ when mode.Contains("delayed") => "hold",
        _ => "tap",
    };
}

// 入力コードの機器接頭辞の扱い。
//   ゲームの書く形: 機器接頭辞を先頭に 1 回だけ付け、修飾キーと本体入力は接頭辞なしで "+" でつなぐ
//     例 (実ファイル): "js1_rctrl+button22"、"gp1_shoulderl+thumbly"、"kb1_lshift+w"。割り当て解除は "js1_ "
public static class InputCodeHelper
{
    private static readonly System.Text.RegularExpressions.Regex PrefixRegex = new(
        @"^(kb\d+_|mo\d+_|gp\d+_|js\d+_)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // 1 つの部分の機器接頭辞 (小文字。"kb1_" など)。無ければ null
    public static string? GetDevicePrefix(string part)
    {
        var m = PrefixRegex.Match((part ?? "").TrimStart());
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    // 本体入力の機器接頭辞: 最後の部分に接頭辞があればそれ、無ければ最初に見つかった接頭辞。どこにも無ければ null
    //   "kb1_rctrl+js1_button30" → "js1_"、"js1_rctrl+button22" → "js1_"、"kb1_lshift+gp1_a" → "gp1_"、"js1_ " → "js1_"
    public static string? GetMainDevicePrefix(string? input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        var parts = input.Split('+');
        var last = GetDevicePrefix(parts[^1]);
        if (last != null) return last;
        foreach (var p in parts)
        {
            var d = GetDevicePrefix(p);
            if (d != null) return d;
        }
        return null;
    }

    // 部分から機器接頭辞を除く (前後の空白も除く)
    public static string StripDevicePrefix(string part)
    {
        var t = (part ?? "").Trim();
        var m = PrefixRegex.Match(t);
        return m.Success ? t[m.Length..].Trim() : t;
    }

    // 割り当てがあるか。空、割り当て解除 (接頭辞と空白だけ。"js1_ " など)、
    //   修飾キーだけで本体入力が無いもの ("js1_rctrl+ " など。最後の '+' より後ろが接頭辞と空白を除くと空) なら false
    public static bool IsAssigned(string? value) =>
        !string.IsNullOrEmpty(value) && StripDevicePrefix(value.Split('+')[^1]).Length > 0;

    // "js3_" 以降のジョイスティック接頭辞か
    public static bool IsJoystick3OrLater(string? prefix) =>
        prefix != null && prefix.StartsWith("js") && prefix.EndsWith("_") &&
        int.TryParse(prefix[2..^1], out var n) && n >= 3;

    // ゲームの形に直す。機器は本体入力の接頭辞で決め、接頭辞がどこにも無ければ hintPrefix を使う (それも無ければそのまま)。
    //   "kb1_rctrl+js1_button30" → "js1_rctrl+button30"、"gp1_shoulderl+gp1_y" → "gp1_shoulderl+y"、
    //   "kb1_lshift+kb1_w" → "kb1_lshift+w"、"button1" (hint js1_) → "js1_button1"、"js1_ " → "js1_ "
    public static string ToGameForm(string? value, string? hintPrefix)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        var device = GetMainDevicePrefix(value) ?? hintPrefix;
        if (device == null) return value;
        var bodies = value.Split('+').Select(StripDevicePrefix).Where(b => b.Length > 0).ToList();
        if (bodies.Count == 0) return device + " ";   // 割り当て解除
        return device + string.Join("+", bodies);
    }
}

public static class InputDisplayHelper
{
    // 従来の表記 (変更前のまま。チャット (ChatService) に渡る値などに使う)
    public static string FormatInput(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        // 割り当て解除 (接頭辞と空白だけ。"js1_ " など) は空 (エクスポート・チャットにも解除の書き方を出さない)
        if (input.Split('+').All(p => InputCodeHelper.StripDevicePrefix(p).Length == 0)) return "";

        var parts = input.Split('+');
        var result = new List<string>();
        foreach (var part in parts)
        {
            result.Add(FormatSingleInput(part.Trim()));
        }
        return string.Join(" + ", result);
    }

    // 画面表示用の日本語表記。機器接頭辞は表示しない。割り当て解除 ("js1_ " など) は空 (画面には出さない。ファイルへの保存は解除の書き方のまま)
    public static string FormatInputJa(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";

        var device = InputCodeHelper.GetMainDevicePrefix(input);
        var bodies = input.Split('+').Select(InputCodeHelper.StripDevicePrefix).ToList();
        if (bodies.All(b => b.Length == 0)) return "";

        var result = new List<string>();
        foreach (var body in bodies)
        {
            if (body.Length == 0) continue;
            result.Add(FormatPart(body, device));
        }
        return string.Join(" + ", result);
    }

    private static string FormatPart(string body, string? device)
    {
        if (KeyboardModifierNames.TryGetValue(body, out var mod)) return mod;
        var kind = device != null && device.Length >= 2 ? device[..2] : "";
        switch (kind)
        {
            case "kb": return FormatKeyboardJa(body);
            case "mo": return MouseNamesJa.TryGetValue(body, out var mn) ? mn : body;
            case "gp": return GamepadNamesJa.TryGetValue(body, out var gn) ? gn : body;
            case "js": return FormatJoystick(body);
        }
        // 接頭辞の無い値: 従来の判定の順 (パッド名 → マウス名 → キーボード) で日本語表記にする
        if (GamepadNamesJa.TryGetValue(body, out var gpName)) return gpName;
        if (MouseNamesJa.TryGetValue(body, out var moName)) return moName;
        return FormatKeyboardJa(body);
    }

    // 画面用のキーボードのキー名。刻印そのもの (英字・数字・記号・F1〜F12・Esc・Tab・Enter・Delete など) はそのまま、
    //   言葉のもの (スペース・バックスペース・矢印・テンキー) を日本語にする。該当しないものは従来の FormatKeyboard の表記
    private static string FormatKeyboardJa(string key)
    {
        var k = key.ToLowerInvariant();
        switch (k)
        {
            case "space": return "スペース";
            case "backspace": return "バックスペース";
            case "capslock": return "英数";   // キーボード図の刻印と揃える
            case "apps": return "アプリ";
            case "up": return "↑";
            case "down": return "↓";
            case "left": return "←";
            case "right": return "→";
            case "np_enter": return "テンキーEnter";
            case "np_period": return "テンキー.";
            case "np_add" or "np_plus": return "テンキー+";
            case "np_subtract" or "np_minus": return "テンキー-";
            case "np_multiply": return "テンキー*";
            case "np_divide": return "テンキー/";
        }
        if (k.Length == 4 && k.StartsWith("np_") && char.IsDigit(k[3])) return $"テンキー{k[3]}";
        if (System.Text.RegularExpressions.Regex.IsMatch(k, @"^f([1-9]|1[0-9]|2[0-4])$")) return k.ToUpperInvariant();   // F1〜F24 は刻印どおり大文字
        return FormatKeyboard(key);
    }

    // 修飾キー (キーボードの修飾名。ジョイスティック・パッドの値でも同じ名前で書かれる)
    private static readonly Dictionary<string, string> KeyboardModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lshift"] = "左Shift", ["rshift"] = "右Shift",
        ["lctrl"] = "左Ctrl", ["rctrl"] = "右Ctrl",
        ["lalt"] = "左Alt", ["ralt"] = "右Alt",
    };

    private static readonly Dictionary<string, string> GamepadNamesJa = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shoulderl"] = "LB", ["shoulderr"] = "RB",
        ["triggerl_btn"] = "LT", ["triggerr_btn"] = "RT",
        ["thumbl"] = "左スティック押し込み", ["thumbr"] = "右スティック押し込み",
        ["thumblx"] = "左スティック左右", ["thumbly"] = "左スティック上下",
        ["thumbrx"] = "右スティック左右", ["thumbry"] = "右スティック上下",
        ["thumbl_up"] = "左スティック上", ["thumbl_down"] = "左スティック下",
        ["thumbl_left"] = "左スティック左", ["thumbl_right"] = "左スティック右",
        ["thumbr_up"] = "右スティック上", ["thumbr_down"] = "右スティック下",
        ["thumbr_left"] = "右スティック左", ["thumbr_right"] = "右スティック右",
        ["dpad_up"] = "十字キー上", ["dpad_down"] = "十字キー下",
        ["dpad_left"] = "十字キー左", ["dpad_right"] = "十字キー右",
        ["btn_a"] = "A", ["a"] = "A", ["btn_b"] = "B", ["b"] = "B",
        ["btn_x"] = "X", ["x"] = "X", ["btn_y"] = "Y", ["y"] = "Y",
        ["start"] = "Start", ["back"] = "Back",
    };

    private static readonly Dictionary<string, string> MouseNamesJa = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mouse1"] = "左クリック", ["mouse2"] = "右クリック", ["mouse3"] = "中クリック",
        ["mouse4"] = "マウスボタン4", ["mouse5"] = "マウスボタン5",
        ["maxis_x"] = "マウス左右", ["maxis_y"] = "マウス上下",
        ["mwheel_up"] = "ホイール↑", ["mwheel_down"] = "ホイール↓",
    };

    private static readonly Dictionary<string, string> JoystickAxisNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["x"] = "X軸", ["y"] = "Y軸", ["z"] = "Z軸",
        ["rotx"] = "RX軸", ["roty"] = "RY軸", ["rotz"] = "RZ軸",
        ["slider1"] = "スライダー1", ["slider2"] = "スライダー2",
    };

    private static readonly System.Text.RegularExpressions.Regex JoystickButtonRegex = new(@"^button(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex JoystickHatRegex = new(@"^hat(\d+)_(up|down|left|right)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ジョイスティックの入力: "button3" → "ボタン3"、"hat1_up" → "ハット1 上"、"rotz" → "RZ軸"
    private static string FormatJoystick(string body)
    {
        var b = JoystickButtonRegex.Match(body);
        if (b.Success) return $"ボタン{b.Groups[1].Value}";
        var h = JoystickHatRegex.Match(body);
        if (h.Success)
        {
            var dir = h.Groups[2].Value.ToLowerInvariant() switch
            {
                "up" => "上", "down" => "下", "left" => "左", _ => "右",
            };
            return $"ハット{h.Groups[1].Value} {dir}";
        }
        return JoystickAxisNames.TryGetValue(body, out var an) ? an : body;
    }

    // 従来の表記用 (変更前のまま)
    private static readonly Dictionary<string, string> GamepadNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shoulderl"] = "LB", ["shoulderr"] = "RB",
        ["triggerl_btn"] = "LT", ["triggerr_btn"] = "RT",
        ["thumbl"] = "L Stick Press", ["thumbr"] = "R Stick Press",
        ["thumblx"] = "L Stick X", ["thumbly"] = "L Stick Y",
        ["thumbrx"] = "R Stick X", ["thumbry"] = "R Stick Y",
        ["thumbl_up"] = "L Stick Up", ["thumbl_down"] = "L Stick Down",
        ["thumbl_left"] = "L Stick Left", ["thumbl_right"] = "L Stick Right",
        ["thumbr_up"] = "R Stick Up", ["thumbr_down"] = "R Stick Down",
        ["thumbr_left"] = "R Stick Left", ["thumbr_right"] = "R Stick Right",
        ["dpad_up"] = "D-Pad Up", ["dpad_down"] = "D-Pad Down",
        ["dpad_left"] = "D-Pad Left", ["dpad_right"] = "D-Pad Right",
        ["btn_a"] = "A", ["a"] = "A", ["btn_b"] = "B", ["b"] = "B",
        ["btn_x"] = "X", ["x"] = "X", ["btn_y"] = "Y", ["y"] = "Y",
        ["start"] = "Start", ["back"] = "Back",
    };

    private static readonly Dictionary<string, string> MouseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mouse1"] = "Left Click", ["mouse2"] = "Right Click", ["mouse3"] = "Middle Click",
        ["mouse4"] = "Mouse4", ["mouse5"] = "Mouse5",
        ["maxis_x"] = "Mouse X", ["maxis_y"] = "Mouse Y",
        ["mwheel_up"] = "Wheel Up", ["mwheel_down"] = "Wheel Down",
    };

    private static string FormatSingleInput(string input)
    {
        if (input.StartsWith("kb1_"))
            return FormatKeyboard(input[4..]);

        if (input.StartsWith("mo1_") || input.StartsWith("mo_"))
        {
            var key = input.Contains("_") ? input[(input.IndexOf('_') + 1)..] : input;
            return MouseNames.TryGetValue(key, out var mn) ? mn : key;
        }

        if (input.StartsWith("gp1_"))
        {
            var key = input[4..];
            return GamepadNames.TryGetValue(key, out var gn) ? gn : key;
        }

        if (input.StartsWith("js"))
            return input;

        // No prefix — try gamepad names first, then mouse names, then keyboard
        if (GamepadNames.TryGetValue(input, out var gpName))
            return gpName;
        if (MouseNames.TryGetValue(input, out var moName))
            return moName;

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
