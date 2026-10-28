using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace StarCitizenJapaneseTextCreater;

// 船の 3D 表示の WebView2 (［詳細］の欄の中と、別ウィンドウ Ship3DWindow で使う。詳細の表の WebView2 とは別)。
//   利用者データの置き場: 作業ディレクトリの webview2_ship3d (詳細の webview2_detail とは分ける)
//   仮想ホスト (SetVirtualHostNameToFolderMapping):
//     scjt-viewer.example → アプリの同梱ファイル (exe の横の wwwroot。three.js とビューア ship3d\viewer.html)
//     scjt-ship3d.example → キャッシュのフォルダ (Ship3DExports.Folder。GLB)。ビューアから fetch で読むので、別のホストからの読み込みを許す (Allow)
//   ビューアの失敗 (chrome.webview.postMessage の {type:'error'}) はアプリのログに残す
internal static class Ship3DWeb
{
    public const string ViewerHost = "scjt-viewer.example";

    // WebView2 を用意する (一度だけ)。使えなければ例外
    public static async Task InitAsync(WebView2 web)
    {
        if (web.CoreWebView2 != null) return;
        var folder = Path.Combine(DetailPanel.WorkDir(), "webview2_ship3d");
        Directory.CreateDirectory(folder);
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: folder);
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2!;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        // 中身はビューアだけ。利用者の操作でほかへ移る・新しい窓を開くことはさせない
        core.NavigationStarting += (_, e) => { if (e.IsUserInitiated) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "error"
                    && doc.RootElement.TryGetProperty("message", out var m))
                    App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] 表示: {m.GetString()}");
            }
            catch { }
        };
        web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1b, 0x1e, 0x24);
        core.SetVirtualHostNameToFolderMapping(ViewerHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
    }

    // その船の GLB をビューアで開く (キャッシュのフォルダは開くたびに割り当て直す。設定で置き場所を変えたときのため)
    public static void Show(WebView2 web, string shortName, string title)
    {
        var core = web.CoreWebView2 ?? throw new InvalidOperationException("WebView2 が用意できていません");
        var folder = Ship3DExports.Folder;
        try { core.ClearVirtualHostNameToFolderMapping(Ship3DExports.CacheHost); } catch { }
        core.SetVirtualHostNameToFolderMapping(Ship3DExports.CacheHost, folder, CoreWebView2HostResourceAccessKind.Allow);
        long ticks = 0;
        try { ticks = File.GetLastWriteTimeUtc(Ship3DExports.GlbPath(shortName)).Ticks; } catch { }
        // ?v= は書き出し直したときに前の GLB を使わないため (ファイルの更新日時)
        var glb = $"https://{Ship3DExports.CacheHost}/{Uri.EscapeDataString(shortName)}.glb?v={ticks}";
        var url = $"https://{ViewerHost}/ship3d/viewer.html?glb={Uri.EscapeDataString(glb)}&title={Uri.EscapeDataString(title)}";
        core.Navigate(url);
    }

    // 表示をやめる (読み込んだ GLB の分のメモリを放す)
    public static void Blank(WebView2 web)
    {
        try { web.CoreWebView2?.NavigateToString("<html><body style=\"background:#1b1e24\"></body></html>"); } catch { }
    }
}
