// The `too_large` measurement: `Encoding.UTF8.GetByteCount` of `JsonSerializer.Serialize` over the
// normalised parameters, which is what the server counts — not the canonical form — so a refusal
// matches the server's. The serialised text is pure ASCII, so a code-point walk is a byte count.
/**
 * The printable ASCII characters the default `JavaScriptEncoder` writes as `\uXXXX` (six bytes)
 * instead of themselves; every other printable ASCII character is written as itself. `\` is the
 * one exception: it is written as `\\` (two bytes). Measured against `JsonSerializer.Serialize`
 * on .NET 10 (`System.Text.Encodings.Web`).
 */
const ENCODER_ESCAPED_ASCII: Record<string, true> = {
  '"': true,
  '&': true,
  "'": true,
  '+': true,
  '<': true,
  '>': true,
  '`': true,
};

/**
 * The length `JsonSerializer.Serialize` gives a string under the default encoder: the two quotes
 * plus, per code point, six bytes for an escaped character (`\uXXXX`), twelve for an astral
 * character (a surrogate pair, so two escapes), two for `\`, and one otherwise. The serialised
 * text is pure ASCII, so its length is its UTF-8 byte count.
 */
function encodedStringLength(text: string): number {
  let length = 2;
  for (const character of text) {
    const codePoint = character.codePointAt(0) as number;
    if (codePoint < 0x20) {
      // Control characters never survive `isClean`; kept so the rule is complete.
      length +=
        character === '\b' ||
        character === '\f' ||
        character === '\n' ||
        character === '\r' ||
        character === '\t'
          ? 2
          : 6;
    } else if (codePoint < 0x7f) {
      length += character === '\\' ? 2 : ENCODER_ESCAPED_ASCII[character] === true ? 6 : 1;
    } else {
      length += codePoint <= 0xffff ? 6 : 12;
    }
  }
  return length;
}

/**
 * The length of `JsonSerializer.Serialize(JsonValue.Create(double))`: the shortest round-trip
 * digits `String(value)` also produces, in .NET's `G17` notation — scientific when the decimal
 * exponent is below −4 or at least 17, uppercase `E`, an explicit sign and at least two exponent
 * digits. Only the notation differs from ECMAScript, which switches to scientific below −6 or at
 * least 21 and writes a lowercase `e` with no leading zero, so `1e-7` is four characters where
 * .NET writes `1E-07`, five.
 */
function dotnetDoubleLength(value: number): number {
  const [mantissa, exponentText] = Math.abs(value).toExponential().split('e');
  const exponent = Number(exponentText);
  const digits = (mantissa as string).replace('.', '');
  const sign = value < 0 ? 1 : 0;
  if (exponent < -4 || exponent >= 17) {
    const head = digits.length === 1 ? digits : `${digits[0]}.${digits.slice(1)}`;
    return sign + head.length + 2 + String(Math.abs(exponent)).padStart(2, '0').length;
  }
  if (exponent < 0) return sign + 2 + -exponent - 1 + digits.length;
  return sign + (digits.length > exponent + 1 ? digits.length + 1 : exponent + 1);
}

/**
 * The length `JsonSerializer.Serialize` gives the number the server stored for `value`
 * (`VariantNormalizer.Scalar`): a `long` written as its decimal digits, or a `double` written by
 * `dotnetDoubleLength`. The reporter has no raw text, so it branches on `String(value)` — the
 * shortest round-trip form `JSON.stringify` emits — exactly as the server branches on the raw
 * text; the spellings where that stands in for the raw text incorrectly are listed in the
 * `variant.ts` header.
 */
function serverNumberLength(value: number): number {
  const text = String(value);
  if (!/[.eE]/.test(text)) return text.length;
  return dotnetDoubleLength(value);
}

/**
 * The byte count the server measures for `too_large`: `Encoding.UTF8.GetByteCount` of
 * `JsonSerializer.Serialize(result)`, where `result` is the `SortedDictionary<string, JsonNode?>`
 * of normalised parameters. Keys are ASCII-only (`KeyShape`) and sorted ordinal, which the default
 * `Array.prototype.sort` order matches; the serialised text is pure ASCII, so its length is its
 * UTF-8 byte count.
 */
export function serverSerializedLength(value: unknown): number {
  if (typeof value === 'string') return encodedStringLength(value);
  if (typeof value === 'number') return serverNumberLength(value);
  if (typeof value === 'boolean') return value ? 4 : 5;
  if (Array.isArray(value)) {
    let length = 2;
    for (const [index, item] of value.entries()) {
      if (index > 0) length += 1;
      length += serverSerializedLength(item);
    }
    return length;
  }
  const record = value as Record<string, unknown>;
  let length = 2;
  for (const [index, key] of Object.keys(record).sort().entries()) {
    if (index > 0) length += 1;
    length += encodedStringLength(key) + 1 + serverSerializedLength(record[key]);
  }
  return length;
}
