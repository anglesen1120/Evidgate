export const MAX_REF_ENTRIES = 32;
export const MAX_TOKEN_NAME_LENGTH = 128;

export function connectionLines(value: string): string[] {
  return value
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);
}

export function validConnectionLines(value: string): boolean {
  const count = connectionLines(value).length;
  return count >= 1 && count <= MAX_REF_ENTRIES;
}

export function invalidRepositoryId(value: string): boolean {
  return !/^\d+$/.test(value.trim()) || !Number.isSafeInteger(Number(value)) || Number(value) <= 0;
}

export function invalidRepositoryName(value: string): boolean {
  return !/^[^/\s]+\/[^/\s]+$/.test(value.trim());
}

export function invalidTokenName(value: string): boolean {
  const length = [...value.trim()].length;
  return length < 1 || length > MAX_TOKEN_NAME_LENGTH;
}
