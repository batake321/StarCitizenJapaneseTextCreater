using System.Text;

namespace StarCitizenJapaneseTextCreater;

public static class GameDeployer
{
    public static void Deploy(string gamePath, string globalIniPath, string language)
    {
        var destDir = Path.Combine(gamePath, "data", "Localization", language);
        Directory.CreateDirectory(destDir);

        var destPath = Path.Combine(destDir, "global.ini");
        File.Copy(globalIniPath, destPath, overwrite: true);
        Console.WriteLine($"Deployed: {destPath} ({new FileInfo(destPath).Length:N0} bytes)");

        var userCfgPath = Path.Combine(gamePath, "user.cfg");
        var cfgLine = $"g_language = {language}";

        if (File.Exists(userCfgPath))
        {
            var lines = File.ReadAllLines(userCfgPath).ToList();
            var idx = lines.FindIndex(l => l.TrimStart().StartsWith("g_language"));
            if (idx >= 0)
                lines[idx] = cfgLine;
            else
                lines.Add(cfgLine);
            File.WriteAllLines(userCfgPath, lines, Encoding.UTF8);
        }
        else
        {
            File.WriteAllText(userCfgPath, cfgLine + "\n", Encoding.UTF8);
        }

        Console.WriteLine($"Updated: {userCfgPath}");
    }
}
