// `evidgate manifests build` end to end against a real local HTTP server: the plan pages are
// `SuiteRevisionExecutionPlan`-shaped, the written file passes the packaged `manifest.v1`
// validator that the upload path uses, two runs of the same input are byte-identical, and
// `manifests upload` sends the built bytes unchanged.
import { once } from 'node:events';
import { readFile } from 'node:fs/promises';
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { join } from 'node:path';

import type { SuiteRevisionExecutionPlan } from '@evidgate/sdk';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import { capture } from '../commands/cliSpec.testing.js';
import { main } from '../main.js';
import { loadPackagedContractSchemas } from '../packagedSchemas.js';
import { resetSecrets } from '../redact.js';

import {
  member,
  page,
  report,
  targetEnv,
  useBuildSpec,
} from '../commands/manifestsBuild.testing.js';

const params = { browser: 'chromium', mode: 'headless' };
const members = [member(1, params), member(2, params), member(3, params)];
const firstPage: SuiteRevisionExecutionPlan = page(members.slice(0, 2), 'page-two');
const secondPage: SuiteRevisionExecutionPlan = page(members.slice(2), null);

interface Received {
  readonly method: string;
  readonly path: string;
  readonly query: URLSearchParams;
  readonly body: string;
}

describe('manifests build end to end', () => {
  const { setup, run } = useBuildSpec();
  const received: Received[] = [];
  let baseUrl = '';
  let server: ReturnType<typeof createServer> | undefined;

  beforeAll(async () => {
    server = createServer((request: IncomingMessage, response: ServerResponse) => {
      let body = '';
      request.on('data', (chunk: Buffer) => {
        body += chunk.toString('utf8');
      });
      request.on('end', () => {
        const url = new URL(request.url ?? '/', 'http://127.0.0.1');
        received.push({
          method: request.method ?? '',
          path: url.pathname,
          query: url.searchParams,
          body,
        });
        if (request.method === 'GET' && url.pathname.endsWith('/execution-plan')) {
          const cursor = url.searchParams.get('cursor');
          const answer = cursor === 'page-two' ? secondPage : firstPage;
          response.writeHead(200, { 'content-type': 'application/json' });
          response.end(JSON.stringify(answer));
          return;
        }
        if (request.method === 'POST' && url.pathname.endsWith('/manifests')) {
          response.writeHead(201, { 'content-type': 'application/json' });
          response.end(
            JSON.stringify({
              manifest_id: '9b2f4c1e-2a3b-4c5d-8e9f-0a1b2c3d4e5f',
              manifest_hash: `sha256:${'a'.repeat(64)}`,
            }),
          );
          return;
        }
        response.writeHead(404, { 'content-type': 'application/json' });
        response.end(
          JSON.stringify({ error: { code: 'not_found', message: 'no', request_id: 'r' } }),
        );
      });
    });
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    const address = server.address() as AddressInfo;
    baseUrl = `http://127.0.0.1:${address.port}`;
  });

  afterAll(async () => {
    if (server === undefined) {
      return;
    }
    const closed = once(server, 'close');
    server.close();
    await closed;
  });

  it('serves, builds, validates, replays bytes and uploads', async () => {
    resetSecrets();
    const fixture = await setup({ text: report(members, params), apiUrl: baseUrl });
    const outcome = await run(fixture, fetch);
    expect(outcome.code).toBe(0);

    const text = await readFile(fixture.out, 'utf8');
    const schemas = loadPackagedContractSchemas();
    expect(schemas.has('manifest.v1')).toBe(true);
    expect(schemas.validate('manifest.v1', text).errors).toEqual([]);

    const document = JSON.parse(text) as Record<string, unknown>;
    expect(Object.keys(document)).toEqual([
      'schema_version',
      'suite_revision_id',
      'planned_executions',
      'matrix',
      'shard_plan',
      'env_needs',
    ]);
    expect(text).toContain(`"target_env":"${targetEnv}"`);

    // The same input to a second output path yields the same bytes.
    const second = join(fixture.root, 'second.json');
    const args = [...fixture.args];
    args[args.indexOf('--out') + 1] = second;
    const replay = await run(fixture, fetch, args);
    expect(replay.code).toBe(0);
    expect(await readFile(second, 'utf8')).toBe(text);

    // The upload command sends the built file byte for byte and accepts the server's 201.
    const upload = capture();
    const code = await main(
      ['manifests', 'upload', '--file', fixture.out],
      fixture.env,
      upload.io,
      { fetch },
    );
    expect(code).toBe(0);
    expect(upload.stderr.join('')).toBe('');
    const posted = received.find((call) => call.method === 'POST');
    expect(posted?.body).toBe(text);

    // Two builds each walked both pages with the route's maximum page size.
    const reads = received.filter((call) => call.method === 'GET');
    expect(reads.map((call) => call.query.get('limit'))).toEqual(['500', '500', '500', '500']);
    expect(reads.map((call) => call.query.get('cursor'))).toEqual([
      null,
      'page-two',
      null,
      'page-two',
    ]);
  });
});
