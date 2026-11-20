// The command registry. A new command is added by appending one entry here; the dispatcher in
// `main.ts` never changes, so commands cannot drift in how they are parsed or dispatched
// (contracts/cli.md §1).
import type { ParseArgsOptionsConfig } from 'node:util';

import type { ExchangeDeps } from '../auth/exchange.js';
import type { ResolvedConfig } from '../config.js';
import type { ExitCode } from '../exitCodes.js';
import type { Io } from '../redact.js';

import { artifactsUploadCommand } from './artifactsUpload.js';
import { authLoginCommand } from './authLogin.js';
import { authLogoutCommand } from './authLogout.js';
import { configCheckCommand } from './configCheck.js';
import { eventsSendCommand } from './eventsSend.js';
import { gateCommand } from './gate.js';
import { manifestsBuildCommand } from './manifestsBuild.js';
import { manifestsUploadCommand } from './manifestsUpload.js';
import { quickstartCommand } from './quickstart.js';
import { runsCreateCommand } from './runsCreate.js';
import { runsFinalizeCommand } from './runsFinalize.js';

/** Values `node:util` `parseArgs` returns for the merged global + command options. */
export type ParsedValues = Record<string, string | boolean | Array<string | boolean> | undefined>;

export interface CommandContext {
  readonly config: ResolvedConfig;
  readonly io: Io;
  readonly env: Readonly<Record<string, string | undefined>>;
  readonly values: ParsedValues;
  readonly positionals: readonly string[];
  /** Transport/back-off seams `main` forwards from its own `deps` argument. */
  readonly deps: ExchangeDeps;
  /**
   * Writes a GitHub `::add-mask::<value>` line to the RAW stdout, bypassing the redacting
   * writer (which would otherwise turn the mask into `[REDACTED]`). A no-op unless `value` is
   * a registered secret without a line break, because the runner parses that line by line.
   */
  readonly maskSecret: (value: string) => void;
}

export interface CommandDefinition {
  /** Space-separated command words, e.g. `config check`. */
  readonly name: string;
  /** One-line description shown by `--help`. */
  readonly summary: string;
  /** Whether `resolveConfig` must find a token source before the command runs. */
  readonly requireToken: boolean;
  /** Per-command `parseArgs` options, merged over the global options. */
  readonly options?: ParseArgsOptionsConfig;
  /** Usage placeholder for the command's positional arguments, e.g. `<file>`. */
  readonly positionals?: string;
  /**
   * Optional pre-flight check `main` runs after `resolveConfig` and BEFORE the `--verbose`
   * config dump, so a command can refuse an unusable configuration without that dump echoing
   * the offending value.
   */
  readonly validateConfig?: (config: ResolvedConfig) => void;
  readonly run: (context: CommandContext) => Promise<ExitCode>;
}

/** Static string-keyed registry; `resolveCommand` matches the longest name prefix. */
export const commandRegistry: Readonly<Record<string, CommandDefinition>> = {
  [configCheckCommand.name]: configCheckCommand,
  [authLoginCommand.name]: authLoginCommand,
  [authLogoutCommand.name]: authLogoutCommand,
  [manifestsBuildCommand.name]: manifestsBuildCommand,
  [manifestsUploadCommand.name]: manifestsUploadCommand,
  [quickstartCommand.name]: quickstartCommand,
  [runsCreateCommand.name]: runsCreateCommand,
  // The two spool commands (`events send`, `runs finalize`), appended last so the dispatcher
  // in `main.ts` never changes (contracts/cli.md §1).
  [eventsSendCommand.name]: eventsSendCommand,
  [runsFinalizeCommand.name]: runsFinalizeCommand,
  // `artifacts upload` (`upload-url` -> presigned PUT -> `complete`).
  [artifactsUploadCommand.name]: artifactsUploadCommand,
  // `gate` reads one run's decision and maps it to the exit code.
  [gateCommand.name]: gateCommand,
};

export interface ResolvedCommand {
  readonly command: CommandDefinition;
  /** How many leading positionals the command name consumed. */
  readonly words: number;
  /** Positionals left after the command words. */
  readonly rest: readonly string[];
}

/**
 * Resolves the command by the longest registered name whose space-separated words
 * prefix `positionals` (contracts/cli.md §1). Returns `undefined` when none matches.
 * The registry is a parameter so a spec can exercise the longest-prefix rule.
 */
export function resolveCommand(
  positionals: readonly string[],
  registry: Readonly<Record<string, CommandDefinition>> = commandRegistry,
): ResolvedCommand | undefined {
  let best: CommandDefinition | undefined;
  let bestWords = 0;
  for (const command of Object.values(registry)) {
    const words = command.name.split(' ');
    if (words.length > positionals.length || words.length <= bestWords) {
      continue;
    }
    if (words.every((word, index) => word === positionals[index])) {
      best = command;
      bestWords = words.length;
    }
  }
  if (best === undefined) {
    return undefined;
  }
  return { command: best, words: bestWords, rest: positionals.slice(bestWords) };
}

/** Renders the `--help` text; the column width follows the longest command label. */
export function renderUsage(commands: readonly CommandDefinition[]): string {
  const rows = commands.map((command) => ({
    label:
      command.positionals === undefined ? command.name : `${command.name} ${command.positionals}`,
    summary: command.summary,
  }));
  const width = Math.max(0, ...rows.map((row) => row.label.length)) + 2;
  return [
    'evidgate - Evidgate CI client',
    '',
    'Usage: evidgate <command> [options]',
    '',
    'Commands:',
    ...rows.map((row) => `  ${row.label.padEnd(width)}${row.summary}`),
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
}

export const usageText = renderUsage(Object.values(commandRegistry));
