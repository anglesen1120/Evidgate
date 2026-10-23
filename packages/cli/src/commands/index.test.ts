import { describe, expect, it } from 'vitest';

import { resolveConfig } from '../config.js';
import { ExitCode } from '../exitCodes.js';

import {
  commandRegistry,
  renderUsage,
  resolveCommand,
  usageText,
  type CommandDefinition,
} from './index.js';

function stub(name: string, summary: string, positionals?: string): CommandDefinition {
  const command: CommandDefinition = {
    name,
    summary,
    requireToken: false,
    run: () => Promise.resolve(ExitCode.Ok),
  };
  return positionals === undefined ? command : { ...command, positionals };
}

// 04.2.1-T2.1 / CR1, CR2: the command registry — longest-prefix resolution, the
// remaining positionals, and the computed help column width.
describe('command registry', () => {
  it('resolves the longest registered name that prefixes the positionals', () => {
    const registry = {
      auth: stub('auth', 'authenticate'),
      'auth login': stub('auth login', 'log in'),
    };
    expect(resolveCommand(['auth', 'login'], registry)).toMatchObject({
      command: registry['auth login'],
      words: 2,
      rest: [],
    });
    expect(resolveCommand(['auth', 'logout'], registry)).toMatchObject({
      command: registry.auth,
      words: 1,
      rest: ['logout'],
    });
  });

  it('returns undefined when no registered name prefixes the positionals', () => {
    expect(resolveCommand([])).toBeUndefined();
    expect(resolveCommand(['frobnicate'])).toBeUndefined();
    expect(resolveCommand(['config'])).toBeUndefined();
  });

  it('resolves config check and passes the remaining positionals', () => {
    expect(resolveCommand(['config', 'check'])).toMatchObject({
      command: commandRegistry['config check'],
      words: 2,
      rest: [],
    });
    expect(resolveCommand(['config', 'check', 'extra'])).toMatchObject({
      command: commandRegistry['config check'],
      words: 2,
      rest: ['extra'],
    });
  });

  it('resolves the auth login and auth logout commands', () => {
    expect(resolveCommand(['auth', 'login'])).toMatchObject({
      command: commandRegistry['auth login'],
      words: 2,
      rest: [],
    });
    expect(resolveCommand(['auth', 'logout'])).toMatchObject({
      command: commandRegistry['auth logout'],
      words: 2,
      rest: [],
    });
    // `--github-env` is a per-command option, never a global one (04.2.1-T2 hand-over 2).
    expect(commandRegistry['auth login']?.options?.['github-env']).toEqual({ type: 'boolean' });
  });

  it('aligns the help column for a long command name', () => {
    const commands = [
      stub('config check', 'resolve configuration'),
      stub('manifests upload', 'upload a manifest'),
    ];
    const lines = renderUsage(commands).split('\n');
    const configLine = lines.find((line) => line.includes('resolve configuration')) ?? '';
    const manifestLine = lines.find((line) => line.includes('upload a manifest')) ?? '';
    const configColumn = configLine.indexOf('resolve configuration');
    const manifestColumn = manifestLine.indexOf('upload a manifest');
    expect(configColumn).toBe(manifestColumn);
    expect(configColumn).toBe('  manifests upload'.length + 2);
  });

  it('shows the positionals usage string in the help line', () => {
    const commands = [stub('manifests upload', 'upload a manifest', '<file>')];
    expect(renderUsage(commands)).toContain('  manifests upload <file>  upload a manifest');
  });

  // 04.2.3: the two spool commands are registered here and nowhere else; the dispatcher
  // in `main.ts` only ever sees `resolveCommand`'s answer.
  it('resolves events send and runs finalize, with single-use options', () => {
    expect(resolveCommand(['events', 'send'])).toMatchObject({
      command: commandRegistry['events send'],
      words: 2,
      rest: [],
    });
    expect(resolveCommand(['events', 'send', 'extra'])).toMatchObject({
      command: commandRegistry['events send'],
      words: 2,
      rest: ['extra'],
    });
    expect(resolveCommand(['runs', 'finalize'])).toMatchObject({
      command: commandRegistry['runs finalize'],
      words: 2,
      rest: [],
    });
    // A repetition must be observable, so every value option is `multiple: true`.
    expect(commandRegistry['events send']?.options?.['run']).toEqual({
      type: 'string',
      multiple: true,
    });
    expect(commandRegistry['events send']?.options?.['producer']).toEqual({
      type: 'string',
      multiple: true,
    });
    expect(commandRegistry['events send']?.options?.['from']).toEqual({
      type: 'string',
      multiple: true,
    });
  });

  it('lists both spool commands in the help text', () => {
    expect(usageText).toContain('events send');
    expect(usageText).toContain('runs finalize');
  });

  // CR8 (S54): the spool surface is lazy, so only the two spool commands force it in their
  // `validateConfig` pre-flight. A malformed `EVIDGATE_SPOOL_MAX_BYTES` is therefore their
  // `invalid_config` — and never a `runs create` / `auth login` failure.
  it('forces the lazy spool settings only in the spool commands', () => {
    const config = resolveConfig({
      env: {
        EVIDGATE_API_URL: 'https://api.evidgate.test',
        EVIDGATE_PROJECT: 'proj-1',
        EVIDGATE_DEV_TOKEN: 'dev-token-value',
        EVIDGATE_SPOOL_DIR: '/var/tmp/evidgate-spool',
        EVIDGATE_SPOOL_MAX_BYTES: 'lots',
      },
    });
    // `resolveConfig` itself is unaffected by the malformed value.
    expect(config.project).toBe('proj-1');
    for (const name of ['events send', 'runs finalize']) {
      const validate = commandRegistry[name]?.validateConfig;
      expect(validate).toBeDefined();
      expect(() => validate?.(config)).toThrowError(
        expect.objectContaining({ code: 'invalid_config' }),
      );
    }
    expect(commandRegistry['runs create']?.validateConfig).toBeUndefined();
    expect(commandRegistry['auth login']?.validateConfig).toBeUndefined();
  });
});
