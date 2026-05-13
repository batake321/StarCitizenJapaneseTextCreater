using Microsoft.Extensions.Configuration;
using StarCitizenJapaneseTextCreater;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var configBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false);
var configuration = configBuilder.Build();
var config = configuration.Get<AppConfig>() ?? new AppConfig();

if (string.IsNullOrEmpty(config.WorkingDirectory))
    config.WorkingDirectory = Path.Combine(AppContext.BaseDirectory, "work");
Directory.CreateDirectory(config.WorkingDirectory);

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";

if (command == "")
{
    Console.WriteLine();
    Console.WriteLine("=== Star Citizen Japanese Text Creator ===");
    Console.WriteLine();
    Console.WriteLine("  --- 翻訳 ---");
    Console.WriteLine("  1. Extract   - Data.p4k から global.ini を抽出");
    Console.WriteLine("  2. Translate - 未翻訳テキストを翻訳");
    Console.WriteLine("  3. Merge     - 翻訳を統合して global.ini を生成");
    Console.WriteLine("  4. Deploy    - ゲームディレクトリに配置");
    Console.WriteLine("  5. All       - 翻訳の全工程を実行");
    Console.WriteLine();
    Console.WriteLine("  --- 翻訳DB ---");
    Console.WriteLine("  6. DB Stats    - 翻訳データベースの統計");
    Console.WriteLine("  7. CSV Export  - 翻訳をCSVにエクスポート");
    Console.WriteLine("  8. CSV Import  - CSVから翻訳をインポート");
    Console.WriteLine();
    Console.WriteLine("  --- プロファイル ---");
    Console.WriteLine("  A. Save Character   - キャラクターデザインを保存");
    Console.WriteLine("  B. Load Character   - キャラクターデザインを読込");
    Console.WriteLine("  C. Save Controls    - キーバインド設定を保存");
    Console.WriteLine("  D. Load Controls    - キーバインド設定を読込");
    Console.WriteLine();
    Console.WriteLine("  0. Exit");
    Console.WriteLine();
    Console.Write("選択: ");
    var choice = Console.ReadLine()?.Trim()?.ToUpperInvariant();
    command = choice switch
    {
        "1" => "extract",
        "2" => "translate",
        "3" => "merge",
        "4" => "deploy",
        "5" => "all",
        "6" => "dbstats",
        "7" => "csvexport",
        "8" => "csvimport",
        "A" => "savechar",
        "B" => "loadchar",
        "C" => "savectrl",
        "D" => "loadctrl",
        "0" or null => "exit",
        _ => choice?.ToLowerInvariant() ?? "exit"
    };
}

if (command == "exit") return;

var enPath = Path.Combine(config.WorkingDirectory, "english", "global.ini");
var jaPath = Path.Combine(config.WorkingDirectory, "japanese_(japan)", "global.ini");
var untranslatedPath = Path.Combine(config.WorkingDirectory, "untranslated.jsonl");
var translatedPath = Path.Combine(config.WorkingDirectory, "translated.jsonl");
var progressPath = Path.Combine(config.WorkingDirectory, "progress.json");
var outputPath = Path.Combine(config.WorkingDirectory, "output", "global.ini");
var dbPath = Path.Combine(config.WorkingDirectory, "translations.db");
var charSavesDir = Path.Combine(config.WorkingDirectory, "saves", "characters");
var ctrlSavesDir = Path.Combine(config.WorkingDirectory, "saves", "controls");

try
{
    // === Translation Pipeline ===
    if (command is "extract" or "all")
    {
        Console.WriteLine("\n--- Extract ---");
        P4kExtractor.ExtractLocalization(config.GamePath, config.WorkingDirectory);
    }

    Dictionary<string, string>? english = null;
    Dictionary<string, string>? japanese = null;

    if (command is "translate" or "merge" or "all")
    {
        if (!File.Exists(enPath) || !File.Exists(jaPath))
        {
            Console.WriteLine("global.ini not found. Running extract first...");
            P4kExtractor.ExtractLocalization(config.GamePath, config.WorkingDirectory);
        }
        Console.WriteLine("\nParsing global.ini...");
        english = GlobalIniParser.Parse(enPath);
        japanese = GlobalIniParser.Parse(jaPath);
        Console.WriteLine($"  English: {english.Count} entries");
        Console.WriteLine($"  Japanese: {japanese.Count} entries");

        // Sync to DB
        using var db = new TranslationDatabase(dbPath);
        db.ImportFromIni(english, japanese);
    }

    if (command is "translate" or "all")
    {
        Console.WriteLine("\n--- Translate ---");

        if (!File.Exists(untranslatedPath))
        {
            TranslationOrchestrator.BuildUntranslatedList(
                english!, japanese!, untranslatedPath, config.ForceEnglishPatterns);
        }

        var enabledBackends = config.Translation.Backends
            .Where(b => b.Enabled)
            .Select(TranslationBackend.Create)
            .ToList();

        if (enabledBackends.Count == 0)
        {
            Console.WriteLine("No translation backends enabled in appsettings.json.");
            Console.WriteLine("Enable at least one backend (Claude, Gemini, or Ollama) and set the API key.");
        }
        else
        {
            var progress = new ProgressTracker(progressPath);
            var orchestrator = new TranslationOrchestrator(
                enabledBackends,
                config.Translation.MaxRetries,
                untranslatedPath,
                translatedPath,
                progress);
            await orchestrator.RunAsync();

            // Sync AI translations to DB
            using var db = new TranslationDatabase(dbPath);
            db.ImportAiTranslations(translatedPath);
        }
    }

    if (command is "merge" or "all")
    {
        Console.WriteLine("\n--- Merge ---");
        english ??= GlobalIniParser.Parse(enPath);
        japanese ??= GlobalIniParser.Parse(jaPath);

        var merged = IniMerger.Merge(english, japanese, translatedPath, config.ForceEnglishPatterns, dbPath);
        GlobalIniParser.Write(outputPath, merged);
        Console.WriteLine($"Output: {outputPath} ({new FileInfo(outputPath).Length:N0} bytes)");
    }

    if (command is "deploy" or "all")
    {
        Console.WriteLine("\n--- Deploy ---");
        if (!File.Exists(outputPath))
        {
            Console.WriteLine($"Output file not found: {outputPath}");
            Console.WriteLine("Run merge first.");
        }
        else
        {
            GameDeployer.Deploy(config.GamePath, outputPath, config.OutputLanguage);
            Console.WriteLine("\nGame will use Japanese localization on next launch.");
        }
    }

    // === Translation DB ===
    if (command == "dbstats")
    {
        Console.WriteLine("\n--- Translation Database ---");
        if (!File.Exists(dbPath))
        {
            Console.WriteLine("Database not found. Run Extract or All first.");
        }
        else
        {
            using var db = new TranslationDatabase(dbPath);
            var (total, translated, official, ai, manual, untranslated) = db.GetStats();
            Console.WriteLine($"  Total entries:    {total:N0}");
            Console.WriteLine($"  Translated:       {translated:N0} ({(double)translated / total * 100:F1}%)");
            Console.WriteLine($"    Official:       {official:N0}");
            Console.WriteLine($"    AI translated:  {ai:N0}");
            Console.WriteLine($"    Manual/CSV:     {manual:N0}");
            Console.WriteLine($"  Untranslated:     {untranslated:N0}");
        }
    }

    if (command == "csvexport")
    {
        Console.WriteLine("\n--- CSV Export ---");
        if (!File.Exists(dbPath))
        {
            Console.WriteLine("Database not found. Run Extract or All first.");
        }
        else
        {
            var csvPath = Path.Combine(config.WorkingDirectory, "translations.csv");
            Console.Write($"Output path [{csvPath}]: ");
            var input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(input)) csvPath = input;

            using var db = new TranslationDatabase(dbPath);
            db.ExportCsv(csvPath);
        }
    }

    if (command == "csvimport")
    {
        Console.WriteLine("\n--- CSV Import ---");
        Console.Write("CSV file path: ");
        var csvPath = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
        {
            Console.WriteLine("File not found.");
        }
        else
        {
            using var db = new TranslationDatabase(dbPath);
            db.ImportCsv(csvPath);

            Console.Write("Merge and deploy now? (y/n): ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() == "y")
            {
                english = GlobalIniParser.Parse(enPath);
                japanese = GlobalIniParser.Parse(jaPath);
                var dbTranslations = db.GetAllTranslations();

                // Write DB translations as JSONL for merger
                using (var writer = new StreamWriter(translatedPath, false, System.Text.Encoding.UTF8))
                {
                    foreach (var (key, ja) in dbTranslations)
                    {
                        writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                            new { key, ja },
                            new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                    }
                }

                var merged = IniMerger.Merge(english, japanese, translatedPath, config.ForceEnglishPatterns, dbPath);
                GlobalIniParser.Write(outputPath, merged);
                GameDeployer.Deploy(config.GamePath, outputPath, config.OutputLanguage);
            }
        }
    }

    // === Profile Management ===
    if (command == "savechar")
    {
        Console.WriteLine("\n--- Save Character Design ---");
        ProfileManager.ListCharacterSaves(charSavesDir);
        Console.Write("Save name: ");
        var name = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            var savePath = Path.Combine(charSavesDir, name);
            ProfileManager.SaveCharacter(config.GamePath, savePath);
        }
    }

    if (command == "loadchar")
    {
        Console.WriteLine("\n--- Load Character Design ---");
        ProfileManager.ListCharacterSaves(charSavesDir);
        Console.Write("Load name: ");
        var name = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            var loadPath = Path.Combine(charSavesDir, name);
            if (Directory.Exists(loadPath))
                ProfileManager.LoadCharacter(config.GamePath, loadPath);
            else
                Console.WriteLine($"Save not found: {name}");
        }
    }

    if (command == "savectrl")
    {
        Console.WriteLine("\n--- Save Controls / Keybinds ---");
        ProfileManager.ListControlSaves(ctrlSavesDir);
        Console.Write("Save name: ");
        var name = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            var savePath = Path.Combine(ctrlSavesDir, name);
            ProfileManager.SaveControls(config.GamePath, savePath);
        }
    }

    if (command == "loadctrl")
    {
        Console.WriteLine("\n--- Load Controls / Keybinds ---");
        ProfileManager.ListControlSaves(ctrlSavesDir);
        Console.Write("Load name: ");
        var name = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            var loadPath = Path.Combine(ctrlSavesDir, name);
            if (Directory.Exists(loadPath))
                ProfileManager.LoadControls(config.GamePath, loadPath);
            else
                Console.WriteLine($"Save not found: {name}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"\nError: {ex.Message}");
    if (args.Contains("--debug"))
        Console.WriteLine(ex.StackTrace);
}

if (args.Length == 0)
{
    Console.WriteLine("\nPress any key to exit...");
    Console.ReadKey();
}
