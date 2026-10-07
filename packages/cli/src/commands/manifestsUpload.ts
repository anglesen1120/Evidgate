// 04.2.2-T1.1: `evidgate manifests upload` — reads the manifest file, sends it
// byte-for-byte to `POST /v1/projects/{p}/manifests` and prints `manifest_id` +
// `manifest_hash` (04.2.2 §5, AC1). The file is read and capped BEFORE `getToken()`,
// so an unreadable or oversize file makes no request at all — not even the token
// exchange. A 401 purges the cached CI token (04.2.1-T1 hand-over, contracts/cli.md
// §7/§8); a 403 keeps it, because a scope refusal does not invalidate the token.
import { createHash } from 'node:crypto';
import { constants } from 'node:fs';
import { open, type FileHandle } from 'node:fs/promises';

import { createManifest, type ManifestV1 } from '@evidgate/sdk';

import { getToken } from '../auth/getToken.js';
import { deleteCachedToken } from '../auth/tokenCache.js';
import { createApiClient } from '../client.js';
import { CliError } from '../errors.js';
import { ExitCode } from '../exitCodes.js';
import { serverErrorFromResponse, transportError } from '../serverError.js';

import type { CommandDefinition } from './index.js';

/** The server's documented body cap (openapi `createManifest`: "Body ≤ 256 KiB → 413"). */
const maxManifestBytes = 256 * 1024;
const requestTimeoutMs = 30_000;
/**
 * The usage-error hint `main.ts` attaches to its parse-level usage errors (`helpHint`
 * there, module-private). Repeated here so the command-level `--file` usage error carries
 * the same pointer (contracts/cli.md §5 `usage` row); keep the two literals in sync.
 */
const helpHint = "run 'evidgate --help' for usage";
// D-2: `O_NONBLOCK` keeps `open` from blocking on a FIFO — without it the non-regular-file
// check below never runs, because the open waits for a writer. Regular-file reads are
// unaffected. win32 has no `O_NONBLOCK`, so the flags reduce to `O_RDONLY` (plain `'r'`).
const openReadFlags = constants.O_RDONLY | (constants.O_NONBLOCK ?? 0);
/** `manifest_id` is a UUID and `manifest_hash` is `sha256:<64 hex>` (openapi `ManifestAccepted`). */
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const manifestHashPattern = /^sha256:[0-9a-f]{64}$/;

export const manifestsUploadCommand: CommandDefinition = {
  name: 'manifests upload',
  summary: 'upload and verify a run manifest',
  requireToken: true,
  options: { file: { type: 'string' } },
  run: async ({ config, io, values, deps }) => {
    const file = values.file;
    if (typeof file !== 'string' || file.length === 0) {
      throw new CliError('missing required flag --file', { code: 'usage', hint: helpHint });
    }
    // The file is read (and capped) before any token work: an oversize or unreadable
    // file must not trigger a request, including the token exchange (04.2.2 §8).
    const body = await readManifestFile(file);
    const { token } = await getToken(config, deps);
    const client = createApiClient(config, token, deps.fetch ?? fetch);
    const result = await createManifest({
      client,
      path: { p: config.project },
      // 00 §9.1: the route requires an `Idempotency-Key`. The file's own digest makes a
      // retry of the same manifest replay the stored 201 instead of writing a second
      // record, and it is the same value the server hashes into `BodySha256`.
      headers: { 'Idempotency-Key': createHash('sha256').update(body).digest('hex') },
      // The generated `body` is typed as the parsed `ManifestV1`, but the contract sends
      // the RAW bytes: the server hashes the canonical form, and a JSON round trip could
      // lose number precision. `bodySerializer: null` stops the client's default
      // `JSON.stringify` from re-encoding them.
      body: body as unknown as ManifestV1,
      bodySerializer: null,
      signal: AbortSignal.timeout(requestTimeoutMs),
    });
    const response = result.response;
    if (response === undefined) {
      // A network error, a timeout or a refused redirect: the SDK returns no response.
      throw transportError(result.error);
    }
    if (!response.ok) {
      if (response.status === 401) {
        // Best-effort: the server's error is the one that must surface (04.2.1 §6).
        await deleteCachedToken(config).catch(() => undefined);
      }
      throw serverErrorFromResponse(result.error, response, {
        code: 'request_failed',
        message: `the manifest upload failed (HTTP ${response.status})`,
      });
    }
    const accepted = readAccepted(result.data);
    if (config.json) {
      io.stdout(
        `${JSON.stringify({
          manifest_id: accepted.manifestId,
          manifest_hash: accepted.manifestHash,
        })}\n`,
      );
    } else {
      io.stdout(`manifest_id=${accepted.manifestId} manifest_hash=${accepted.manifestHash}\n`);
    }
    return ExitCode.Ok;
  },
};

/**
 * Reads the manifest file with a hard byte cap. The size is never trusted from `stat`
 * alone: at most `maxManifestBytes + 1` bytes are read, so a file that grows between
 * the `fstat` and the read is still refused. The read loops until EOF or a full buffer,
 * so a short read on a regular file cannot silently truncate the body. The content is
 * never echoed and the path is never named (contracts/cli.md §5).
 */
async function readManifestFile(path: string): Promise<Uint8Array> {
  let handle: FileHandle;
  try {
    handle = await open(path, openReadFlags);
  } catch (error) {
    throw unreadable(error);
  }
  try {
    const stats = await handle.stat();
    if (!stats.isFile()) {
      throw new CliError('the manifest file could not be read (not a regular file)', {
        code: 'manifest_unreadable',
      });
    }
    const buffer = Buffer.allocUnsafe(maxManifestBytes + 1);
    let bytesRead = 0;
    while (bytesRead < buffer.length) {
      const read = await handle.read(buffer, bytesRead, buffer.length - bytesRead, bytesRead);
      if (read.bytesRead === 0) {
        break;
      }
      bytesRead += read.bytesRead;
    }
    if (bytesRead > maxManifestBytes) {
      throw new CliError(`the manifest file exceeds ${maxManifestBytes} bytes`, {
        code: 'manifest_too_large',
      });
    }
    if (bytesRead === 0) {
      throw new CliError('the manifest file is empty', { code: 'manifest_unreadable' });
    }
    return buffer.subarray(0, bytesRead);
  } catch (error) {
    if (error instanceof CliError) {
      throw error;
    }
    throw unreadable(error);
  } finally {
    await handle.close().catch(() => undefined);
  }
}

/** `manifest_unreadable` naming only the errno code; the path and the content stay out. */
function unreadable(error: unknown): CliError {
  const code = error instanceof Error ? (error as NodeJS.ErrnoException).code : undefined;
  return new CliError(
    `the manifest file could not be read (${typeof code === 'string' && code.length > 0 ? code : 'unknown'})`,
    { code: 'manifest_unreadable' },
  );
}

/** Validates the 201 body; anything else is `request_failed` and the body is never echoed. */
function readAccepted(data: unknown): { manifestId: string; manifestHash: string } {
  const body: Record<string, unknown> =
    typeof data === 'object' && data !== null ? (data as Record<string, unknown>) : {};
  const manifestId = body['manifest_id'];
  const manifestHash = body['manifest_hash'];
  if (
    typeof manifestId !== 'string' ||
    !uuidPattern.test(manifestId) ||
    typeof manifestHash !== 'string' ||
    !manifestHashPattern.test(manifestHash)
  ) {
    throw new CliError('the manifest upload response was not valid', { code: 'request_failed' });
  }
  return { manifestId, manifestHash };
}
