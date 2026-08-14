using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SpecRunner.Core;

namespace SpecRunner.Llm;

/// <summary>
/// Feature 3.1 - front matter is the complete and only source of call configuration. No
/// application-level defaults are merged in, a missing key is a halt naming the key and the
/// template, and an *unknown* key is also a halt, because an unknown key is nearly always a typo
/// and a typo that silently becomes a default is invisible.
/// </summary>
public sealed record TemplateConfig(
    string Model,
    double Temperature,
    double TopP,
    int MaxTokens,
    int Seed,
    int TimeoutSeconds,
    string Parser,
    IReadOnlyList<string> OutputVariables)
{
    /// <summary>The closed key set. Nothing may be added at a call site; only here.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "model", "temperature", "top_p", "max_tokens", "seed", "timeout_seconds", "parser", "output_variables"
    ];
}

/// <summary>One substituted value, kept so feature 4.4 can record which step produced it.</summary>
public sealed record SubstitutedValue(string Name, string Value, string Hash, string Source);

/// <summary>A template body after substitution, with everything needed to explain it later.</summary>
public sealed record ResolvedPrompt(
    string TemplatePath,
    string TemplateHash,
    TemplateConfig Config,
    string Text,
    string TextHash,
    IReadOnlyList<SubstitutedValue> Values);

/// <summary>
/// Loads a prompt template and substitutes its placeholders.
///
/// Section 10, decision 1 - the delimiter is Mustache-style <c>{{ var_name }}</c> with optional
/// interior whitespace, and a backslash immediately before the opening delimiter suppresses
/// substitution and emits the delimiter literally, consuming the backslash:
///
///     \{{           -> literal {{
///     \\{{var}}     -> literal \ followed by a real substitution
///     }}            -> just text; the parser only ever looks for a matched {{...}} pair
///
/// Artifact bodies are model-written Markdown and will eventually contain braces, so the round
/// trip is specified here rather than discovered later (feature 4.2).
/// </summary>
public static class PromptTemplate
{
    private static readonly Regex PlaceholderName = new(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    public static (TemplateConfig Config, string Body, string FileHash) Load(string absolutePath, string displayPath)
    {
        if (!File.Exists(absolutePath))
        {
            throw new HaltException($"Prompt template '{displayPath}' does not exist at {absolutePath}.");
        }

        var content = File.ReadAllText(absolutePath);
        var doc = MdDoc.Parse(content, displayPath);
        doc.RequireExactKeys(TemplateConfig.Keys, displayPath);

        var config = new TemplateConfig(
            doc.Require("model", displayPath),
            doc.RequireDouble("temperature", displayPath),
            doc.RequireDouble("top_p", displayPath),
            doc.RequireInt("max_tokens", displayPath),
            doc.RequireInt("seed", displayPath),
            doc.RequireInt("timeout_seconds", displayPath),
            doc.Require("parser", displayPath),
            doc.RequireList("output_variables", displayPath));

        if (config.OutputVariables.Count == 0)
        {
            throw new HaltException(
                $"Template {displayPath} declares no output variables. Feature 4.6 checks the declared set " +
                "against what the parser produced; an empty declaration makes that check vacuous.");
        }

        if (config.TimeoutSeconds <= 0)
        {
            throw new HaltException($"Template {displayPath} declares timeout_seconds={config.TimeoutSeconds}; it must be positive.");
        }

        OutputParsers.Require(config.Parser, displayPath);

        return (config, doc.Body, Canonical.Hash(content));
    }

    /// <summary>
    /// Feature 4.1 - substitution is strict in both directions. An unresolved placeholder is a
    /// halt; a supplied variable the template never uses is a halt; a variable resolving to
    /// empty or whitespace is a halt. Silence in any of these cases produces a plausible-looking
    /// prompt that is wrong, which is the worst outcome available.
    /// </summary>
    public static ResolvedPrompt Resolve(
        string templatePath,
        string templateHash,
        TemplateConfig config,
        string body,
        IReadOnlyList<SubstitutedValue> supplied)
    {
        var byName = new Dictionary<string, SubstitutedValue>(StringComparer.Ordinal);
        foreach (var value in supplied)
        {
            if (!byName.TryAdd(value.Name, value))
            {
                throw new HaltException($"Variable '{value.Name}' was supplied twice to template {templatePath}.");
            }

            if (string.IsNullOrWhiteSpace(value.Value))
            {
                throw new HaltException(
                    $"Variable '{value.Name}' supplied to template {templatePath} is empty or whitespace-only " +
                    $"(produced by {value.Source}). An empty substitution silently changes what the prompt asks for.");
            }
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var text = Substitute(body, templatePath, byName, used);

        var unused = byName.Keys.Where(k => !used.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unused.Count > 0)
        {
            throw new HaltException(
                $"Template {templatePath} never uses supplied variable(s): {string.Join(", ", unused)}. " +
                "A value that reaches the prompt-building step and then goes nowhere means the step and the " +
                "template disagree about what this call is (feature 4.1).");
        }

        return new ResolvedPrompt(
            templatePath,
            templateHash,
            config,
            text,
            Canonical.Hash(text),
            [.. supplied]);
    }

    private static string Substitute(
        string body,
        string templatePath,
        IReadOnlyDictionary<string, SubstitutedValue> values,
        HashSet<string> used)
    {
        var output = new StringBuilder(body.Length);
        var i = 0;

        while (i < body.Length)
        {
            if (body[i] == '\\')
            {
                var backslashes = 0;
                while (i + backslashes < body.Length && body[i + backslashes] == '\\')
                {
                    backslashes++;
                }

                var followedByOpener = i + backslashes + 1 < body.Length
                                       && body[i + backslashes] == '{'
                                       && body[i + backslashes + 1] == '{';

                if (!followedByOpener)
                {
                    output.Append('\\', backslashes);
                    i += backslashes;
                    continue;
                }

                if (backslashes % 2 == 1)
                {
                    // Odd run: the last backslash escapes the delimiter and is consumed.
                    output.Append('\\', (backslashes - 1) / 2);
                    output.Append("{{");
                    i += backslashes + 2;
                    continue;
                }

                // Even run: the backslashes are literal (halved) and the delimiter is real.
                output.Append('\\', backslashes / 2);
                i += backslashes;
                continue;
            }

            if (body[i] == '{' && i + 1 < body.Length && body[i + 1] == '{')
            {
                var close = body.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    throw new HaltException(
                        $"Template {templatePath} has an opening '{{{{' at offset {i} with no closing '}}}}'. " +
                        "Write '\\{{' if a literal delimiter was intended (feature 4.2).");
                }

                var name = body[(i + 2)..close].Trim();
                if (!PlaceholderName.IsMatch(name))
                {
                    throw new HaltException(
                        $"Template {templatePath} contains placeholder '{{{{{name}}}}}', whose name does not match " +
                        "[a-z][a-z0-9_]*.");
                }

                if (!values.TryGetValue(name, out var value))
                {
                    throw new HaltException(
                        $"Template {templatePath} contains an unresolved placeholder '{{{{{name}}}}}'. " +
                        $"Supplied variables were: {(values.Count == 0 ? "(none)" : string.Join(", ", values.Keys.OrderBy(k => k, StringComparer.Ordinal)))}.");
                }

                used.Add(name);
                output.Append(value.Value);
                i = close + 2;
                continue;
            }

            output.Append(body[i]);
            i++;
        }

        return output.ToString();
    }

    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
