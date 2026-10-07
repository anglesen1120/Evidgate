// 04.2.1-T2.1: the one SDK client every command uses (04.2.1 §8 SSRF hygiene). The
// base URL is the validated `EVIDGATE_API_URL`, the bearer is the token from
// `getToken()`, and `redirect: 'error'` stops the generated client from following a
// redirect to a host the operator never configured. No request is made at T2.
import { createClient, type Client } from '@evidgate/sdk';

import type { ResolvedConfig } from './config.js';

export function createApiClient(config: ResolvedConfig, token: string): Client {
  return createClient({
    baseUrl: config.apiUrl,
    headers: { Authorization: `Bearer ${token}` },
    redirect: 'error',
  });
}
