using Microsoft.Extensions.Configuration;
using StarCitizenJapaneseTextCreater;

var configBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false);
var configuration = configBuilder.Build();
var config = configuration.Get<AppConfig>() ?? new AppConfig();

// Resolve working directory
if (string.IsNullOrEmpty(config.WorkingDirectory))
    config.WorkingDirectory = Path.Combine(AppContext.BaseDirectory, "work");
Directory.CreateDirectory(config.WorkingDirectory);

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";

if (command == "")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("=== Star Citizen Japanese Text Creator ===");
    Console.WriteLine();
    Console.WriteLine("  1. Extract   - Data.p4k から global.ini を抽出");
    Console.WriteLine("  2. Translate - 未翻訳テキストを翻訳");
    Console.WriteLine("  3. Merge     - 翻訳を統合して global.ini を生成");
    Console.WriteLine("  4. Deploy    - ゲームディレクトリに配置");
    Console.WriteLine("  5. All       - 全工程を実行");
    Console.WriteLine("  0. Exit");
    Console.WriteLine();
    Console.Write("選択: ");
    var choice = Console.ReadLine()?.Trim();
    command = choice switch
    {
        "1" => "extract",
        "2" => "translate",
        "3" => "merge",
        "4" => "deploy",
        "5" => "all",
        "0" or null => "exit",
        _ => choice.ToLowerInvariant()
    };
}

if (command == "exit") return;

var enPath = Path.Combine(config.WorkingDirectory, "english", "global.ini");
var jaPath = Path.Combine(config.WorkingDirectory, "japanese_(japan)", "global.ini");
var untranslatedPath = Path.Combine(config.WorkingDirectory, "untranslated.jsonl");
var translatedPath = Path.Combine(config.WorkingDirectory, "translated.jsonl");
var progressPath = Path.Combine(config.WorkingDirectory, "progress.json");
var outputPath = Path.Combine(config.WorkingDirectory, "output", "global.ini");

try
{
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
        }
    }

    if (command is "merge" or "all")
    {
        Console.WriteLine("\n--- Merge ---");
        english ??= GlobalIniParser.Parse(enPath);
        japanese ??= GlobalIniParser.Parse(jaPath);

        var merged = IniMerger.Merge(english, japanese, translatedPath, config.ForceEnglishPatterns);
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
