/**
 * One read, settled so a rejection is inspected instead of thrown. Shared by the token list and the
 * CI-binding track: each settles its own state, and neither may turn a refusal into a rejection of
 * the page's effect call.
 */
export type SettledRead<T> =
  { readonly ok: true; readonly value: T } | { readonly ok: false; readonly error: unknown };

/** Runs one read and keeps its rejection as data, so the caller can classify it. */
export async function settledRead<T>(read: () => Promise<T>): Promise<SettledRead<T>> {
  try {
    return { ok: true, value: await read() };
  } catch (error) {
    return { ok: false, error };
  }
}
