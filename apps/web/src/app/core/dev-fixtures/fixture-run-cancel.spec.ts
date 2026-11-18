import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { cancelRun, getRun } from '@evidgate/sdk';
import collecting from '../../../../../../fixtures/web-api/run-detail-collecting/input.json';
import { setupFixtureBackend } from './fixture-backend.test-support';
import { RunCancelFixtureScenario } from './fixture-run-cancel';

const RUN_ID = collecting.body.run_id;

describe('opt-in cancel polling fixture', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('uses the SDK 202 response, preserves the first collecting read, then supplies canceled', async () => {
    const { gateway, toggles, handled } = setupFixtureBackend();
    toggles.setVariant('getRun', 'detail-cancel-poll');
    const read = () => gateway.call((client) => getRun({ client, path: { r: RUN_ID } }));
    expect((await read()).state).toBe('collecting');
    const accepted = await gateway.callWithResponse((client) =>
      cancelRun({
        client,
        path: { r: RUN_ID },
        body: { reason: 'A fixture cancellation' },
        headers: { 'Idempotency-Key': '018f0000-0000-7000-8000-000000000a73' },
      }),
    );
    // 00 §9.1: the 202 carries no body, and the SDK resolves its empty JSON object, not undefined.
    expect(accepted.status).toBe(202);
    expect(accepted.data).toEqual({});
    expect((await read()).state).toBe('collecting');
    expect((await read()).state).toBe('canceled');
    expect((await read()).state).toBe('canceled');
    expect(handled.filter((request) => request.method === 'POST')).toHaveLength(1);
  });

  it('leaves other variants, run IDs, methods and refused cancellations untouched', () => {
    const scenario = new RunCancelFixtureScenario();
    const path = `/runs/${RUN_ID}`;
    const get = new HttpRequest('GET', path);
    const post = new HttpRequest('POST', `${path}/cancel`, null);
    expect(scenario.entry(get, path, 'detail-ready', 'ok')).toBeUndefined();
    expect(scenario.entry(get, '/runs/another-run', 'detail-cancel-poll', 'ok')).toBeUndefined();
    expect(
      scenario.entry(post, `${path}/cancel`, 'detail-cancel-poll', 'forbidden'),
    ).toBeUndefined();
    expect(
      scenario.entry(new HttpRequest('DELETE', path), path, 'detail-cancel-poll', 'ok'),
    ).toBeUndefined();
    expect(scenario.entry(get, path, 'detail-cancel-poll', 'ok')?.document.body).toEqual(
      collecting.body,
    );
    expect(scenario.entry(get, path, 'detail-cancel-poll', 'ok')?.document.body).toEqual(
      collecting.body,
    );
  });
});
