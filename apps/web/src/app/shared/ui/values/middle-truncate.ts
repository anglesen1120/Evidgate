const graphemes = new Intl.Segmenter(undefined, { granularity: 'grapheme' });

export function middleTruncate(value: string, maxLength = 32): string {
  const budget = Number.isFinite(maxLength) ? Math.max(3, Math.floor(maxLength)) : 32;
  if (value.length <= budget) return value;
  const characters = Array.from(graphemes.segment(value), ({ segment }) => segment);
  if (characters.length <= budget) return value;
  const leading = Math.ceil((budget - 1) / 2);
  const trailing = Math.floor((budget - 1) / 2);
  return `${characters.slice(0, leading).join('')}…${characters.slice(-trailing).join('')}`;
}
