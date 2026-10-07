import { describe, expect, it } from 'vitest';

import { createApiClient } from './client.js';
import { resolveConfig } from './config.js';

// 04.2.1-T2.1 / 04.2.1 §8 SSRF hygiene: the client carries the validated base URL, the
// bearer token and `redirect: 'error'`. No request is made here — the config is
// inspected directly.
describe('createApiClient', () => {
  it('sets the base URL, the bearer header and redirect: error', () => {
    const config = resolveConfig({
      env: { EVIDGATE_API_URL: 'https://api.evidgate.test', EVIDGATE_PROJECT: 'proj-1' },
    });

    const client = createApiClient(config, 'bearer-token-value');
    const clientConfig = client.getConfig();

    expect(clientConfig.baseUrl).toBe('https://api.evidgate.test');
    expect(clientConfig.redirect).toBe('error');
    expect(clientConfig.headers).toBeInstanceOf(Headers);
    expect((clientConfig.headers as Headers).get('authorization')).toBe(
      'Bearer bearer-token-value',
    );
  });
});
