// One table over every caller that reads a body: a 2xx whose body dies after its first
// chunk must end in the read-failure code (events send: the transport class), with no
// success line and no byte, URL or token from the answer echoed anywhere. `gate` is the
// one exclusion: it reads its own body and is covered by its own spec.
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { exchangeToken } from './auth/exchange.js';
import { bodyCutResponse, bodyCutSecrets } from './bodyCut.testing.js';
import { capture, stubFetch } from './commands/cliSpec.testing.js';
import {
  useArtifactsUploadSpec,
  stubFetch as artifactsStubFetch,
} from './commands/artifactsUpload.testing.js';
import { devStubFetch } from './commands/devExchange.testing.js';
import {
  baseEnv,
  cleanupSpecState,
  envelopeText,
  invoke as eventsInvoke,
  sendArgv,
  stubFetch as eventsStubFetch,
  tempDir as eventsTempDir,
  virtualClock,
} from './commands/eventsSend.testing.js';
import { useBuildSpec } from './commands/manifestsBuild.testing.js';
import { useManifestSpec } from './commands/manifestsUpload.testing.js';
import {
  createArgv,
  invoke as createInvoke,
  stubDevFetch as createStubDevFetch,
  useRunsCreateSpec,
} from './commands/runsCreate.testing.js';
import {
  finalizeWith,
  invoke as finalizeInvoke,
  stubDevFetch as finalizeStubDevFetch,
  useFinalizeSpec,
} from './commands/runsFinalize.testing.js';
import { resolveConfig } from './config.js';
import { CliError } from './errors.js';
import { ExitCode } from './exitCodes.js';
import { main } from './main.js';
import { resetSecrets } from './redact.js';

/** The one shape every caller is asserted through, whatever route reaches it. */
interface Outcome {
  readonly exit: number;
  readonly stdout: string;
  /** stderr for a command, or the rendered `CliError` for a direct call. */
  readonly text: string;
}

interface Caller {
  readonly name: string;
  /** Codes the outcome must name: the read failure first, then its classification. */
  readonly codes: readonly string[];
  readonly run: () => Promise<Outcome>;
}

interface CommandRun {
  readonly exit: number;
  readonly stdout: readonly string[];
  readonly stderr: readonly string[];
}

function commandOutcome(run: CommandRun): Outcome {
  return { exit: run.exit, stdout: run.stdout.join(''), text: run.stderr.join('') };
}

/** Runs `main()` and keeps the exit code with both streams. */
async function runMain(
  argv: readonly string[],
  env: Readonly<Record<string, string | undefined>>,
  fetchImpl: typeof fetch,
): Promise<Outcome> {
  const out = capture();
  const exit = await main(argv, env, out.io, { fetch: fetchImpl });
  return commandOutcome({ ...out, exit });
}

describe('cut response bodies', () => {
  const finalizeSpec = useFinalizeSpec();
  const createSpec = useRunsCreateSpec();
  const manifestSpec = useManifestSpec();
  const buildSpec = useBuildSpec();
  const artifactsSpec = useArtifactsUploadSpec();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await cleanupSpecState();
  });

  const callers: readonly Caller[] = [
    {
      name: 'exchange',
      codes: ['response_read_failed'],
      run: async () => {
        const config = resolveConfig({
          env: {
            EVIDGATE_API_URL: 'https://api.evidgate.test',
            EVIDGATE_PROJECT: 'proj-1',
            EVIDGATE_CACHE_DIR: await manifestSpec.tempDir(),
            EVIDGATE_INGEST_TOKEN: 'egt_ingest_secret_value',
          },
        });
        const stub = stubFetch(() => bodyCutResponse(200));
        try {
          await exchangeToken(config, {
            fetch: stub.fetchImpl,
            now: () => 1_700_000_000_000,
            random: () => 0,
            sleep: () => Promise.resolve(),
          });
        } catch (error) {
          if (!(error instanceof CliError)) {
            throw error;
          }
          return {
            exit: ExitCode.Error,
            stdout: '',
            text: `${error.code}: ${error.message} ${error.hint ?? ''}`,
          };
        }
        return { exit: ExitCode.Ok, stdout: '', text: '' };
      },
    },
    {
      name: 'manifests upload',
      codes: ['response_read_failed'],
      run: async () => {
        const file = await manifestSpec.manifestFile('{"schema":"manifest.v1"}\n');
        const env = await manifestSpec.devEnv();
        const stub = devStubFetch(() => bodyCutResponse(201));
        return runMain(['manifests', 'upload', '--file', file], env, stub.fetchImpl);
      },
    },
    {
      name: 'runs create',
      codes: ['response_read_failed'],
      run: async () => {
        const env = await createSpec.devEnv();
        const stub = createStubDevFetch(() => bodyCutResponse(201));
        const out = capture();
        const exit = await createInvoke(createArgv(), env, out, stub.fetchImpl);
        return commandOutcome({ ...out, exit });
      },
    },
    {
      name: 'runs finalize',
      codes: ['response_read_failed'],
      run: async () => {
        const { spoolDir } = await finalizeSpec.writeSpool({ spooled: 2, acked: 2 });
        const env = await finalizeSpec.devEnv(spoolDir);
        const stub = finalizeStubDevFetch(() => bodyCutResponse(202));
        const out = capture();
        const exit = await finalizeInvoke(finalizeWith(), env, out, stub.fetchImpl);
        return commandOutcome({ ...out, exit });
      },
    },
    {
      name: 'manifests build',
      codes: ['response_read_failed'],
      run: async () => {
        const fixture = await buildSpec.setup();
        const stub = stubFetch(() => bodyCutResponse(200));
        const outcome = await buildSpec.run(fixture, stub.fetchImpl);
        return commandOutcome({
          exit: outcome.code,
          stdout: outcome.stdout,
          stderr: outcome.stderr,
        });
      },
    },
    {
      name: 'events send',
      codes: ['deadline_exceeded', 'transport_failed'],
      run: async () => {
        const input = await eventsTempDir();
        const spoolDir = await eventsTempDir();
        const file = join(input, 'events.jsonl');
        await writeFile(file, `${envelopeText(1)}\n${envelopeText(2)}\n`);
        const clock = virtualClock();
        const stub = eventsStubFetch(() => bodyCutResponse(200));
        const out = capture();
        const exit = await eventsInvoke(
          sendArgv(['--from', file]),
          baseEnv(spoolDir, { EVIDGATE_SEND_DEADLINE: '5s' }),
          out,
          {
            fetch: stub.fetchImpl,
            now: clock.now,
            sleep: clock.sleep,
            random: () => 0.5,
          },
        );
        return commandOutcome({ ...out, exit });
      },
    },
    {
      name: 'artifacts upload',
      codes: ['response_read_failed'],
      run: async () => {
        const file = await artifactsSpec.evidence();
        const env = artifactsSpec.devEnv();
        const stub = artifactsStubFetch(() => bodyCutResponse(201));
        return runMain(artifactsSpec.argv(file), env, stub.fetchImpl);
      },
    },
  ];

  it.each(callers)(
    '$name ends on the read failure with no success output and no leak',
    async (caller) => {
      const outcome = await caller.run();

      expect(outcome.exit).toBe(ExitCode.Error);
      expect(outcome.stdout).toBe('');
      for (const code of caller.codes) {
        expect(outcome.text).toContain(code);
      }
      expect(outcome.text).not.toContain('ack_mismatch');
      for (const secret of bodyCutSecrets) {
        expect(outcome.text).not.toContain(secret);
        expect(outcome.stdout).not.toContain(secret);
      }
    },
  );
});
