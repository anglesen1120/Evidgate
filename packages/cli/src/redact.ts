// 04.2.1-T2.1/T2.2: redaction of every secret the CLI reads from the environment (T78,
// 04.2.1 §8 S3 analogue). `registerEnvSecrets` (called by `main` before parsing argv
// and by `resolveConfig`) registers each env secret; `getToken`/`createApiClient`
// register the runtime token. Every stdout/stderr write in `main` goes through
// `createRedactingIo`, which replaces any registered value with `[REDACTED]` and never
// lets an `Authorization` header through.
import { escapeControlCharacters } from './errors.js';

/** Placeholder that replaces every registered secret. */
export const redacted = '[REDACTED]';

/** The two writers `main` is given; both are pure sinks so the CLI stays testable. */
export interface Io {
  stdout(text: string): void;
  stderr(text: string): void;
}

const registeredSecrets = new Set<string>();

/** Registered secrets, longest first, so a shorter prefix cannot mask a longer value. */
let secretsByLength: readonly string[] = [];

const authorizationHeader = /(authorization\s*:\s*)[^\r\n]*/gi;

/**
 * Registers a secret value and the forms it can take in an error message: the value
 * itself, its trimmed form, its percent-encoded form, its JSON-escaped form and its
 * control-character-escaped form. Empty forms are skipped; a value shorter than four
 * characters is still registered (fail-safe).
 *
 * The escaped form matters because `formatCliError`/`formatInternalError` run
 * `escapeControlCharacters` on the message before the redacting writer sees it, so a
 * secret containing a `Cc`/`Cf` character would otherwise no longer match (D-2).
 */
export function registerSecret(value: string | undefined): void {
  if (value === undefined || value.length === 0) {
    return;
  }
  let added = false;
  const jsonEscaped = JSON.stringify(value).slice(1, -1);
  for (const form of [
    value,
    value.trim(),
    encodeURIComponent(value),
    jsonEscaped,
    escapeControlCharacters(value),
  ]) {
    if (form.length > 0 && !registeredSecrets.has(form)) {
      registeredSecrets.add(form);
      added = true;
    }
  }
  if (added) {
    secretsByLength = [...registeredSecrets].sort((a, b) => b.length - a.length);
  }
}

/** Clears the registry — `main` calls it at entry, specs between cases. */
export function resetSecrets(): void {
  registeredSecrets.clear();
  secretsByLength = [];
}

/** Replaces every registered secret and any `Authorization` header value. */
export function redact(text: string): string {
  let output = text;
  for (const secret of secretsByLength) {
    output = output.split(secret).join(redacted);
  }
  return output.replace(authorizationHeader, `$1${redacted}`);
}

/** Wraps an `Io` so every write is redacted. */
export function createRedactingIo(io: Io): Io {
  return {
    stdout: (text) => io.stdout(redact(text)),
    stderr: (text) => io.stderr(redact(text)),
  };
}
