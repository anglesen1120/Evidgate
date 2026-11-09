// The HealingSwitchStore's read, derivation and write rules, pinned against a fake data service and
// the real session/permission signals (the harness is `healing-switch.test-support.ts`):
//
// - the resolution is read once per org/project, only with `healing:triage`, and an answer for a
//   project the page has left never reaches the screen;
// - a control is disabled by a `disabled` ancestor row (and by an `unavailable` resolution), and
//   the banner names the outermost row holding the mode in force;
// - a write needs `healing:configure`, is refused while an ancestor blocks the target, and carries
//   the project id only on a project scope;
// - a retry after a transport failure reuses the intent's key, a server decision starts a new one;
// - a 412 re-reads the resolution, a 404 reads as "not found" with no tenant hint, and a 422 keeps
//   the server's message;
// - a write answer for a scope the page has left is dropped.

import { TestBed } from '@angular/core/testing';

import type { HealingSwitchResolutionView } from '@evidgate/sdk';

import {
  CONFIGURE,
  type HealingHarness,
  TRIAGE,
  mountHealingSwitch,
  refusal,
  resolution,
  switchRow,
} from './healing-switch.test-support';

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

const SYSTEM_DISABLED = resolution({
  system: switchRow('system', 'disabled'),
  resolved: 'disabled',
});

const TENANT_DISABLED = resolution({
  system: switchRow('system', 'enabled'),
  tenant: switchRow('tenant', 'disabled'),
  resolved: 'disabled',
});

const SETTABLE = resolution({
  system: switchRow('system', 'enabled'),
  resolved: 'enabled',
});

describe('HealingSwitchStore', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('blocks both controls under a disabled system row and names the platform scope', async () => {
    const h = mountHealingSwitch({ answer: SYSTEM_DISABLED });
    await h.settle();
    expect(h.store.blockedBy('tenant')).toBe('system');
    expect(h.store.blockedBy('project')).toBe('system');
    expect(h.store.decidingScope()).toBe('system');
  });

  it('blocks a project under a disabled tenant row and leaves the tenant settable', async () => {
    const h = mountHealingSwitch({ answer: TENANT_DISABLED });
    await h.settle();
    expect(h.store.blockedBy('tenant')).toBeNull();
    expect(h.store.blockedBy('project')).toBe('tenant');
    expect(h.store.decidingScope()).toBe('tenant');
  });

  it('does not block on a shadow ancestor, and names it as the decider', async () => {
    const h = mountHealingSwitch({ answer: resolution({ system: switchRow('system', 'shadow') }) });
    await h.settle();
    expect(h.store.blockedBy('tenant')).toBeNull();
    expect(h.store.blockedBy('project')).toBeNull();
    expect(h.store.decidingScope()).toBe('system');
  });

  it('names no decider when no scope carries a row', async () => {
    const h = mountHealingSwitch({ answer: resolution() });
    await h.settle();
    expect(h.store.decidingScope()).toBeNull();
    expect(h.store.blockedBy('project')).toBeNull();
  });

  it('blocks every control while the platform reports unavailable and writes nothing', async () => {
    const h = mountHealingSwitch({ answer: resolution({ resolved: 'unavailable' }) });
    await h.settle();
    expect(h.store.decidingScope()).toBeNull();
    expect(h.store.blockedBy('tenant')).not.toBeNull();
    expect(h.store.blockedBy('project')).not.toBeNull();
    expect(await h.store.save('tenant', 'shadow', null)).toBe(false);
    expect(h.data.writes).toEqual([]);
  });

  it('refuses a write without healing:configure and issues no PUT', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE, codes: [TRIAGE] });
    await h.settle();
    expect(h.store.canConfigure()).toBe(false);
    expect(await h.store.save('tenant', 'disabled', 'Region outage')).toBe(false);
    expect(h.data.writes).toEqual([]);
  });

  it('reads nothing without healing:triage', async () => {
    const h = mountHealingSwitch({ codes: [] });
    await h.settle();
    expect(h.store.scope()).toBeUndefined();
    expect(h.store.resolution()).toBeNull();
    expect(h.data.reads).toEqual([]);
  });

  it('requests nothing when no project is current and refuses the write', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE, project: null });
    await h.settle();
    expect(h.data.reads).toEqual([]);
    expect(await h.store.save('tenant', 'disabled', null)).toBe(false);
    expect(h.data.writes).toEqual([]);
  });

  it('writes a tenant scope without a project id and with an intent key', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    const saving = h.store.save('tenant', 'disabled', 'Region outage');
    await h.advance();
    expect(h.data.writes).toHaveLength(1);
    expect(h.data.writes[0]?.body).toEqual({
      scope: 'tenant',
      mode: 'disabled',
      reason: 'Region outage',
    });
    expect(h.data.writes[0]?.body.project_id).toBeUndefined();
    expect(h.data.writes[0]?.intent.idempotencyKey).toMatch(UUID_V4);
    expect(await saving).toBe(true);
    await h.settle();
    expect(h.store.saved()).toBe('tenant');
  });

  it('writes a project scope with its project id and no reason when none was given', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    const saving = h.store.save('project', 'shadow', null);
    await h.advance();
    expect(h.data.writes[0]?.body).toEqual({
      scope: 'project',
      project_id: 'p-1',
      mode: 'shadow',
    });
    expect(await saving).toBe(true);
    await h.settle();
  });

  it('re-reads the resolution after a successful write', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    h.data.answer = SYSTEM_DISABLED;
    expect(await h.store.save('tenant', 'disabled', 'Region outage')).toBe(true);
    await h.settle();
    expect(h.data.reads).toHaveLength(2);
    expect(h.store.resolution()?.resolved).toBe('disabled');
    expect(h.store.decidingScope()).toBe('system');
  });

  it('reuses the intent key when retrying after a transport failure', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    h.data.holdWrites = true;
    const first = h.store.save('tenant', 'disabled', 'Region outage');
    await h.advance();
    h.data.failWrite(refusal('network', 0));
    expect(await first).toBe(false);
    const second = h.store.save('tenant', 'disabled', 'Region outage');
    await h.advance();
    expect(h.data.writes).toHaveLength(2);
    expect(h.data.writes[1]?.intent.idempotencyKey).toBe(h.data.writes[0]?.intent.idempotencyKey);
    h.data.releaseWrite();
    expect(await second).toBe(true);
    await h.settle();
  });

  it('mints a new intent key after a server decision', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    h.data.writeFailure = refusal('validation', 422, 'mode_not_available (test)');
    expect(await h.store.save('tenant', 'enabled', null)).toBe(false);
    h.data.writeFailure = null;
    const second = h.store.save('tenant', 'enabled', null);
    await h.advance();
    expect(h.data.writes).toHaveLength(2);
    expect(h.data.writes[1]?.intent.idempotencyKey).not.toBe(
      h.data.writes[0]?.intent.idempotencyKey,
    );
    expect(await second).toBe(true);
    await h.settle();
  });

  it('re-reads after a 412 and reports the reason the row changed', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    h.data.writeFailure = refusal(
      'precondition_failed',
      412,
      'The switch changed since you read it.',
    );
    expect(await h.store.save('tenant', 'disabled', null)).toBe(false);
    await h.settle();
    expect(h.data.reads).toHaveLength(2);
    expect(h.store.writeError('tenant')?.kind).toBe('precondition_failed');
    expect(h.store.writeError('tenant')?.message).toBe('The switch changed since you read it.');
    expect(h.store.saving()).toBeNull();
  });

  it('reads a 404 as not found without a tenant hint', async () => {
    const h = mountHealingSwitch();
    h.data.readFailure = refusal('not_found', 404, 'Not found');
    await h.settle();
    expect(h.store.notFound()).toBe(true);
    expect(h.store.readError()?.kind).toBe('not_found');
    expect(h.store.resolution()).toBeNull();
  });

  it('reads a 404 on a write as not found', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    h.data.writeFailure = refusal('not_found', 404, 'Not found');
    expect(await h.store.save('project', 'shadow', null)).toBe(false);
    expect(h.store.notFound()).toBe(true);
    expect(h.store.writeError('project')?.kind).toBe('not_found');
    expect(h.store.writeError('tenant')).toBeNull();
  });

  it('keeps the server message of a refused mode', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    h.data.writeFailure = refusal(
      'validation',
      422,
      'mode_not_available: healing applies nothing while Healing:Apply is off',
    );
    expect(await h.store.save('tenant', 'enabled', null)).toBe(false);
    expect(h.store.writeError('tenant')?.message).toBe(
      'mode_not_available: healing applies nothing while Healing:Apply is off',
    );
  });

  it('drops a write answer for a project the page has left', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    h.data.holdWrites = true;
    const saving = h.store.save('tenant', 'disabled', null);
    await h.advance();
    expect(h.store.saving()).toBe('tenant');
    h.session.setProject('p-2');
    await h.advance();
    await h.advance();
    expect(h.data.reads).toHaveLength(2);
    h.data.releaseWrite();
    expect(await saving).toBe(false);
    await h.settle();
    expect(h.store.saved()).toBeNull();
    expect(h.store.saving()).toBeNull();
    expect(h.store.writeError('tenant')).toBeNull();
  });

  it('never publishes a read for a project the page has left', async () => {
    const h: HealingHarness = mountHealingSwitch({ answer: resolution() });
    h.data.holdReads = true;
    await h.advance();
    await h.advance();
    expect(h.data.reads).toHaveLength(1);
    h.session.setProject('p-2');
    await h.advance();
    await h.advance();
    expect(h.data.reads).toHaveLength(2);
    const disabled: HealingSwitchResolutionView = resolution({
      tenant: switchRow('tenant', 'disabled'),
      resolved: 'disabled',
    });
    h.data.releaseRead('p-2', disabled);
    await h.advance();
    await h.advance();
    expect(h.store.resolution()?.resolved).toBe('disabled');
    h.data.releaseRead('p-1', resolution({ resolved: 'enabled' }));
    await h.advance();
    expect(h.store.resolution()?.resolved).toBe('disabled');
    expect(h.data.reads[0]?.signal?.aborted).toBe(true);
  });

  it('reports loading only before the first resolution, not during a reload', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    h.data.holdReads = true;
    await h.advance();
    await h.advance();
    expect(h.store.loading()).toBe(true);

    h.data.releaseRead('p-1', SETTABLE);
    await h.settle();
    expect(h.store.loading()).toBe(false);

    h.data.holdReads = true;
    h.store.reload();
    await h.advance();
    await h.advance();
    expect(h.store.loading()).toBe(false);
    expect(h.store.resolution()).not.toBeNull();

    h.data.releaseRead('p-1', SETTABLE);
    await h.settle();
  });

  it('reports an untyped read failure instead of leaving the page loading', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    h.data.readFailure = new Error('the payload did not parse');
    await h.settle();
    expect(h.store.readFailed()).toBe(true);
    expect(h.store.readError()).toBeNull();
    expect(h.store.loading()).toBe(false);
    expect(h.store.resolution()).toBeNull();
  });

  it('drops the attempt entry once its save settles', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    h.data.holdWrites = true;
    const saving = h.store.save('tenant', 'disabled', null);
    await h.advance();
    expect(h.store.attempts.latest.size).toBe(1);

    h.data.releaseWrite();
    expect(await saving).toBe(true);
    expect(h.store.attempts.latest.size).toBe(0);
    await h.settle();
  });

  it('drops the attempt entry after a refused save', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    h.data.writeFailure = refusal('validation', 422, 'mode_not_available (test)');
    expect(await h.store.save('tenant', 'enabled', null)).toBe(false);
    expect(h.store.attempts.latest.size).toBe(0);
  });

  it('reports a 413 as a payload failure with no re-read', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    h.data.writeFailure = refusal('payload_too_large', 413, 'request body too large');
    expect(await h.store.save('tenant', 'disabled', 'Region outage')).toBe(false);
    await h.settle();

    expect(h.store.writeError('tenant')?.kind).toBe('payload_too_large');
    expect(h.store.writeError('tenant')?.status).toBe(413);
    expect(h.data.reads).toHaveLength(1);
    expect(h.store.saving()).toBeNull();
  });

  it('refuses a reason over the serialized byte cap without issuing a PUT', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();
    // 1013 four-byte emoji are inside the 2000 code-point limit but over 4096 serialized bytes.
    expect(await h.store.save('tenant', 'disabled', '🙂'.repeat(1013))).toBe(false);
    expect(h.data.writes).toEqual([]);
    expect(h.store.saved()).toBeNull();
  });

  it('sends the trimmed reason it validated, not the raw input', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE });
    await h.settle();

    expect(await h.store.save('tenant', 'disabled', '  Region outage  ')).toBe(true);
    expect(h.data.writes[0]?.body).toEqual({
      scope: 'tenant',
      mode: 'disabled',
      reason: 'Region outage',
    });

    // Whitespace padding cannot smuggle an over-cap body past the guard: the bytes checked and
    // the bytes sent are the same trimmed string.
    expect(await h.store.save('tenant', 'disabled', 'a'.repeat(5000) + '   ')).toBe(false);
    expect(await h.store.save('tenant', 'disabled', '   ')).toBe(true);
    expect(h.data.writes).toHaveLength(2);
    expect(h.data.writes[1]?.body).toEqual({ scope: 'tenant', mode: 'disabled' });
    await h.settle();
  });

  it('reports no saved claim and no write error before a write', async () => {
    const h = mountHealingSwitch({ answer: SETTABLE, codes: [TRIAGE, CONFIGURE] });
    await h.settle();
    expect(h.store.canConfigure()).toBe(true);
    expect(h.store.saved()).toBeNull();
    expect(h.store.writeError('tenant')).toBeNull();
  });
});
