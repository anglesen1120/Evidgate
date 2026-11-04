// Detail and message rendering for `evidgate manifests upload`: which detail fields are
// skipped, the control-character escaping that keeps a field from forging a workflow
// command, the 200-code-point caps, and the envelope edges (unsafe code, request_id
// fallback, server 413).
import { beforeEach, describe, expect, it } from 'vitest';

import { main } from '../main.js';
import { resetSecrets } from '../redact.js';

import { devStubFetch } from './devExchange.testing.js';
import {
  capture,
  errorEnvelope as envelope,
  jsonResponse,
  useManifestSpec,
} from './manifestsUpload.testing.js';

describe('manifests upload — boundaries', () => {
  const { manifestFile, devEnv } = useManifestSpec('evidgate-cli-qa-');

  beforeEach(() => {
    resetSecrets();
  });

  // ---- details rendering: skipped fields, escaping, caps ----

  it('details_skip_non_string_index_and_path', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(
        422,
        envelope('manifest_invalid', 'bad', {
          details: [{ index: 'zero', path: 42, reason: 'fine' }],
        }),
      ),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    // contracts/cli.md §5: `index=` is omitted when the entry has none and a field that
    // is not a string or a number is skipped.
    expect(stderr.join('')).toContain('evidgate: detail: reason=fine\n');
    expect(stderr.join('')).not.toContain('index=');
    expect(stderr.join('')).not.toContain('path=');
  });

  // Pins contracts/cli.md §5's fail-closed rule: a detail whose `reason` is not a string
  // makes the whole envelope unusable (`isErrorEnvelope` requires a string `reason`), so
  // the reply falls back to `request_failed` and no `detail:` line is printed.
  it('details_non_string_reason_drops_the_envelope', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(
        422,
        envelope('manifest_invalid', 'bad', {
          details: [
            { index: 0, path: '/a', reason: 7 },
            { index: 1, path: '/b', reason: 'fine' },
          ],
        }),
      ),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error request_failed: ');
    expect(text).not.toContain('detail:');
  });

  it('detail_fields_are_escaped_and_capped', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(
        422,
        envelope('manifest_invalid', 'msg\n::error::forged\u0007', {
          details: [{ index: 0, path: '/a\nb', reason: 'r'.repeat(300) }],
        }),
      ),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('msg\\u000a::error::forged\\u0007');
    expect(text).toContain('path=/a\\u000ab');
    // Every stderr line carries the prefix, so no line can start with `::`.
    for (const line of text.split('\n').filter((l) => l.length > 0)) {
      expect(line.startsWith('evidgate: ')).toBe(true);
    }
    const reasonLine = text.split('\n').find((line) => line.includes('reason=')) ?? '';
    expect(reasonLine.length - 'evidgate: detail: index=0 path=/a\\u000ab reason='.length).toBe(
      200,
    );
  });

  it('message_is_capped_at_200_chars', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(422, envelope('manifest_invalid', 'm'.repeat(500))),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const line = stderr.join('').split('\n')[0] ?? '';
    expect(line).toBe(`evidgate: error manifest_invalid: ${'m'.repeat(200)}`);
  });

  it('message_cap_never_leaves_a_secret_prefix', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    // The dev token starts at code point 188, so the raw 200-code-point cut would fall
    // inside it. Redaction runs before the cap, so no prefix of the token can survive.
    const message = `${'x'.repeat(188)}dev-token-value${'y'.repeat(50)}`;
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(422, envelope('manifest_invalid', message)),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).not.toContain('dev-token-value');
    expect(text).not.toContain('dev-toke');
    expect(text).toContain('[REDACTED]');
  });

  it('message_cap_does_not_split_an_astral_char', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    // The emoji is the 200th code point; a UTF-16 `slice(0, 200)` would keep only its
    // high surrogate, so the cap counts code points instead.
    const message = `${'a'.repeat(199)}\u{1F600}${'b'.repeat(10)}`;
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(422, envelope('manifest_invalid', message)),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text.split('\n')[0]).toBe(
      `evidgate: error manifest_invalid: ${'a'.repeat(199)}\u{1F600}`,
    );
    // No lone surrogate anywhere in stderr.
    expect(text).not.toMatch(
      /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/,
    );
  });

  // ---- envelope edge cases: unsafe code, request_id escaping, header fallback ----

  it('unsafe_code_pattern_falls_back_to_request_failed', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() => jsonResponse(422, envelope('Bad Code!', 'bad')));
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error request_failed: bad\n');
  });

  it('request_id_control_chars_are_escaped', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(
        422,
        envelope('manifest_invalid', 'ok', { request_id: 'req\n::error::forged\u0007' }),
      ),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: request_id: req\\u000a::error::forged\\u0007\n');
  });

  it('request_id_is_capped_at_200_code_points', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(422, envelope('manifest_invalid', 'ok', { request_id: 'r'.repeat(5000) })),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain(`evidgate: request_id: ${'r'.repeat(200)}\n`);
    expect(text).not.toContain('r'.repeat(201));
  });

  it('x_request_id_header_is_the_fallback', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(500, { unexpected: true }, { 'x-request-id': 'header-req-9' }),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain(
      'evidgate: error request_failed: the manifest upload failed (HTTP 500)\n',
    );
    expect(text).toContain('evidgate: request_id: header-req-9\n');
  });

  it('server_413_code_is_echoed', async () => {
    const file = await manifestFile('{"schema":"manifest.v1"}');
    const env = await devEnv();
    const { fetchImpl } = devStubFetch(() =>
      jsonResponse(413, envelope('payload_too_large', 'body too large')),
    );
    const { io, stderr } = capture();

    await expect(
      main(['manifests', 'upload', '--file', file], env, io, { fetch: fetchImpl }),
    ).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error payload_too_large: body too large\n');
  });
});
