/**
 * The outcome of one read: its value, or the rejection the adapter threw. A page either renders a
 * value or a failure, never both, so the union narrows without a non-null assertion at the call
 * site, and a caller that must not fail (a background read) cannot accidentally let one escape.
 */
export type SettledRead<T> =
  { readonly ok: true; readonly value: T } | { readonly ok: false; readonly error: unknown };

/** Awaits one read and reports which of the two outcomes it had. Never rejects. */
export async function settledRead<T>(read: () => Promise<T>): Promise<SettledRead<T>> {
  try {
    return { ok: true, value: await read() };
  } catch (error) {
    return { ok: false, error };
  }
}
