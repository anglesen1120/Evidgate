import { describe, expect, it } from 'vitest';

import { ExitCode } from '../exitCodes.js';

import { commandRegistry, renderUsage, resolveCommand, type CommandDefinition } from './index.js';

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
});
