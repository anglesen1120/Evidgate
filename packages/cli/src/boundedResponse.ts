// The one bounded read of a server answer. The generated client reads a body whole with no
// limit of its own, so every response is wrapped here before the client sees it: the stream
// is counted chunk by chunk and aborted once it passes the cap, so at most `cap` bytes plus
// one chunk are ever pulled. `Content-Length` is only a fast path, because it can be absent
// or wrong; the running count is the control.
import { CliError } from './errors.js';

/**
 * The cap for one API answer. The largest legitimate answer is a 500-member execution plan
 * page (about 100 KiB); every other command answer is a small object.
 */
export const maxResponseBytes = 1024 * 1024;

/**
 * Refusal for an answer over its cap; the text names neither the URL nor any body byte. `cap` is
 * the limit the refusing reader counted against, so a caller reading through a second, smaller
 * wrapper can tell which of the two caps fired.
 */
export class ResponseTooLargeError extends CliError {
  readonly cap: number;

  constructor(cap: number) {
    super('the server answer is larger than the CLI accepts', {
      code: 'response_too_large',
      hint: 'the answer exceeded the size limit; retrying will not help',
    });
    this.name = 'ResponseTooLargeError';
    this.cap = cap;
  }
}

/**
 * Refusal for an answer whose body failed or ended early while it was read: an abort, a
 * timeout or a socket drop is never a JSON verdict and never a success. The partial bytes
 * are discarded, and the text names no URL, token or body byte.
 */
export class ResponseReadError extends CliError {
  constructor() {
    super('the server answer could not be read to the end', {
      code: 'response_read_failed',
      hint: 'the answer could not be read; the command can be run again',
    });
    this.name = 'ResponseReadError';
  }
}

/**
 * `Response` cannot represent a status outside 200-599, so such an answer cannot be re-wrapped
 * or have its error body cut. Refusing is final: a rejection from `fetch` would be retried as
 * a transport failure. The body is cancelled so the socket is released.
 */
export function refuseUnsupportedStatus(response: Response): void {
  if (response.status >= 200 && response.status <= 599) {
    return;
  }
  void response.body?.cancel().catch(() => undefined);
  throw new CliError('the server answered with a status the CLI does not support', {
    code: 'request_failed',
  });
}

/**
 * Declared length as a number, or undefined when the header is absent, not a plain integer
 * or describes an encoded body: the cap counts decoded bytes, and an encoded length says
 * nothing about them. Exported so a reader with its own cap applies the same fast path.
 */
export function declaredLength(response: Response): number | undefined {
  const encoding = response.headers.get('content-encoding')?.trim().toLowerCase();
  if (encoding !== undefined && encoding !== '' && encoding !== 'identity') {
    return undefined;
  }
  const raw = response.headers.get('content-length');
  return raw !== null && /^\d{1,15}$/.test(raw) ? Number(raw) : undefined;
}

/** One source reader plus whether a read is still settling (it keeps the lock until then). */
interface ChunkSource {
  readonly reader: ReadableStreamDefaultReader<Uint8Array>;
  reading: boolean;
}

/** Releases the lock unless a read is still settling; that read's own pull releases it. */
function release(source: ChunkSource): void {
  if (!source.reading) {
    source.reader.releaseLock();
  }
}

/** What one `reader.read()` settles with. */
type Chunk =
  | { readonly done: true; readonly value?: Uint8Array | undefined }
  | { readonly done: false; readonly value: Uint8Array };

/** What one read settles with, including a nested bounded wrapper's own refusal. */
type ReadOutcome =
  | { readonly kind: 'chunk'; readonly chunk: Chunk }
  | { readonly kind: 'refused'; readonly error: CliError }
  | { readonly kind: 'failed' };

/**
 * One chunk. A nested wrapper refuses by erroring its stream, and that `CliError` is the answer:
 * only a bare rejection (abort, timeout, socket drop) is a read failure.
 */
async function readChunk(source: ChunkSource): Promise<ReadOutcome> {
  source.reading = true;
  try {
    return { kind: 'chunk', chunk: await source.reader.read() };
  } catch (error) {
    return error instanceof CliError ? { kind: 'refused', error } : { kind: 'failed' };
  } finally {
    source.reading = false;
  }
}

/** Cancels the source, releases its lock and fails the wrapper with `error`. */
async function refuse(
  source: ChunkSource,
  controller: ReadableStreamDefaultController<Uint8Array>,
  error: CliError,
): Promise<void> {
  await source.reader.cancel().catch(() => undefined);
  release(source);
  controller.error(error);
}

/**
 * The same answer with a body that errors with `ResponseTooLargeError` once it passes `cap`
 * bytes, or with `ResponseReadError` when the source fails or ends mid-answer. The refusal is
 * raised from the stream, so `text()`, `json()` and a manual reader all see it, and the
 * underlying reader is cancelled and released as soon as it fires. A nested wrapper's refusal is
 * re-raised unchanged instead of being counted as a read failure.
 */
export function boundedResponse(response: Response, cap: number = maxResponseBytes): Response {
  const body = response.body;
  if (body === null) {
    return response;
  }
  refuseUnsupportedStatus(response);
  // `response.body` is typed as a stream of `any`; the reader fixes the chunk type.
  const source: ChunkSource = { reader: body.getReader(), reading: false };
  const declared = declaredLength(response);
  let bytes = 0;
  const bounded = new ReadableStream<Uint8Array>({
    async pull(controller) {
      if (declared !== undefined && declared > cap) {
        await refuse(source, controller, new ResponseTooLargeError(cap));
        return;
      }
      const read = await readChunk(source);
      if (read.kind === 'refused') {
        // The inner wrapper's own refusal already says whether retrying can help; re-raise it.
        await refuse(source, controller, read.error);
        return;
      }
      if (read.kind === 'failed') {
        // An abort, a timeout or a socket drop mid-answer is a read failure, not bad JSON.
        await refuse(source, controller, new ResponseReadError());
        return;
      }
      const chunk = read.chunk;
      if (chunk.done) {
        release(source);
        controller.close();
        return;
      }
      // A non-byte chunk (only an injected fetch can produce one) cannot be sized.
      const value = chunk.value;
      bytes += value instanceof Uint8Array ? value.byteLength : Number.POSITIVE_INFINITY;
      if (bytes > cap) {
        await refuse(source, controller, new ResponseTooLargeError(cap));
        return;
      }
      controller.enqueue(value);
    },
    cancel: (reason: unknown) => {
      const cancelled = source.reader.cancel(reason).catch(() => undefined);
      return cancelled.then(() => release(source));
    },
  });
  return new Response(bounded, {
    status: response.status,
    statusText: response.statusText,
    headers: response.headers,
  });
}

/**
 * The whole body as text under `cap`; an over-cap answer raises `ResponseTooLargeError` and a
 * body that fails or ends early raises `ResponseReadError`.
 */
export async function readBoundedText(response: Response, cap: number): Promise<string> {
  return boundedResponse(response, cap).text();
}
