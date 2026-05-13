namespace StarCitizenJapaneseTextCreater;

public static class ProfileManager
{
    private static readonly string[] CharacterFiles = { "T1.chf" };

    private static readonly string[] ControlFiles =
    {
        @"Profiles\default\actionmaps.xml",
        @"Profiles\default\attributes.xml",
        @"Profiles\default\profile.xml",
        @"Profiles\default\HintStatus.xml",
        @"Profiles\defaultActions.xml",
        @"Profiles\ActiveFilters.xml",
        @"Profiles\StringFilters.xml",
    };

    private static string GetUserDir(string gamePath)
    {
        var userDir = Path.Combine(gamePath, "user", "client", "0");
        if (!Directory.Exists(userDir))
            throw new DirectoryNotFoundException($"User directory not found: {userDir}");
        return userDir;
    }

    // --- Character Design ---

    public static void SaveCharacter(string gamePath, string savePath)
    {
        var userDir = GetUserDir(gamePath);
        var charDir = Path.Combine(userDir, "customcharacters");
        Directory.CreateDirectory(savePath);

        int count = 0;
        foreach (var file in CharacterFiles)
        {
            var src = Path.Combine(charDir, file);
            if (File.Exists(src))
            {
                File.Copy(src, Path.Combine(savePath, file), overwrite: true);
                count++;
            }
        }

        // Also save any other .chf files
        if (Directory.Exists(charDir))
        {
            foreach (var chf in Directory.GetFiles(charDir, "*.chf"))
            {
                var name = Path.GetFileName(chf);
                var dest = Path.Combine(savePath, name);
                if (!File.Exists(dest))
                {
                    File.Copy(chf, dest, overwrite: true);
                    count++;
                }
            }
        }

        Console.WriteLine($"  Character saved: {count} files -> {savePath}");
    }

    public static void LoadCharacter(string gamePath, string loadPath)
    {
        var userDir = GetUserDir(gamePath);
        var charDir = Path.Combine(userDir, "customcharacters");
        Directory.CreateDirectory(charDir);

        int count = 0;
        foreach (var chf in Directory.GetFiles(loadPath, "*.chf"))
        {
            var dest = Path.Combine(charDir, Path.GetFileName(chf));
            File.Copy(chf, dest, overwrite: true);
            count++;
        }

        Console.WriteLine($"  Character loaded: {count} files from {loadPath}");
    }

    public static void ListCharacterSaves(string savesDir)
    {
        if (!Directory.Exists(savesDir))
        {
            Console.WriteLine("  No character saves found.");
            return;
        }

        var dirs = Directory.GetDirectories(savesDir);
        if (dirs.Length == 0)
        {
            Console.WriteLine("  No character saves found.");
            return;
        }

        Console.WriteLine("  Character saves:");
        foreach (var dir in dirs)
        {
            var chfCount = Directory.GetFiles(dir, "*.chf").Length;
            var time = Directory.GetLastWriteTime(dir);
            Console.WriteLine($"    {Path.GetFileName(dir)} ({chfCount} files, {time:yyyy-MM-dd HH:mm})");
        }
    }

    // --- Controls / Keybinds ---

    public static void SaveControls(string gamePath, string savePath)
    {
        var userDir = GetUserDir(gamePath);
        Directory.CreateDirectory(savePath);

        int count = 0;
        foreach (var relPath in ControlFiles)
        {
            var src = Path.Combine(userDir, relPath);
            if (File.Exists(src))
            {
                var dest = Path.Combine(savePath, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: true);
                count++;
            }
        }

        // Also save custom control mappings
        var mappingsDir = Path.Combine(userDir, "controls", "mappings");
        if (Directory.Exists(mappingsDir))
        {
            foreach (var file in Directory.GetFiles(mappingsDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(userDir, file);
                var dest = Path.Combine(savePath, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
                count++;
            }
        }

        Console.WriteLine($"  Controls saved: {count} files -> {savePath}");
    }

    public static void LoadControls(string gamePath, string loadPath)
    {
        var userDir = GetUserDir(gamePath);
        int count = 0;

        foreach (var file in Directory.GetFiles(loadPath, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(loadPath, file);
            var dest = Path.Combine(userDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            count++;
        }

        Console.WriteLine($"  Controls loaded: {count} files from {loadPath}");
    }

    public static void ListControlSaves(string savesDir)
    {
        if (!Directory.Exists(savesDir))
        {
            Console.WriteLine("  No control saves found.");
            return;
        }

        var dirs = Directory.GetDirectories(savesDir);
        if (dirs.Length == 0)
        {
            Console.WriteLine("  No control saves found.");
            return;
        }

        Console.WriteLine("  Control saves:");
        foreach (var dir in dirs)
        {
            var fileCount = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
            var time = Directory.GetLastWriteTime(dir);
            Console.WriteLine($"    {Path.GetFileName(dir)} ({fileCount} files, {time:yyyy-MM-dd HH:mm})");
        }
    }
}
