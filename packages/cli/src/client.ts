// 04.2.1-T2.1 / 04.2.1-T1.1: the one SDK client every command uses (04.2.1 §8 SSRF
// hygiene). The base URL is the validated `EVIDGATE_API_URL`, the bearer is the token
// from `getToken()` — omitted for the ingest exchange, which carries the credential in
// the body instead — and `redirect: 'error'` stops the generated client from following a
// redirect to a host the operator never configured. `fetchImpl` is an injected seam so a
// spec can stub transport without a network call.
import { createClient, type Client } from '@evidgate/sdk';

import type { ResolvedConfig } from './config.js';
import { registerSecret } from './redact.js';

export function createApiClient(
  config: ResolvedConfig,
  token?: string,
  fetchImpl: typeof fetch = fetch,
): Client {
  const headers: Record<string, string> = {};
  if (token !== undefined) {
    // The bearer is registered so an error message that embeds it is still redacted (SEC6).
    registerSecret(token);
    headers.Authorization = `Bearer ${token}`;
  }
  return createClient({
    baseUrl: config.apiUrl,
    headers,
    redirect: 'error',
    // The generated client spreads per-call options after the config, so a later
    // command could re-enable redirects. Rebuild the Request with `redirect: 'error'`
    // on every call, whatever the caller passed (SEC7).
    fetch: (input, init) => fetchImpl(new Request(input, { ...init, redirect: 'error' })),
  });
}
