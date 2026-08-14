using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SpecRunner.Core;

/// <summary>
/// The one file format this application reads and writes: a Markdown body under a YAML front
/// matter block. Artifacts, records, run logs, questions, answers, decisions and the project
/// state projection are all this shape (Pillar 7 - the filesystem is the record, and the record
/// is something a person opens in an editor).
///
/// The front matter grammar is a deliberately small, strict subset of YAML - scalars, sequences
/// of scalars, and sequences of flat maps. It is hand-written rather than delegated to a YAML
/// library for three reasons that the pillars actually force:
///   - Feature 2.5 requires byte-identical serialization of identical content. A general
///     serializer's formatting choices are not part of this application's contract.
///   - Feature 3.1 requires that an *unknown* key be a halt. That is trivial over a closed
///     grammar and awkward over a permissive one.
///   - Pillar 1 prefers the readable path: the whole grammar fits in this file, and a developer
///     diagnosing a malformed record reads the parser rather than a dependency's issue tracker.
/// Anything outside the grammar is a halt, never a lenient interpretation.
/// </summary>
public sealed class MdDoc
{
    private static readonly Regex KeyPattern = new(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex MapItemStart = new(@"^[a-z][a-z0-9_]*:(\s|$)", RegexOptions.Compiled);
    // A bare scalar may contain ':' because every split in the parser takes the *first* colon on
    // the line, which always belongs to the key. List items are the one ambiguous position, and
    // FormatScalar quotes those (forceQuoteIfColon).
    private static readonly Regex BareScalarSafe = new(@"^[A-Za-z0-9_][A-Za-z0-9_./@+:\-]*$", RegexOptions.Compiled);

    private readonly List<string> _order = [];
    private readonly Dictionary<string, object> _values = [];

    /// <summary>The Markdown body beneath the front matter, in canonical form.</summary>
    public string Body { get; set; } = "";

    /// <summary>
    /// Set on the two record kinds that must preserve bytes exactly as received: the raw request
    /// payload and the raw response body (feature 2.3). Canonicalization strips trailing
    /// whitespace per line, and trailing whitespace is meaningful in Markdown - so for evidence
    /// records the body is written untouched and only the front matter is canonicalized.
    ///
    /// This is safe precisely because these records are evidence and never input: nothing hashes
    /// them as a dependency, so nothing depends on their being canonically stable.
    /// </summary>
    public bool PreserveBodyVerbatim { get; set; }

    /// <summary>Front matter keys in the order they were set or parsed.</summary>
    public IReadOnlyList<string> Keys => _order;

    public bool Has(string key) => _values.ContainsKey(key);

    // ---- writing -----------------------------------------------------------------------

    public MdDoc Set(string key, string value)
    {
        RequireValidKey(key);
        Track(key);
        _values[key] = value;
        return this;
    }

    public MdDoc Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public MdDoc Set(string key, double value) => Set(key, value.ToString("R", CultureInfo.InvariantCulture));

    public MdDoc Set(string key, bool value) => Set(key, value ? "true" : "false");

    public MdDoc SetList(string key, IEnumerable<string> values)
    {
        RequireValidKey(key);
        Track(key);
        _values[key] = values.ToList();
        return this;
    }

    public MdDoc SetMapList(string key, IEnumerable<IReadOnlyList<KeyValuePair<string, string>>> rows)
    {
        RequireValidKey(key);
        Track(key);
        _values[key] = rows.Select(r => r.ToList()).ToList();
        return this;
    }

    // ---- reading -----------------------------------------------------------------------

    /// <summary>Reads a scalar, halting if the key is absent or is not a scalar.</summary>
    public string Require(string key, string origin)
    {
        if (!_values.TryGetValue(key, out var value))
        {
            throw new HaltException($"Required front matter key '{key}' is missing in {origin}.");
        }

        if (value is not string scalar)
        {
            throw new HaltException($"Front matter key '{key}' in {origin} is a list where a single value was required.");
        }

        return scalar;
    }

    public string? Optional(string key)
        => _values.TryGetValue(key, out var value) && value is string scalar ? scalar : null;

    public int RequireInt(string key, string origin)
    {
        var raw = Require(key, origin);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new HaltException($"Front matter key '{key}' in {origin} must be an integer; found '{raw}'.");
        }

        return parsed;
    }

    public double RequireDouble(string key, string origin)
    {
        var raw = Require(key, origin);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new HaltException($"Front matter key '{key}' in {origin} must be a number; found '{raw}'.");
        }

        return parsed;
    }

    public IReadOnlyList<string> RequireList(string key, string origin)
    {
        if (!_values.TryGetValue(key, out var value))
        {
            throw new HaltException($"Required front matter key '{key}' is missing in {origin}.");
        }

        return value switch
        {
            List<string> list => list,
            List<List<KeyValuePair<string, string>>> maps when maps.Count == 0 => [],
            _ => throw new HaltException($"Front matter key '{key}' in {origin} must be a list of values.")
        };
    }

    public IReadOnlyList<IReadOnlyList<KeyValuePair<string, string>>> RequireMapList(string key, string origin)
    {
        if (!_values.TryGetValue(key, out var value))
        {
            throw new HaltException($"Required front matter key '{key}' is missing in {origin}.");
        }

        return value switch
        {
            List<List<KeyValuePair<string, string>>> maps => maps,
            List<string> list when list.Count == 0 => [],
            _ => throw new HaltException($"Front matter key '{key}' in {origin} must be a list of maps.")
        };
    }

    /// <summary>
    /// Feature 3.1 - a missing key is a halt naming the key and the file; an unknown key is also
    /// a halt, because an unknown key is almost always a typo, and a typo that silently becomes
    /// a default is exactly the invisible failure Pillar 3 rejects.
    /// </summary>
    public void RequireExactKeys(IReadOnlyList<string> expected, string origin)
    {
        var missing = expected.Where(k => !_values.ContainsKey(k)).ToList();
        var unknown = _order.Where(k => !expected.Contains(k)).ToList();

        if (missing.Count == 0 && unknown.Count == 0)
        {
            return;
        }

        var report = new StringBuilder($"Front matter in {origin} does not match the required closed key set.");
        if (missing.Count > 0)
        {
            report.Append($"\n  Missing keys: {string.Join(", ", missing)}");
        }

        if (unknown.Count > 0)
        {
            report.Append($"\n  Unknown keys: {string.Join(", ", unknown)}");
        }

        report.Append($"\n  Permitted keys: {string.Join(", ", expected)}");
        throw new HaltException(report.ToString());
    }

    // ---- serialization -----------------------------------------------------------------

    /// <summary>
    /// Feature 2.5 - deterministic serialization. The same content serialized twice is
    /// byte-identical, so a hash identifies content rather than the moment it was written, and a
    /// hand-diff between two versions shows only what actually changed.
    /// </summary>
    public string Serialize()
    {
        var text = new StringBuilder();
        text.Append("---\n");

        foreach (var key in _order)
        {
            var value = _values[key];
            switch (value)
            {
                case string scalar:
                    text.Append(key).Append(": ").Append(FormatScalar(scalar)).Append('\n');
                    break;

                case List<string> list:
                    if (list.Count == 0)
                    {
                        text.Append(key).Append(": []\n");
                        break;
                    }

                    text.Append(key).Append(":\n");
                    foreach (var item in list)
                    {
                        text.Append("  - ").Append(FormatScalar(item, forceQuoteIfColon: true)).Append('\n');
                    }

                    break;

                case List<List<KeyValuePair<string, string>>> maps:
                    if (maps.Count == 0)
                    {
                        text.Append(key).Append(": []\n");
                        break;
                    }

                    text.Append(key).Append(":\n");
                    foreach (var row in maps)
                    {
                        for (var i = 0; i < row.Count; i++)
                        {
                            RequireValidKey(row[i].Key);
                            text.Append(i == 0 ? "  - " : "    ")
                                .Append(row[i].Key).Append(": ")
                                .Append(FormatScalar(row[i].Value))
                                .Append('\n');
                        }
                    }

                    break;

                default:
                    throw new HaltException($"Front matter key '{key}' holds an unsupported value type.");
            }
        }

        text.Append("---\n");

        if (PreserveBodyVerbatim)
        {
            var frontMatter = Canonical.Text(text.ToString());
            return Body.Length == 0 ? frontMatter : frontMatter + "\n" + Body;
        }

        if (Body.Length > 0)
        {
            text.Append('\n').Append(Canonical.Text(Body));
        }

        return Canonical.Text(text.ToString());
    }

    // ---- parsing -----------------------------------------------------------------------

    public static MdDoc Parse(string content, string origin)
    {
        var text = Canonical.Text(content);
        var lines = text.Split('\n');

        if (lines.Length == 0 || lines[0] != "---")
        {
            throw new HaltException(
                $"{origin} does not begin with a '---' front matter block. " +
                "Every file this application reads or writes carries front matter; a file without it is malformed, not empty.");
        }

        var doc = new MdDoc();
        var index = 1;

        while (true)
        {
            if (index >= lines.Length)
            {
                throw new HaltException($"{origin} has an unterminated front matter block (no closing '---').");
            }

            var line = lines[index];
            if (line == "---")
            {
                index++;
                break;
            }

            if (line.Length == 0)
            {
                index++;
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator < 0 || line.StartsWith(' '))
            {
                throw new HaltException($"{origin} line {index + 1}: expected 'key: value' in front matter, found '{line}'.");
            }

            var key = line[..separator];
            RequireValidKey(key, $"{origin} line {index + 1}");
            var rest = line[(separator + 1)..].Trim();
            index++;

            if (rest == "[]")
            {
                doc.SetList(key, []);
                continue;
            }

            if (rest.Length > 0)
            {
                doc.Set(key, ParseScalar(rest, origin, index));
                continue;
            }

            // A block sequence: either scalars or flat maps, decided by the first item.
            var scalarItems = new List<string>();
            var mapItems = new List<List<KeyValuePair<string, string>>>();
            var isMapList = false;
            var sawItem = false;

            while (index < lines.Length && lines[index].StartsWith("  - "))
            {
                var itemText = lines[index][4..];
                index++;

                if (!sawItem)
                {
                    sawItem = true;
                    isMapList = !itemText.StartsWith('"') && MapItemStart.IsMatch(itemText);
                }

                if (!isMapList)
                {
                    scalarItems.Add(ParseScalar(itemText, origin, index));
                    continue;
                }

                var row = new List<KeyValuePair<string, string>> { ParseMapEntry(itemText, origin, index) };
                while (index < lines.Length && lines[index].StartsWith("    ") && !lines[index].StartsWith("    -"))
                {
                    row.Add(ParseMapEntry(lines[index][4..], origin, index + 1));
                    index++;
                }

                mapItems.Add(row);
            }

            if (!sawItem)
            {
                throw new HaltException(
                    $"{origin} line {index}: key '{key}' has no value and no list items beneath it. " +
                    "Write '[]' for an empty list; an empty value is not a value.");
            }

            if (isMapList)
            {
                doc.SetMapList(key, mapItems);
            }
            else
            {
                doc.SetList(key, scalarItems);
            }
        }

        // Skip exactly one blank separator line between front matter and body, if present.
        if (index < lines.Length && lines[index].Length == 0)
        {
            index++;
        }

        doc.Body = index < lines.Length ? string.Join("\n", lines[index..]) : "";
        doc.Body = doc.Body.Length == 0 ? "" : Canonical.Text(doc.Body);
        return doc;
    }

    /// <summary>
    /// Splits a file into its raw front matter text and raw body text without interpreting
    /// either. Used where the body's own bytes must be hashed exactly as stored - an artifact's
    /// <c>body_hash</c> covers the body alone, not the origin header above it (feature 2.1).
    /// </summary>
    public static (string FrontMatter, string Body) SplitRaw(string content, string origin)
    {
        var text = Canonical.Text(content);
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0] != "---")
        {
            throw new HaltException($"{origin} does not begin with a '---' front matter block.");
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i] != "---")
            {
                continue;
            }

            var frontMatter = string.Join("\n", lines[1..i]);
            var start = i + 1;
            if (start < lines.Length && lines[start].Length == 0)
            {
                start++;
            }

            var body = start < lines.Length ? string.Join("\n", lines[start..]) : "";
            return (frontMatter, body.Length == 0 ? "" : Canonical.Text(body));
        }

        throw new HaltException($"{origin} has an unterminated front matter block (no closing '---').");
    }

    // ---- helpers -----------------------------------------------------------------------

    private void Track(string key)
    {
        if (!_values.ContainsKey(key))
        {
            _order.Add(key);
        }
    }

    private static void RequireValidKey(string key, string? origin = null)
    {
        if (KeyPattern.IsMatch(key))
        {
            return;
        }

        var where = origin is null ? "" : $" in {origin}";
        throw new HaltException(
            $"Invalid front matter key '{key}'{where}. Keys must match [a-z][a-z0-9_]* - " +
            "this application's front matter grammar is a closed subset, not general YAML.");
    }

    private static KeyValuePair<string, string> ParseMapEntry(string text, string origin, int lineNumber)
    {
        var separator = text.IndexOf(':');
        if (separator < 0)
        {
            throw new HaltException($"{origin} line {lineNumber}: expected 'key: value' inside a list item, found '{text}'.");
        }

        var key = text[..separator];
        RequireValidKey(key, $"{origin} line {lineNumber}");
        return new KeyValuePair<string, string>(key, ParseScalar(text[(separator + 1)..].Trim(), origin, lineNumber));
    }

    private static string ParseScalar(string raw, string origin, int lineNumber)
    {
        if (!raw.StartsWith('"'))
        {
            return raw;
        }

        if (raw.Length < 2 || !raw.EndsWith('"'))
        {
            throw new HaltException($"{origin} line {lineNumber}: unterminated quoted value {raw}.");
        }

        var inner = raw[1..^1];
        var result = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\')
            {
                result.Append(inner[i]);
                continue;
            }

            i++;
            if (i >= inner.Length)
            {
                throw new HaltException($"{origin} line {lineNumber}: value ends with a dangling escape.");
            }

            result.Append(inner[i] switch
            {
                'n' => '\n',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => throw new HaltException($"{origin} line {lineNumber}: unknown escape '\\{inner[i]}'.")
            });
        }

        return result.ToString();
    }

    private static string FormatScalar(string value, bool forceQuoteIfColon = false)
    {
        var needsQuoting = value.Length == 0
            || !BareScalarSafe.IsMatch(value)
            || (forceQuoteIfColon && value.Contains(':'));

        if (!needsQuoting)
        {
            return value;
        }

        var quoted = new StringBuilder(value.Length + 2);
        quoted.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': quoted.Append("\\\""); break;
                case '\\': quoted.Append("\\\\"); break;
                case '\n': quoted.Append("\\n"); break;
                case '\t': quoted.Append("\\t"); break;
                case '\r': break;
                default: quoted.Append(c); break;
            }
        }

        quoted.Append('"');
        return quoted.ToString();
    }
}
