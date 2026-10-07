// 04.2.1-T2.1 / 04.2.1-T1.1: the pure entry point. `main` takes argv, the environment,
// the two output writers and the optional exchange seams, returns the exit code and
// never calls `process.exit` (that is `bin.ts`). Every write goes through a redacting
// writer, so a secret can never reach stdout/stderr (T78); the one exception is the
// `::add-mask::` line, which a command writes through `maskSecret` to the RAW stdout.
import { readFileSync } from 'node:fs';
import { parseArgs, type ParseArgsOptionsConfig } from 'node:util';

import type { ExchangeDeps } from './auth/exchange.js';
import {
  resolveCommand,
  usageText,
  type CommandDefinition,
  type ParsedValues,
} from './commands/index.js';
import {
  formatConfigForVerbose,
  registerEnvSecrets,
  resolveConfig,
  type ConfigFlags,
} from './config.js';
import {
  CliError,
  escapeControlCharacters,
  formatCliError,
  formatInternalError,
  prefixStderrLines,
} from './errors.js';
import { ExitCode } from './exitCodes.js';
import { createRedactingIo, redact, resetSecrets, type Io } from './redact.js';

const helpHint = "run 'evidgate --help' for usage";

/** Options every command accepts; a command's own `options` are merged on top. */
const globalOptions: ParseArgsOptionsConfig = {
  help: { type: 'boolean', short: 'h' },
  version: { type: 'boolean' },
  verbose: { type: 'boolean' },
  json: { type: 'boolean' },
  'api-url': { type: 'string' },
  project: { type: 'string' },
};

interface ParsedArguments {
  readonly values: ParsedValues;
  readonly positionals: string[];
}

export async function main(
  argv: readonly string[],
  env: Readonly<Record<string, string | undefined>>,
  io: Io,
  deps: ExchangeDeps = {},
): Promise<ExitCode> {
  // Register every secret before any argument parsing, so a usage error that echoes a
  // secret value is still redacted (QA-D1, contracts/cli.md §6).
  resetSecrets();
  registerEnvSecrets(env);
  const output = createRedactingIo(io);
  // The mask line must reach the runner verbatim, so it is written to the RAW stdout.
  // `redact(value) !== value` is true only for a registered secret, and the CR/LF guard
  // keeps a value from forging a second workflow command (04.2.1-T1.1, T78).
  const maskSecret = (value: string): void => {
    if (value.length > 0 && !/[\r\n]/.test(value) && redact(value) !== value) {
      io.stdout(`::add-mask::${value}\n`);
    }
  };
  let verbose = false;
  try {
    const global = parseGlobal(argv);
    verbose = global.values.verbose === true;
    if (
      global.values.help === true ||
      (global.positionals.length === 1 && global.positionals[0] === 'help')
    ) {
      output.stdout(usageText);
      return ExitCode.Ok;
    }
    if (global.values.version === true) {
      output.stdout(`${readCliVersion()}\n`);
      return ExitCode.Ok;
    }
    const resolved = resolveCommand(global.positionals);
    if (resolved === undefined) {
      const text = global.positionals.join(' ');
      throw new CliError(text.length === 0 ? 'no command given' : `unknown command '${text}'`, {
        code: 'usage',
        hint: helpHint,
      });
    }
    const parsed = parseCommand(argv, resolved.command);
    const rest = parsed.positionals.slice(resolved.words);
    if (rest.length > 0 && resolved.command.positionals === undefined) {
      throw new CliError(`unexpected argument '${rest[0] ?? ''}'`, {
        code: 'usage',
        hint: helpHint,
      });
    }
    const config = resolveConfig({
      env,
      flags: toConfigFlags(parsed.values),
      requireToken: resolved.command.requireToken,
    });
    if (config.verbose) {
      output.stderr(prefixStderrLines(`${formatConfigForVerbose(config)}\n`));
    }
    return await resolved.command.run({
      config,
      io: output,
      env,
      values: parsed.values,
      positionals: rest,
      deps,
      maskSecret,
    });
  } catch (error) {
    return reportError(error, output, verbose);
  }
}

/**
 * First parse pass: global options only, tolerant of unknown options so a per-command
 * flag does not fail before the command is known. Only the positionals (and the global
 * `--help`/`--version`/`--verbose` flags) are used from this pass.
 */
function parseGlobal(argv: readonly string[]): ParsedArguments {
  try {
    const { values, positionals } = parseArgs({
      args: [...argv],
      options: globalOptions,
      allowPositionals: true,
      strict: false,
    });
    return { values, positionals };
  } catch (error) {
    throw usageError(error);
  }
}

/** Second parse pass: the global options merged with the command's own, strict. */
function parseCommand(argv: readonly string[], command: CommandDefinition): ParsedArguments {
  try {
    const { values, positionals } = parseArgs({
      args: [...argv],
      options: { ...globalOptions, ...command.options },
      allowPositionals: true,
      strict: true,
    });
    return { values, positionals };
  } catch (error) {
    throw usageError(error);
  }
}

function usageError(error: unknown): CliError {
  const message = error instanceof Error ? error.message.split('\n')[0] : undefined;
  return new CliError(message ?? 'invalid arguments', { code: 'usage', hint: helpHint });
}

function toConfigFlags(values: ParsedValues): ConfigFlags {
  const flags: ConfigFlags = {};
  const apiUrl = values['api-url'];
  if (typeof apiUrl === 'string') {
    flags.apiUrl = apiUrl;
  }
  const project = values.project;
  if (typeof project === 'string') {
    flags.project = project;
  }
  if (values.verbose === true) {
    flags.verbose = true;
  }
  if (values.json === true) {
    flags.json = true;
  }
  return flags;
}

function reportError(error: unknown, io: Io, verbose: boolean): ExitCode {
  if (error instanceof CliError) {
    io.stderr(prefixStderrLines(`${formatCliError(error)}\n`));
    return ExitCode.Error;
  }
  io.stderr(prefixStderrLines(`${formatInternalError(error)}\n`));
  if (verbose && error instanceof Error && error.stack !== undefined) {
    io.stderr(
      prefixStderrLines(`${error.stack.split('\n').map(escapeControlCharacters).join('\n')}\n`),
    );
  }
  return ExitCode.Error;
}

/** The package version, read from the manifest next to `dist/` (or `src/`). */
function readCliVersion(): string {
  const manifest = JSON.parse(
    readFileSync(new URL('../package.json', import.meta.url), 'utf8'),
  ) as { version?: unknown };
  return typeof manifest.version === 'string' ? manifest.version : '0.0.0';
}
