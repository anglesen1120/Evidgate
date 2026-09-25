using System.Collections;
using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Observability.Redaction;

/// <summary>
/// The log state handed to the real providers: every structured value is
/// redacted by key, and every value classified as a whole-secret is also
/// replaced inside the rendered message — otherwise <c>LogInformation("auth {Authorization}", …)</c>
/// would still print the token (the formatted text embeds values, AC4).
/// </summary>
internal sealed class RedactedLogState<TState> : IReadOnlyList<KeyValuePair<string, object?>>
{
    private const string OriginalFormatKey = "{OriginalFormat}";

    private readonly SecretRedactor _redactor;
    private readonly IReadOnlyList<KeyValuePair<string, object?>>? _pairs;
    private readonly string _formatted;

    internal RedactedLogState(
        TState state,
        SecretRedactor redactor,
        Func<TState, Exception?, string> formatter,
        Exception? exception)
    {
        _redactor = redactor;
        _pairs = state as IReadOnlyList<KeyValuePair<string, object?>>;

        var formatted = redactor.RedactText(formatter(state, exception));
        if (_pairs is not null)
        {
            foreach (var pair in _pairs)
            {
                var raw = pair.Value as string;
                if (!string.IsNullOrEmpty(raw) && pair.Key != OriginalFormatKey && SecretRedactor.IsWholeValueSecret(pair.Key))
                {
                    formatted = formatted.Replace(raw, SecretRedactor.Mask, StringComparison.Ordinal);
                }
            }
        }

        _formatted = formatted;
    }

    /// <inheritdoc />
    public int Count => _pairs?.Count ?? 1;

    /// <inheritdoc />
    public KeyValuePair<string, object?> this[int index]
    {
        get
        {
            if (_pairs is null)
            {
                return index == 0
                    ? new KeyValuePair<string, object?>(OriginalFormatKey, _formatted)
                    : throw new ArgumentOutOfRangeException(nameof(index));
            }

            var pair = _pairs[index];
            var value = pair.Key == OriginalFormatKey
                ? _redactor.RedactText(pair.Value as string)
                : _redactor.RedactValue(pair.Key, pair.Value);

            return new KeyValuePair<string, object?>(pair.Key, value);
        }
    }

    /// <inheritdoc />
    public override string ToString() => _formatted;

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
