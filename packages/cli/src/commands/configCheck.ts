// 04.2.1-T2.1: `evidgate config check` — resolves the configuration and the token
// source so AC5/AC8 are exercisable on the built binary. It never prints a token (T78);
// `--json` emits `{ok, api_url, project, token_source}` only.
import { getToken } from '../auth/getToken.js';
import { ExitCode } from '../exitCodes.js';

import type { CommandDefinition } from './index.js';

export const configCheckCommand: CommandDefinition = {
  name: 'config check',
  summary: 'resolve configuration and the token source',
  requireToken: true,
  run: async ({ config, io, deps }) => {
    const { source } = await getToken(config, deps);
    if (config.json) {
      const payload = {
        ok: true,
        api_url: config.apiUrl,
        project: config.project,
        token_source: source,
      };
      io.stdout(`${JSON.stringify(payload)}\n`);
    } else {
      io.stdout(`ok api_url=${config.apiUrl} project=${config.project} token_source=${source}\n`);
    }
    return ExitCode.Ok;
  },
};
