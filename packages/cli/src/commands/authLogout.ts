// `evidgate auth logout` — deletes the cached `ci_producer` token so the next run
// re-authenticates. It needs no token source itself, so it still works once the exchange
// credential is gone; the server-side token is not revoked here and stays valid until it
// expires.
import { deleteCachedToken } from '../auth/tokenCache.js';
import { ExitCode } from '../exitCodes.js';

import type { CommandDefinition } from './index.js';

export const authLogoutCommand: CommandDefinition = {
  name: 'auth logout',
  summary: 'delete the cached CI token',
  requireToken: false,
  run: async ({ config, io }) => {
    await deleteCachedToken(config);
    io.stdout(config.json ? `${JSON.stringify({ ok: true })}\n` : 'ok\n');
    return ExitCode.Ok;
  },
};
