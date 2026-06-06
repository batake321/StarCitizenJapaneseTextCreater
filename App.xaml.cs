using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Extensions.Configuration;

namespace StarCitizenJapaneseTextCreater;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = new();
    public static string ConfigPath { get; private set; } = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2 && e.Args[0] == "--export-backup")
        {
            RunBackupExport(e.Args[1]);
            Shutdown(0);
            return;
        }

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StarCitizenJapaneseTextCreater");
        Directory.CreateDirectory(appDataDir);
        ConfigPath = Path.Combine(appDataDir, "appsettings.json");

        // Migrate: if user config doesn't exist yet but exe-local one has user data, copy it
        var exeLocalConfig = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(ConfigPath) && File.Exists(exeLocalConfig))
            File.Copy(exeLocalConfig, ConfigPath, overwrite: false);

        // Load: user config (AppData) > default (exe dir)
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile(ConfigPath, optional: true, reloadOnChange: false);
        var configuration = configBuilder.Build();
        Config = configuration.Get<AppConfig>() ?? new AppConfig();

        if (string.IsNullOrEmpty(Config.GamePath) || !Directory.Exists(Config.GamePath))
        {
            var detected = DetectFromLauncherLog();
            if (detected.Count > 0)
                Config.GamePath = detected[0];
        }

        if (string.IsNullOrEmpty(Config.WorkingDirectory))
            Config.WorkingDirectory = @"C:\temp";
        Directory.CreateDirectory(Config.WorkingDirectory);

        // 同梱DBをWorkDirにコピー（同梱版が新しければ上書き）
        var baseDir = AppContext.BaseDirectory;
        foreach (var dbName in new[] { "translations.db", "gamedata_cache.db" })
        {
            var src = Path.Combine(baseDir, dbName);
            var dest = Path.Combine(Config.WorkingDirectory, dbName);
            if (!File.Exists(src)) continue;
            if (!File.Exists(dest) || File.GetLastWriteTime(src) > File.GetLastWriteTime(dest))
                File.Copy(src, dest, overwrite: true);
        }
    }

    public static List<string> DetectGameChannels()
    {
        var paths = DetectFromLauncherLog();
        if (paths.Count > 0) return paths;

        var gamePath = Config.GamePath;
        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath)) return paths;

        var parent = Path.GetDirectoryName(gamePath);
        if (parent == null || !Directory.Exists(parent)) return new List<string> { gamePath };

        return Directory.GetDirectories(parent)
            .Where(d => File.Exists(Path.Combine(d, "Data.p4k")))
            .ToList();
    }

    private void RunBackupExport(string outputDir)
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StarCitizenJapaneseTextCreater");
        var configPath = Path.Combine(appDataDir, "appsettings.json");
        var exeLocalConfig = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile(configPath, optional: true, reloadOnChange: false);
        var config = configBuilder.Build().Get<AppConfig>() ?? new AppConfig();

        var workDir = string.IsNullOrEmpty(config.WorkingDirectory) ? @"C:\temp" : config.WorkingDirectory;
        var translationDb = Path.Combine(workDir, "translations.db");
        var indexDb = Path.Combine(workDir, "gamedata_cache.db");

        Directory.CreateDirectory(outputDir);

        var outPath = Path.Combine(outputDir, "sc_japanese_backup.zip");
        DatabaseBackupService.ExportAsync(translationDb, indexDb, outPath,
            s => Console.WriteLine($"  {s}")).Wait();

        if (File.Exists(outPath))
            Console.WriteLine($"Backup: {new FileInfo(outPath).Length / 1024.0:N0} KB -> {outPath}");

        Console.WriteLine("Export complete.");
    }

    private static List<string> DetectFromLauncherLog()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "rsilauncher", "logs", "log.log");

        if (!File.Exists(logPath)) return new List<string>();

        var channelPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var regex = new Regex(@"\[Launcher::launch\] Launching Star Citizen \w+ from \((.+?)\)");

        try
        {
            foreach (var line in File.ReadLines(logPath))
            {
                var m = regex.Match(line);
                if (!m.Success) continue;
                var path = m.Groups[1].Value.Replace("\\\\", "\\");
                var channel = Path.GetFileName(path);
                channelPaths[channel] = path;
            }
        }
        catch { }

        if (channelPaths.Count == 0) return new List<string>();

        var ordered = new List<string>();
        foreach (var ch in new[] { "PTU", "EPTU", "TECH-PREVIEW", "HOTFIX", "LIVE" })
        {
            if (channelPaths.TryGetValue(ch, out var p))
            {
                ordered.Add(p);
                channelPaths.Remove(ch);
            }
        }
        ordered.AddRange(channelPaths.Values);

        return ordered.Where(Directory.Exists).ToList();
    }
}
