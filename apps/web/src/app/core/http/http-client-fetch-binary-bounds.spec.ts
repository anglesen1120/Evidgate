// Chunk accumulation, decoded-size accounting and the response-less stream of the binary path.
import { HttpEventType, HttpHeaders, HttpResponse, type HttpEvent } from '@angular/common/http';
import { EmptyError, firstValueFrom, of, toArray } from 'rxjs';

import { BINARY_BODY_LIMIT_BYTES } from './binary-transport';
import {
  enforceBinarySizeCap,
  exceedsBinaryCap,
  readBinaryRequestBody,
} from './http-client-fetch-binary';
import { errorResponseOrThrow } from './http-client-fetch-transport';

const OVER = BINARY_BODY_LIMIT_BYTES + 1;

function streamOf(chunks: readonly Uint8Array[], log: { reads: number; cancelled: boolean }) {
  let index = 0;
  const body = new ReadableStream<Uint8Array>({
    pull(controller) {
      log.reads += 1;
      const next = chunks[index];
      index += 1;
      if (next === undefined) {
        controller.close();
      } else {
        controller.enqueue(next);
      }
    },
    cancel() {
      log.cancelled = true;
    },
  });
  return new Request('http://localhost/v1/x', {
    method: 'POST',
    body,
    // @ts-expect-error Request streaming bodies need `duplex` in Chromium.
    duplex: 'half',
  });
}

describe('binary request chunk accumulation', () => {
  it('returns the exact bytes across odd chunk sizes and buffer growth', async () => {
    const expected = new Uint8Array(200_001).map((_, index) => index % 251);
    const chunks: Uint8Array[] = [];
    for (let offset = 0; offset < expected.length; offset += 1 + (offset % 7919)) {
      chunks.push(expected.slice(offset, offset + 1 + (offset % 7919)));
    }
    const log = { reads: 0, cancelled: false };

    const buffer = await readBinaryRequestBody(streamOf(chunks, log), true, null);

    expect(new Uint8Array(buffer ?? new ArrayBuffer(0))).toEqual(expected);
  });

  it('refuses at the chunk that crosses the cap and cancels the producer', async () => {
    const mebibyte = new Uint8Array(1024 * 1024);
    const chunks = Array.from({ length: 12 }, () => mebibyte);
    const log = { reads: 0, cancelled: false };

    await expect(readBinaryRequestBody(streamOf(chunks, log), true, null)).rejects.toThrow(
      'exceeds limit',
    );

    expect(log.cancelled).toBe(true);
    // 8 chunks fit; the 9th crosses; the stream is not pulled to its end.
    expect(log.reads).toBeLessThanOrEqual(10);
  });

  it('refuses shadowed-length 64 KiB chunks over the cap and cancels the producer', async () => {
    const chunk = new Uint8Array(64 * 1024);
    Object.defineProperty(chunk, 'byteLength', { value: 0 });
    const chunks = Array.from({ length: BINARY_BODY_LIMIT_BYTES / (64 * 1024) + 2 }, () => chunk);
    const log = { reads: 0, cancelled: false };

    await expect(readBinaryRequestBody(streamOf(chunks, log), true, null)).rejects.toThrow(
      'exceeds limit',
    );

    expect(log.cancelled).toBe(true);
    expect(log.reads).toBeLessThanOrEqual(chunks.length);
  });

  it('preserves the true bytes of a shadowed-length chunk within the cap', async () => {
    const chunk = new Uint8Array(64 * 1024).map((_, index) => index % 251);
    Object.defineProperty(chunk, 'byteLength', { value: 0 });
    const expected = new Uint8Array(64 * 1024 + 1);
    expected.set(chunk);
    expected[64 * 1024] = 255;
    const log = { reads: 0, cancelled: false };

    const buffer = await readBinaryRequestBody(
      streamOf([chunk, new Uint8Array([255])], log),
      true,
      null,
    );

    expect(buffer?.byteLength).toBe(expected.length);
    expect(new Uint8Array(buffer ?? new ArrayBuffer(0))).toEqual(expected);
    expect(log.cancelled).toBe(false);
  });

  it('accepts exactly the cap', async () => {
    const chunks = [new Uint8Array(BINARY_BODY_LIMIT_BYTES - 1), new Uint8Array(1)];
    const buffer = await readBinaryRequestBody(
      streamOf(chunks, { reads: 0, cancelled: false }),
      true,
      null,
    );
    expect(buffer?.byteLength).toBe(BINARY_BODY_LIMIT_BYTES);
  });
});

describe('decoded-size accounting', () => {
  // Fetch "handle content codings" decodes before the body is read; XHR `loaded` counts those
  // decoded bytes. `Content-Length` is the wire size.
  it('refuses on decoded progress although the wire Content-Length is small', async () => {
    const events: HttpEvent<ArrayBuffer>[] = [
      {
        type: HttpEventType.ResponseHeader,
        headers: new HttpHeaders({ 'Content-Length': '1000', 'Content-Encoding': 'gzip' }),
        status: 200,
        statusText: 'OK',
        url: null,
        ok: true,
      } as unknown as HttpEvent<ArrayBuffer>,
      { type: HttpEventType.DownloadProgress, loaded: OVER },
    ];

    await expect(
      firstValueFrom(enforceBinarySizeCap(of(...events)).pipe(toArray())),
    ).rejects.toThrow('exceeds limit');
  });

  it.each([NaN, Infinity, -Infinity, '0', undefined, null])(
    'refuses invalid decoded progress loaded=%s',
    async (loaded) => {
      const progress: HttpEvent<ArrayBuffer> = { type: HttpEventType.DownloadProgress, loaded: 0 };
      Object.defineProperty(progress, 'loaded', { value: loaded });

      expect(exceedsBinaryCap(progress)).toBe(true);
      await expect(firstValueFrom(enforceBinarySizeCap(of(progress)))).rejects.toThrow(
        'exceeds limit',
      );
    },
  );

  it('lets finite decoded progress at the cap through', async () => {
    const progress: HttpEvent<ArrayBuffer> = {
      type: HttpEventType.DownloadProgress,
      loaded: BINARY_BODY_LIMIT_BYTES,
    };

    expect(exceedsBinaryCap(progress)).toBe(false);
    await expect(firstValueFrom(enforceBinarySizeCap(of(progress)))).resolves.toBe(progress);
  });

  it('refuses a final over-cap body in the stream stage, with no progress event before it', async () => {
    const final = new HttpResponse<ArrayBuffer>({ status: 200, body: new ArrayBuffer(OVER) });

    expect(exceedsBinaryCap(final)).toBe(true);
    await expect(firstValueFrom(enforceBinarySizeCap(of(final)))).rejects.toThrow('exceeds limit');
  });

  it('lets a final body at the cap through', () => {
    const final = new HttpResponse<ArrayBuffer>({
      status: 200,
      body: new ArrayBuffer(BINARY_BODY_LIMIT_BYTES),
    });
    expect(exceedsBinaryCap(final)).toBe(false);
  });
});

describe('response-less event stream', () => {
  it('maps an EmptyError nobody aborted to the network-failure TypeError', () => {
    const signal = new AbortController().signal;
    const error = new EmptyError();

    let thrown: unknown;
    try {
      errorResponseOrThrow(error, signal);
    } catch (caught) {
      thrown = caught;
    }

    expect(thrown).toBeInstanceOf(TypeError);
    expect((thrown as TypeError).cause).toBe(error);
  });

  it('still reports an aborted call as an AbortError', () => {
    const controller = new AbortController();
    controller.abort();
    expect(() => errorResponseOrThrow(new EmptyError(), controller.signal)).toThrow(
      expect.objectContaining({ name: 'AbortError' }),
    );
  });
});
