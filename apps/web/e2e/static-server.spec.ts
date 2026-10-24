import { expect, test } from '@playwright/test';

// 09.1.1-T1.2: the static server has routing logic of its own, so it is tested directly and
// not only through the browser. These requests go to the same `webServer` the smoke test
// uses (`e2e/static-server.mjs` over `apps/web/dist/browser`).
test('serves index.html for an unknown client route (SPA fallback)', async ({ request }) => {
  const response = await request.get('/runs/2f1c9d3e-0b7a-4c55-9a12-6d0f4b8e1a77');

  expect(response.status()).toBe(200);
  expect(response.headers()['content-type']).toContain('text/html');
  expect(await response.text()).toContain('<app-root></app-root>');
});

test('rejects a traversal path instead of reading outside dist/browser', async ({ request }) => {
  // `%2f` keeps the escape encoded through URL normalisation, so the server itself must
  // decode and reject it; the body must never be a workspace file such as package.json.
  const response = await request.get('/..%2f..%2fpackage.json');

  expect(response.status()).toBe(403);
  expect(await response.text()).not.toContain('@evidgate/web');
});
