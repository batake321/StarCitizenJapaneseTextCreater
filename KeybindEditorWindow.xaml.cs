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
        hotasPanel.DisplaySwapChanged = swapped => SaveDisplaySwapSetting(_data.FileJoystickFingerprint, swapped);
        hotasPanel.ClearOriginalJs1Requested = ClearOriginalJs1Record;   // ［記録を消す］(元の js1 の機器)
        hotasPanel.InstanceSwapped = () => UpdateOriginalJs1Status();   // ［js1/js2 の入れ替え］を押したら、保存待ちを含めて判定の表示を書き直す
        LoadData();
        StartControllerInput();
        Closed += (_, _) => StopControllerInput();
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
    }

    // ===== 「表示の入れ替え」の設定 (機器の定義の指紋ごと) =====
    //   保存フォルダの中には置かない (フォルダ内の全ファイルがゲームへコピーされるため。また「ゲームから保存」の上書きはフォルダを消さずに写すので古い記録が残る)。
    //   作業フォルダ直下の JSON に {機器の定義の指紋 ("fp:..."、ActionMapData.FileJoystickFingerprint): true} で覚える。
    //   指紋が同じファイルなら、コピー・取込・同じ名前での作り直しでも同じ記録が効き、上書きで機器の定義が変われば古い記録は効かない。
    //   古い形 ({保存フォルダのフルパス(小文字): true}。ed6aa15 から) の鍵は、読み込むときに、その保存フォルダの actionmaps.xml の指紋へ移し替える
    //   (actionmaps.xml が無い・読めない保存フォルダの鍵は捨てる)
    private static string DisplaySwapSettingsPath =>
        Path.Combine(App.Config.WorkingDirectory ?? "", "keybind_hotas_display_swap.json");

    // ===== 作業フォルダ直下の記録の JSON (表示の入れ替え・手で選んだ js 番号) の読み込み =====
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
    private const string DisplaySwapEffect = "表示の入れ替えの設定を保存できませんでした。\nエディターを開き直すと、今回の表示の入れ替えの設定は使われません。";
    private const string DeviceJsEffect = "手で選んだ js 番号を保存できませんでした。\nエディターを開き直すと、今回選んだ js 番号は使われません。";

    private static void NotifySettingsWriteFailed(string effect, string path, string reason) =>
        MessageBox.Show($"{effect}\n\n{reason}\n\n記録のファイル:\n{path}", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void NotifySettingsOnce(string path, string message)
    {
        if (!SettingsNoticeShown.Add(path)) return;
        MessageBox.Show(message, "設定ファイル", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private const string FingerprintKeyPrefix = "fp:";

    // 読み込んで、古い形の鍵 (保存フォルダのパス) を指紋へ移し替える。移し替えたら書き戻す (書けなくても、この読み込みでは移し替えた形を使う)
    private static Dictionary<string, bool> LoadDisplaySwapSettings(out bool canWrite)
    {
        canWrite = TryLoadSettingsJson<Dictionary<string, bool>>(DisplaySwapSettingsPath, "表示の入れ替え", out var settings);
        var legacy = settings.Keys.Where(k => !k.StartsWith(FingerprintKeyPrefix, StringComparison.Ordinal)).ToList();
        if (legacy.Count == 0) return settings;
        foreach (var oldKey in legacy)
        {
            var value = settings[oldKey];
            settings.Remove(oldKey);
            if (!value) continue;
            var fp = ActionMapParser.ComputeJoystickFingerprintForSave(oldKey);
            if (fp != null) settings[fp] = true;   // actionmaps.xml が無い・読めない保存フォルダの分は捨てる
        }
        if (canWrite)
        {
            try
            {
                // 書き戻す前に、元のファイルを「<名前>.pre-migration」として 1 回だけ控えに残す (控えが既にあれば作らない)。
                //   控えを作れなければ書き戻さずに知らせる (この読み込みでは移し替えた形を使う)
                var preMigration = DisplaySwapSettingsPath + ".pre-migration";
                if (!File.Exists(preMigration))
                {
                    try
                    {
                        File.Copy(DisplaySwapSettingsPath, preMigration, overwrite: false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 知らせは起動中 1 回 (保存のたびの「保存できませんでした」は SaveDisplaySwapSetting が出す)。
                        //   書けないものとして返す (読み込むたびに移し替えた形を使い、控えも作り直してみる)
                        NotifySettingsOnce(DisplaySwapSettingsPath + "|pre-migration",
                            $"表示の入れ替えの記録を新しい形に移し替える前の控えを作れなかったため、記録のファイルを書き換えません:\n{preMigration}\n\n{ex.Message}\n\n" +
                            "移し替えた形は、読み込むたびにメモリの上だけで使います。");
                        canWrite = false;
                        return settings;
                    }
                }
                File.WriteAllText(DisplaySwapSettingsPath,
                    System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                // 移し替えの書き戻しに失敗した知らせは起動中 1 回だけ (書き戻せなかったことは、保存のたびの「保存できませんでした」で伝わる)
                NotifySettingsOnce(DisplaySwapSettingsPath + "|migration-write",
                    $"{DisplaySwapEffect}\n\n表示の入れ替えの記録を新しい形に移し替えて書き戻せませんでした。\n{ex.Message}\n\n記録のファイル:\n{DisplaySwapSettingsPath}");
            }
        }
        return settings;
    }

    private static bool LoadDisplaySwapSetting(string fingerprint)
    {
        try
        {
            return LoadDisplaySwapSettings(out _).TryGetValue(fingerprint, out var v) && v;
        }
        catch { return false; }
    }

    private static void SaveDisplaySwapSetting(string fingerprint, bool swapped)
    {
        try
        {
            var settings = LoadDisplaySwapSettings(out var canWrite);
            if (!canWrite)
            {
                // 壊れた元のファイルを控え無しで上書きしない・読めなかったファイルに書かない。書けなかったことは保存のたびに知らせる
                NotifySettingsWriteFailed(DisplaySwapEffect, DisplaySwapSettingsPath, SettingsNotWritableReason);
                return;
            }
            if (swapped) settings[fingerprint] = true;
            else if (!settings.Remove(fingerprint)) return;   // 記録が無く、外すものも無ければファイルに触れない
            File.WriteAllText(DisplaySwapSettingsPath,
                System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            NotifySettingsWriteFailed(DisplaySwapEffect, DisplaySwapSettingsPath, ex.Message);
        }
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
    //   機器の定義を入れ替え、表示の入れ替えと手で選んだ js 番号の記録を新しい指紋のほうにも書く
    //   (手で選んだ js 番号は、保存で番号も入れ替わったメモリの値のまま書く)。入れ替えていない元の指紋の記録はそのまま残す
    private void MoveFingerprintRecordsAfterSwapSave()
    {
        _data.SwapJoystickDefinitionsAfterSave();
        SaveDisplaySwapSetting(_data.FileJoystickFingerprint, _data.JoystickDisplaySwapped);
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

        _data.JoystickDisplaySwapped = LoadDisplaySwapSetting(_data.FileJoystickFingerprint);
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
        txtStatus.Text = $"全{_allActions.Count}件 | 表示{_filteredActions.Count}件 | 変更{modified}件 | 未割当{unbound}件";
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

public class CategoryItem
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ActionCount { get; set; } = "";
}
