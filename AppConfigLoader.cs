using Microsoft.Extensions.Configuration;

namespace StarCitizenJapaneseTextCreater;

// 起動時の設定の読み込み。既定 (exe と同じフォルダの appsettings.json) と個人の設定 (%LOCALAPPDATA% の appsettings.json) を重ねて読む。
// 個人の設定ファイルが壊れていて読めない (JSON の解析・設定値の読み取りで例外になる) ときは、そのファイルを
// 「appsettings.json.broken-<日時>」に名前を変えて残し、今までどおり既定を写して初期値で起動する (BrokenBackupPath に残した場所を返す)。
// --export-backup (copyDefaultIfMissing: false) では既定を写さない (個人の設定が無いときも、壊れていて改名したときも、既定を読むだけ)。
// 改名できない (別のプロセスが開いている等) ときは、別名の控えを File.Copy で作って既定で続ける。控えも作れないときは、
// その起動中は個人の設定ファイルへ保存しない (SaveDisabled。壊れた元のファイルを控え無しで上書きしないため)。
// 既定の appsettings.json 自体が読めないときは、今までどおり例外にする
internal static class AppConfigLoader
{
    // Notice: 起動後 (--export-backup ではコンソール) に 1 回だけ出す知らせ。無ければ null
    // SaveDisabled: この起動中は個人の設定ファイルへ保存しない (壊れたファイルの控えを作れなかったとき)
    internal sealed record Result(AppConfig Config, string? BrokenBackupPath, string? Notice = null, bool SaveDisabled = false);

    // copyDefaultIfMissing: 個人の設定がまだ無いときに既定を写すか (画面のある起動は true。--export-backup は今までどおり写さない)
    internal static Result Load(string baseDir, string userConfigPath, DateTime now, bool copyDefaultIfMissing = true)
    {
        var defaultPath = Path.Combine(baseDir, "appsettings.json");
        // 個人の設定がまだ無ければ、既定を写す (今までの流れ)
        if (copyDefaultIfMissing && !File.Exists(userConfigPath) && File.Exists(defaultPath))
            File.Copy(defaultPath, userConfigPath, overwrite: false);

        try
        {
            return new Result(Build(baseDir, userConfigPath), null);
        }
        catch (Exception) when (File.Exists(userConfigPath))
        {
            // 既定だけで読めるか確かめる (読めなければ既定が壊れているので、その例外をそのまま出す)
            var defaults = Build(baseDir, null);
            // 別名の衝突 (同じ秒にもう一度起動した等) は -2, -3 … で避ける
            var backup = userConfigPath + ".broken-" + now.ToString("yyyyMMdd-HHmmss");
            for (int n = 2; File.Exists(backup) && n < 1000; n++)
                backup = userConfigPath + ".broken-" + now.ToString("yyyyMMdd-HHmmss") + "-" + n;
            try
            {
                File.Move(userConfigPath, backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 別名にできない (別のプロセスが開いている等) ときは、まず別名の控えを作る (元のファイルはそのまま)。
                // 控えが作れたら、その場所を知らせて既定の設定で続ける (保存は今までどおり。上書きしても控えが残る)
                try
                {
                    File.Copy(userConfigPath, backup, overwrite: false);
                    return new Result(defaults, backup, BrokenNotice(backup));
                }
                catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException)
                {
                    // 控えも作れないときは、この起動中は個人の設定ファイルへ保存しない
                    return new Result(defaults, null, BrokenNotSavedNotice(userConfigPath), SaveDisabled: true);
                }
            }
            // 画面のある起動は今までどおり既定を写す。--export-backup は写さない (改名して既定で読むだけ)
            if (copyDefaultIfMissing && File.Exists(defaultPath))
                File.Copy(defaultPath, userConfigPath, overwrite: false);
            return new Result(copyDefaultIfMissing ? Build(baseDir, userConfigPath) : defaults, backup, BrokenNotice(backup));
        }
    }

    private static AppConfig Build(string baseDir, string? userConfigPath)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: false);
        if (userConfigPath != null)
            builder.AddJsonFile(userConfigPath, optional: true, reloadOnChange: false);
        return builder.Build().Get<AppConfig>() ?? new AppConfig();
    }

    // 起動後に 1 回だけ出す知らせの文
    internal static string BrokenNotice(string backupPath) =>
        $"設定ファイルが壊れていたので初期値で起動しました。元のファイルは {backupPath} に残しています。";

    internal static string BrokenNotSavedNotice(string userConfigPath) =>
        $"設定ファイルが壊れていたので初期値で起動しました。元のファイルを別名にできなかったので、この起動中は設定を保存しません: {userConfigPath}";
}
