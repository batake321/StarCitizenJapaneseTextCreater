using System.IO;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace StarCitizenJapaneseTextCreater;

public partial class KeybindEditorWindow : Window
{
    private ActionMapData _data = new();
    private List<ActionBinding> _allActions = new();
    private List<ActionBinding> _filteredActions = new();
    private readonly string _gamePath;
    private string _savePath;   // 「新規作成」で保存したら新しいフォルダに切り替わる

    // 物理コントローラー / HOTAS の押下の待ち受け (エディターを開いている間)
    private GameControllerInput? _controllerInput;

    public KeybindEditorWindow(string gamePath, string savePath)
    {
        InitializeComponent();
        _gamePath = gamePath ?? "";
        _savePath = savePath ?? "";
        Title = $"キーバインド エディタ — {Path.GetFileName(savePath)}";
        hotasPanel.DisplaySwapChanged = _ => SaveCurrentStickSides();   // ［表示の入れ替え］で HOTAS の左右の選択を変えたら覚える
        hotasPanel.ClearOriginalJs1Requested = ClearOriginalJs1Record;   // ［記録を消す］(元の js1 の機器)
        hotasPanel.InstanceSwapped = () => UpdateOriginalJs1Status();   // ［js1/js2 の入れ替え］を押したら、保存待ちを含めて判定の表示を書き直す
        hotasPanel.StickSidesSetupRequested = () => RunStickSideDialog(showHowToChange: false);   // ［左右を設定…］: 保存済みの選択があっても開く
        LoadData();
        StartControllerInput();
        Closed += (_, _) => StopControllerInput();
        // 画面が出てから、HOTAS の左右をまだ選んでいなければ聞く (スティックが 2 本以上あるときだけ)
        ContentRendered += (_, _) => AskStickSidesIfNeeded();
    }

    private void StartControllerInput()
    {
        try
        {
            _controllerInput = new GameControllerInput();
            _controllerInput.InputPressed += ControllerInput_Pressed;
            _controllerInput.Start();
        }
        catch (Exception)
        {
            // 待ち受けを始められない環境では押下での強調は使えない (他の機能は従来どおり)
            StopControllerInput();
        }
    }

    private void StopControllerInput()
    {
        if (_controllerInput == null) return;
        _controllerInput.InputPressed -= ControllerInput_Pressed;
        _controllerInput.Dispose();
        _controllerInput = null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    // このウィンドウの上にモーダルのダイアログ (バインディング編集・入力の割り当てなど) が開いているか。
    //   モーダルのダイアログを開くと、その間このウィンドウは Win32 で無効にされる (閉じると有効に戻る)
    private bool IsBlockedByModalDialog()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero && !IsWindowEnabled(hwnd);
    }

    // HOTAS タブ表示中はスティック、コントローラータブ表示中はゲームパッドの押下を強調する
    //   モーダルのダイアログが開いている間は何もしない (閉じたら、その後に押した入力から再び強調する)
    private void ControllerInput_Pressed(object? sender, GameControllerInputEventArgs e)
    {
        if (IsBlockedByModalDialog()) return;
        if (tabMain.SelectedIndex == 4 && e.Kind == GameControllerKind.Joystick)
        {
            // js 番号を決めて強調する。決められなければ「この機器は js◯」を選ぶ欄を出す (HotasLayoutPanel)
            hotasPanel.HandleJoystickPress(e);
        }
        else if (tabMain.SelectedIndex == 3 && e.Kind == GameControllerKind.Gamepad)
        {
            gamepadPanel.HighlightInput(e.InputCode);
        }
        else if (tabMain.SelectedIndex == 0)
        {
            // ［機能別］タブ: 押した入力が割り当ててある機能の行へ移る
            HandleFunctionTabPress(e);
        }
    }

    // ===== ［機能別］タブで押した入力の機能へ移る =====
    //   押した入力 (js1_button22・gp1_a など) が本体の入力として入っている割り当ての行を全部オレンジの枠で目立たせ、その 1 つを選んで見える所へ出す。
    //   並びは修飾キー無しが先、その次に修飾キー付き (それぞれ表の元の並び)。同じ入力を続けて押すたびに次の行へ移る。
    //   行が絞り込みで見えないときは、絞り込み (検索・カテゴリ・入力の種類) を外してから移る。
    //   状態の欄に、何件のうち何件目か (割り当てが無ければ「この入力の割り当てはありません」) を出す。
    //   js 番号を決められない機器は、HOTAS タブと同じく「この機器は js◯」を手で選ぶ
    private List<ActionBinding> _pressMatches = new();
    private string? _pressMatchKey;        // 前に押した入力 (ゲームの形)。同じなら次の行へ
    private int _pressMatchIndex;
    private string _baseStatus = "";       // ApplyFilter が出した件数の文 (押した入力の知らせはその後ろに付ける)
    private GameControllerInputEventArgs? _funcJsChoicePress;

    private static string GetDeviceValue(ActionBinding a, string deviceType) => deviceType switch
    {
        "gamepad" => a.Gamepad,
        "joystick1" => a.Joystick1,
        "joystick2" => a.Joystick2,
        _ => "",
    };

    // 割り当て v が、押した入力 (機器の接頭辞 prefix、部分コード code) を本体の入力として持つか。hasModifiers = 修飾キー付きか
    private static bool MatchesPressed(string v, string prefix, string code, out bool hasModifiers)
    {
        hasModifiers = false;
        if (!InputCodeHelper.IsAssigned(v)) return false;
        if (InputCodeHelper.GetMainDevicePrefix(v) != prefix) return false;
        var parts = v.Split('+');
        if (!string.Equals(InputCodeHelper.StripDevicePrefix(parts[^1]), code, StringComparison.OrdinalIgnoreCase)) return false;
        hasModifiers = parts.Length > 1;
        return true;
    }

    private void HandleFunctionTabPress(GameControllerInputEventArgs e)
    {
        HideFuncJsChoice();
        string prefix, deviceType, deviceName = e.DisplayName;
        if (e.Kind == GameControllerKind.Gamepad)
        {
            prefix = "gp1_";
            deviceType = "gamepad";
        }
        else
        {
            if (!_data.TryResolveJsForDevice(e.VendorId, e.ProductId, e.DeviceId, out var js, out var manual))
            {
                txtStatus.Text = $"{_baseStatus}  |  {e.DisplayName} {e.InputCode}: どの js 番号か判別できません";
                ShowFuncJsChoice(e);
                return;
            }
            prefix = $"js{js}_";
            deviceType = $"joystick{js}";
            var optionName = _data.GetJsProductName(js);
            if (!string.IsNullOrEmpty(optionName)) deviceName = optionName;
            if (manual) ShowFuncJsChoice(e);   // 手で選んだ番号のときは、選び直せるように出す
        }

        var full = prefix + e.InputCode;
        var label = $"{deviceName} — {InputDisplayHelper.FormatInputJa(full)} ({full})";
        var matches = _allActions
            .Select(a => (Action: a, Ok: MatchesPressed(GetDeviceValue(a, deviceType), prefix, e.InputCode, out var mods), Mods: mods))
            .Where(x => x.Ok)
            .OrderBy(x => x.Mods ? 1 : 0)   // 修飾キー無しが先 (OrderBy は同じ順位の中の並びを変えない)
            .Select(x => x.Action)
            .ToList();

        foreach (var a in _pressMatches) a.IsPressHighlighted = false;
        if (matches.Count == 0)
        {
            _pressMatches = new();
            _pressMatchKey = null;
            txtStatus.Text = $"{_baseStatus}  |  {label}: この入力の割り当てはありません";
            return;
        }

        bool same = _pressMatchKey == full && _pressMatches.SequenceEqual(matches);
        _pressMatchIndex = same ? (_pressMatchIndex + 1) % matches.Count : 0;
        _pressMatchKey = full;
        _pressMatches = matches;
        foreach (var a in matches) a.IsPressHighlighted = true;

        var target = matches[_pressMatchIndex];
        if (!_filteredActions.Contains(target))
        {
            // 絞り込みで見えないので、絞り込みを外す (検索を空、カテゴリを全部、入力の種類を全入力)
            txtSearch.Text = "";
            cmbCategory.SelectedIndex = 0;
            cmbInputFilter.SelectedIndex = 0;
            ApplyFilter();
        }
        dgActions.SelectedItem = target;
        dgActions.ScrollIntoView(target);
        txtStatus.Text = $"{_baseStatus}  |  {label}: {matches.Count} 件中 {_pressMatchIndex + 1} 件目 ({target.DisplayName})";
    }

    private void ShowFuncJsChoice(GameControllerInputEventArgs e)
    {
        _funcJsChoicePress = e;
        btnFuncJs1.Content = $"js1 (HOTAS {_data.GetJsSideLabel(1)})";
        btnFuncJs2.Content = $"js2 (HOTAS {_data.GetJsSideLabel(2)})";
        pnlFuncJsChoice.Visibility = Visibility.Visible;
    }

    private void HideFuncJsChoice()
    {
        _funcJsChoicePress = null;
        pnlFuncJsChoice.Visibility = Visibility.Collapsed;
    }

    private void FuncJs1_Click(object sender, RoutedEventArgs e) => ChooseFuncJs(1);
    private void FuncJs2_Click(object sender, RoutedEventArgs e) => ChooseFuncJs(2);

    // 選んだ js 番号を覚えて (保存は ManualJoystickInstancesChanged で行う)、同じ押下で移り直す
    private void ChooseFuncJs(int js)
    {
        if (_funcJsChoicePress is not { } press) return;
        _data.SetManualJsForDevice(press.VendorId, press.ProductId, press.DeviceId, js);
        HandleFunctionTabPress(press);
    }

    // 右の表で選んだ機能のカテゴリを、左のカテゴリの一覧で水色にして見える所へ出す (カテゴリの選択・絞り込みは変えない。契約タブと同じ作り)
    private CategoryItem? _highlightedCategory;
    private void DgActions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CategoryItem? item = null;
        if (dgActions.SelectedItem is ActionBinding a && lstCategories.ItemsSource is IEnumerable<CategoryItem> items)
            item = items.FirstOrDefault(i => i.Name == a.CategoryName);
        if (!ReferenceEquals(_highlightedCategory, item) && _highlightedCategory != null)
            _highlightedCategory.IsHighlighted = false;
        _highlightedCategory = item;
        if (item == null) return;
        item.IsHighlighted = true;
        lstCategories.ScrollIntoView(item);
    }

    // ===== HOTAS の左右の選択 (機器の定義の指紋ごと) =====
    //   「js1 = 左手・js2 = 右手」か「js1 = 右手・js2 = 左手」かを、利用者が選ぶ (機器の名前からは決めない。actionmaps.xml は変えない)。
    //   保存フォルダの中には置かない (フォルダ内の全ファイルがゲームへコピーされるため。また「ゲームから保存」の上書きはフォルダを消さずに写すので古い記録が残る)。
    //   作業フォルダ直下の JSON に {機器の定義の指紋 ("fp:..."、ActionMapData.FileJoystickFingerprint): "js1-left" か "js1-right"} で覚える。
    //   値は、その指紋の actionmaps.xml の js 番号での向き (まだ保存していない js1/js2 の入れ替えがあれば戻した向き) で書く。
    //   指紋が同じファイルなら、コピー・取込・同じ名前での作り直しでも同じ記録が効き、上書きで機器の定義が変われば古い記録は効かない。
    //   前の版の「表示の入れ替え」の記録 (keybind_hotas_display_swap.json) は、物理スティックと js 番号の対応を入れ替えるもので意味が違うので読まない
    private static string StickSideSettingsPath =>
        Path.Combine(App.Config.WorkingDirectory ?? "", "keybind_hotas_stick_side.json");

    private const string StickSideJs1Left = "js1-left";
    private const string StickSideJs1Right = "js1-right";

    // ===== 作業フォルダ直下の記録の JSON (HOTAS の左右・手で選んだ js 番号・元の js1 の機器) の読み込み =====
    //   無ければ空。壊れている (JSON として読めない・中身が null) ときは、黙って上書きしないよう
    //   「<名前>.broken-<日時>」に名前を変えて残してから空として扱い、利用者に 1 回だけ知らせる (AppConfigLoader と同じ作り)。
    //   別名にできない (別のプロセスが開いている等) ときは控えを複製する。控えも作れないときは、この起動中はそのファイルに書かない。
    //   ファイル自体を読めない (別のプロセスが開いている等) ときも空として扱い、そのときは書かない。
    //   戻り値 false = このファイルに書いてはいけない
    private static readonly HashSet<string> SettingsNoticeShown = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SettingsWriteDisabled = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SettingsBrokenBackedUp = new(StringComparer.OrdinalIgnoreCase);   // 控えを複製したが元が残っているもの

    private static bool TryLoadSettingsJson<T>(string path, string label, out T value) where T : class, new()
    {
        value = new T();
        if (SettingsWriteDisabled.Contains(path)) return false;
        if (!File.Exists(path)) return true;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifySettingsOnce(path, $"{label}の記録を読めませんでした。今回は書き込みません:\n{path}\n\n{ex.Message}");
            return false;
        }

        try
        {
            var v = System.Text.Json.JsonSerializer.Deserialize<T>(text);
            if (v != null)
            {
                value = v;
                SettingsBrokenBackedUp.Remove(path);   // 書き直されて読めるようになった (また壊れたら控えを作る)
                return true;
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or ArgumentException) { }

        // 壊れている。控えを複製済みで元が残っているだけなら、もう一度は控えを作らない
        if (SettingsBrokenBackedUp.Contains(path)) return true;

        var now = DateTime.Now;
        var backup = path + ".broken-" + now.ToString("yyyyMMdd-HHmmss");
        for (int n = 2; (File.Exists(backup) || Directory.Exists(backup)) && n < 1000; n++)
            backup = path + ".broken-" + now.ToString("yyyyMMdd-HHmmss") + "-" + n;
        try
        {
            File.Move(path, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Copy(path, backup, overwrite: false);
                SettingsBrokenBackedUp.Add(path);
            }
            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException)
            {
                SettingsWriteDisabled.Add(path);
                NotifySettingsOnce(path, $"{label}の記録が壊れていました。元のファイルを別名にできなかったので、この起動中は書き込みません:\n{path}");
                return false;
            }
        }
        NotifySettingsOnce(path, $"{label}の記録が壊れていたので、新しく作り直します。元のファイルは次の場所に残しています:\n{backup}");
        return true;
    }

    // 記録を書けなかったことを、保存のたびにその場で知らせる (読み込みのときの 1 回だけの知らせとは別)。
    //   effect: 何の記録を保存できなかったかと、エディターを開き直すとどうなるか
    private const string SettingsNotWritableReason = "記録のファイルが壊れていて元のファイルの控えを作れないか、ファイルを読めないため、上書きしませんでした。";
    private const string StickSideEffect = "HOTAS の左右の選択を保存できませんでした。\nエディターを開き直すと、今回選んだ左右は使われません (左右をもう一度聞きます)。";
    private const string DeviceJsEffect = "手で選んだ js 番号を保存できませんでした。\nエディターを開き直すと、今回選んだ js 番号は使われません。";

    private static void NotifySettingsWriteFailed(string effect, string path, string reason) =>
        MessageBox.Show($"{effect}\n\n{reason}\n\n記録のファイル:\n{path}", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void NotifySettingsOnce(string path, string message)
    {
        if (!SettingsNoticeShown.Add(path)) return;
        MessageBox.Show(message, "設定ファイル", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private const string FingerprintKeyPrefix = "fp:";

    private static Dictionary<string, string> LoadStickSideSettings(out bool canWrite)
    {
        canWrite = TryLoadSettingsJson<Dictionary<string, string>>(StickSideSettingsPath, "HOTAS の左右", out var settings);
        return settings;
    }

    // 指紋の記録: true = js1 が左手、false = js1 が右手、null = 選んでいない (記録が無い・読めない値)
    private static bool? LoadStickSideSetting(string fingerprint)
    {
        try
        {
            if (!LoadStickSideSettings(out _).TryGetValue(fingerprint, out var v)) return null;
            return v switch
            {
                StickSideJs1Left => true,
                StickSideJs1Right => false,
                _ => null,
            };
        }
        catch { return null; }
    }

    private static void SaveStickSideSetting(string fingerprint, bool js1IsLeft)
    {
        try
        {
            var settings = LoadStickSideSettings(out var canWrite);
            if (!canWrite)
            {
                // 壊れた元のファイルを控え無しで上書きしない・読めなかったファイルに書かない。書けなかったことは保存のたびに知らせる
                NotifySettingsWriteFailed(StickSideEffect, StickSideSettingsPath, SettingsNotWritableReason);
                return;
            }
            settings[fingerprint] = js1IsLeft ? StickSideJs1Left : StickSideJs1Right;
            File.WriteAllText(StickSideSettingsPath,
                System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            NotifySettingsWriteFailed(StickSideEffect, StickSideSettingsPath, ex.Message);
        }
    }

    // 今の左右の選択を、今のファイルの指紋の記録として書く (まだ保存していない js1/js2 の入れ替えがあれば、向きを戻した値で書く)
    private void SaveCurrentStickSides()
    {
        _data.StickSidesChosen = true;
        var js1IsLeftInFile = _data.JoystickInstancesSwapPending ? !_data.Js1IsLeft : _data.Js1IsLeft;
        SaveStickSideSetting(_data.FileJoystickFingerprint, js1IsLeftInFile);
    }

    // HOTAS の左右をまだ選んでいない機器の定義で、options の joystick が 2 本以上あれば、左右を聞く (StickSideDialog)。
    //   ［OK］なら覚えて、変え方を知らせる。［中止］・窓を閉じたときは覚えず、今回は今の既定 (js1 = 左手・js2 = 右手) で表示する (次に開いたときにまた聞く)
    private bool _stickSidesAsked;
    private void AskStickSidesIfNeeded()
    {
        if (_stickSidesAsked) return;
        _stickSidesAsked = true;
        if (_data.StickSidesChosen || _data.JoystickOptions.Count < 2) return;
        RunStickSideDialog(showHowToChange: true);
    }

    // 「HOTAS の左右」の窓を開く。［OK］なら選択を覚えて表を出し直す (showHowToChange なら変え方も知らせる)。［中止］・閉じたときは何も変えない
    private void RunStickSideDialog(bool showHowToChange)
    {
        var dlg = new StickSideDialog(_data) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Js1IsLeft is not bool js1IsLeft) return;
        _data.Js1IsLeft = js1IsLeft;
        SaveCurrentStickSides();
        dgActions.Items.Refresh();
        ApplyFilter();
        hotasPanel.UpdateBindingTables();
        if (showHowToChange)
            MessageBox.Show("あとで［HOTAS］タブの［左右を設定…］（ボタンを押して設定し直す）か［表示の入れ替え］（左右を反転する）で変えられます。", "HOTAS の左右", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ===== 「js1/js2 の入れ替え」の判定: 元の js1 の機器の記録 (保存フォルダによらず 1 つ) =====
    //   作業フォルダ直下の JSON に {"OriginalJs1Device": "<機器の見分け>", "OriginalJs1Name": "<機器の名前>"} で覚える
    //   (壊れたときの扱い・書けなかったときの知らせは他の記録と同じ)。
    //   機器の見分けは options の Product から決める (JoystickDeviceOption.DeviceIdentity。VID:PID、無ければ GUID)。
    //   js1/js2 の入れ替えを保存したときに、記録が無ければ、入れ替える前の js1 の機器を記録する (記録が既にあれば変えない)。
    //   開いたとき: その actionmaps.xml の options で、js2 (instance 2) が元の js1 の機器で、js1 (instance 1) がそうでなければ
    //   入れ替え済みとして、既定の割り当てと既定のモードを左右入れ替えて使う (ActionMapData.SwapJoystickDefaultsForSavedSwap)。
    //   決められないとき (記録が無い、options に機器が書いていない、どちらにも当たらない、両方に当たる) は入れ替えていないものとして扱う。
    //   記録のファイルが無い・壊れていて、壊れたときの控え (「<名前>.broken-*」) が残っているときは、逆向きに記録しないよう新しくは記録しない。
    //   HOTAS タブに、記録している機器と判定を出し、［記録を消す］で消せる (消すと中身の無い記録のファイルを書くので、次に入れ替えを保存したときに記録し直す)
    private sealed class OriginalJs1Record
    {
        public string? OriginalJs1Device { get; set; }
        public string? OriginalJs1Name { get; set; }
    }

    private enum SavedSwapJudgement { NoRecord, NotSwapped, Swapped, Undetermined }

    private const string OriginalJs1RecordFileName = "keybind_hotas_original_js1.json";

    private static string OriginalJs1RecordPath =>
        Path.Combine(App.Config.WorkingDirectory ?? "", OriginalJs1RecordFileName);

    private const string OriginalJs1Effect = "js1/js2 の入れ替えの記録 (元の js1 の機器) を保存できませんでした。\nエディターを開き直すと、既定の割り当てと既定のモードが入れ替わらずに使われます (［既定値に戻す］と変更済みの表示も入れ替わらない既定に合わせて動きます)。";

    // メモリ上の既定の割り当て・既定のモードに、「入れ替え済みの保存データ」としての左右の入れ替えが入っているか
    //   (読み込みで入れ替え済みと判定したら true。入れ替えを保存するたびに反転する。［記録を消す］で入っていれば戻して false)
    private bool _defaultsSwappedForSavedSwap;

    // メモリ上の options の instance 1 / 2 の機器 (保存待ちの js1/js2 の入れ替えを含む。読み込み直後と保存の直後はファイルと同じ)
    private (string? Id1, string? Id2) CurrentInstanceIdentities() =>
        (_data.JoystickOptions.FirstOrDefault(o => o.Instance == 1)?.DeviceIdentity,
         _data.JoystickOptions.FirstOrDefault(o => o.Instance == 2)?.DeviceIdentity);

    private static SavedSwapJudgement Judge(OriginalJs1Record rec, string? id1, string? id2)
    {
        var original = rec.OriginalJs1Device;
        if (string.IsNullOrEmpty(original)) return SavedSwapJudgement.NoRecord;
        bool is1 = string.Equals(id1, original, StringComparison.OrdinalIgnoreCase);
        bool is2 = string.Equals(id2, original, StringComparison.OrdinalIgnoreCase);
        if (is1 && !is2) return SavedSwapJudgement.NotSwapped;
        if (is2 && !is1) return SavedSwapJudgement.Swapped;
        return SavedSwapJudgement.Undetermined;
    }

    // 決められないときの理由 (表示・知らせの文)
    private static string UndeterminedReason(string? id1, string? id2) =>
        $"{UndeterminedCause(id1, id2)}ので、入れ替えていないものとして扱います";

    // 決められない原因 (「…ので」の前の部分)
    private static string UndeterminedCause(string? id1, string? id2)
    {
        if (string.IsNullOrEmpty(id1) && string.IsNullOrEmpty(id2))
            return "この保存データには機器の定義が無い";
        if (string.Equals(id1, id2, StringComparison.OrdinalIgnoreCase))
            return "この保存データの js1 と js2 が同じ機器 (同じ VID/PID) で見分けられない";
        return "この保存データの js1 / js2 に記録の機器が無い";
    }

    // ［記録を消す］の後に入れ替えを保存すれば記録し直せるか (RecordOriginalJs1AfterSwapSave が記録できる形か: js2 に機器があり、js1 と違う)
    private static bool CanRecordAfterClear(string? id1, string? id2) =>
        !string.IsNullOrEmpty(id2) && !string.Equals(id1, id2, StringComparison.OrdinalIgnoreCase);

    private static OriginalJs1Record LoadOriginalJs1Record()
    {
        try
        {
            TryLoadSettingsJson<OriginalJs1Record>(OriginalJs1RecordPath, "元の js1 の機器", out var rec);
            return rec;
        }
        catch { return new OriginalJs1Record(); }
    }

    // 開いた保存データが、元の js1 の機器の記録から見て入れ替え済みか
    private bool IsSavedSwapByOriginalJs1()
    {
        var (id1, id2) = CurrentInstanceIdentities();
        return Judge(LoadOriginalJs1Record(), id1, id2) == SavedSwapJudgement.Swapped;
    }

    // HOTAS タブに、記録している元の js1 の機器と、今のメモリの状態 (保存待ちの js1/js2 の入れ替えを含む) での判定を出す
    private void UpdateOriginalJs1Status(string? note = null)
    {
        var rec = LoadOriginalJs1Record();
        var (id1, id2) = CurrentInstanceIdentities();
        var judgement = Judge(rec, id1, id2);
        var pending = _data.JoystickInstancesSwapPending;
        string text;
        if (judgement == SavedSwapJudgement.NoRecord)
        {
            text = pending
                ? "元の js1 の機器: 記録なし (保存すると、入れ替える前の js1 の機器を記録します)"
                : "元の js1 の機器: 記録なし (js1/js2 の入れ替えを保存したときに記録します)";
        }
        else
        {
            var name = string.IsNullOrEmpty(rec.OriginalJs1Name) ? "(名前なし)" : rec.OriginalJs1Name;
            var device = $"元の js1 の機器: {name} ({rec.OriginalJs1Device})";
            // 今のメモリの既定に左右の入れ替えが入っているか (開いたときの判定による入れ替えと、保存待ちの入れ替え)
            var memoryDefaultsSwapped = _defaultsSwappedForSavedSwap ^ pending;
            if (judgement == SavedSwapJudgement.Undetermined && memoryDefaultsSwapped)
            {
                // 決められないが、今は入れ替えた既定を使っている (保存待ちの入れ替えなど)。保存して開き直すと既定は入れ替わらない
                text = $"{device} — いまは js1/js2 の入れ替え{(pending ? " (保存待ち)" : "")}に合わせて既定を左右入れ替えて使っていますが、" +
                       $"{UndeterminedCause(id1, id2)}ので、保存して開き直すと既定は入れ替わりません";
                if (CanRecordAfterClear(id1, id2))
                    text += "。［記録を消す］を押してから保存すると、この保存データの機器で記録し直します";
            }
            else
            {
                text = judgement switch
                {
                    SavedSwapJudgement.Swapped => $"{device} — この保存データは js2 がこの機器なので、入れ替え済みとして既定を左右入れ替えて使います",
                    SavedSwapJudgement.NotSwapped => $"{device} — この保存データは js1 がこの機器なので、入れ替えていないものとして扱います",
                    _ => $"{device} — {UndeterminedReason(id1, id2)}",
                };
                // 保存待ちの入れ替えがあるときは、その旨を付け足す (判定は入れ替えた後のもの)
                if (pending) text += " (js1/js2 の入れ替えは保存待ち。この判定は保存した後のもの)";
            }
        }
        if (!string.IsNullOrEmpty(note)) text += $" ({note})";
        hotasPanel.ShowOriginalJs1Status(text, canClear: judgement != SavedSwapJudgement.NoRecord);
    }

    // ［記録を消す］: 確かめてから、中身の無い記録を書く。メモリ上の既定に「入れ替え済み」としての入れ替えが入っていれば戻す
    //   (記録が無いときは入れ替えていないものとして扱うので、開き直したときと同じにする)
    private void ClearOriginalJs1Record()
    {
        var rec = LoadOriginalJs1Record();
        if (string.IsNullOrEmpty(rec.OriginalJs1Device)) { UpdateOriginalJs1Status(); return; }
        var name = string.IsNullOrEmpty(rec.OriginalJs1Name) ? "(名前なし)" : rec.OriginalJs1Name;
        var msg = $"元の js1 の機器の記録 ({name} ({rec.OriginalJs1Device})) を消しますか？\n\n" +
                  "消すと、どの保存データも入れ替えていないものとして扱い、次に js1/js2 の入れ替えを保存したときに記録し直します。";
        if (MessageBox.Show(msg, "元の js1 の機器の記録", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var path = OriginalJs1RecordPath;
        try
        {
            var canWrite = TryLoadSettingsJson<OriginalJs1Record>(path, "元の js1 の機器", out _);
            if (!canWrite)
            {
                NotifySettingsWriteFailed("元の js1 の機器の記録を消せませんでした。", path, SettingsNotWritableReason);
                return;
            }
            File.WriteAllText(path,
                System.Text.Json.JsonSerializer.Serialize(new OriginalJs1Record(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            NotifySettingsWriteFailed("元の js1 の機器の記録を消せませんでした。", path, ex.Message);
            return;
        }

        if (_defaultsSwappedForSavedSwap)
        {
            _data.SwapJoystickDefaultsForSavedSwap();   // 2 回目で元に戻る
            _defaultsSwappedForSavedSwap = false;
            dgActions.Items.Refresh();
            ApplyFilter();
            hotasPanel.UpdateBindingTables();
        }
        UpdateOriginalJs1Status("記録を消しました");
    }

    // js1/js2 の入れ替えを保存した後に呼ぶ: 記録が無ければ、入れ替える前の js1 の機器を記録する。
    //   記録できない・記録と合わないために、開き直すと既定が入れ替わらないときは、その場で知らせる
    private void RecordOriginalJs1AfterSwapSave()
    {
        var path = OriginalJs1RecordPath;
        try
        {
            // 記録のファイルが今あって読めるか (壊れていて控えに移されたもの・控えを複製しただけで壊れたまま残っているものは数えない)
            var canWrite = TryLoadSettingsJson<OriginalJs1Record>(path, "元の js1 の機器", out var rec);
            bool recordFileValid = File.Exists(path) && !SettingsBrokenBackedUp.Contains(path);
            if (!canWrite)
            {
                NotifySettingsWriteFailed(OriginalJs1Effect, path, SettingsNotWritableReason);
                return;
            }

            // 保存したファイルの instance 1 / 2 の機器 (メモリ上の options は入れ替え後の番号。保存でファイルも同じになった)
            var (now1, now2) = CurrentInstanceIdentities();

            if (!string.IsNullOrEmpty(rec.OriginalJs1Device))
            {
                // 記録は変えない。この保存データの機器に記録の機器が無ければ (別の機器の組)、開き直しても入れ替え済みと分からない
                if (Judge(rec, now1, now2) == SavedSwapJudgement.Undetermined)
                    NotifySettingsWriteFailed(OriginalJs1Effect, path,
                        $"記録している元の js1 の機器 ({rec.OriginalJs1Device}) から、入れ替え済みかを決められません: {UndeterminedReason(now1, now2)}。");
                return;
            }

            // 記録のファイルが無い・壊れていて、壊れたときの控えが残っていれば、元の向きが分からないので新しくは記録しない (逆向きに記録しないため)。
            //   ［記録を消す］で書いた中身の無い記録があるときは、利用者が消したので記録し直す
            var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            var broken = Directory.Exists(dir) ? Directory.GetFiles(dir, OriginalJs1RecordFileName + ".broken-*") : Array.Empty<string>();
            if (!recordFileValid && broken.Length > 0)
            {
                NotifySettingsWriteFailed(OriginalJs1Effect, path,
                    $"記録のファイルが壊れていたことがあり (控え: {broken[0]})、元の js1 の機器が分からないため、逆向きに記録しないよう新しくは記録しません。控えを直して元の名前に戻すか、控えを消すと、次に入れ替えを保存したときに記録します。");
                return;
            }

            // 入れ替える前の js1 = 保存したファイルの instance 2 の機器
            if (string.IsNullOrEmpty(now2) || string.Equals(now1, now2, StringComparison.OrdinalIgnoreCase))
            {
                NotifySettingsWriteFailed(OriginalJs1Effect, path,
                    "この保存データの options に js1 / js2 の機器 (Product) が書いていないか、同じ機器が 2 台のため、元の js1 の機器を見分けられません。");
                return;
            }
            rec.OriginalJs1Device = now2;
            rec.OriginalJs1Name = _data.JoystickOptions.FirstOrDefault(o => o.Instance == 2)?.Name;
            File.WriteAllText(path,
                System.Text.Json.JsonSerializer.Serialize(rec, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            NotifySettingsWriteFailed(OriginalJs1Effect, path, ex.Message);
        }
    }

    // ===== 手で選んだ js 番号 (「この機器は js◯」。機器の定義の指紋ごと) =====
    //   表示の入れ替えの設定と同じく、保存フォルダの中には置かず、作業フォルダ直下の JSON に
    //   {機器の定義の指紋: {機器のキー: その指紋の actionmaps.xml での instance (1/2)}} で覚える。
    //   指紋で見分けがつかない (指紋が同じなのに本当は違う向きの) ファイルでは、押したときに出る js 番号の選択 (手で選んだ番号のときも出す) で選び直せる。
    //   古い形 (保存フォルダのパスの鍵) は今回の版で足したもので公開していないので、移し替えずに使わない (書くときに取り除く)。
    //   メモリ上の instance は「js1/js2 の入れ替え」で 1↔2 が替わるので、まだ保存していない入れ替えがあれば戻した値 (ファイルの値) で書く
    private static string DeviceJsSettingsPath =>
        Path.Combine(App.Config.WorkingDirectory ?? "", "keybind_hotas_device_js.json");

    private static Dictionary<string, Dictionary<string, int>> LoadDeviceJsSettings(out bool canWrite)
    {
        canWrite = TryLoadSettingsJson<Dictionary<string, Dictionary<string, int>>>(DeviceJsSettingsPath, "手で選んだ js 番号", out var settings);
        return settings;
    }

    // 指紋の記録をメモリ (_data.ManualJoystickInstances) に入れる (読み込み直後。まだ入れ替えは無い)
    private void LoadDeviceJsSetting()
    {
        try
        {
            if (!LoadDeviceJsSettings(out _).TryGetValue(_data.FileJoystickFingerprint, out var map) || map == null) return;
            foreach (var (deviceKey, instance) in map)
                if (instance == 1 || instance == 2) _data.ManualJoystickInstances[deviceKey] = instance;
        }
        catch { }
    }

    // メモリ上の手で選んだ js 番号を、今のファイルの指紋の記録として書く (無ければ記録を消す)。
    //   まだ保存していない js1/js2 の入れ替えがあれば、番号を戻した値 (ファイルの値) で書く
    private void SaveDeviceJsSetting()
    {
        try
        {
            var settings = LoadDeviceJsSettings(out var canWrite);
            if (!canWrite)
            {
                NotifySettingsWriteFailed(DeviceJsEffect, DeviceJsSettingsPath, SettingsNotWritableReason);
                return;
            }
            // 古い形 (保存フォルダのパスの鍵) は使わないので取り除く
            bool removedLegacy = false;
            foreach (var k in settings.Keys.Where(k => !k.StartsWith(FingerprintKeyPrefix, StringComparison.Ordinal)).ToList())
                removedLegacy |= settings.Remove(k);
            var key = _data.FileJoystickFingerprint;
            var pending = _data.JoystickInstancesSwapPending;
            var map = _data.ManualJoystickInstances
                .Where(kv => kv.Value == 1 || kv.Value == 2)
                .ToDictionary(kv => kv.Key, kv => pending ? 3 - kv.Value : kv.Value);
            if (map.Count == 0)
            {
                if (!settings.Remove(key) && !removedLegacy) return;   // 記録が無く、書くものも無ければファイルに触れない
            }
            else
            {
                settings[key] = map;
            }
            File.WriteAllText(DeviceJsSettingsPath,
                System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            NotifySettingsWriteFailed(DeviceJsEffect, DeviceJsSettingsPath, ex.Message);
        }
    }

    // js1/js2 の入れ替えを actionmaps.xml に保存した後に呼ぶ: ファイルの instance が入れ替わって指紋が変わったので、
    //   機器の定義を入れ替え、HOTAS の左右の選択と手で選んだ js 番号の記録を新しい指紋のほうにも書く
    //   (どちらも、保存で番号も入れ替わったメモリの値のまま書く。左右は選んでいるときだけ)。入れ替えていない元の指紋の記録はそのまま残す
    private void MoveFingerprintRecordsAfterSwapSave()
    {
        _data.SwapJoystickDefinitionsAfterSave();
        if (_data.StickSidesChosen) SaveStickSideSetting(_data.FileJoystickFingerprint, _data.Js1IsLeft);
        SaveDeviceJsSetting();
    }

    private void LoadData()
    {
        try
        {
            _data = ActionMapParser.LoadFromGameAndSave(_gamePath, _savePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"キーバインドデータの読み込みに失敗しました:\n{ex.Message}\n\n{ex.StackTrace}", "エラー");
            _data = new ActionMapData();
        }

        // HOTAS の左右: 選んだ記録があればそれ、無ければ js1 = 左手・js2 = 右手 (画面が出た後に聞く。AskStickSidesIfNeeded)
        var stickSide = LoadStickSideSetting(_data.FileJoystickFingerprint);
        _data.Js1IsLeft = stickSide ?? true;
        _data.StickSidesChosen = stickSide.HasValue;
        // 元の js1 の機器の記録から見て入れ替え済みの保存データなら、既定の割り当て・既定のモードを左右入れ替えて使う
        _defaultsSwappedForSavedSwap = IsSavedSwapByOriginalJs1();
        if (_defaultsSwappedForSavedSwap) _data.SwapJoystickDefaultsForSavedSwap();
        LoadDeviceJsSetting();
        _data.ManualJoystickInstancesChanged += SaveDeviceJsSetting;

        _allActions = _data.Categories.SelectMany(c => c.Actions).ToList();

        ChatService.SetKeybindData(_data);

        if (_allActions.Count == 0)
        {
            MessageBox.Show(
                "キーバインドデータが見つかりませんでした。\n\n" +
                "先に翻訳タブの［新しいパッチを読み取る］を実行して Data.p4k からデータを抽出してください。\n" +
                "または、ゲームパスが正しいか確認してください。",
                "データなし", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        cmbCategory.Items.Clear();
        cmbCategory.Items.Add(new CategoryItem { Name = "", DisplayName = "全カテゴリ", ActionCount = $"({_allActions.Count})" });
        foreach (var cat in _data.Categories.OrderBy(c => c.Name))
            cmbCategory.Items.Add(new CategoryItem
            {
                Name = cat.Name,
                DisplayName = cat.DisplayName,
                ActionCount = $"({cat.Actions.Count})"
            });
        cmbCategory.SelectedIndex = 0;

        lstCategories.ItemsSource = _data.Categories
            .OrderBy(c => c.Name)
            .Select(c => new CategoryItem
            {
                Name = c.Name,
                DisplayName = c.DisplayName,
                ActionCount = $"({c.Actions.Count})"
            }).ToList();

        ApplyFilter();

        Action onChanged = () =>
        {
            dgActions.Items.Refresh();
            ApplyFilter();
        };

        keyboardPanel.SetData(_data, onChanged);
        mousePanel.SetData(_data, onChanged);
        gamepadPanel.SetData(_data, onChanged);
        hotasPanel.SetData(_data, onChanged);
        UpdateOriginalJs1Status();   // 記録している元の js1 の機器と、この保存データの判定を HOTAS タブに出す
    }

    private void TabMain_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != tabMain) return;
        switch (tabMain.SelectedIndex)
        {
            case 1: keyboardPanel.UpdateKeyColors(); break;
            case 2: mousePanel.UpdateColors(); break;
            case 3: gamepadPanel.UpdateColors(); break;
            case 4: hotasPanel.UpdateBindingTables(); break;
        }
    }

    private void ApplyFilter()
    {
        if (dgActions == null || txtStatus == null) return;

        var search = txtSearch?.Text?.Trim() ?? "";
        var catItem = cmbCategory?.SelectedItem as CategoryItem;
        var catFilter = catItem?.Name ?? "";
        var inputFilter = (cmbInputFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全入力";

        _filteredActions = _allActions;

        if (!string.IsNullOrEmpty(catFilter))
            _filteredActions = _filteredActions.Where(a => a.CategoryName == catFilter).ToList();

        if (!string.IsNullOrEmpty(search))
            _filteredActions = _filteredActions.Where(a =>
                a.ActionName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.CategoryName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.KeyboardDisplayJa.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.GamepadDisplayJa.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.HotasDisplay.Contains(search, StringComparison.OrdinalIgnoreCase)
            ).ToList();

        _filteredActions = inputFilter switch
        {
            "キーボード" => _filteredActions.Where(a => InputCodeHelper.IsAssigned(a.Keyboard)).ToList(),   // 割り当て解除は数えない
            "マウス" => _filteredActions.Where(a => InputCodeHelper.IsAssigned(a.Mouse)).ToList(),
            "ゲームパッド" => _filteredActions.Where(a => InputCodeHelper.IsAssigned(a.Gamepad)).ToList(),
            "HOTAS" => _filteredActions.Where(a => InputCodeHelper.IsAssigned(a.Joystick1) || InputCodeHelper.IsAssigned(a.Joystick2)).ToList(),   // 割り当て解除は数えない
            "未割当のみ" => _filteredActions.Where(a => !a.HasAnyBinding).ToList(),
            "変更済みのみ" => _filteredActions.Where(a => a.IsModified).ToList(),
            _ => _filteredActions
        };

        dgActions.ItemsSource = _filteredActions;

        var modified = _allActions.Count(a => a.IsModified);
        var unbound = _allActions.Count(a => !a.HasAnyBinding);
        _baseStatus = $"全{_allActions.Count}件 | 表示{_filteredActions.Count}件 | 変更{modified}件 | 未割当{unbound}件";
        txtStatus.Text = _baseStatus;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Category_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void InputFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void CategoryList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (lstCategories.SelectedItem is not CategoryItem item) return;
        for (int i = 0; i < cmbCategory.Items.Count; i++)
        {
            if (cmbCategory.Items[i] is CategoryItem ci && ci.Name == item.Name)
            {
                cmbCategory.SelectedIndex = i;
                break;
            }
        }
    }

    private void DgActions_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (dgActions.SelectedItem is not ActionBinding binding) return;

        var dlg = new BindingEditDialog(binding) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            dgActions.Items.Refresh();
            ApplyFilter();
        }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        var doc = new FlowDocument
        {
            PagePadding = new Thickness(40),
            ColumnWidth = double.MaxValue,
            FontFamily = new FontFamily("Meiryo UI"),
            FontSize = 10
        };

        doc.Blocks.Add(new Paragraph(new Run("Star Citizen キーバインド一覧"))
        {
            FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10)
        });

        var table = new Table { CellSpacing = 0 };
        table.Columns.Add(new TableColumn { Width = new GridLength(160) });
        table.Columns.Add(new TableColumn { Width = new GridLength(110) });
        table.Columns.Add(new TableColumn { Width = new GridLength(80) });
        table.Columns.Add(new TableColumn { Width = new GridLength(110) });
        table.Columns.Add(new TableColumn { Width = new GridLength(150) });

        var headerGroup = new TableRowGroup();
        var headerRow = new TableRow { Background = Brushes.LightGray };
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("機能")) { FontWeight = FontWeights.Bold }));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("キーボード")) { FontWeight = FontWeights.Bold }));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("マウス")) { FontWeight = FontWeights.Bold }));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("ゲームパッド")) { FontWeight = FontWeights.Bold }));
        headerRow.Cells.Add(new TableCell(new Paragraph(new Run("HOTAS")) { FontWeight = FontWeights.Bold }));
        headerGroup.Rows.Add(headerRow);
        table.RowGroups.Add(headerGroup);

        string? currentCat = null;
        var bodyGroup = new TableRowGroup();
        foreach (var action in _filteredActions)
        {
            if (action.CategoryName != currentCat)
            {
                currentCat = action.CategoryName;
                var catRow = new TableRow { Background = new SolidColorBrush(Color.FromRgb(230, 240, 250)) };
                var catCell = new TableCell(new Paragraph(new Run(
                    ActionMapNames.GetCategoryName(currentCat))) { FontWeight = FontWeights.Bold })
                { ColumnSpan = 5 };
                catRow.Cells.Add(catCell);
                bodyGroup.Rows.Add(catRow);
            }

            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.DisplayName))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.KeyboardDisplayJa))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.MouseDisplayJa))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.GamepadDisplayJa))));
            row.Cells.Add(new TableCell(new Paragraph(new Run(action.HotasDisplay))));
            bodyGroup.Rows.Add(row);
        }
        table.RowGroups.Add(bodyGroup);
        doc.Blocks.Add(table);

        var paginator = ((IDocumentPaginatorSource)doc).DocumentPaginator;
        paginator.PageSize = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
        dlg.PrintDocument(paginator, "Star Citizen キーバインド");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "XML Files|*.xml|CSV Files|*.csv",
            FileName = "keybinds_export.xml"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            if (dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                using var writer = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true));
                // HOTAS 列は 右・左 の順。見出しに実際の js 番号を付ける (例 HotasR(js2),HotasL(js1))
                int leftJs = _data.LeftJs, rightJs = 3 - leftJs;
                writer.WriteLine($"Category,Action,Keyboard,Mouse,Gamepad,HotasR(js{rightJs}),HotasL(js{leftJs})");
                foreach (var a in _filteredActions)
                {
                    var r = rightJs == 1 ? a.Joystick1Display : a.Joystick2Display;
                    var l = leftJs == 1 ? a.Joystick1Display : a.Joystick2Display;
                    writer.WriteLine($"{Csv(a.CategoryName)},{Csv(a.ActionName)},{Csv(a.KeyboardDisplay)},{Csv(a.MouseDisplay)},{Csv(a.GamepadDisplay)},{Csv(r)},{Csv(l)}");
                }
            }
            else
            {
                ActionMapParser.SaveUserOverrides(_data, dlg.FileName);
            }
            MessageBox.Show($"エクスポート完了: {dlg.FileName}", "完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "XML Files|*.xml",
            Title = "キーバインドXMLをインポート"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var imported = ActionMapParser.LoadFromFile(dlg.FileName);
            foreach (var (key, binding) in imported.AllBindings)
            {
                if (_data.AllBindings.TryGetValue(key, out var existing))
                {
                    if (!string.IsNullOrEmpty(binding.Keyboard)) existing.Keyboard = binding.Keyboard;
                    if (!string.IsNullOrEmpty(binding.Mouse)) existing.Mouse = binding.Mouse;
                    if (!string.IsNullOrEmpty(binding.Gamepad)) existing.Gamepad = binding.Gamepad;
                    if (!string.IsNullOrEmpty(binding.Joystick1)) existing.Joystick1 = binding.Joystick1;
                    if (!string.IsNullOrEmpty(binding.Joystick2)) existing.Joystick2 = binding.Joystick2;
                }
            }
            dgActions.Items.Refresh();
            ApplyFilter();
            MessageBox.Show("インポート完了", "完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void SaveApply_Click(object sender, RoutedEventArgs e)
    {
        switch (AskSaveMode())
        {
            case "overwrite": SaveOverwrite(); break;
            case "new": SaveAsNew(); break;
        }
    }

    // 保存方法の選択: "overwrite" / "new" / null (キャンセル)
    private string? AskSaveMode()
    {
        string? result = null;
        var win = new Window
        {
            Title = "保存",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
        };
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = $"保存方法を選んでください。\n\n" +
                   $"上書き保存: 「{Path.GetFileName(_savePath)}」の actionmaps.xml の割り当てだけを書き換えます。\n" +
                   $"新規作成: 「{Path.GetFileName(_savePath)}」を複製した新しい保存データに書き込みます。",
            Margin = new Thickness(0, 0, 0, 12),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var btnOverwrite = new Button { Content = "上書き保存", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        btnOverwrite.Click += (_, _) => { result = "overwrite"; win.DialogResult = true; };
        var btnNew = new Button { Content = "新規作成", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        btnNew.Click += (_, _) => { result = "new"; win.DialogResult = true; };
        var btnCancel = new Button { Content = "キャンセル", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        buttons.Children.Add(btnOverwrite);
        buttons.Children.Add(btnNew);
        buttons.Children.Add(btnCancel);
        root.Children.Add(buttons);
        win.Content = root;
        win.ShowDialog();
        return result;
    }

    // 保存フォルダの actionmaps.xml に書く。既存ファイルがあれば rebind だけを書き換え、無ければ従来どおり新規に作る
    private string WriteActionMaps(string saveDir)
    {
        var outputPath = Path.Combine(saveDir, "Profiles", "default", "actionmaps.xml");
        if (File.Exists(outputPath))
        {
            ActionMapParser.SaveMergedOverrides(_data, outputPath, _data.JoystickInstancesSwapPending);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            ActionMapParser.SaveUserOverrides(_data, outputPath);
        }
        _data.JoystickInstancesSwapPending = false;
        return outputPath;
    }

    private void SaveOverwrite()
    {
        try
        {
            var swapPendingBefore = _data.JoystickInstancesSwapPending;
            var outputPath = WriteActionMaps(_savePath);
            // js1/js2 の入れ替えをファイルに書いたので、表示の入れ替え・手で選んだ js 番号の記録を新しい指紋のほうにも書き、
            //   記録が無ければ元の js1 の機器を記録する
            if (swapPendingBefore)
            {
                MoveFingerprintRecordsAfterSwapSave();
                _defaultsSwappedForSavedSwap = !_defaultsSwappedForSavedSwap;   // 入れ替えを保存したので、今の既定は保存データの向きに合う
                RecordOriginalJs1AfterSwapSave();
                UpdateOriginalJs1Status();
            }
            MessageBox.Show($"セーブに保存しました。\n{outputPath}\n\nゲームに反映するには「ゲームに反映」ボタンを使用してください。", "保存完了");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エラー: {ex.Message}", "エラー");
        }
    }

    private void SaveAsNew()
    {
        var dlg = new InputDialog("新しい保存データの名前を入力してください:") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var rawName = dlg.txtInput.Text ?? "";
        if (rawName.Any(char.IsWhiteSpace))   // 全角の空白も含む
        {
            MessageBox.Show("名前に空白 (全角を含む) は使えません。", "エラー");
            return;
        }
        var name = dlg.ResponseText;
        if (string.IsNullOrEmpty(name)) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..")
        {
            MessageBox.Show($"「{name}」はフォルダ名に使えません。", "エラー");
            return;
        }

        var parentDir = Path.GetDirectoryName(Path.GetFullPath(_savePath).TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(parentDir))
        {
            MessageBox.Show("保存先のフォルダを決められません。", "エラー");
            return;
        }
        var newPath = Path.Combine(parentDir, name);
        if (Directory.Exists(newPath) || File.Exists(newPath))
        {
            MessageBox.Show($"「{name}」は既にあります。別の名前を指定してください。", "エラー");
            return;
        }

        // 同じ親フォルダの下の一時的な名前のフォルダに複製と書き込みをしてから、Directory.Move で newPath へ移す。
        //   Move は移し先があると失敗するので、作る瞬間に別のプロセスが同じ名前を作っても、そのフォルダには触れない。
        //   失敗したときに消すのは、この処理で作った一時フォルダだけ
        string? tempPath = null;
        var swapPendingBefore = _data.JoystickInstancesSwapPending;   // WriteActionMaps が保存後に false にするので、失敗したら戻す
        bool moved = false;
        try
        {
            if (Directory.Exists(newPath) || File.Exists(newPath))
                throw new IOException($"「{name}」は既にあります。別の名前を指定してください。");

            string candidate;
            do candidate = Path.Combine(parentDir, Guid.NewGuid().ToString("N"));
            while (Directory.Exists(candidate) || File.Exists(candidate));
            Directory.CreateDirectory(candidate);
            tempPath = candidate;

            // 元の保存フォルダの全ファイルを複製してから、そこに上書き保存と同じ方式で書く
            if (Directory.Exists(_savePath))
                CopyDirectory(_savePath, tempPath);
            WriteActionMaps(tempPath);

            try
            {
                Directory.Move(tempPath, newPath);
            }
            catch (IOException) when (Directory.Exists(newPath) || File.Exists(newPath))
            {
                throw new IOException($"「{name}」は既にあります。別の名前を指定してください。");
            }
            moved = true;   // 保存できた (これより後は消さない)
            var outputPath = Path.Combine(newPath, "Profiles", "default", "actionmaps.xml");

            _savePath = newPath;
            Title = $"キーバインド エディタ — {Path.GetFileName(_savePath)}";
            // 複製したファイルは機器の定義が元と同じなので、同じ指紋の記録がそのまま効く。
            //   入れ替えを書いたときだけ指紋が変わるので、記録を新しい指紋のほうにも書く (元の保存データの記録はそのまま)
            if (swapPendingBefore)
            {
                MoveFingerprintRecordsAfterSwapSave();
                _defaultsSwappedForSavedSwap = !_defaultsSwappedForSavedSwap;   // 入れ替えを保存したので、今の既定は保存データの向きに合う
                RecordOriginalJs1AfterSwapSave();   // 記録が無ければ元の js1 の機器を記録する
            }
            UpdateOriginalJs1Status();   // 新しい保存データの判定を出す

            MessageBox.Show($"新しい保存データ「{name}」に保存しました。\n{outputPath}\n\nゲームに反映するには「ゲームに反映」ボタンを使用してください。", "保存完了");
        }
        catch (Exception ex)
        {
            // 作りかけの一時フォルダ (この処理で作ったものだけ) を消す。消せなければ場所を知らせる
            var msg = $"エラー: {ex.Message}";
            if (!moved)
            {
                _data.JoystickInstancesSwapPending = swapPendingBefore;
                if (tempPath != null && !TryDeleteCreatedDirectory(tempPath))
                    msg += $"\n\n作りかけのフォルダを削除できませんでした:\n{tempPath}";
            }
            MessageBox.Show(msg, "エラー");
        }
    }

    // SaveAsNew で新しく作ったフォルダを中身ごと消す (複製で読み取り専用のファイルが入ることがあるので、属性を外してから消す)
    private static bool TryDeleteCreatedDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return true;
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void CopyDirectory(string srcDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(srcDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(srcDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}

public class CategoryItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ActionCount { get; set; } = "";

    // 右の表で選んだ機能のカテゴリとして水色にするか (左のカテゴリの一覧)
    private bool _isHighlighted;
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (_isHighlighted == value) return;
            _isHighlighted = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsHighlighted)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

// 「HOTAS の左右」の窓: 左スティックのボタンを押してもらい、押されたスティックを左手として表示の左右を決める
//   (表示だけの選択。actionmaps.xml の js1・js2 は変えない)。窓の中では js1 = Stick1、js2 = Stick2 と呼ぶ。
//   押下の待ち受けはエディターと同じ GameControllerInput、js 番号は ActionMapData.TryResolveJsForDevice で決める。
//   決められない機器のボタンが押されたら「このスティックは: [Stick1] [Stick2]」で選べる (エディターの「この機器は js◯」と同じく覚える)
public sealed class StickSideDialog : Window
{
    private readonly ActionMapData _data;
    private GameControllerInput? _input;
    private int? _leftJs;                                   // 左手のスティックの js 番号 (最初のボタンが押されるまで null)
    private GameControllerInputEventArgs? _choicePress;     // js 番号を決められなかった押下
    private int? _lastPressedJs;                            // 最後に押されたスティック (枠を光らせる)

    private readonly TextBlock _txtConfirm;
    private readonly Grid _frames;
    private readonly StackPanel _pnlChoice;
    private readonly TextBlock _txtChoice;
    private readonly Button _btnSwap;
    private readonly Button _btnOk;

    // ［OK］で閉じたときの選択 (true = js1 が左手)。それ以外は null
    public bool? Js1IsLeft { get; private set; }

    public StickSideDialog(ActionMapData data)
    {
        _data = data;
        Title = "HOTAS の左右";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(16), MinWidth = 460 };
        root.Children.Add(new TextBlock
        {
            Text = "左スティックのボタンをどれか押してください。",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
        });
        root.Children.Add(new TextBlock
        {
            Text = "押したスティックを左手のスティックとして表示します (表示の左右だけが変わり、ファイルは変わりません)。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new Thickness(0, 4, 0, 0),
        });
        root.Children.Add(new TextBlock
        {
            Text = "スティックを接続していないときは［中止］を押してください (あとで［HOTAS］タブの［左右を設定…］で設定できます)。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
            Margin = new Thickness(0, 4, 0, 0),
        });

        // js 番号を決められなかったときの選択 (エディターの「この機器は: js1 / js2」と同じ)
        _txtChoice = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        _pnlChoice = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        _pnlChoice.Children.Add(_txtChoice);
        var btnStick1 = new Button { Content = "Stick1", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
        btnStick1.Click += (_, _) => ChooseStick(1);
        var btnStick2 = new Button { Content = "Stick2", Padding = new Thickness(8, 2, 8, 2) };
        btnStick2.Click += (_, _) => ChooseStick(2);
        _pnlChoice.Children.Add(btnStick1);
        _pnlChoice.Children.Add(btnStick2);
        root.Children.Add(_pnlChoice);

        // 左右の枠 (最初のボタンが押されてから出す)
        _frames = new Grid { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        _frames.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _frames.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        _frames.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(_frames);

        _txtConfirm = new TextBlock
        {
            Text = "左右のスティックのボタンを押して確認してください。",
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(_txtConfirm);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _btnSwap = new Button { Content = "入れ替え", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        _btnSwap.Click += (_, _) => { if (_leftJs is int l) { _leftJs = 3 - l; BuildFrames(); } };
        _btnOk = new Button { Content = "OK", Padding = new Thickness(20, 4, 20, 4), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, FontWeight = FontWeights.Bold };
        _btnOk.Click += (_, _) =>
        {
            if (_leftJs is not int l) return;
            Js1IsLeft = l == 1;
            DialogResult = true;
        };
        var btnCancel = new Button { Content = "中止", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        buttons.Children.Add(_btnSwap);
        buttons.Children.Add(_btnOk);
        buttons.Children.Add(btnCancel);
        root.Children.Add(buttons);

        Content = root;

        Loaded += (_, _) =>
        {
            try
            {
                _input = new GameControllerInput();
                _input.InputPressed += Input_Pressed;
                _input.Start();
            }
            catch (Exception)
            {
                // 待ち受けを始められない環境では押下を待てない (［中止］で閉じられる)
                _input?.Dispose();
                _input = null;
            }
        };
        Closed += (_, _) =>
        {
            if (_input == null) return;
            _input.InputPressed -= Input_Pressed;
            _input.Dispose();
            _input = null;
        };
    }

    private void Input_Pressed(object? sender, GameControllerInputEventArgs e)
    {
        if (e.Kind != GameControllerKind.Joystick) return;   // ゲームパッドは使わない
        if (!_data.TryResolveJsForDevice(e.VendorId, e.ProductId, e.DeviceId, out var js, out _))
        {
            // js 番号を決められない: Stick1 / Stick2 を選んでもらう
            _choicePress = e;
            _txtChoice.Text = $"どのスティックか分かりません ({e.DisplayName})。このスティックは:";
            _pnlChoice.Visibility = Visibility.Visible;
            return;
        }
        _choicePress = null;
        _pnlChoice.Visibility = Visibility.Collapsed;
        if (_leftJs == null)
        {
            _leftJs = js;   // 最初に押されたスティックを左手にする
            _btnSwap.IsEnabled = true;
            _btnOk.IsEnabled = true;
            _frames.Visibility = Visibility.Visible;
            _txtConfirm.Visibility = Visibility.Visible;
        }
        _lastPressedJs = js;
        BuildFrames();
    }

    // 選んだ番号を覚えて (ActionMapData。保存はエディターが行う)、同じ押下で続ける
    private void ChooseStick(int js)
    {
        if (_choicePress is not { } press) return;
        _data.SetManualJsForDevice(press.VendorId, press.ProductId, press.DeviceId, js);
        Input_Pressed(this, press);
    }

    // 左右の枠を作り直す (左 = 左手のスティック)。最後に押されたスティックの枠を光らせる (HOTAS タブの押した行の強調と同じオレンジ)
    private void BuildFrames()
    {
        _frames.Children.Clear();
        if (_leftJs is not int left) return;
        AddFrame(left, 0);
        AddFrame(3 - left, 2);
    }

    private void AddFrame(int js, int column)
    {
        var name = _data.GetJsProductName(js);
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = $"Stick{js}",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(name) ? "(名前なし)" : name,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });
        bool lit = _lastPressedJs == js;
        var frame = new Border
        {
            Child = panel,
            Padding = new Thickness(12, 10, 12, 10),
            MinHeight = 60,
            Background = new SolidColorBrush(lit ? Color.FromRgb(255, 243, 224) : Colors.White),
            BorderBrush = new SolidColorBrush(lit ? Color.FromRgb(255, 111, 0) : Color.FromRgb(200, 200, 200)),
            BorderThickness = new Thickness(lit ? 3 : 1),
            CornerRadius = new CornerRadius(4),
        };
        Grid.SetColumn(frame, column);
        _frames.Children.Add(frame);
    }
}
