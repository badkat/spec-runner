using System.Text;

namespace SpecRunner.Llm;

/// <summary>
/// Feature 2.3 - the raw request payload and the raw response body are preserved verbatim.
/// They live inside Markdown records (Pillar 7: the project contains only Markdown), so they
/// need a fence that the content itself cannot break out of.
///
/// The fence is made longer than the longest run of backticks in the content, which is the rule
/// CommonMark already uses, and the chosen length is recorded in the record's front matter so a
/// reader never has to count.
/// </summary>
public static class Fence
{
    public static (string Block, int FenceLength) Wrap(string content, string language)
    {
        var longest = 0;
        var current = 0;
        foreach (var c in content)
        {
            current = c == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        var length = Math.Max(3, longest + 1);
        var ticks = new string('`', length);

        var block = new StringBuilder();
        block.Append(ticks).Append(language).Append('\n');
        block.Append(content);
        if (!content.EndsWith('\n'))
        {
            block.Append('\n');
        }

        block.Append(ticks);
        return (block.ToString(), length);
    }
}
