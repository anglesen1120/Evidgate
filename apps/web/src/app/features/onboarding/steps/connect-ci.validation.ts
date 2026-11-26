export const MAX_TOKEN_NAME_LENGTH = 128;

export function invalidTokenName(value: string): boolean {
  const length = [...value.trim()].length;
  return length < 1 || length > MAX_TOKEN_NAME_LENGTH;
}
