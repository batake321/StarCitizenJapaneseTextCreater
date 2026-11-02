using System.IO;
using Microsoft.Web.WebView2.Core;

namespace StarCitizenJapaneseTextCreater;

// 星図の施設のアイコン (ゲームの svg) を［詳細］・［マップ］の WebView2 で見せる。
//   svg は GameIcons.EnsureAsync が利用者の Data.p4k から作業フォルダへ取り出したもの (リポジトリ・配布物には入れない)。
//   WebView2 の仮想ホスト (SetVirtualHostNameToFolderMapping) で、取り出したフォルダ (<フォルダ>\Data\UI\Textures\…) を https://scjt-icons.example/ に割り当てる。
//   ページ (NavigateToString の自分で作った HTML) からは <img>・svg の <image> で読む (CSP の img-src にこのホストだけを足す: DetailHtml.Page)。
//   取り出せないとき (Data.p4k が無い・starbreaker が無い等) はアイコン無しで文字だけ出す (Ready が false のまま)
internal static class AmenityIcons
{
    public const string Host = "scjt-icons.example";

    private static Task<string?>? s_folder;

    // アイコンのフォルダ (一度だけ取り出す。取り出せなければ null。失敗はこのアプリの起動の間は覚える)
    public static Task<string?> EnsureFolderAsync() => s_folder ??= EnsureCoreAsync();

    private static async Task<string?> EnsureCoreAsync()
    {
        try { return await GameIcons.EnsureAsync(); }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[施設のアイコン] 取り出せませんでした: {ex.Message}");
            return null;
        }
    }

    // WebView2 にアイコンのフォルダを割り当てる (取り出していなければ取り出してから)。割り当てられたら true
    public static async Task<bool> MapAsync(CoreWebView2 core)
    {
        var folder = await EnsureFolderAsync();
        if (folder == null || !Directory.Exists(folder)) return false;
        try
        {
            try { core.ClearVirtualHostNameToFolderMapping(Host); } catch { }
            // ページは NavigateToString の HTML (元は about:blank) なので、別のホストからの読み込みを許す (Allow。Ship3DWeb のキャッシュのホストと同じ)
            core.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.Allow);
            return true;
        }
        catch (Exception ex)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[施設のアイコン] WebView2 に割り当てられませんでした: {ex.Message}");
            return false;
        }
    }

    // アイコンのパス (星図の施設の icon。Data/ の下。例 UI/Textures/General/Markers/marker_icon_refinery.svg) → 仮想ホストの URL。
    // svg でない・".." を含む・空のときは null
    public static string? Url(string icon)
    {
        var parts = (icon ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p == ".." || p == ".") || !parts[^1].EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return null;
        return $"https://{Host}/Data/" + string.Join("/", parts.Select(Uri.EscapeDataString));
    }
}
