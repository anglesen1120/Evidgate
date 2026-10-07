// 04.2.1-T2.1/T2.2: redaction of every secret the CLI reads from the environment (T78,
// 04.2.1 §8 S3 analogue). `resolveConfig` registers each secret value; every
// stdout/stderr write in `main` goes through `createRedactingIo`, which replaces any
// registered value with `[REDACTED]` and never lets an `Authorization` header through.

/** Placeholder that replaces every registered secret. */
export const redacted = '[REDACTED]';

/** The two writers `main` is given; both are pure sinks so the CLI stays testable. */
export interface Io {
  stdout(text: string): void;
  stderr(text: string): void;
}

const registeredSecrets = new Set<string>();

const authorizationHeader = /(authorization\s*:\s*)[^\r\n]*/gi;

/** Registers a secret value; empty and undefined values are ignored. */
export function registerSecret(value: string | undefined): void {
  if (value !== undefined && value.length > 0) {
    registeredSecrets.add(value);
  }
}

/** Clears the registry — `main` calls it at entry, specs between cases. */
export function resetSecrets(): void {
  registeredSecrets.clear();
}

/** Replaces every registered secret and any `Authorization` header value. */
export function redact(text: string): string {
  let output = text;
  for (const secret of registeredSecrets) {
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
