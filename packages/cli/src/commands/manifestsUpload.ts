// `evidgate manifests upload` — reads the manifest file, sends it byte-for-byte to
// `POST /v1/projects/{p}/manifests` and prints `manifest_id` + `manifest_hash`. The file is
// read and capped BEFORE `getToken()`, so an unreadable or oversize file makes no request at
// all — not even the token exchange. A 401 purges the cached CI token (contracts/cli.md
// §7/§8); a 403 keeps it, because a scope refusal does not invalidate the token.
import { createHash } from 'node:crypto';

import {
  type Client,
  type CreateManifestErrors,
  type CreateManifestResponses,
} from '@evidgate/sdk';

import { getToken } from '../auth/getToken.js';
import { deleteCachedToken } from '../auth/tokenCache.js';
import { readFileBounded, type BoundedFileFailure } from '../boundedFile.js';
import { callApi } from '../callApi.js';
import { createApiClient } from '../client.js';
import { CliError } from '../errors.js';
import { ExitCode } from '../exitCodes.js';
import { maxManifestBytes } from '../requestCaps.js';
import { serverErrorFromResponse, transportError } from '../serverError.js';

import type { CommandDefinition } from './index.js';

/**
 * The openapi `createManifest` path, spelled once: the command calls the client's own
 * `post` (below), whose body is untyped, because the route takes the RAW file bytes and
 * the generated operation types the body as the parsed `ManifestV1`.
 */
const createManifestPath = '/projects/{p}/manifests';
/**
 * The usage-error hint `main.ts` attaches to its parse-level usage errors (`helpHint`
 * there, module-private). Repeated here so the command-level `--file` usage error carries
 * the same pointer (contracts/cli.md §5 `usage` row); keep the two literals in sync.
 */
const helpHint = "run 'evidgate --help' for usage";
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
    // The file is read (and capped) before any token work: an oversize or unreadable file
    // must not trigger a request, including the token exchange.
    const body = await readManifestFile(file);
    const { token } = await getToken(config, deps);
    const client = createApiClient(config, token, deps.fetch ?? fetch);
    // Fixed before the call so every attempt sends one value: the file digest is what the
    // server also hashes into `BodySha256`, so a retry replays the stored 201.
    const idempotencyKey = createHash('sha256').update(body).digest('hex');
    const result = await callApi(
      (signal) => createManifestRawBytes(client, config.project, idempotencyKey, body, signal),
      // The digest key makes the upload replay-safe, so a transport failure, a 429 or a 5xx is
      // retried with the SAME key and the SAME bytes unless the envelope says `retryable: false`.
      { policy: 'replay-safe', deps },
    );
    const response = result.response;
    if (response === undefined) {
      // A network error, a timeout or a refused redirect: the SDK returns no response.
      throw transportError(result.error);
    }
    if (!response.ok) {
      if (response.status === 401) {
        // Best-effort: the server's error is the one that must surface, but a refused
        // bearer must not be reused by the next command.
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
 * `POST /v1/projects/{p}/manifests` with the file's own bytes. The generated operation
 * types `body` as the parsed `ManifestV1`, but the contract sends the RAW bytes: the
 * server hashes the canonical form, and a JSON round trip could lose number precision.
 * The client's own `post` takes an untyped body, and `bodySerializer: null` stops the
 * default `JSON.stringify` from re-encoding them, so no type assertion is needed.
 */
function createManifestRawBytes(
  client: Client,
  project: string,
  idempotencyKey: string,
  body: Uint8Array,
  signal: AbortSignal,
) {
  return client.post<CreateManifestResponses, CreateManifestErrors>({
    url: createManifestPath,
    path: { p: project },
    headers: { 'Idempotency-Key': idempotencyKey, 'Content-Type': 'application/json' },
    body,
    bodySerializer: null,
    signal,
  });
}

/**
 * Reads the manifest file under the command's own cap, keeping `manifest_unreadable` and
 * `manifest_too_large` as its only refusal codes. The content is never echoed and the
 * path is never named (contracts/cli.md §5).
 */
async function readManifestFile(path: string): Promise<Uint8Array> {
  const result = await readFileBounded(path, maxManifestBytes);
  if (result.ok) {
    return result.bytes;
  }
  throw manifestRefusal(result.failure);
}

/** The command's own code and message for each failure the shared reader reports. */
function manifestRefusal(failure: BoundedFileFailure): CliError {
  switch (failure.kind) {
    case 'too_large':
      return new CliError(`the manifest file exceeds ${maxManifestBytes} bytes`, {
        code: 'manifest_too_large',
      });
    case 'empty':
      return new CliError('the manifest file is empty', { code: 'manifest_unreadable' });
    case 'not_regular_file':
      return new CliError('the manifest file could not be read (not a regular file)', {
        code: 'manifest_unreadable',
      });
    case 'unreadable':
      return unreadable(failure.errno);
    case 'changed':
      return unreadable('ESTALE');
  }
}

/** `manifest_unreadable` naming only the errno code; the path and the content stay out. */
function unreadable(errno: string): CliError {
  return new CliError(`the manifest file could not be read (${errno})`, {
    code: 'manifest_unreadable',
  });
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
