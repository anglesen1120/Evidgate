// Path-guard and kind-cap boundaries: a FIFO, a directory, symlink chains, a dot-dot
// segment, root building and the exact cap byte.
import { execFileSync } from 'node:child_process';
import { mkdirSync, realpathSync, symlinkSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { isOverCap } from './attachMap.js';
import { createPathGuard, defaultAttachmentRoots } from './attachPaths.js';
import { attachment, file, harness, input, recordAt, tempDir } from './attach-qa.testing.js';

describe('QA probe: AC6 path guard boundaries', () => {
  it('refuses a FIFO, a directory and a symlink to a directory without hanging', async () => {
    const root = tempDir('qa-s87-fifo-');
    const fifo = join(root, 'trace-fifo.zip');
    execFileSync('mkfifo', [fifo]);
    mkdirSync(join(root, 'sub'));
    const dirLink = join(root, 'shot-dirlink.png');
    symlinkSync(join(root, 'sub'), dirLink);
    const guard = createPathGuard({ roots: [root], cwd: root });

    const started = Date.now();
    const fifoVerdict = await guard.check(fifo);
    const dirVerdict = await guard.check(dirLink);
    expect(Date.now() - started).toBeLessThan(2_000);

    expect(fifoVerdict.ok).toBe(false);
    if (!fifoVerdict.ok) {
      expect(fifoVerdict.code).toBe('attachment_path_refused');
      expect(fifoVerdict.message).toContain('not a regular file');
    }
    expect(dirVerdict.ok).toBe(false);
    if (!dirVerdict.ok) expect(dirVerdict.code).toBe('attachment_path_refused');
  });

  it('refuses a two-hop symlink chain that ends outside the root', async () => {
    const root = tempDir('qa-s87-chain-');
    const outside = tempDir('qa-s87-outside-');
    const secret = file(outside, 'trace-secret.zip');
    symlinkSync(secret.path, join(root, 'hop1.zip'));
    symlinkSync(join(root, 'hop1.zip'), join(root, 'trace-hop2.zip'));
    const guard = createPathGuard({ roots: [root], cwd: root });

    const verdict = await guard.check(join(root, 'trace-hop2.zip'));
    expect(verdict.ok).toBe(false);
    if (!verdict.ok) expect(verdict.code).toBe('attachment_path_refused');
  });

  it('accepts a dot-dot segment that resolves back inside the root', async () => {
    const root = tempDir('qa-s87-dotdot-');
    mkdirSync(join(root, 'sub'));
    const inside = file(join(root, 'sub'), 'shot.png');
    const guard = createPathGuard({ roots: [root], cwd: root });

    const verdict = await guard.check(`${root}/sub/../sub/shot.png`);
    expect(verdict.ok).toBe(true);
    if (verdict.ok) expect(verdict.realPath).toBe(realpathSync(inside.path));
  });

  it('drops empty and blank entries when the roots are built', () => {
    const root = tempDir('qa-s87-roots-');
    const roots = defaultAttachmentRoots({
      outputDirs: ['', '   '],
      rootDir: root,
      extraDirs: ['', '  '],
    });
    expect(roots).toEqual([join(root, 'test-results')]);
  });

  it('treats zero as a valid cap and refuses the first byte over it', () => {
    expect(isOverCap('screenshot', 0, { screenshot: 0 })).toBe(false);
    expect(isOverCap('screenshot', 1, { screenshot: 0 })).toBe(true);
    expect(isOverCap('screenshot', -1, { screenshot: 10 })).toBe(true);
    expect(isOverCap('screenshot', Number.NaN, { screenshot: 10 })).toBe(true);
    expect(isOverCap('screenshot', Number.POSITIVE_INFINITY, { screenshot: 10 })).toBe(true);
  });

  it('uploads a file exactly at its kind cap and skips cap+1 without an upload', async () => {
    const { pipeline, uploader, root } = harness({ caps: { screenshot: 4 } });
    const at = file(root, 'shot-at.png', 4);
    const over = file(root, 'shot-over.png', 5);

    const result = await pipeline.process(
      input([
        attachment(at.path, 'shot-at.png', 'image/png'),
        attachment(over.path, 'shot-over.png', 'image/png'),
      ]),
    );
    await pipeline.finish();

    expect(result.records.map((record) => record.outcome)).toEqual(['uploaded', 'skipped']);
    expect(recordAt(result, 1).code).toBe('attachment_over_cap');
    expect(uploader.calls).toHaveLength(1);
  });
});
