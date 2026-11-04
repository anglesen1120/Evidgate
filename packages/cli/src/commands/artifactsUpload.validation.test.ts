// The local refusals of `evidgate artifacts upload` (contracts/cli.md §5, §8): the file
// guard and the kind cap before any request, the flag usage errors, an unknown or
// media-type-less kind, and a malformed upload deadline. None of these reaches the
// network — not even the dev-token exchange — so each case asserts an empty `calls` and
// `exchanges` list. The fixtures are in `artifactsUpload.testing.ts`.
import { mkdtemp, open, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { beforeEach, describe, expect, it } from 'vitest';

import { main } from '../main.js';

import {
  capture,
  executionKey,
  runId,
  stubFetch,
  useArtifactsUploadSpec,
} from './artifactsUpload.testing.js';

describe('artifacts upload', () => {
  const spec = useArtifactsUploadSpec();
  const { dirs, evidence, devEnv, happyStore, argv } = spec;
  let root: string;

  beforeEach(() => {
    root = spec.rootDir();
  });

  // §8: the file guard runs before any request — not even the token exchange.
  it('refuses_a_file_outside_the_allowed_directories_before_any_request', async () => {
    const outside = join(await mkdtemp(join(tmpdir(), 'evidgate-outside-')), 'passwd');
    dirs.push(join(outside, '..'));
    await writeFile(outside, 'root:x:0:0', 'utf8');
    const store = stubFetch(happyStore());
    const { io, stdout, stderr } = capture();

    await expect(
      main(
        [
          'artifacts',
          'upload',
          '--run',
          runId,
          '--file',
          outside,
          '--kind',
          'trace',
          '--media-type',
          'application/zip',
          '--execution-key',
          executionKey,
        ],
        devEnv(),
        io,
        { fetch: store.fetchImpl },
      ),
    ).resolves.toBe(3);

    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
    expect(stdout.join('')).toBe('');
    expect(stderr.join('')).toContain('evidgate: error upload_path_refused: ');
    expect(stderr.join('')).toContain('--dir');
  });

  it('refuses_a_file_above_the_kind_cap_before_any_request', async () => {
    const file = await evidence('big.png');
    const handle = await open(file, 'r+');
    await handle.truncate(10_485_761);
    await handle.close();
    const store = stubFetch(happyStore());
    const { io, stderr } = capture();

    await expect(
      main(
        [
          'artifacts',
          'upload',
          '--run',
          runId,
          '--file',
          file,
          '--kind',
          'screenshot',
          '--execution-key',
          executionKey,
          '--dir',
          root,
        ],
        devEnv(),
        io,
        { fetch: store.fetchImpl },
      ),
    ).resolves.toBe(3);

    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
    expect(stderr.join('')).toContain('evidgate: error upload_too_large: ');
  });

  // §5: usage errors exit 3 with the help hint and never reach the network.
  it.each([
    [['--run', 'not-a-uuid'], '--run must be a UUID', ['--run', 'not-a-uuid']],
    [
      ['--execution-key', 'abc'],
      '--execution-key must be 64 lowercase hex characters',
      ['--execution-key', 'abc'],
    ],
    [['--attempt', '0'], '--attempt must be an integer of at least 1', ['--attempt', '0']],
    [['--producer', 'bad/producer'], '--producer must be 1..128', ['--producer', 'bad/producer']],
    [
      ['--media-type', 'bad\u0000type'],
      '--media-type must be 1..255 characters',
      ['--media-type', 'bad\u0000type'],
    ],
  ])('refuses_%s_as_a_usage_error', async (_label, message, overrides) => {
    const file = await evidence();
    const store = stubFetch(happyStore());
    const { io, stderr } = capture();

    // The override replaces the base value of the same flag (a repeat would be refused as
    // "may be given only once" before the value is validated).
    const flag = overrides[0] as string;
    const base = argv(file).filter(
      (entry: string, index: number, all: string[]) => entry !== flag && all[index - 1] !== flag,
    );
    await expect(
      main([...base, ...overrides], devEnv(), io, { fetch: store.fetchImpl }),
    ).resolves.toBe(3);

    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
    expect(stderr.join('')).toContain('evidgate: error usage: ');
    expect(stderr.join('')).toContain(message);
    expect(stderr.join('')).toContain("run 'evidgate --help' for usage");
  });

  // The shared flag helper refuses an empty required value, so `--file ''` fails as a usage
  // error before any request rather than reaching the path guard (or `--run ''` the UUID
  // check), which are the wrong errors for a flag that is present but empty.
  it('refuses_an_empty_required_value_before_any_request', async () => {
    const store = stubFetch(happyStore());
    const { io, stdout, stderr } = capture();

    await expect(
      main(
        [
          'artifacts',
          'upload',
          '--run',
          runId,
          '--file',
          '',
          '--kind',
          'trace',
          '--execution-key',
          executionKey,
          '--dir',
          root,
        ],
        devEnv(),
        io,
        { fetch: store.fetchImpl },
      ),
    ).resolves.toBe(3);

    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
    expect(stdout.join('')).toBe('');
    expect(stderr.join('')).toContain(
      'evidgate: error usage: --file must be a single, non-empty value',
    );
    expect(stderr.join('')).toContain("run 'evidgate --help' for usage");
  });

  it('refuses_an_unknown_kind', async () => {
    const file = await evidence();
    const store = stubFetch(happyStore());
    const { io, stderr } = capture();

    await expect(
      main(
        [
          'artifacts',
          'upload',
          '--run',
          runId,
          '--file',
          file,
          '--kind',
          'nope',
          '--execution-key',
          executionKey,
          '--dir',
          root,
        ],
        devEnv(),
        io,
        { fetch: store.fetchImpl },
      ),
    ).resolves.toBe(3);
    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
    expect(stderr.join('')).toContain('--kind must be one of trace, video, screenshot');
  });

  it('requires_a_media_type_when_no_static_default_exists', async () => {
    const file = await evidence('snapshot.html', '<html></html>');
    const store = stubFetch(happyStore());
    const { io, stderr } = capture();

    await expect(
      main(
        [
          'artifacts',
          'upload',
          '--run',
          runId,
          '--file',
          file,
          '--kind',
          'dom_snapshot',
          '--execution-key',
          executionKey,
          '--dir',
          root,
        ],
        devEnv(),
        io,
        { fetch: store.fetchImpl },
      ),
    ).resolves.toBe(3);
    expect(stderr.join('')).toContain('--media-type is required for --kind dom_snapshot');
    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
  });

  // §3: a malformed EVIDGATE_UPLOAD_DEADLINE refuses before the token exchange.
  it('refuses_a_malformed_upload_deadline', async () => {
    const file = await evidence();
    const store = stubFetch(happyStore());
    const { io, stderr } = capture();

    await expect(
      main(argv(file), devEnv({ EVIDGATE_UPLOAD_DEADLINE: 'forever' }), io, {
        fetch: store.fetchImpl,
      }),
    ).resolves.toBe(3);

    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
    expect(stderr.join('')).toContain('evidgate: error invalid_config: ');
    expect(stderr.join('')).toContain('EVIDGATE_UPLOAD_DEADLINE');
    expect(stderr.join('')).not.toContain('forever');
  });
});
