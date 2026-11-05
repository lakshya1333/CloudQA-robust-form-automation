using System.Text.Json;

namespace CloudQA.RobustForm.Config;

/// <summary>
/// Settings precedence (later wins): built-in defaults, appsettings.json,
/// environment variables (CLOUDQA_*), command-line arguments.
/// </summary>
public static class ConfigLoader
{
    public const string HelpText = """
        CloudQA robust form automation

        Usage:
          dotnet run -- [options]
          dotnet run -- <url-or-html-file>

        Options:
          --url <url|file>        Page to test (http/https URL or a local .html path)
          --suite <folder>        Run against every .html page in a folder (e.g. TestPages)
          --browser <name>        chrome | edge | firefox          (default: chrome)
          --headless [true|false] Run without a visible browser window
          --timeout <seconds>     How long to keep searching for the fields (default: 15)
          --first-name <text>     Value for First Name             (default: John)
          --gender <text>         Gender option to choose          (default: Female)
          --state <text>          State option to choose           (default: India)
          --no-iframes            Do not search inside iframes
          --no-shadow-dom         Do not search inside shadow DOM
          --min-score <number>    Confidence needed to accept a field (default: 0.45)
          --report-dir <folder>   Where reports/screenshots go     (default: Reports)
          --config <file>         Use another settings file instead of appsettings.json
          --help                  Show this help

        Environment variables: CLOUDQA_URL, CLOUDQA_BROWSER, CLOUDQA_HEADLESS, CLOUDQA_TIMEOUT
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppConfig Load(string[] args)
    {
        var configPath = ValueOf(args, "--config");
        var cfg = ReadFile(configPath) ?? new AppConfig();

        ApplyEnvironment(cfg);
        ApplyArguments(cfg, args);

        cfg.Url = NormalizeUrl(cfg.Url);
        if (cfg.SuiteDirectory != null) cfg.SuiteDirectory = ResolvePath(cfg.SuiteDirectory) ?? cfg.SuiteDirectory;
        if (cfg.TimeoutSeconds <= 0) cfg.TimeoutSeconds = 15;
        return cfg;
    }

    public static bool WantsHelp(string[] args) =>
        args.Any(a => a is "--help" or "-h" or "/?");

    private static AppConfig? ReadFile(string? explicitPath)
    {
        var candidates = explicitPath != null
            ? new[] { explicitPath }
            : new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
                Path.Combine(AppContext.BaseDirectory, "appsettings.json")
            };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Could not read settings file '{path}': {ex.Message}", ex);
            }
        }

        if (explicitPath != null) throw new FileNotFoundException("Settings file not found", explicitPath);
        return null;
    }

    private static void ApplyEnvironment(AppConfig cfg)
    {
        var url = Environment.GetEnvironmentVariable("CLOUDQA_URL");
        if (!string.IsNullOrWhiteSpace(url)) cfg.Url = url;

        var browser = Environment.GetEnvironmentVariable("CLOUDQA_BROWSER");
        if (!string.IsNullOrWhiteSpace(browser)) cfg.Browser = browser;

        if (bool.TryParse(Environment.GetEnvironmentVariable("CLOUDQA_HEADLESS"), out var headless)) cfg.Headless = headless;
        if (int.TryParse(Environment.GetEnvironmentVariable("CLOUDQA_TIMEOUT"), out var timeout)) cfg.TimeoutSeconds = timeout;
    }

    private static void ApplyArguments(AppConfig cfg, string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value after {arg}");

            switch (arg.ToLowerInvariant())
            {
                case "--url": cfg.Url = Next(); break;
                case "--suite": cfg.SuiteDirectory = Next(); break;
                case "--browser": cfg.Browser = Next(); break;
                case "--timeout": cfg.TimeoutSeconds = int.Parse(Next()); break;
                case "--first-name": cfg.TestData.FirstName = Next(); break;
                case "--gender": cfg.TestData.Gender = Next(); break;
                case "--state": cfg.TestData.State = Next(); break;
                case "--report-dir": cfg.ReportDirectory = Next(); break;
                case "--min-score": cfg.MinScore = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--config": Next(); break; // already handled
                case "--no-iframes": cfg.SearchIframes = false; break;
                case "--no-shadow-dom": cfg.IncludeShadowDom = false; break;
                case "--headless":
                    if (i + 1 < args.Length && bool.TryParse(args[i + 1], out var h)) { cfg.Headless = h; i++; }
                    else cfg.Headless = true;
                    break;
                default:
                    if (!arg.StartsWith("-", StringComparison.Ordinal)) cfg.Url = arg; // positional URL
                    else throw new ArgumentException($"Unknown option '{arg}'. Use --help.");
                    break;
            }
        }
    }

    private static string? ValueOf(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    /// <summary>Accepts http(s)/file URLs, or a path to a local .html file.</summary>
    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("No URL configured. Use --url.");
        url = url.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile))
        {
            return uri.AbsoluteUri;
        }

        var local = ResolvePath(url);
        if (local != null && File.Exists(local)) return new Uri(local).AbsoluteUri;

        // Looks like a host without scheme, e.g. app.cloudqa.io/home/AutomationPracticeForm
        return "https://" + url.TrimStart('/');
    }

    private static string? ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var fromCwd = Path.GetFullPath(path);
        if (File.Exists(fromCwd) || Directory.Exists(fromCwd)) return fromCwd;
        var fromBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (File.Exists(fromBase) || Directory.Exists(fromBase)) return fromBase;
        return null;
    }
}
