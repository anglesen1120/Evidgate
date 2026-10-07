// 04.2.1-T2.1: the command registry (04.2.1 §5). 04.2.2/04.2.3 add a command by
// appending one entry here; the dispatcher in `main.ts` never changes.
import type { ResolvedConfig } from '../config.js';
import type { Io } from '../redact.js';

import { configCheckCommand } from './configCheck.js';

export interface CommandContext {
  readonly config: ResolvedConfig;
  readonly io: Io;
}

export interface CommandDefinition {
  /** Space-joined positionals, e.g. `config check`. */
  readonly name: string;
  /** One-line description shown by `--help`. */
  readonly summary: string;
  /** Whether `resolveConfig` must find a token source before the command runs. */
  readonly requireToken: boolean;
  readonly run: (context: CommandContext) => Promise<number>;
}

/** Static string-keyed registry; `findCommand` guards against inherited keys. */
export const commandRegistry: Readonly<Record<string, CommandDefinition>> = {
  [configCheckCommand.name]: configCheckCommand,
};

/** Registry lookup that never returns an `Object.prototype` member. */
export function findCommand(name: string): CommandDefinition | undefined {
  return Object.hasOwn(commandRegistry, name) ? commandRegistry[name] : undefined;
}

export const usageText = [
  'evidgate - Evidgate CI client',
  '',
  'Usage: evidgate <command> [options]',
  '',
  'Commands:',
  ...Object.values(commandRegistry).map(
    (command) => `  ${command.name.padEnd(14)}${command.summary}`,
  ),
  '',
  'Options:',
  '  -h, --help          show this help and exit',
  '      --version       print the CLI version and exit',
  '      --verbose       print the resolved config to stderr (secrets redacted)',
  '      --json          machine-readable output',
  '      --api-url <url> Evidgate API base URL (env: EVIDGATE_API_URL)',
  '      --project <id>  Evidgate project (env: EVIDGATE_PROJECT)',
  '',
].join('\n');
