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

/** Refusal for an answer over its cap; the text names neither the URL nor any body byte. */
export class ResponseTooLargeError extends CliError {
  constructor() {
    super('the server answer is larger than the CLI accepts', {
      code: 'response_too_large',
      hint: 'retrying will not help; check that EVIDGATE_API_URL names an Evidgate API',
    });
    this.name = 'ResponseTooLargeError';
  }
}

/**
 * Declared length as a number, or undefined when the header is absent, not a plain integer
 * or describes an encoded body: the cap counts decoded bytes, and an encoded length says
 * nothing about them.
 */
function declaredLength(response: Response): number | undefined {
  const encoding = response.headers.get('content-encoding')?.trim().toLowerCase();
  if (encoding !== undefined && encoding !== '' && encoding !== 'identity') {
    return undefined;
  }
  const raw = response.headers.get('content-length');
  return raw !== null && /^\d{1,15}$/.test(raw) ? Number(raw) : undefined;
}

/**
 * The same answer with a body that errors with `ResponseTooLargeError` once it passes `cap`
 * bytes. The refusal is raised from the stream, so `text()`, `json()` and a manual reader all
 * see it, and the underlying reader is cancelled as soon as it fires.
 */
export function boundedResponse(response: Response, cap: number = maxResponseBytes): Response {
  const body = response.body;
  if (body === null) {
    return response;
  }
  // `response.body` is typed as a stream of `any`; the reader fixes the chunk type.
  const reader: ReadableStreamDefaultReader<Uint8Array> = body.getReader();
  if (response.status < 200 || response.status > 599) {
    // `Response` cannot represent this status, so the body cannot be re-wrapped. Refusing is
    // final; a rejection from `fetch` would be retried as a transport failure.
    void reader.cancel().catch(() => undefined);
    throw new CliError('the server answered with a status the CLI does not support', {
      code: 'request_failed',
    });
  }
  const declared = declaredLength(response);
  let bytes = 0;
  const bounded = new ReadableStream<Uint8Array>({
    async pull(controller) {
      if (declared !== undefined && declared > cap) {
        await reader.cancel().catch(() => undefined);
        controller.error(new ResponseTooLargeError());
        return;
      }
      const { done, value } = await reader.read();
      if (done) {
        controller.close();
        return;
      }
      // A non-byte chunk (only an injected fetch can produce one) cannot be sized.
      bytes += value instanceof Uint8Array ? value.byteLength : Number.POSITIVE_INFINITY;
      if (bytes > cap) {
        await reader.cancel().catch(() => undefined);
        controller.error(new ResponseTooLargeError());
        return;
      }
      controller.enqueue(value);
    },
    cancel: (reason: unknown) => reader.cancel(reason).catch(() => undefined),
  });
  return new Response(bounded, {
    status: response.status,
    statusText: response.statusText,
    headers: response.headers,
  });
}

/** The whole body as text under `cap`; an over-cap answer raises `ResponseTooLargeError`. */
export async function readBoundedText(response: Response, cap: number): Promise<string> {
  return boundedResponse(response, cap).text();
}
