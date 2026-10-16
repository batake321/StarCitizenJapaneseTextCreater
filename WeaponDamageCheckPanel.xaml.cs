using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

// ［個人戦闘］の［ダメージ判定］。上の選択で銃の分類・銃・アタッチメント (スコープ / マズル / アンダーバレル / マガジン) を選び、
// 下の表に「オプションなし（既定）」(［表］と同じ行。既定のマガジンが補正を持てばそれを入れた行) と「選択したオプション」(補正とマガジンを入れて計算し直した行) の 2 行を出す。
//   銃の分類・銃の一覧は［表］と同じ (GameDataQueryService.GetPersonalWeaponTable)。列も［表］と同じ (WeaponTablePanel.PersonalColumns) に、行の区別の列を前に足す
//   スコープ・マズル・アンダーバレルの選択肢は、その銃の枠に付くものだけ + 「なし」(GameDataQueryService.GetWeaponAttachmentSetup)。枠の無い欄は選べない
//   (欄に当たる枠が 1 つも無い銃 (手榴弾・近接武器など) は 4 つとも選べない)。
//   色違いは別の項目にせず、基本品目の項目に「（色違い: …）」と書き添える (WeaponAttachmentCandidate.DisplayLabel)
//   マガジンは「なし」を出さず、必ずどれかを選ぶ。既定は銃の既定のマガジン。替えると弾と装弾数をそのマガジンから取る。
//   既定のマガジンは既定の構成の一部として、その補正を「オプションなし（既定）」と「選択したオプション」(既定のマガジンのまま) の両方に掛ける
//   (補正を持たないマガジンなら「オプションなし（既定）」は［表］の行そのもの)
//   補正は倍率の掛け算で合わせる (推定。画面に注記を出す)
public partial class WeaponDamageCheckPanel : UserControl
{
    private const string EstimateNote = "補正の合成方法はゲームデータから確認できないため推定（倍率の掛け算）";
    private const string DefaultRowLabel = "オプションなし（既定）";
    private const string SelectedRowLabel = "選択したオプション";

    // 選択肢 1 つ (Label を出す。Value は欄ごとの値)
    private sealed record Choice(string Label, object? Value)
    {
        public override string ToString() => Label;
    }

    // マガジンの選択肢の値。Record: null = 既定のまま、それ以外 = そのマガジンの record。Multipliers = そのマガジンの補正
    private sealed record MagazinePick(string? Record, WeaponStatMultipliers Multipliers);

    private bool _initialized, _updating;
    // 品目名が作り直された (GameDataExtractor.ItemNamesChanged) ので、次に表示されたときに読み直す
    private bool _namesStale;
    private int _loadVersion, _setupVersion, _calcVersion;
    private int _readySetupVersion = -1;      // オプションの欄を作り終えた銃の _setupVersion (違えば枠を読んでいる途中なので計算しない)
    private WeaponStatMultipliers _defaultMagazineMultipliers = WeaponStatMultipliers.None;   // 既定のマガジンの補正 (既定の構成に掛ける)
    private string _dbPath = "";
    private List<WeaponTableRow> _rows = new();
    private List<WeaponAttachmentCandidate> _candidates = new();
    private Dictionary<string, List<(string Location, double Price)>>? _uex;
    private WeaponTableRow? _weapon;          // 選んでいる銃の［表］の行
    private WeaponTableRow? _shownDefault;    // 表に出している「オプションなし（既定）」の行
    private bool _setupFailed;                // 選んでいる銃の枠の読み込みがエラーで終わった (同じ銃を選び直したら読み直す)

    public WeaponDamageCheckPanel()
    {
        InitializeComponent();
        cmbKind.SelectionChanged += (_, _) => { if (!_updating) FillWeapons(null); };
        cmbWeapon.SelectionChanged += (_, _) => { if (!_updating) OnWeaponChanged(); };
        // 枠の読み込みでエラーが出た銃を選び直したとき (同じ項目なので SelectionChanged は来ない) は、読み込みをやり直す
        cmbWeapon.DropDownClosed += (_, _) => { if (!_updating && _setupFailed) OnWeaponChanged(); };
        foreach (var c in new[] { cmbScope, cmbMuzzle, cmbUnderbarrel, cmbMagazine })
            c.SelectionChanged += (_, _) => { if (!_updating) Recompute(); };
        // 品目名が作り直されたら、次に表示されたとき (Loaded) に読み直す (選んでいた銃・アタッチメントは選び直しになる)
        GameDataExtractor.ItemNamesChanged += () => _namesStale = true;
        Loaded += async (_, _) =>
        {
            if (_initialized)
            {
                if (!_namesStale) return;
                _namesStale = false;
                try { await LoadAsync(); }
                catch (Exception ex) { ShowMessage($"読み込めませんでした: {ex.Message}"); }
                return;
            }
            _initialized = true;
            try
            {
                table.SetColumns(Columns());
                AddArmorSelectors();
                UpdateNotes([]);
                await LoadAsync();
            }
            catch (Exception ex) { ShowMessage($"読み込めませんでした: {ex.Message}"); }
        };
    }

    // 行の区別の列 + ［表］と同じ列
    private List<GridColumnSpec> Columns()
    {
        string Label(object o) => ReferenceEquals(o, _shownDefault) ? DefaultRowLabel : SelectedRowLabel;
        var cols = new List<GridColumnSpec> { new() { Header = "構成", Filterable = false, Text = Label, Key = Label } };
        cols.AddRange(WeaponTablePanel.PersonalColumns());
        return cols;
    }

    // ［表］と同じ「胴: [H|M|L]」「頭: [H|M|L]」の選択 (列の位置は行の区別の列の分だけ右)
    private void AddArmorSelectors()
    {
        foreach (var (label, column, head) in new[] { ("胴:", WeaponTablePanel.TorsoDownColumn + 1, false), ("頭:", WeaponTablePanel.HeadDownColumn + 1, true) })
        {
            var combo = new ComboBox { ItemsSource = new[] { "H", "M", "L" }, SelectedIndex = 0, Margin = new Thickness(4, 0, 8, 0), MinWidth = 44 };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string s && s.Length == 1) table.ReplaceColumn(column, WeaponTablePanel.DownColumn(s[0], head));
            };
            table.AddToolbarItem(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            table.AddToolbarItem(combo);
        }
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        ShowMessage("読み込み中...");
        _dbPath = WeaponTablePanel.DbPath();
        var dbPath = _dbPath;
        (List<WeaponTableRow> Rows, List<WeaponAttachmentCandidate> Candidates)? data;
        try
        {
            data = await Task.Run<(List<WeaponTableRow>, List<WeaponAttachmentCandidate>)?>(() =>
            {
                if (!File.Exists(dbPath)) return null;
                // 読み取り専用で開く (DB に書き込まない)
                using var q = GameDataQueryService.OpenReadOnly(dbPath);
                if (!q.HasItemIndex()) return null;
                return (q.GetPersonalWeaponTable(), q.LoadWeaponAttachmentCandidates());
            });
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) ShowMessage($"読み込めませんでした: {ex.Message}");
            return;
        }
        if (version != _loadVersion) return;
        if (data == null) { ShowMessage("インデックスが未構築です (設定タブの［インデックス構築］)"); return; }
        if (data.Value.Rows.Count == 0) { ShowMessage("データがありません"); return; }

        ShowMessage(null);
        _rows = data.Value.Rows;
        _candidates = data.Value.Candidates;
        FillKinds(null, null);

        // 店の最安値: まず保存データで出し、UEX の値段は裏で取ってから埋める (［表］と同じ取得・キャッシュを共用)
        var ids = GameDataQueryService.PersonalWeaponTableCategories.SelectMany(c => c.UexCategoryIds ?? []).Distinct().ToArray();
        if (ids.Length > 0) await FillUexPricesAsync(ids, dbPath, version);
    }

    private async Task FillUexPricesAsync(int[] ids, string dbPath, int version)
    {
        try
        {
            var uex = await ChatService.GetSharedUexItemPricesAsync(ids);
            if (uex.Count == 0 || version != _loadVersion) return;
            var rows = await Task.Run(() =>
            {
                using var q = GameDataQueryService.OpenReadOnly(dbPath);
                return q.GetPersonalWeaponTable(uex);
            });
            if (version != _loadVersion || rows.Count == 0) return;
            _uex = uex;
            _rows = rows;
            // 選んでいる分類・銃はそのまま (同じ銃ならアタッチメントの選択も残す)
            FillKinds((cmbKind.SelectedItem as Choice)?.Value as string, _weapon?.Record);
        }
        catch { }   // UEX が取れなくても保存データの値のまま
    }

    // 銃の分類の選択肢 (［表］の「種類」。並びは［表］と同じ)。kind・record があればそれを選び直す
    private void FillKinds(string? kind, string? record)
    {
        _updating = true;
        try
        {
            var kinds = _rows.Select(r => r.Kind).Distinct().Select(k => new Choice(k.Length > 0 ? k : "(種類なし)", k)).ToList();
            cmbKind.ItemsSource = kinds;
            var index = kind != null ? kinds.FindIndex(c => (string?)c.Value == kind) : -1;
            cmbKind.SelectedIndex = index >= 0 ? index : kinds.Count > 0 ? 0 : -1;
        }
        finally { _updating = false; }
        FillWeapons(record);
    }

    // 銃の名前の選択肢 (選んだ分類の銃。並びは［表］と同じ)。record があればそれを選び直す
    private void FillWeapons(string? record)
    {
        var kind = (cmbKind.SelectedItem as Choice)?.Value as string;
        _updating = true;
        try
        {
            var weapons = _rows.Where(r => r.Kind == kind).Select(r => new Choice(r.DisplayName, r)).ToList();
            cmbWeapon.ItemsSource = weapons;
            var index = record != null ? weapons.FindIndex(c => ((WeaponTableRow)c.Value!).Record == record) : -1;
            cmbWeapon.SelectedIndex = index >= 0 ? index : weapons.Count > 0 ? 0 : -1;
        }
        finally { _updating = false; }
        // 同じ銃を選び直したとき (UEX の値段で表を読み直したとき) は、枠を読み直さずに行だけ差し替えて計算し直す
        if (record != null && _weapon?.Record == record && cmbWeapon.SelectedItem is Choice { Value: WeaponTableRow same } && same.Record == record)
        {
            _weapon = same;
            Recompute();
        }
        else OnWeaponChanged();
    }

    // 銃を選んだ: 枠と付けられるアタッチメントを読み、オプションの欄を作り直してから計算する
    private async void OnWeaponChanged()
    {
        var row = (cmbWeapon.SelectedItem as Choice)?.Value as WeaponTableRow;
        _weapon = row;
        var version = ++_setupVersion;
        ++_calcVersion;   // 前の銃の計算の結果は出さない
        _setupFailed = false;
        SetOptionsEnabled(false);
        if (row == null) { _shownDefault = null; table.SetItems([]); UpdateNotes([]); return; }
        var dbPath = _dbPath;
        var candidates = _candidates;
        WeaponAttachmentSetup setup;
        try
        {
            setup = await Task.Run(() =>
            {
                using var q = GameDataQueryService.OpenReadOnly(dbPath);
                return q.GetWeaponAttachmentSetup(row.Record, candidates);
            });
        }
        catch (Exception ex)
        {
            if (version != _setupVersion) return;
            // 前の銃の 2 行と選択肢を残さない: 表と 4 つの欄を空にし、枠の知らせの欄にエラーを出す
            _setupFailed = true;
            _updating = true;
            try { foreach (var c in new[] { cmbScope, cmbMuzzle, cmbUnderbarrel, cmbMagazine }) c.ItemsSource = null; }
            finally { _updating = false; }
            _shownDefault = null;
            table.SetItems([]);
            UpdateNotes([]);
            ShowMessage(null);
            txtPortInfo.Text = $"アタッチメントの枠を読めませんでした: {ex.Message}";
            txtPortInfo.Visibility = Visibility.Visible;
            return;
        }
        if (version != _setupVersion) return;

        _updating = true;
        try
        {
            FillOption(cmbScope, setup, WeaponAttachmentSlotKind.Scope);
            FillOption(cmbMuzzle, setup, WeaponAttachmentSlotKind.Muzzle);
            FillOption(cmbUnderbarrel, setup, WeaponAttachmentSlotKind.Underbarrel);
            FillMagazine(setup);
        }
        finally { _updating = false; }
        _readySetupVersion = version;

        txtPortInfo.Text = setup.PortsFound ? "" :
            "この銃はアタッチメントの枠のデータ (raw_json) が無く、基本品目にもたどれないため、オプションを選べません。";
        txtPortInfo.Visibility = setup.PortsFound ? Visibility.Collapsed : Visibility.Visible;
        Recompute();
    }

    private void SetOptionsEnabled(bool enabled)
    {
        foreach (var c in new[] { cmbScope, cmbMuzzle, cmbUnderbarrel, cmbMagazine }) c.IsEnabled = enabled;
    }

    // スコープ・マズル・アンダーバレルの欄: 「なし」+ その枠に付くもの。枠が無ければ「なし」だけで選べない
    private static void FillOption(ComboBox combo, WeaponAttachmentSetup setup, WeaponAttachmentSlotKind kind)
    {
        var port = setup.Ports.FirstOrDefault(p => p.Kind == kind);
        var items = new List<Choice> { new("なし", null) };
        if (port != null) items.AddRange(port.Options.Select(c => new Choice(c.DisplayLabel, c)));
        combo.ItemsSource = items;
        combo.SelectedIndex = 0;
        combo.IsEnabled = port != null;
    }

    // マガジンの欄: その枠に付くもの (「なし」は出さない)。既定 (銃の既定のマガジン) を選んでおく。
    // 既定のマガジンが枠に付くものの中に無いとき (除外したもの・規則に合わないもの) も、既定として選択肢の先頭に出す。
    // 銃に既定のマガジンが無いときは「既定（マガジンなし）」を先頭に出して選んでおく (既定のまま計算する)。
    // 枠が無ければ選べない (既定のまま計算する)。欄の表記は、既定のマガジンがあっても空にする (利用者の決定)。
    // 既定のマガジンの補正は _defaultMagazineMultipliers に入れ、既定のマガジンの選択肢の補正にも使う
    private void FillMagazine(WeaponAttachmentSetup setup)
    {
        var port = setup.Ports.FirstOrDefault(p => p.Kind == WeaponAttachmentSlotKind.Magazine);
        bool IsDefault(WeaponAttachmentCandidate c) =>
            setup.DefaultMagazineRecord != null && string.Equals(c.Record, setup.DefaultMagazineRecord, StringComparison.OrdinalIgnoreCase);
        // 既定のマガジンの補正 (枠に付くもの → アタッチメントの全件の順に探す。見つからなければ補正なし)
        var defaultMult = port?.Options.FirstOrDefault(IsDefault)?.Multipliers
            ?? _candidates.FirstOrDefault(IsDefault)?.Multipliers
            ?? WeaponStatMultipliers.None;
        _defaultMagazineMultipliers = defaultMult;
        var defaultLabel = setup.DefaultMagazineName != null ? $"{setup.DefaultMagazineName}（既定）" : null;
        var items = new List<Choice>();
        var selected = 0;
        if (port == null)
        {
            // 枠が無い: 既定のまま (マガジンを替えない)。欄の表記は既定のマガジンの有無にかかわらず空にする
            // (既定のマガジンの補正 defaultMult は、表記を空にしても計算に使う)
            items.Add(new Choice("", new MagazinePick(null, defaultMult)));
        }
        else
        {
            var hasDefault = false;
            foreach (var c in port.Options)
            {
                var isDefault = IsDefault(c);
                // 既定のマガジンは「既定のまま」(Record = null) として計算する (［表］と同じ計算になる)
                items.Add(new Choice(isDefault ? $"{c.DisplayLabel}（既定）" : c.DisplayLabel, new MagazinePick(isDefault ? null : c.Record, c.Multipliers)));
                if (isDefault) { hasDefault = true; selected = items.Count - 1; }
            }
            if (!hasDefault)
            {
                items.Insert(0, new Choice(defaultLabel ?? "既定（マガジンなし）", new MagazinePick(null, defaultMult)));
                selected = 0;
            }
        }
        cmbMagazine.ItemsSource = items;
        cmbMagazine.SelectedIndex = selected;
        cmbMagazine.IsEnabled = port != null;
    }

    // 選んだオプションの補正とマガジンで計算し直し、表に 2 行を出す。
    // 「オプションなし（既定）」は既定のマガジンの補正を掛けた行 (補正が無ければ［表］の行そのもの)
    private async void Recompute()
    {
        // 枠を読んでいる途中 (欄にまだ前の銃のオプションが入っている) は計算しない。読み終えたときに計算する
        if (_weapon is not { } row || _readySetupVersion != _setupVersion) return;
        var mult = WeaponStatMultipliers.None;
        foreach (var c in new[] { cmbScope, cmbMuzzle, cmbUnderbarrel })
            if ((c.SelectedItem as Choice)?.Value is WeaponAttachmentCandidate a) mult = mult.Times(a.Multipliers);
        string? magazine = null;
        if ((cmbMagazine.SelectedItem as Choice)?.Value is MagazinePick mp)
        {
            mult = mult.Times(mp.Multipliers);
            magazine = mp.Record;
        }
        var defaultMult = _defaultMagazineMultipliers;
        var version = ++_calcVersion;
        var dbPath = _dbPath;
        var uex = _uex;
        WeaponTableRow? modified, shownDefault;
        string? error = null;
        try
        {
            (shownDefault, modified) = await Task.Run(() =>
            {
                using var q = GameDataQueryService.OpenReadOnly(dbPath);
                var def = defaultMult.IsNone ? row : q.GetPersonalWeaponRowWithAttachments(row.Record, row.Kind, defaultMult, null, uex);
                return (def, q.GetPersonalWeaponRowWithAttachments(row.Record, row.Kind, mult, magazine, uex));
            });
        }
        catch (Exception ex) { shownDefault = defaultMult.IsNone ? row : null; modified = null; error = ex.Message; }
        if (version != _calcVersion) return;

        _shownDefault = shownDefault;
        var items = new List<object>();
        if (shownDefault != null) items.Add(shownDefault);
        if (modified != null) items.Add(modified);
        table.SetItems(items);
        var messages = new List<string>();
        if (shownDefault == null) messages.Add("既定の構成 (既定のマガジンの補正を入れた値) を計算できませんでした");
        if (modified == null) messages.Add("選択したオプションの値を計算できませんでした");
        if (error != null && messages.Count > 0) messages[^1] += $": {error}";
        ShowMessage(messages.Count > 0 ? string.Join(Environment.NewLine, messages) : null);
        UpdateNotes(items.OfType<WeaponTableRow>().ToList());
    }

    // 補正の合成方法の注記と、表に出している行に注1・注2 の印があるときのバースト DPS の注
    private void UpdateNotes(IReadOnlyList<WeaponTableRow> rows)
    {
        var notes = new List<string> { EstimateNote };
        if (rows.Any(r => r.ChargeNote)) notes.Add(GameDataQueryService.DpsNoteChargeText);
        if (rows.Any(r => r.MixedRateNote)) notes.Add(GameDataQueryService.DpsNoteMixedText);
        txtNotes.Text = string.Join(Environment.NewLine, notes);
    }

    private void ShowMessage(string? text)
    {
        txtMessage.Text = text ?? "";
        txtMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
