// 04.2.1-T2.1: the pure entry point. `main` takes argv, the environment and the two
// output writers, returns the exit code and never calls `process.exit` (that is
// `bin.ts`). Every write goes through a redacting writer, so a secret can never reach
// stdout/stderr (T78).
import { readFileSync } from 'node:fs';
import { parseArgs } from 'node:util';

import { findCommand, usageText } from './commands/index.js';
import { formatConfigForVerbose, resolveConfig, type ConfigFlags } from './config.js';
import { CliError, formatCliError } from './errors.js';
import { ExitCode } from './exitCodes.js';
import { createRedactingIo, resetSecrets, type Io } from './redact.js';

const helpHint = "run 'evidgate --help' for usage";

const cliOptions = {
  help: { type: 'boolean', short: 'h' },
  version: { type: 'boolean' },
  verbose: { type: 'boolean' },
  json: { type: 'boolean' },
  'api-url': { type: 'string' },
  project: { type: 'string' },
} as const;

interface ParsedArguments {
  readonly command: string;
  readonly flags: ConfigFlags;
  readonly help: boolean;
  readonly version: boolean;
}

export async function main(
  argv: readonly string[],
  env: Readonly<Record<string, string | undefined>>,
  io: Io,
): Promise<number> {
  resetSecrets();
  const output = createRedactingIo(io);
  let verbose = false;
  try {
    const parsed = parseArguments(argv);
    verbose = parsed.flags.verbose === true;
    if (parsed.help) {
      output.stdout(usageText);
      return ExitCode.Ok;
    }
    if (parsed.version) {
      output.stdout(`${readCliVersion()}\n`);
      return ExitCode.Ok;
    }
    const command = findCommand(parsed.command);
    if (command === undefined) {
      const message =
        parsed.command.length === 0 ? 'no command given' : `unknown command '${parsed.command}'`;
      throw new CliError(message, { code: 'usage', hint: helpHint });
    }
    const config = resolveConfig({ env, flags: parsed.flags, requireToken: command.requireToken });
    if (config.verbose) {
      output.stderr(`${formatConfigForVerbose(config)}\n`);
    }
    return await command.run({ config, io: output });
  } catch (error) {
    return reportError(error, output, verbose);
  }
}

function parseArguments(argv: readonly string[]): ParsedArguments {
  try {
    const { values, positionals } = parseArgs({
      args: [...argv],
      options: cliOptions,
      allowPositionals: true,
      strict: true,
    });
    const flags: ConfigFlags = {};
    const apiUrl = values['api-url'];
    if (apiUrl !== undefined) {
      flags.apiUrl = apiUrl;
    }
    const project = values.project;
    if (project !== undefined) {
      flags.project = project;
    }
    if (values.verbose === true) {
      flags.verbose = true;
    }
    if (values.json === true) {
      flags.json = true;
    }
    const command = positionals.join(' ');
    return {
      command,
      flags,
      help: values.help === true || command === 'help',
      version: values.version === true,
    };
  } catch (error) {
    const message = error instanceof Error ? error.message.split('\n')[0] : undefined;
    throw new CliError(message ?? 'invalid arguments', { code: 'usage', hint: helpHint });
  }
}

function reportError(error: unknown, io: Io, verbose: boolean): number {
  if (error instanceof CliError) {
    io.stderr(`${formatCliError(error)}\n`);
    return error.exitCode;
  }
  const message = error instanceof Error ? error.message : 'unexpected non-Error failure';
  io.stderr(`evidgate: error internal_error: ${message}\n`);
  if (verbose && error instanceof Error && error.stack !== undefined) {
    io.stderr(`${error.stack}\n`);
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
