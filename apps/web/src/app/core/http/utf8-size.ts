/**
 * Whether the UTF-8 encoding of `text` is longer than `limit` bytes, counted without allocating.
 * `TextEncoder` converts the input to a USVString first (WHATWG Encoding, "UTF-8 encode"; WebIDL
 * USVString conversion), so a lone surrogate becomes U+FFFD and costs 3 bytes, a valid pair costs 4.
 * The count stops as soon as it passes the limit.
 */
export function utf8ExceedsBytes(text: string, limit: number): boolean {
  // Every UTF-16 unit costs at most 3 bytes and at least 1, which settles the common cases.
  if (text.length > limit) {
    return true;
  }
  if (text.length * 3 <= limit) {
    return false;
  }
  let bytes = 0;
  for (let i = 0; i < text.length; i += 1) {
    const unit = text.charCodeAt(i);
    if (unit < 0x80) {
      bytes += 1;
    } else if (unit < 0x800) {
      bytes += 2;
    } else if (unit >= 0xd800 && unit <= 0xdbff && isLowSurrogate(text.charCodeAt(i + 1))) {
      bytes += 4;
      i += 1;
    } else {
      bytes += 3;
    }
    if (bytes > limit) {
      return true;
    }
  }
  return false;
}

function isLowSurrogate(unit: number): boolean {
  return unit >= 0xdc00 && unit <= 0xdfff;
}
