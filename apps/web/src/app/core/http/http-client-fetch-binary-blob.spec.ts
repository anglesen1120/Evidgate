// @vitest-environment node
// The Blob final body of a binary call. jsdom's Blob is not accepted by Node's native `Response`,
// so this file runs in the node environment, where `Blob` is the one `Response` reads.
import { HttpHeaders, HttpResponse } from '@angular/common/http';

import { toBinaryResponse } from './http-client-fetch-binary';

const source = new HttpResponse({ status: 200, body: null });

function respond(body: unknown): Response {
  return toBinaryResponse(200, 'OK', new HttpHeaders(), body, source);
}

describe('Blob final body of the binary adapter', () => {
  it('forwards the payload bytes of a non-empty Blob unchanged', async () => {
    const bytes = Uint8Array.from([0, 1, 2, 253, 254, 255]);
    const response = respond(new Blob([bytes]));
    expect(new Uint8Array(await response.arrayBuffer())).toEqual(bytes);
  });

  it('reaches the SDK as no body for an empty Blob, like an empty buffer', async () => {
    const response = respond(new Blob([]));
    expect(response.body).toBeNull();
    expect((await response.arrayBuffer()).byteLength).toBe(0);
    expect(respond(new ArrayBuffer(0)).body).toBeNull();
  });
});
