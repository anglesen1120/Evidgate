const graphemes = new Intl.Segmenter(undefined, { granularity: 'grapheme' });

export interface MiddleTruncation {
  leading: string;
  trailing: string;
}

export function middleTruncate(value: string, maxLength = 32): string {
  const budget = Number.isFinite(maxLength) ? Math.max(3, Math.floor(maxLength)) : 32;
  if (value.length <= budget) return value;
  const parts = middleTruncateParts(value, budget);
  return parts.trailing ? `${parts.leading}…${parts.trailing}` : parts.leading;
}

export function middleTruncateParts(value: string, maxLength = 32): MiddleTruncation {
  const budget = Number.isFinite(maxLength) ? Math.max(3, Math.floor(maxLength)) : 32;
  if (value.length <= budget) return { leading: value, trailing: '' };
  const characters = Array.from(graphemes.segment(value), ({ segment }) => segment);
  if (characters.length <= budget) return { leading: value, trailing: '' };
  const leading = Math.ceil((budget - 1) / 2);
  const trailing = Math.floor((budget - 1) / 2);
  return {
    leading: characters.slice(0, leading).join(''),
    trailing: characters.slice(-trailing).join(''),
  };
}
