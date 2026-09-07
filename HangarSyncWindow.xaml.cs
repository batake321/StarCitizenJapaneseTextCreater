using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace StarCitizenJapaneseTextCreater;

// RSI の My Hangar から pledge / 船 / CCU を取得するウィンドウ。
// WebView2 に素の RSI サイトを表示し、利用者が手動でログインした認証済みセッションを使って巡回する。
// アプリ側からは click()/submit() を一切発行せず、DOM の読み取りだけを行う。
public partial class HangarSyncWindow : Window
{
    private const string BaseUrl = "https://robertsspaceindustries.com";
    private const string PledgesUrl = BaseUrl + "/en/account/pledges?page=";
    private const string LoginUrl = BaseUrl + "/en/connect";
    private const string BuybackUrl = BaseUrl + "/en/account/buy-back-pledges";
    private const string BillingUrl = BaseUrl + "/en/account/billing";
    private const int MaxPages = 60;   // 1ページ10件なので600 pledge まで。無限ループ防止

    // セレクタ未確定時のフォールバック (innerText 全体に適用)
    // 実ページの文言 (2026-09-07 確認): Billing は "$0.00 USD" の直後に "STORE CREDITS"、
    // Buy Back は "You have <strong>2</strong> opportunity to buy back ..."
    private static readonly Regex StoreCreditRegex =
        new(@"\$\s*([\d,]+\.\d{2})\s*USD\s*STORE\s+CREDITS", RegexOptions.IgnoreCase);
    private static readonly Regex BuybackTokensRegex =
        new(@"You\s+have\s+(\d+)\s+opportunit", RegexOptions.IgnoreCase);

    // ログイン情報 (Cookie) の暗号化保存先。DPAPI (CurrentUser) で保護するため、このユーザーアカウントのみ復号できる
    private const string SessionFileName = "hangar_session.dat";
    private static readonly byte[] SessionEntropy = Encoding.UTF8.GetBytes("SCJTC.HangarSession.v1");

    private readonly string _workDir;
    private readonly HangarService _hangar = new();
    private CancellationTokenSource? _cts;

    private string SessionFilePath => Path.Combine(_workDir, SessionFileName);

    // 暗号化ファイルに保存する Cookie 1 件分
    private sealed class SavedCookie
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string Domain { get; set; } = "";
        public string Path { get; set; } = "/";
        public DateTime Expires { get; set; }
        public bool IsHttpOnly { get; set; }
        public bool IsSecure { get; set; }
        public CoreWebView2CookieSameSiteKind SameSite { get; set; }
    }

    public HangarSyncWindow(string workDir)
    {
        InitializeComponent();
        _workDir = workDir;
        _hangar.SetCacheDir(workDir);
        _hangar.OnProgress += Log;

        // 保存済みのログイン情報があれば「保持する」を ON で初期化
        chkKeepLogin.IsChecked = File.Exists(SessionFilePath);

        Loaded += HangarSyncWindow_Loaded;
        Closing += HangarSyncWindow_Closing;
    }

    // === WebView2 初期化 ===

    private async void HangarSyncWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(_workDir, "webview2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await webView.EnsureCoreWebView2Async(env);
            Log("WebView2 を初期化しました。");

            var core = webView.CoreWebView2;
            if (core != null)
            {
                // 前回異常終了などでプロファイルに残った平文 Cookie を消してから、暗号化ファイルから復元する
                try
                {
                    core.CookieManager.DeleteAllCookies();
                }
                catch (Exception ex)
                {
                    Log($"Cookie の削除に失敗しました: {ex.Message}");
                }
                RestoreSession(core);
            }

            txtStatus.Text = "準備完了";
        }
        catch (Exception ex)
        {
            btnSync.IsEnabled = false;
            txtStatus.Text = "初期化失敗";
            Log($"WebView2 の初期化に失敗しました: {ex.Message}");
        }
    }

    // Cookie の取得が非同期のため、いったん Close を取り消して保存処理を待ち、完了後に改めて Close する
    private bool _closeReady;
    private bool _closeInProgress;

    private void HangarSyncWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closeInProgress) return;
        _closeInProgress = true;
        _ = SaveSessionAndCloseAsync();
    }

    private async Task SaveSessionAndCloseAsync()
    {
        try
        {
            await SaveOrClearSessionAsync();
        }
        catch (Exception ex)
        {
            Log($"ログイン情報の保存処理に失敗しました: {ex.Message}");
        }
        finally
        {
            _closeReady = true;
            Close();
        }
    }

    // === ログイン情報 (Cookie) の暗号化保存・復元 ===

    // 保持 ON: RSI の Cookie を DPAPI で暗号化して hangar_session.dat に保存し、プロファイル側の Cookie は必ず削除する
    // 保持 OFF: Cookie を削除し、hangar_session.dat があれば削除する
    private async Task SaveOrClearSessionAsync()
    {
        var keep = chkKeepLogin.IsChecked == true;

        if (keep)
        {
            await SaveSessionAsync(deleteProfileCookies: true);
        }
        else
        {
            try
            {
                if (File.Exists(SessionFilePath))
                {
                    File.Delete(SessionFilePath);
                    Log("保存されていたログイン情報を削除しました。");
                }
            }
            catch (Exception ex)
            {
                Log($"ログイン情報ファイルの削除に失敗しました: {ex.Message}");
            }

            // 保持 OFF でも、プロファイル側には平文 Cookie を残さない
            DeleteProfileCookies();
        }
    }

    // RSI の Cookie を DPAPI で暗号化して hangar_session.dat に保存する。
    // deleteProfileCookies = true のときは保存後にプロファイル側の Cookie を削除する (ウィンドウ終了時)。
    // false のときは保存のみ (同期中の途中保存。異常終了時の取りこぼし対策)
    private async Task SaveSessionAsync(bool deleteProfileCookies)
    {
        var core = webView.CoreWebView2;

        if (core != null)
        {
            try
            {
                // Path 違い・サブドメインの Cookie も含めるため全件取得し、RSI ドメインのものだけを保存対象にする
                var allCookies = await core.CookieManager.GetCookiesAsync(null);
                var cookies = allCookies.Where(c => IsRsiDomain(c.Domain));
                var saved = cookies.Select(c => new SavedCookie
                {
                        Name = c.Name,
                        Value = c.Value,
                        Domain = c.Domain,
                        Path = c.Path,
                        Expires = c.Expires,
                        IsHttpOnly = c.IsHttpOnly,
                        IsSecure = c.IsSecure,
                        SameSite = c.SameSite,
                    }).ToList();

                if (saved.Count == 0)
                {
                    // 取得できた Cookie が 0 件なら空のセッションファイルは作らない (既存があれば削除)
                    if (File.Exists(SessionFilePath)) File.Delete(SessionFilePath);
                    Log("保存する Cookie がありません。");
                }
                else
                {
                    var plain = JsonSerializer.SerializeToUtf8Bytes(saved);
                    var enc = ProtectedData.Protect(plain, SessionEntropy, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(SessionFilePath, enc);
                    Log($"ログイン情報を暗号化して保存しました ({saved.Count} 件): {SessionFilePath}");
                }
            }
            catch (Exception ex)
            {
                Log($"ログイン情報の保存に失敗しました: {ex.Message}");
            }
        }

        if (deleteProfileCookies)
        {
            // 保存後、プロファイル側には平文 Cookie を残さない
            DeleteProfileCookies();
        }
    }

    // Cookie の Domain が robertsspaceindustries.com またはそのサブドメインか (先頭の "." は無視、大文字小文字無視)
    private static bool IsRsiDomain(string? domain)
    {
        if (string.IsNullOrEmpty(domain)) return false;
        var d = domain.TrimStart('.');
        return d.EndsWith("robertsspaceindustries.com", StringComparison.OrdinalIgnoreCase);
    }

    // プロファイル側の Cookie をすべて削除する
    private void DeleteProfileCookies()
    {
        try
        {
            webView.CoreWebView2?.CookieManager.DeleteAllCookies();
        }
        catch (Exception ex)
        {
            Log($"Cookie の削除に失敗しました: {ex.Message}");
        }
    }

    // hangar_session.dat があれば復号して Cookie を復元する。復号失敗 (別ユーザー・別 PC・破損) はファイルを削除して通常のログイン待ちへ
    private void RestoreSession(CoreWebView2 core)
    {
        var path = SessionFilePath;
        if (!File.Exists(path)) return;

        List<SavedCookie> cookies;
        try
        {
            var enc = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(enc, SessionEntropy, DataProtectionScope.CurrentUser);
            cookies = JsonSerializer.Deserialize<List<SavedCookie>>(plain) ?? new List<SavedCookie>();
        }
        catch (Exception ex)
        {
            Log($"保存されたログイン情報を復号できませんでした ({ex.Message})。ファイルを削除して通常のログインに戻ります。");
            try
            {
                File.Delete(path);
            }
            catch (Exception ex2)
            {
                Log($"ログイン情報ファイルの削除に失敗しました: {ex2.Message}");
            }
            return;
        }

        int restored = 0;
        foreach (var c in cookies)
        {
            try
            {
                var cookie = core.CookieManager.CreateCookie(c.Name, c.Value, c.Domain, c.Path);
                cookie.Expires = c.Expires;
                cookie.IsHttpOnly = c.IsHttpOnly;
                cookie.IsSecure = c.IsSecure;
                cookie.SameSite = c.SameSite;
                core.CookieManager.AddOrUpdateCookie(cookie);
                restored++;
            }
            catch (Exception ex)
            {
                Log($"Cookie の復元に失敗しました ({c.Name}): {ex.Message}");
            }
        }
        Log($"保存されたログイン情報を復元しました ({restored} / {cookies.Count} 件)。");
    }

    // === 抽出スクリプト ===

    // hangar_selectors.json のセレクタを JSON エンコードして埋め込んだ、読み取り専用の抽出スクリプトを組み立てる
    private string BuildExtractionScript()
    {
        var sel = HangarService.LoadSelectors();

        const string template = """
            (function () {
              var body = document.body ? document.body.innerText : '';
              if (/RESTRICTED AREA/i.test(body) || /You must be authenticated/i.test(body)) {
                return JSON.stringify({ authenticated: false, pledges: [] });
              }
              var roots = document.querySelectorAll(__PLEDGE_ROOT__);
              var out = [];
              for (var i = 0; i < roots.length; i++) {
                var li = roots[i];
                function val(sel) {
                  var e = li.querySelector(sel);
                  if (!e) return '';
                  if (e.value !== undefined && e.value !== null) return String(e.value);
                  return (e.textContent || '').trim();
                }
                function txt(sel) {
                  var e = li.querySelector(sel);
                  return e ? (e.textContent || '').trim() : '';
                }
                var items = [];
                var nodes = li.querySelectorAll(__ITEM_ROOT__);
                for (var j = 0; j < nodes.length; j++) {
                  var it = nodes[j];
                  var t = it.querySelector(__ITEM_TITLE__);
                  var k = it.querySelector(__ITEM_KIND__);
                  var l = it.querySelector(__ITEM_LINER__);
                  items.push({
                    title: t ? (t.textContent || '').trim() : '',
                    kind: k ? (k.textContent || '').trim() : '',
                    liner: l ? (l.textContent || '').trim() : ''
                  });
                }
                var ns = li.querySelector(__NAMEABLE__);
                out.push({
                  id: val(__PLEDGE_ID__),
                  name: val(__PLEDGE_NAME__),
                  value: val(__PLEDGE_VALUE__),
                  currency: val(__PLEDGE_CURRENCY__),
                  configValue: val(__PLEDGE_CONFIG__),
                  notBuybackable: val(__PLEDGE_NOT_BUYBACK__),
                  availability: txt(__AVAILABILITY__),
                  date: txt(__DATE_COL__),
                  nameableShips: ns ? (ns.textContent || '') : '',
                  items: items
                });
              }
              return JSON.stringify({ authenticated: true, pledges: out });
            })()
            """;

        return template
            .Replace("__PLEDGE_ROOT__", JsonSerializer.Serialize(sel.PledgeRoot))
            .Replace("__ITEM_ROOT__", JsonSerializer.Serialize(sel.ItemRoot))
            .Replace("__ITEM_TITLE__", JsonSerializer.Serialize(sel.ItemTitle))
            .Replace("__ITEM_KIND__", JsonSerializer.Serialize(sel.ItemKind))
            .Replace("__ITEM_LINER__", JsonSerializer.Serialize(sel.ItemLiner))
            .Replace("__PLEDGE_ID__", JsonSerializer.Serialize(sel.PledgeId))
            .Replace("__PLEDGE_NAME__", JsonSerializer.Serialize(sel.PledgeName))
            .Replace("__PLEDGE_VALUE__", JsonSerializer.Serialize(sel.PledgeValue))
            .Replace("__PLEDGE_CURRENCY__", JsonSerializer.Serialize(sel.PledgeCurrency))
            .Replace("__PLEDGE_CONFIG__", JsonSerializer.Serialize(sel.PledgeConfigValue))
            .Replace("__PLEDGE_NOT_BUYBACK__", JsonSerializer.Serialize(sel.PledgeNotBuybackable))
            .Replace("__AVAILABILITY__", JsonSerializer.Serialize(sel.Availability))
            .Replace("__DATE_COL__", JsonSerializer.Serialize(sel.DateCol))
            .Replace("__NAMEABLE__", JsonSerializer.Serialize(sel.NameableShips));
    }

    // アカウント情報ページ (Buy Back / Billing) 用: 認証状態・innerText 全体・セレクタ要素の textContent を返す
    private static string BuildAccountPageScript(string selector)
    {
        const string template = """
            (function () {
              var body = document.body ? document.body.innerText : '';
              var auth = !(/RESTRICTED AREA/i.test(body) || /You must be authenticated/i.test(body));
              var selText = '';
              var sel = __SEL__;
              if (sel) {
                var e = document.querySelector(sel);
                if (e) selText = (e.textContent || '').trim();
              }
              return JSON.stringify({ authenticated: auth, text: body, selText: selText });
            })()
            """;
        return template.Replace("__SEL__", JsonSerializer.Serialize(selector ?? ""));
    }

    // === 同期 ===

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        btnSync.IsEnabled = false;
        btnCancel.IsEnabled = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            // --- 0. Ship Matrix (認証不要・HttpClient)。失敗しても同期は続行 ---
            txtStatus.Text = "Ship Matrix を確認中...";
            try
            {
                await _hangar.RefreshShipMatrixAsync(force: false);
            }
            catch (Exception ex)
            {
                Log($"警告: Ship Matrix の取得に失敗しました ({ex.Message})。名寄せなしで同期を続行します。");
            }
            ct.ThrowIfCancellationRequested();

            // --- 1. 認証チェック (セッションは短命なので毎回行う) ---
            txtStatus.Text = "認証を確認中...";
            Log("認証を確認しています...");
            await NavigateAndWaitAsync(PledgesUrl + "1", ct);
            var firstJson = await ExtractAsync(ct);

            if (NeedsLogin(firstJson))
            {
                if (!await WaitForLoginAsync(ct))
                {
                    Log("中止しました");
                    txtStatus.Text = "中止";
                    return;
                }

                await NavigateAndWaitAsync(PledgesUrl + "1", ct);
                firstJson = await ExtractAsync(ct);
                if (NeedsLogin(firstJson))
                {
                    Log("エラー: ログインが確認できませんでした。同期を中止します。");
                    txtStatus.Text = "未認証";
                    return;
                }
            }
            Log("認証を確認しました。");

            // 保持 ON なら、認証確認直後にも暗号化保存しておく (同期中の異常終了で Closing が走らず取りこぼすのを防ぐ)。
            // ここでは保存のみで、プロファイル側の Cookie は削除しない (巡回に必要)
            if (chkKeepLogin.IsChecked == true)
            {
                await SaveSessionAsync(deleteProfileCookies: false);
            }

            // --- 2. ページ巡回 (1ページ10件固定・逐次・重複 id で終了判定)。各ページは取得直後に解析する ---
            var fetchedAt = DateTime.Now.ToString("o");
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var byId = new Dictionary<string, HangarPledge>(StringComparer.Ordinal);
            var pledges = new List<HangarPledge>();

            for (int page = 1; page <= MaxPages; page++)
            {
                ct.ThrowIfCancellationRequested();

                var json = await FetchPledgePageAsync(page, ct);

                var ids = ExtractPledgeIds(json, out var pledgeCount);

                // 0 件は「船が無い」ではなくページ構造の変化とみなす
                if (pledgeCount == 0)
                {
                    var dump = await SaveRawHtmlAsync();
                    throw new InvalidOperationException(
                        $"{page} ページ目の pledge が 0 件でした。ページ構造が変わった可能性があります。"
                        + (dump != null ? $" 保存した HTML を確認してください: {dump}" : ""));
                }

                // id が 1 件も取れないのに pledge 要素はある = id セレクタの破損。無言で終わらせない
                if (ids.All(string.IsNullOrEmpty))
                {
                    var dump = await SaveRawHtmlAsync();
                    throw new InvalidOperationException(
                        $"{page} ページ目で pledge id が 1 件も取得できませんでした。セレクタが変わった可能性があります。"
                        + (dump != null ? $" 保存した HTML を確認してください: {dump}" : ""));
                }

                int added = 0;
                foreach (var id in ids)
                    if (seenIds.Add(id)) added++;

                if (added == 0)
                {
                    Log($"{page} ページ目に新規 pledge がありません。最終ページとみなします。");
                    break;
                }

                // 解析 (失敗時は生 HTML を保存してから再スロー)
                List<HangarPledge> parsed;
                try
                {
                    parsed = _hangar.ParseExtractionJson(json, fetchedAt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"{page} ページ目の解析に失敗しました: {ex.Message}");
                    await SaveRawHtmlAsync();
                    throw;
                }

                foreach (var p in parsed)
                {
                    if (byId.ContainsKey(p.Id)) continue;
                    byId[p.Id] = p;
                    pledges.Add(p);
                }

                txtStatus.Text = $"取得中... {page} ページ / {seenIds.Count} 件";
                Log($"{page} ページ取得: {pledgeCount} 件 (新規 {added} 件 / 累計 {seenIds.Count} 件)");

                await Task.Delay(Random.Shared.Next(200, 501), ct);
            }

            // --- 3. CCU 抽出 ---
            var ccus = new List<HangarCcu>();
            foreach (var p in pledges)
            {
                var ccu = HangarService.TryParseCcu(p);
                if (ccu != null) ccus.Add(ccu);
            }

            // --- 4. Buy Back トークン残数 / Store Credit 残高 ---
            var (buybackTokens, storeCreditCents) = await FetchAccountInfoAsync(ct);

            // --- 5. ±30% ガード ---
            var prevCount = _hangar.LoadPledges().Count;
            if (prevCount > 0)
            {
                var diffRatio = Math.Abs(pledges.Count - prevCount) / (double)prevCount;
                if (diffRatio > 0.30)
                {
                    var ans = MessageBox.Show(this,
                        $"前回 {prevCount} 件に対し今回 {pledges.Count} 件です。取り込みますか？",
                        "件数が大きく変化しています", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (ans != MessageBoxResult.Yes)
                    {
                        Log("取り込みを中止しました (件数の変化を確認してください)");
                        txtStatus.Text = "中止";
                        return;
                    }
                }
            }

            // --- 6. 保存 ---
            _hangar.SaveSnapshot(pledges, ccus, fetchedAt);
            _hangar.SaveAccountInfo(buybackTokens, storeCreditCents);

            var shipCount = pledges.Sum(p => p.ShipNames.Count);
            Log($"完了: pledge {pledges.Count} 件 / 船 {shipCount} 件 / CCU {ccus.Count} 件"
                + (buybackTokens != null ? $" / Buy Back トークン {buybackTokens} 個" : "")
                + (storeCreditCents != null ? $" / Store Credit ${storeCreditCents.Value / 100.0:N2}" : ""));
            txtStatus.Text = "完了";
        }
        catch (OperationCanceledException)
        {
            Log("中止しました");
            txtStatus.Text = "中止";
        }
        catch (Exception ex)
        {
            Log($"エラー: {ex.Message}");
            txtStatus.Text = "エラー";
            MessageBox.Show(this, ex.Message, "同期エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btnSync.IsEnabled = true;
            btnCancel.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // pledge ページを 1 枚取得して抽出 JSON を返す。認証が切れていればログイン待ちののち同じページを再取得する
    private async Task<string> FetchPledgePageAsync(int page, CancellationToken ct)
    {
        var url = PledgesUrl + page;
        await NavigateAndWaitAsync(url, ct);
        var json = await ExtractAsync(ct);
        if (!NeedsLogin(json)) return json;

        Log($"{page} ページ目で認証が切れました。");
        if (!await WaitForLoginAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException(ct);
        }

        await NavigateAndWaitAsync(url, ct);
        json = await ExtractAsync(ct);
        if (NeedsLogin(json))
            throw new InvalidOperationException($"ログインが確認できませんでした ({page} ページ目)。同期を中止します。");
        return json;
    }

    private TaskCompletionSource<bool>? _loginTcs;

    private void LoginDone_Click(object sender, RoutedEventArgs e) => _loginTcs?.TrySetResult(true);

    // ログインページへ遷移し、「ログイン完了 → 再試行」の押下を待つ。
    // true: 押された / false: 中止された。呼び出し側が同じページを再取得して認証を確認する。
    // モーダルダイアログだと WebView の操作がブロックされるため、ボタン押下を非同期に待つ
    private async Task<bool> WaitForLoginAsync(CancellationToken ct)
    {
        Log("未認証です。表示されたページでログインし、完了したら「ログイン完了 → 再試行」を押してください。");
        txtStatus.Text = "ログイン待ち";
        await NavigateAndWaitAsync(LoginUrl, ct);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loginTcs = tcs;
        btnLoginDone.Visibility = Visibility.Visible;
        try
        {
            using (ct.Register(() => tcs.TrySetResult(false)))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            btnLoginDone.Visibility = Visibility.Collapsed;
            _loginTcs = null;
        }
    }

    // 本文の判定に加え、ログインページへ飛ばされた場合も未認証とみなす
    private bool NeedsLogin(string json)
    {
        var src = webView.CoreWebView2?.Source ?? "";
        if (src.Contains("/connect", StringComparison.OrdinalIgnoreCase)) return true;
        return !IsAuthenticated(json);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    // === アカウント情報 (Buy Back トークン / Store Credit) ===

    // buy-back-pledges → billing の順に遷移し、値を読み取る。取れなかった項目は null (保存しない)
    private async Task<(int? BuybackTokens, long? StoreCreditCents)> FetchAccountInfoAsync(CancellationToken ct)
    {
        var sel = HangarService.LoadSelectors();
        int? tokens = null;
        long? credit = null;

        // Buy Back
        txtStatus.Text = "Buy Back トークン残数を取得中...";
        await Task.Delay(Random.Shared.Next(200, 501), ct);
        var (bbText, bbSelText) = await FetchAccountPageAsync(BuybackUrl, sel.BuybackTokens, ct);
        if (!string.IsNullOrEmpty(sel.BuybackTokens) && !string.IsNullOrEmpty(bbSelText))
        {
            var m = Regex.Match(bbSelText, @"\d+");
            if (m.Success && int.TryParse(m.Value, out var v)) tokens = v;
        }
        if (tokens == null)
        {
            var m = BuybackTokensRegex.Match(bbText);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var v)) tokens = v;
        }
        if (tokens == null)
        {
            var dump = await SaveRawHtmlAsync("buyback");
            Log($"警告: Buy Back トークン残数を取得できませんでした (セレクタ未確定・HTML を保存しました{(dump != null ? $": {dump}" : "")})");
        }
        else
        {
            Log($"Buy Back トークン残数: {tokens} 個");
        }

        // Store Credit
        txtStatus.Text = "Store Credit 残高を取得中...";
        await Task.Delay(Random.Shared.Next(200, 501), ct);
        var (scText, scSelText) = await FetchAccountPageAsync(BillingUrl, sel.StoreCredit, ct);
        if (!string.IsNullOrEmpty(sel.StoreCredit) && !string.IsNullOrEmpty(scSelText) && Regex.IsMatch(scSelText, @"\d"))
        {
            credit = HangarService.ParseMoneyCents(scSelText);
        }
        if (credit == null)
        {
            var m = StoreCreditRegex.Match(scText);
            if (m.Success) credit = HangarService.ParseMoneyCents(m.Groups[1].Value);
        }
        if (credit == null)
        {
            var dump = await SaveRawHtmlAsync("billing");
            Log($"警告: Store Credit 残高を取得できませんでした (セレクタ未確定・HTML を保存しました{(dump != null ? $": {dump}" : "")})");
        }
        else
        {
            Log($"Store Credit 残高: ${credit.Value / 100.0:N2}");
        }

        return (tokens, credit);
    }

    // アカウント情報ページへ遷移し、(innerText, セレクタ要素の textContent) を返す。認証切れならログイン待ちののち再取得
    private async Task<(string Text, string SelText)> FetchAccountPageAsync(string url, string selector, CancellationToken ct)
    {
        var script = BuildAccountPageScript(selector);

        await NavigateAndWaitAsync(url, ct);
        var json = await RunScriptAsync(script, ct);
        if (NeedsLogin(json))
        {
            Log($"認証が切れました: {url}");
            if (!await WaitForLoginAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                throw new OperationCanceledException(ct);
            }
            await NavigateAndWaitAsync(url, ct);
            json = await RunScriptAsync(script, ct);
            if (NeedsLogin(json))
                throw new InvalidOperationException($"ログインが確認できませんでした ({url})。同期を中止します。");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        var selText = root.TryGetProperty("selText", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        return (text, selText);
    }

    // === ヘルパ ===

    private static bool IsAuthenticated(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("authenticated", out var v)
               && v.ValueKind == JsonValueKind.True;
    }

    // 抽出 JSON から pledge id の一覧を取り出す。pledgeCount は id が空のものも含む pledge 総数
    private static List<string> ExtractPledgeIds(string json, out int pledgeCount)
    {
        var ids = new List<string>();
        pledgeCount = 0;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("pledges", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return ids;

        foreach (var p in arr.EnumerateArray())
        {
            pledgeCount++;
            if (!p.TryGetProperty("id", out var idEl)) continue;
            var id = (idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.ToString()) ?? "";
            id = id.Trim();
            if (id.Length > 0) ids.Add(id);
        }
        return ids;
    }

    // 指定 URL へ遷移し、NavigationCompleted を待つ。タイムアウトは 60 秒
    private async Task NavigateAndWaitAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var core = webView.CoreWebView2
                   ?? throw new InvalidOperationException("WebView2 が初期化されていません。");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigationCompleted(object? s, CoreWebView2NavigationCompletedEventArgs args)
            => tcs.TrySetResult(args.IsSuccess);

        core.NavigationCompleted += OnNavigationCompleted;
        try
        {
            core.Navigate(url);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));
            if (finished != tcs.Task)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"ページの読み込みがタイムアウトしました: {url}");
            }

            if (!tcs.Task.Result)
                Log($"警告: ページの読み込みが正常に完了しませんでした: {url}");
        }
        finally
        {
            core.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    // 抽出スクリプトを実行し、生の JSON 文字列を返す
    private Task<string> ExtractAsync(CancellationToken ct) => RunScriptAsync(BuildExtractionScript(), ct);

    // 読み取り専用スクリプトを実行し、その戻り値 (文字列) を返す
    private async Task<string> RunScriptAsync(string script, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var core = webView.CoreWebView2
                   ?? throw new InvalidOperationException("WebView2 が初期化されていません。");

        // ExecuteScriptAsync の戻り値は「JSON エンコードされた文字列」なので中身を取り出す
        var raw = await core.ExecuteScriptAsync(script);
        if (string.IsNullOrEmpty(raw) || raw == "null")
            throw new InvalidOperationException("抽出スクリプトが結果を返しませんでした。");

        var json = JsonSerializer.Deserialize<string>(raw);
        if (string.IsNullOrEmpty(json))
            throw new InvalidOperationException("抽出スクリプトの結果が空でした。");
        return json;
    }

    // 生 HTML を WorkDir に保存する (セレクタ更新の材料)。保存パスを返す。失敗時は null
    // tag を指定すると hangar_dump_{tag}_*.html になる
    private async Task<string?> SaveRawHtmlAsync(string? tag = null)
    {
        try
        {
            var core = webView.CoreWebView2;
            if (core == null) return null;

            var raw = await core.ExecuteScriptAsync("document.documentElement.outerHTML");
            var html = (string.IsNullOrEmpty(raw) || raw == "null")
                ? ""
                : (JsonSerializer.Deserialize<string>(raw) ?? "");

            var prefix = string.IsNullOrEmpty(tag) ? "hangar_dump" : $"hangar_dump_{tag}";
            var path = Path.Combine(_workDir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            File.WriteAllText(path, html);
            Log($"生 HTML を保存しました: {path}");
            return path;
        }
        catch (Exception ex)
        {
            Log($"生 HTML の保存に失敗しました: {ex.Message}");
            return null;
        }
    }

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Log(message));
            return;
        }
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        txtLog.ScrollToEnd();
    }
}
