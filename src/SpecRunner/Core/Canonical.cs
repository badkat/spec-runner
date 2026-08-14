using System.Security.Cryptography;
using System.Text;

namespace SpecRunner.Core;

/// <summary>
/// Feature 1.5 - declared canonicalization before hashing, with a version stamp.
///
/// Every hash in this application is taken over the canonical form produced here, never over
/// raw bytes. That is what lets an operator save a file from a different editor, or check it
/// out through git with different line-ending settings, without silently invalidating the
/// pipeline - Pillar 7 promises a person may hand-edit, and a byte-hash would make that a trap.
///
/// Changing any rule below is a deliberate act: bump <see cref="Version"/>, which invalidates
/// every record in the project at once, loudly and on purpose.
/// </summary>
public static class Canonical
{
    /// <summary>Canonicalization version. Bumping this invalidates every record everywhere.</summary>
    public const int Version = 1;

    /// <summary>Recorded in every record so a future reader knows what produced the digest.</summary>
    public const string HashAlgorithm = "SHA-256";

    /// <summary>
    /// The canonical form: UTF-8, BOM stripped, CRLF/CR to LF, trailing whitespace stripped per
    /// line, exactly one trailing newline.
    /// </summary>
    public static string Text(string input)
    {
        if (input.Length > 0 && input[0] == '﻿')
        {
            input = input[1..];
        }

        input = input.Replace("\r\n", "\n").Replace("\r", "\n");

        var lines = input.Split('\n');
        var builder = new StringBuilder(input.Length + 1);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(lines[i].TrimEnd(' ', '\t'));
        }

        // Exactly one trailing newline, whatever the input had.
        var body = builder.ToString().TrimEnd('\n');
        return body + "\n";
    }

    /// <summary>
    /// Digest of the canonical form, as <c>sha256:&lt;lowercase hex&gt;</c>. The prefix is part
    /// of the value so a record can be read without consulting its algorithm field to know how
    /// to interpret the digits.
    /// </summary>
    public static string Hash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(Text(content));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    /// Digest of a file's canonical form. Callers hash content, never file metadata - feature 1.4
    /// forbids consulting mtimes anywhere in the application.
    /// </summary>
    public static string HashFile(string path)
    {
        return Hash(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>
    /// Digest of a variable value. Identical to <see cref="Hash"/>; named separately so call
    /// sites read as what they are, and so a future divergence in the two rules has a place to go.
    /// </summary>
    public static string HashValue(string value) => Hash(value);
}
