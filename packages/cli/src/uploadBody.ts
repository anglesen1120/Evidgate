// The PUT body: streams the guarded descriptor in bounded chunks while hashing, and refuses
// to finish unless the bytes are exactly the declared size and digest, so a half-sent object
// is never completed.
import { createHash, type Hash } from 'node:crypto';

import { checksumMismatch, toCliError } from './uploadErrors.js';
import { uploadChunkBytes } from './uploadOptions.js';

import type { FileHandle } from 'node:fs/promises';

/** The connection stopped moving: retryable, and never confused with a file change. */
export class UploadStalledError extends Error {
  constructor(milliseconds: number) {
    super(`the presigned upload made no progress for ${milliseconds} ms`);
    this.name = 'UploadStalledError';
  }
}

/**
 * The PUT body: streams the file in bounded chunks while hashing, and refuses to finish if
 * the bytes are not exactly the declared size and digest — which aborts the request, so a
 * half-sent object is never completed. It reads the guarded descriptor `putOnce` re-opened
 * (never a path), so a swapped file cannot be sent, and reports every chunk to the idle
 * timer.
 *
 * `done` always settles, whoever ends the request first: the generator itself, the store
 * (an early 2xx — see {@link abandon}), or `putOnce`'s refusal path ({@link abort}).
 */
export class BodyStream implements AsyncIterable<Uint8Array> {
  readonly hash: Hash;
  bytes = 0;
  failure: Error | undefined;
  /** The size and the digest matched: the object is exactly the file. */
  finished = false;
  /** The transport dropped the body before it finished, so `bytes` is short. */
  abandoned = false;
  readonly done: Promise<void>;

  private resolveDone: () => void = () => undefined;
  private rejectDone: (error: unknown) => void = () => undefined;
  private settled = false;

  constructor(
    private readonly handle: FileHandle,
    private readonly size: number,
    private readonly sha256: string,
    private readonly onProgress: () => void,
    private readonly onFinish: () => void,
  ) {
    this.hash = createHash('sha256');
    // `Promise.withResolvers` is ES2024 and the workspace targets ES2022 (tsconfig.base).
    this.done = new Promise<void>((resolve, reject) => {
      this.resolveDone = resolve;
      this.rejectDone = reject;
    });
    // `done` is awaited by `putOnce`; this keeps an abort from becoming an unhandled
    // rejection when the response settled first and `done` is never awaited.
    this.done.catch(() => undefined);
  }

  async *[Symbol.asyncIterator](): AsyncGenerator<Uint8Array> {
    try {
      const stream = this.handle.createReadStream({
        start: 0,
        autoClose: false,
        highWaterMark: uploadChunkBytes,
      }) as AsyncIterable<Uint8Array>;
      for await (const chunk of stream) {
        this.bytes += chunk.byteLength;
        if (this.bytes > this.size) {
          throw checksumMismatch(
            `the file grew while it was uploaded (${this.size} bytes declared, more found)`,
          );
        }
        this.hash.update(chunk);
        this.onProgress();
        yield chunk;
      }
      if (this.bytes !== this.size) {
        throw checksumMismatch(
          `the file shrank while it was uploaded (${this.size} bytes declared, ${this.bytes} sent)`,
        );
      }
      if (`sha256:${this.hash.digest('hex')}` !== this.sha256) {
        throw checksumMismatch('the file changed between the hash pass and the upload pass');
      }
      this.finished = true;
      this.settle();
    } catch (error) {
      this.failure = toCliError(error);
      this.fail(this.failure);
      throw this.failure;
    } finally {
      // The last chunk is out (or the generator is being closed), so the idle timer has
      // no meaning any more — the wait for the response is bounded by the deadline alone.
      this.onFinish();
      // A generator closed by a destroyed request must still settle `done`, or
      // `putOnce` would await it forever. Idempotent: a finished or failed body is settled.
      this.abandon();
    }
  }

  /** Stops a request whose response already failed; the body is never read to the end. */
  abort(): void {
    this.settle();
  }

  /**
   * Settles `done` for a body the transport dropped before it finished. The stream's
   * `close` calls this, because a generator that never started runs no `finally` at all.
   */
  abandon(): void {
    if (this.settled) return;
    this.abandoned = true;
    this.settle();
  }

  private settle(): void {
    if (this.settled) return;
    this.settled = true;
    this.resolveDone();
  }

  private fail(error: Error): void {
    if (this.settled) return;
    this.settled = true;
    this.rejectDone(error);
  }
}
