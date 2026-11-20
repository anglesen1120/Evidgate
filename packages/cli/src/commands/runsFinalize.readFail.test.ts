// `runs finalize`: a 202 whose body is cut off is a read failure, not a finalize. The
// command must exit 3 with the read-failure code, print no success line, and keep every
// spool byte — the purge belongs after a fully read 202, never before.
import { existsSync } from 'node:fs';

import { describe, expect, it } from 'vitest';

import { bodyCutResponse, bodyCutSecrets } from '../bodyCut.testing.js';

import {
  accepted,
  apiCalls,
  capture,
  finalizeWith,
  invoke,
  producerId,
  runId,
  snapshotTree,
  stubDevFetch,
  useFinalizeSpec,
} from './runsFinalize.testing.js';

describe('runs finalize', () => {
  const { writeSpool, devEnv } = useFinalizeSpec();

  it('keeps_the_spool_when_the_accepted_body_is_cut', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 3, acked: 3 });
    const env = await devEnv(spoolDir);
    const stub = stubDevFetch(() => bodyCutResponse(202));
    const out = capture();
    const before = await snapshotTree(producerDir);

    await expect(invoke(finalizeWith(), env, out, stub.fetchImpl)).resolves.toBe(3);

    expect(out.stdout.join('')).toBe('');
    const text = out.stderr.join('');
    expect(text).toContain('evidgate: error response_read_failed: ');
    expect(text).toContain('evidgate: hint: ');
    // One request: a rejection is not retried into a second finalize.
    expect(apiCalls(stub)).toHaveLength(1);
    // Every chunk and the cursor are still there, byte for byte.
    expect(existsSync(producerDir)).toBe(true);
    expect(await snapshotTree(producerDir)).toEqual(before);
    for (const secret of bodyCutSecrets) {
      expect(text).not.toContain(secret);
    }
  });

  // A cut accept keeps the spool, so the operator's next run is a replay: the derived key
  // must be unchanged and the fully read 202 must purge. Safety of that replay rests on the
  // credential staying the same too — the server hashes the caller into the replay identity.
  it('reruns_with_the_same_key_and_credential_then_purges', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 3, acked: 3 });
    const env = await devEnv(spoolDir);
    let finalizes = 0;
    const stub = stubDevFetch(() => {
      finalizes += 1;
      return finalizes === 1 ? bodyCutResponse(202) : accepted();
    });

    const first = capture();
    await expect(invoke(finalizeWith(), env, first, stub.fetchImpl)).resolves.toBe(3);
    expect(first.stdout.join('')).toBe('');
    const cutText = first.stderr.join('');
    expect(cutText).toContain('evidgate: error response_read_failed: ');
    // The hint's wording is the seam's to own; only its presence and emptiness of secrets matter.
    expect(cutText).toContain('evidgate: hint: ');
    for (const secret of bodyCutSecrets) {
      expect(cutText).not.toContain(secret);
    }
    expect(existsSync(producerDir)).toBe(true);

    const second = capture();
    await expect(invoke(finalizeWith(), env, second, stub.fetchImpl)).resolves.toBe(0);
    expect(second.stdout.join('')).toContain(
      `evidgate: finalized run=${runId} producer=${producerId}`,
    );
    expect(second.stderr.join('')).toBe('');
    // No `--keep-spool`: the replayed 202 purges the producer directory whole.
    expect(existsSync(producerDir)).toBe(false);

    const sent = apiCalls(stub);
    expect(sent).toHaveLength(2);
    expect(sent[0]?.key).toBeTruthy();
    expect(sent[1]?.key).toBe(sent[0]?.key);
    // Same cached ci_producer token: the replay identity the server hashes is unchanged.
    expect(sent[1]?.headers.get('authorization')).toBe(sent[0]?.headers.get('authorization'));
  });
});
