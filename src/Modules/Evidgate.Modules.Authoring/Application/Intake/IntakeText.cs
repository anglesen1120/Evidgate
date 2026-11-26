using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// The rules of a text intake and the one digest of intake text, shared by the request path and the extraction job so
/// both sides hash the same bytes.
/// </summary>
internal static class IntakeText
{
    private const int LongestWord = 4096;

    /// <summary><c>sha256:&lt;hex&gt;</c> of the RFC 8785 canonical JSON string of an NFC text.</summary>
    /// <param name="nfcText">The normalised text.</param>
    internal static string Hash(string nfcText)
    {
        ArgumentNullException.ThrowIfNull(nfcText);
        return CanonicalHash.Compute(JsonSerializer.Serialize(nfcText));
    }

    /// <summary>Normalises and judges one text; the refusal never echoes the text.</summary>
    /// <param name="raw">The text as sent.</param>
    /// <param name="maxCodePoints">The cap in code points.</param>
    /// <param name="text">The NFC text when accepted.</param>
    internal static Error? Check(string raw, int maxCodePoints, out string text)
    {
        text = string.Empty;
        var normalised = AuthoringText.Normalize(raw) ?? string.Empty;

        if (normalised.AsSpan().Trim().IsEmpty)
        {
            return Refuse("empty_input", "the text is empty");
        }

        var codePoints = 0;
        for (var index = 0; index < normalised.Length; codePoints++)
        {
            if (!Rune.TryGetRuneAt(normalised, index, out var rune))
            {
                return Refuse("invalid_text", "the text holds an unpaired surrogate");
            }

            if (rune.Value == 0 || (Rune.IsControl(rune) && rune.Value is not ('\t' or '\n' or '\r')))
            {
                return Refuse("invalid_text", "the text holds a control character");
            }

            index += rune.Utf16SequenceLength;
        }

        if (codePoints > maxCodePoints)
        {
            return Refuse("size_limit_exceeded", $"the text is longer than {maxCodePoints} code points");
        }

        if (LooksSecret(normalised))
        {
            return Refuse("secret_value_detected", "the text looks like a credential; remove it and send the requirement again");
        }

        text = normalised;
        return null;
    }

    /// <summary>
    /// Judges the text word by word: the whole-value rule of <see cref="SecretShape"/> treats an ordinary sentence that
    /// mentions a "token " as a secret, so each word is tested on its own, with a <c>Bearer</c>/<c>Basic</c> word tested
    /// together with the word after it and private-key armour tested as a phrase.
    /// </summary>
    private static bool LooksSecret(string text)
    {
        if (text.Contains("-----BEGIN ", StringComparison.Ordinal))
        {
            return true;
        }

        var previousWasScheme = false;
        foreach (var range in text.AsSpan().SplitAny(" \t\r\n"))
        {
            var word = text.AsSpan(range).Trim("\"'()[]<>,;");
            if (word.IsEmpty)
            {
                continue;
            }

            if (previousWasScheme && word.Length >= 16)
            {
                return true;
            }

            previousWasScheme = word.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
                || word.Equals("Basic", StringComparison.OrdinalIgnoreCase);

            if (word.Length >= 8 && SecretShape.ValueLooksSecret(word.Length > LongestWord ? word[..LongestWord].ToString() : word.ToString()))
            {
                return true;
            }
        }

        return false;
    }

    private static Error Refuse(string reason, string message) => Error.Invalid(message, [new ErrorDetail("/text", reason)]);
}
