// The manifest `params` view of normalised variant parameters. The normaliser itself is the
// shared `@evidgate/variant` package, the one the Playwright reporter imports too.
import { MAX_VARIANT_KEYS } from '@evidgate/variant';

/** The manifest `params` member's own key rule (`manifest.v1.json` `$defs/params`). */
const MANIFEST_KEY = /^[a-z][a-z0-9_]{0,63}$/;
/** The manifest `params` value rule: a string of at most this many characters. */
const MANIFEST_VALUE_MAX = 256;

/**
 * The manifest `params` object for already-normalised parameters, or undefined when the
 * manifest format cannot carry them. `manifest.v1.json` allows only lowercase keys and string
 * values, so a numeric, boolean or array value has no representation there.
 */
export function manifestParams(
  params: Readonly<Record<string, unknown>>,
): Record<string, string> | undefined {
  const entries = Object.entries(params);
  if (entries.length > MAX_VARIANT_KEYS) {
    return undefined;
  }
  const out: Record<string, string> = {};
  for (const [key, value] of entries) {
    if (!MANIFEST_KEY.test(key) || key.length === 0) {
      return undefined;
    }
    if (typeof value !== 'string' || value.length > MANIFEST_VALUE_MAX) {
      return undefined;
    }
    out[key] = value;
  }
  return out;
}
