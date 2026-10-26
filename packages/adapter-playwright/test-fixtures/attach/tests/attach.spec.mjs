// 04.3.3-T2.2 e2e fixture: one passing test that attaches a trace-like zip and a png, both
// written into the Playwright output directory — the ONLY place the reporter's allow-list
// accepts (AC6). The bytes are tiny on purpose: this spec proves the upload path, not the
// size handling (the caps are `attach-map.test.ts`/`attach.test.ts`).
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

import { test } from '@playwright/test';

test('attaches a trace and a screenshot', async ({}, testInfo) => {
  mkdirSync(testInfo.outputDir, { recursive: true });

  // A zip-shaped header plus a payload: enough for `trace*.zip → trace`.
  const tracePath = join(testInfo.outputDir, 'trace-attach.zip');
  writeFileSync(tracePath, Buffer.from('PK\u0003\u0004evidgate-e2e-trace-payload'));

  // A real 8-byte PNG signature: enough for `*.png → screenshot`.
  const screenshotPath = join(testInfo.outputDir, 'screenshot-attach.png');
  writeFileSync(screenshotPath, Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));

  await testInfo.attach('trace-attach.zip', {
    path: tracePath,
    contentType: 'application/zip',
  });
  await testInfo.attach('screenshot-attach.png', {
    path: screenshotPath,
    contentType: 'image/png',
  });
});
