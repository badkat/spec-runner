using System.Text.Json;
using SpecRunner.Core;

namespace SpecRunner.Llm;

/// <summary>
/// Feature 4.5 - the output parser is declared in front matter and selected by that declaration.
/// The parser is never chosen by inspecting the response: interpreting the response to decide
/// how to interpret the response is exactly the holistic interpretation Pillar 4 rejects.
///
/// There is no lenient mode, no regex salvage, and no partial parse. The grammar is strict and
/// failure is a halt with the raw response already on disk (feature 3.4).
/// </summary>
public interface IOutputParser
{
    string Id { get; }

    /// <summary>Human-readable statement of the grammar, shown in halts and in the step detail view.</summary>
    string Grammar { get; }

    IReadOnlyDictionary<string, string> Parse(string responseText, string origin);
}

public static class OutputParsers
{
    /// <summary>
    /// Feature 7.1 - the closed verdict enum. Code branches on this value and never on the
    /// prose beside it; an absent or out-of-enum verdict is a halt.
    /// </summary>
    public const string VerdictPass = "pass";

    public const string VerdictUpstreamDefectSuspected = "upstream-defect-suspected";

    public static readonly IReadOnlyList<string> Verdicts = [VerdictPass, VerdictUpstreamDefectSuspected];

    private static readonly IOutputParser[] All =
    [
        new WholeMarkdownParser(),
        new NumberedListParser(),
        new VerdictParser()
    ];

    public static IOutputParser Require(string id, string origin)
    {
        foreach (var parser in All)
        {
            if (parser.Id == id)
            {
                return parser;
            }
        }

        throw new HaltException(
            $"Template {origin} declares parser '{id}', which does not exist. " +
            $"Available parsers: {string.Join(", ", All.Select(p => p.Id))}.");
    }

    /// <summary>
    /// Feature 4.6 - the declared output variable set is checked against what the parser actually
    /// produced, and a mismatch in either direction is a halt.
    /// </summary>
    public static void RequireDeclaredOutputs(
        IReadOnlyList<string> declared,
        IReadOnlyDictionary<string, string> produced,
        string templatePath,
        string parserId)
    {
        var missing = declared.Where(d => !produced.ContainsKey(d)).ToList();
        var extra = produced.Keys.Where(p => !declared.Contains(p)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        if (missing.Count == 0 && extra.Count == 0)
        {
            return;
        }

        throw new HaltException(
            $"Template {templatePath} declares output_variables that do not match what parser '{parserId}' produced." +
            (missing.Count > 0 ? $"\n  Declared but not produced: {string.Join(", ", missing)}" : "") +
            (extra.Count > 0 ? $"\n  Produced but not declared: {string.Join(", ", extra)}" : ""));
    }

    /// <summary>Whole response body, taken as Markdown. One output variable: <c>content</c>.</summary>
    private sealed class WholeMarkdownParser : IOutputParser
    {
        public string Id => "whole-markdown";

        public string Grammar => "The entire response is taken as the Markdown body. Produces: content.";

        public IReadOnlyDictionary<string, string> Parse(string responseText, string origin)
        {
            var content = Canonical.Text(responseText);
            if (content.Trim().Length == 0)
            {
                throw new HaltException($"Parser '{Id}' received an empty response body from {origin}.");
            }

            return new Dictionary<string, string>(StringComparer.Ordinal) { ["content"] = content };
        }
    }

    /// <summary>
    /// A contiguous numbered list starting at 1, one item per line, blank lines permitted between
    /// items and nowhere else. Produces: <c>items</c> (a JSON array of strings) and <c>count</c>.
    /// </summary>
    private sealed class NumberedListParser : IOutputParser
    {
        public string Id => "numbered-list";

        public string Grammar =>
            "Every non-blank line must be '<n>. <text>', numbered contiguously from 1. " +
            "Produces: items (JSON array), count.";

        public IReadOnlyDictionary<string, string> Parse(string responseText, string origin)
        {
            var items = new List<string>();
            var lines = Canonical.Text(responseText).Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var dot = line.IndexOf('.');
                if (dot <= 0
                    || !int.TryParse(line[..dot], out var number)
                    || dot + 1 >= line.Length
                    || line[dot + 1] != ' ')
                {
                    throw new HaltException(
                        $"Parser '{Id}' rejected {origin} at line {i + 1}: expected '<n>. <text>', found '{lines[i]}'. " +
                        "There is no lenient mode and no salvage (feature 4.5); the raw response is on disk.");
                }

                if (number != items.Count + 1)
                {
                    throw new HaltException(
                        $"Parser '{Id}' rejected {origin} at line {i + 1}: expected item {items.Count + 1}, found item {number}.");
                }

                var text = line[(dot + 2)..].Trim();
                if (text.Length == 0)
                {
                    throw new HaltException($"Parser '{Id}' rejected {origin} at line {i + 1}: item {number} has no text.");
                }

                items.Add(text);
            }

            if (items.Count == 0)
            {
                throw new HaltException($"Parser '{Id}' rejected {origin}: the response contains no list items.");
            }

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["items"] = JsonSerializer.Serialize(items),
                ["count"] = items.Count.ToString()
            };
        }
    }

    /// <summary>
    /// Feature 7.1 - a verdict artifact: front matter carrying <c>verdict</c> from a closed enum
    /// and <c>suspected_artifact</c>, over prose for the human. Code reads the enum alone.
    /// Produces: <c>verdict</c>, <c>suspected_artifact</c>, <c>rationale</c>.
    /// </summary>
    private sealed class VerdictParser : IOutputParser
    {
        public string Id => "verdict";

        public string Grammar =>
            "A '---' front matter block containing exactly 'verdict' (" + string.Join(" | ", Verdicts) + ") " +
            "and 'suspected_artifact', over prose. Produces: verdict, suspected_artifact, rationale.";

        public IReadOnlyDictionary<string, string> Parse(string responseText, string origin)
        {
            var doc = MdDoc.Parse(responseText, origin);
            doc.RequireExactKeys(["verdict", "suspected_artifact"], origin);

            var verdict = doc.Require("verdict", origin);
            if (!Verdicts.Contains(verdict))
            {
                throw new HaltException(
                    $"Parser '{Id}' rejected {origin}: verdict '{verdict}' is outside the closed enum " +
                    $"({string.Join(", ", Verdicts)}). An out-of-enum verdict is a halt (feature 7.1) - " +
                    "the application does not interpret what the model probably meant.");
            }

            var rationale = doc.Body.Trim();
            if (rationale.Length == 0)
            {
                throw new HaltException($"Parser '{Id}' rejected {origin}: the verdict carries no prose for the human to read.");
            }

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["verdict"] = verdict,
                ["suspected_artifact"] = doc.Require("suspected_artifact", origin),
                ["rationale"] = rationale
            };
        }
    }
}
