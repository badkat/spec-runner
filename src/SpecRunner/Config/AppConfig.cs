using System.Text.Json;
using SpecRunner.Core;

namespace SpecRunner.Config;

/// <summary>
/// Everything the application resolved at startup, and where each value came from.
///
/// Feature 8.10 - every setting is echoed to the terminal before the server binds, with any
/// secret elided. The <see cref="Source"/> on each entry exists because "what is the port?" and
/// "why is the port that?" are different questions, and the second one is the one an operator
/// asks when something is wrong.
/// </summary>
public sealed record ConfigValue(string Key, string Value, string Source, bool Secret);

public sealed class AppConfig
{
    public const string ApiKeyEnvironmentVariable = "SPECRUNNER_API_KEY";

    private AppConfig(
        string projectDirectory,
        string promptsDirectory,
        string baseUrl,
        string apiKey,
        int port,
        IReadOnlyList<ConfigValue> resolved)
    {
        ProjectDirectory = projectDirectory;
        PromptsDirectory = promptsDirectory;
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        Port = port;
        Resolved = resolved;
    }

    public string ProjectDirectory { get; }

    public string PromptsDirectory { get; }

    public string BaseUrl { get; }

    public string ApiKey { get; }

    /// <summary>Feature 9.2 - a fixed port. No auto-increment; a bound port is a fatal terminal error.</summary>
    public int Port { get; }

    public IReadOnlyList<ConfigValue> Resolved { get; }

    public string ListenUrl => $"http://127.0.0.1:{Port}";

    /// <summary>
    /// Resolves configuration from an optional JSON file plus one environment variable for the
    /// API key. Relative paths in the file resolve against the file's own directory, so a config
    /// file is portable and does not depend on the working directory the operator happened to be
    /// in.
    /// </summary>
    public static AppConfig Resolve(string? explicitConfigPath)
    {
        var defaultPath = Path.Combine(AppContext.BaseDirectory, "specrunner.config.json");
        var configPath = explicitConfigPath is null
            ? File.Exists(defaultPath) ? defaultPath : null
            : Path.GetFullPath(explicitConfigPath);

        if (explicitConfigPath is not null && !File.Exists(configPath!))
        {
            throw new HaltException($"The config file '{configPath}' does not exist.");
        }

        var baseDirectory = configPath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(configPath)!;
        var file = configPath is null ? new FileSettings() : ReadFile(configPath);
        var fileSource = configPath is null ? "built-in default" : configPath;

        var resolved = new List<ConfigValue>();

        string Pick(string key, string? fromFile, string fallback)
        {
            var value = string.IsNullOrWhiteSpace(fromFile) ? fallback : fromFile;
            resolved.Add(new ConfigValue(key, value, string.IsNullOrWhiteSpace(fromFile) ? "built-in default" : fileSource, false));
            return value;
        }

        var projectDirectory = Path.GetFullPath(Path.Combine(baseDirectory, Pick("project_directory", file.ProjectDirectory, "project")));
        // Templates live with the code that names them, not with the operator's project data.
        // A step declares its template as a compiled string constant and the template's hash is
        // one of that step's declared inputs (feature 4.3), so a template is a dependency of a
        // specific class in the same way a source file is. The path stays configurable because
        // pointing a run at a different prompt set is how the workflow gets tested without a
        // model, but the default follows the code.
        var promptsDirectory = Path.GetFullPath(Path.Combine(
            baseDirectory,
            Pick("prompts_directory", file.PromptsDirectory, Path.Combine("src", "SpecRunner", "prompts"))));
        var baseUrl = Pick("base_url", file.BaseUrl, "https://api.openai.com/v1");

        var portText = Pick("port", file.Port?.ToString(), "5099");
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            throw new HaltException($"Configured port '{portText}' is not a valid TCP port number.");
        }

        // The key is the one secret this application handles. It is read from the environment
        // first so a config file can be checked in without it, and it is never printed.
        var environmentKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        var apiKey = environmentKey ?? file.ApiKey ?? "";
        var apiKeySource = environmentKey is not null
            ? $"environment variable {ApiKeyEnvironmentVariable}"
            : file.ApiKey is not null ? fileSource : "not set";

        resolved.Add(new ConfigValue("api_key", apiKey, apiKeySource, true));
        resolved.Insert(0, new ConfigValue("config_file", configPath ?? "(none; built-in defaults in effect)", "command line or working directory", false));

        return new AppConfig(projectDirectory, promptsDirectory, baseUrl, apiKey, port, resolved);
    }

    private static FileSettings ReadFile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<FileSettings>(
                       File.ReadAllText(path),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
                   ?? new FileSettings();
        }
        catch (JsonException ex)
        {
            throw new HaltException($"The config file '{path}' is not valid JSON: {ex.Message}", ex);
        }
    }

    private sealed class FileSettings
    {
        public string? ProjectDirectory { get; init; }

        public string? PromptsDirectory { get; init; }

        public string? BaseUrl { get; init; }

        public string? ApiKey { get; init; }

        public int? Port { get; init; }
    }
}
