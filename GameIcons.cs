using System.IO;

namespace StarCitizenJapaneseTextCreater;

// ゲームの施設のアイコン (Data.p4k の Data/UI/Textures/General/Markers/*.svg。64×64 の白一色の SVG) を、利用者の Data.p4k から作業フォルダへ取り出して使う
// (2026-11-02、利用者の指示「ゲームでは、修理など含めアイコンで示される」。ゲームの画像はリポジトリ・配布物には入れず、使うときに利用者の Data.p4k から取り出す)。
// 置き場所は 作業フォルダ\game_icons\<Data.p4k の印のハッシュ>\ (Data.p4k が変わったら取り出し直す)。取り出しは starbreaker の p4k extract
internal static class GameIcons
{
    // 取り出すもの (starbreaker p4k extract --filter の glob)
    private static readonly string[] Filters = ["Data/UI/Textures/General/Markers/*.svg", "Data/UI/Textures/Vector/General/CommonIcons/*.svg"];
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // 取り出したアイコンのフォルダ (Data/UI/Textures/... の上。例: <Folder>\Data\UI\Textures\General\Markers\marker_icon_clinic.svg)。
    // 取り出していなければ取り出す。Data.p4k が見つからない・取り出せなければ null (呼ぶ側はアイコン無しで出す)
    public static async Task<string?> EnsureAsync(CancellationToken ct = default)
    {
        var stamp = Ship3DExports.CurrentStamp();
        if (stamp == null) return null;
        var dir = Path.Combine(DetailPanel.WorkDir(), "game_icons", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stamp.Key)))[..16]);
        var done = Path.Combine(dir, ".done");
        if (File.Exists(done)) return dir;
        await Gate.WaitAsync(ct);
        try
        {
            if (File.Exists(done)) return dir;
            // (2026-11-02、検証の指摘) 3D の書き出しと同じく、CPU 使用率が 90% 以上 (Ship3DExports の判定) の間は starbreaker を動かさずに待つ (ゲームの邪魔にならないように)。
            // 呼ぶ側 (［詳細］・［マップ］のアイコンの割り当て、地図の端末のアイコン) は待たずに表示を続け、取り出せたらアイコンに差し替える
            if (Ship3DExports.CpuHighNow)
            {
                App.AppendStartupLog(App.Config.WorkingDirectory, "[ゲームのアイコン] CPU 使用率が 90% 以上なので、下がるまで取り出しを待ちます");
                while (Ship3DExports.CpuHighNow) await Task.Delay(1000, ct);
            }
            Directory.CreateDirectory(dir);
            foreach (var f in Filters)
            {
                // (2026-11-04、利用者の決定「starbreaker の処理を自前で持つ」) starbreaker の p4k extract の代わりに、アプリの中の読み取り (P4kTools) で取り出す
                var (code, tail) = await P4kTools.RunExtractAsync(stamp.Path, dir, f, null, false, ct);
                if (code != 0)
                {
                    App.AppendStartupLog(App.Config.WorkingDirectory, $"[ゲームのアイコン] 取り出せませんでした ({f}): 終了コード {code} {tail}");
                    return null;
                }
            }
            await File.WriteAllTextAsync(done, stamp.Key, ct);
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ゲームのアイコン] 取り出しました: {dir}");
            // (2026-11-02、検証の指摘) 前の Data.p4k の印で取り出したフォルダ (game_icons の下の今の印でないもの) は消す
            foreach (var old in Directory.GetDirectories(Path.Combine(DetailPanel.WorkDir(), "game_icons")))
            {
                if (string.Equals(Path.GetFullPath(old).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(old, recursive: true); App.AppendStartupLog(App.Config.WorkingDirectory, $"[ゲームのアイコン] 前の Data.p4k のアイコンを消しました: {old}"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[ゲームのアイコン] 前の Data.p4k のアイコンを消せませんでした: {old}: {ex.Message}"); }
            }
            return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[ゲームのアイコン] 取り出せませんでした: {ex.Message}");
            return null;
        }
        finally { Gate.Release(); }
    }

    // 施設のアイコンのファイル名 (Markers の中) → 取り出したフォルダの中のパス
    public static string MarkerPath(string folder, string fileName) => Path.Combine(folder, "Data", "UI", "Textures", "General", "Markers", fileName);
}
