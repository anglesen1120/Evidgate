// `evidgate config check` — resolves the configuration and the token source so a CI job can
// prove its wiring before it sends anything. It never prints a token, only
// `{ok, api_url, project, token_source}`, because that output lands in build logs.
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
