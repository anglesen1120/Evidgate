// The one SDK client every command uses. The base URL is the validated `EVIDGATE_API_URL`
// plus the `/v1` version prefix, the bearer is the token from `getToken()` — omitted for the
// ingest exchange, which carries the credential in the body instead — and `redirect: 'error'`
// stops the generated client from following a redirect to a host the operator never
// configured. `fetchImpl` is an injected seam so a spec can stub transport without a network
// call.
import { createClient, type Client } from '@evidgate/sdk';

import { boundedResponse, ResponseReadError } from './boundedResponse.js';
import { CliError } from './errors.js';
import type { ResolvedConfig } from './config.js';
import { registerSecret } from './redact.js';

/**
 * The API version prefix every SDK operation path is relative to. The openapi document
 * declares `servers: - url: /v1`, and the generated operations carry only the path after
 * it (`/ci/token`, `/projects/{p}/manifests`). `EVIDGATE_API_URL` is the API root WITHOUT
 * the version (contracts/cli.md §3), so the client appends it here; a value that already
 * ends in `/v1` therefore yields `/v1/v1`.
 */
const API_VERSION_PREFIX = '/v1';

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
  const client = createClient({
    baseUrl: `${config.apiUrl}${API_VERSION_PREFIX}`,
    headers,
    redirect: 'error',
    // Without a JSON content type the client would hand back the unread body stream, and the
    // cap only fires on a read. Reading it as JSON keeps every ordinary answer under the cap;
    // `gate` asks for the stream explicitly and applies its own cap.
    parseAs: 'json',
    // The generated client spreads per-call options after the config, so a caller could
    // re-enable redirects. Rebuild the Request with `redirect: 'error'` on every call,
    // whatever the caller passed, so no request can follow a redirect. Every answer then goes
    // through the byte cap, so no command can buffer an unbounded body.
    fetch: async (input, init) =>
      boundedResponse(await fetchImpl(new Request(input, { ...init, redirect: 'error' }))),
  });
  // The generated client turns a body-read failure into a returned error next to the response
  // it was reading. A failure on a 2xx answer can never be an answer, so it is rethrown; on an
  // error status the error is returned, keeping the status mapping and retry policy unchanged.
  client.interceptors.error.use((error, response) => {
    if (error instanceof ResponseReadError) {
      if (response?.ok === true) {
        throw error;
      }
      return error;
    }
    if (error instanceof CliError) {
      throw error;
    }
    return error;
  });
  return client;
}
