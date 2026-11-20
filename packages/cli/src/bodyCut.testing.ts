export const bodyCutSecrets = [
  'ghp_SECRETBODYPREFIX',
  'https://response.example/private?signature=read-cut-private-query',
  'read-cut-private-query',
  'egt_read_cut_private_token',
] as const;

/** A first chunk reaches the reader before the next read rejects with sensitive cause text. */
export function bodyCutResponse(status = 200): Response {
  let delivered = false;
  const stream = new ReadableStream<Uint8Array>(
    {
      pull(controller) {
        if (!delivered) {
          delivered = true;
          controller.enqueue(new TextEncoder().encode(bodyCutSecrets[0]));
          return;
        }
        controller.error(new Error(bodyCutSecrets.join(' ')));
      },
    },
    { highWaterMark: 0 },
  );
  return new Response(stream, {
    status,
    headers: { 'content-type': 'application/json' },
  });
}
