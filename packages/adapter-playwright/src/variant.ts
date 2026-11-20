// The reporter's `variant_hash` entry point. The rules, the secret-shape check and the digest are
// the shared `@evidgate/variant` package (the CLI uses the same one); the reporter must not import
// the CLI, which it loads lazily so a broken CLI install only degrades attachments.
import { normalizeVariant as normalizeVariantShared } from '@evidgate/variant';

/** One refusal of a parameter set: where and why. */
export interface VariantProblem {
  /** JSON-pointer-like path inside the parameters (`/params/api_token`). */
  path: string;
  /** A short token (`secret_key`, `secret_value`, `not_scalar`, `invalid_key`, …). */
  reason: string;
}

/**
 * The normalised parameters' content-addressed identity, or every refusal.
 * `hash` is `sha256:<lowercase hex>` over the RFC 8785 canonical form of the normalised
 * parameters; `paramsJson` is that canonical text.
 */
export type VariantResult =
  { ok: true; hash: string; paramsJson: string } | { ok: false; problems: VariantProblem[] };

/**
 * Normalises the Playwright project's `metadata.evidgate.params` into a content-addressed
 * identity. Pure and I/O-free; only own enumerable properties are read.
 *
 * @param params The Playwright project `metadata.evidgate.params` object, or null/undefined for none.
 */
export function normalizeVariant(params: unknown): VariantResult {
  const result = normalizeVariantShared(params);
  if (!result.ok) {
    return { ok: false, problems: result.problems.map(({ path, reason }) => ({ path, reason })) };
  }
  return { ok: true, hash: result.hash, paramsJson: result.paramsJson };
}
