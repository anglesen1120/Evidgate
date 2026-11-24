import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideLayoutTestStubs } from '../../../../shared/testing/layout.testing';
import { useLightweightTablePresentation } from '../../../../shared/ui/data-table/data-table-volume.testing';
import type { ProducerSlot } from './producer-slots';
import { ProducerStatusTableComponent } from './producer-status-table.component';
import { ProducerStatusTableHarness } from './producer-status-table.harness';

function slots(count: number): ProducerSlot[] {
  return Array.from({ length: count }, (_, index) => ({
    producerId: `shard-${index}`,
    identityState: 'bound',
    finalizeStatus: 'finalized',
    lastSequence: 1,
    count: 1,
    expected: 1,
    staleEpochEvents: 0,
    afterCancelEvents: 0,
  }));
}

describe('ProducerStatusTableComponent paging', () => {
  provideLayoutTestStubs();
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ProducerStatusTableComponent],
      providers: [provideZonelessChangeDetection()],
    });
    useLightweightTablePresentation();
  });
  afterEach(() => TestBed.resetTestingModule());

  async function render(rows: readonly ProducerSlot[]) {
    const fixture = TestBed.createComponent(ProducerStatusTableComponent);
    fixture.componentRef.setInput('slots', rows);
    const table = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      ProducerStatusTableHarness,
    );
    return { fixture, table };
  }

  it('shows exact first, middle and last ranges with bounded navigation', async () => {
    const { table } = await render(slots(53));
    expect(await (await table.summary()).text()).toBe('1–20 of 53');
    const previous = await table.previous();
    expect(await previous.getAttribute('aria-disabled')).toBe('true');
    expect(await previous.getProperty('disabled')).toBe(false);
    await previous.focus();
    await previous.click();
    expect(await previous.isFocused()).toBe(true);
    expect(await (await table.summary()).text()).toBe('1–20 of 53');
    expect(await table.rowIds()).toEqual(slots(20).map((row) => row.producerId));
    await (await table.next()).click();
    expect(await (await table.summary()).text()).toBe('21–40 of 53');
    expect(await (await table.previous()).getAttribute('aria-disabled')).toBe('false');
    expect(await (await table.next()).getAttribute('aria-disabled')).toBe('false');
    expect((await table.rowIds())[0]).toBe('shard-20');
    await (await table.next()).click();
    expect(await (await table.summary()).text()).toBe('41–53 of 53');
    expect(await table.rowIds()).toHaveLength(13);
    const next = await table.next();
    expect(await next.getAttribute('aria-disabled')).toBe('true');
    expect(await next.getProperty('disabled')).toBe(false);
    await next.focus();
    await next.click();
    expect(await next.isFocused()).toBe(true);
    expect(await (await table.summary()).text()).toBe('41–53 of 53');
    await (await table.previous()).click();
    expect(await (await table.summary()).text()).toBe('21–40 of 53');
  });

  it.each([0, 1, 20])('shows the exact single-page range for %i producers', async (count) => {
    const { table } = await render(slots(count));
    expect(await (await table.summary()).text()).toBe(
      `${count === 0 ? 0 : 1}–${count} of ${count}`,
    );
    for (const button of [await table.previous(), await table.next()]) {
      expect(await button.getAttribute('aria-disabled')).toBe('true');
      expect(await button.getProperty('disabled')).toBe(false);
      await button.focus();
      await button.click();
      expect(await button.isFocused()).toBe(true);
      expect(await (await table.summary()).text()).toBe(
        `${count === 0 ? 0 : 1}–${count} of ${count}`,
      );
    }
    expect(await table.rowIds()).toHaveLength(count);
  });

  it.each([50, 100])('resets to the first page when choosing size %i', async (size) => {
    const { table } = await render(slots(123));
    await (await table.next()).click();
    await table.chooseSize(size);
    expect(await (await table.summary()).text()).toBe(`1–${size} of 123`);
    expect(await table.rowIds()).toHaveLength(size);
    expect((await table.rowIds())[0]).toBe('shard-0');
  });

  it.each([1, 2])('preserves page %i and updates cells on same-order refresh', async (page) => {
    const original = slots(53);
    const { fixture, table } = await render(original);
    for (let index = 0; index < page; index++) await (await table.next()).click();
    for (const refreshed of [
      [...original],
      original.map((slot) => ({ ...slot, lastSequence: 987 })),
    ]) {
      fixture.componentRef.setInput('slots', refreshed);
      expect(await (await table.summary()).text()).toBe(page === 1 ? '21–40 of 53' : '41–53 of 53');
      expect((await table.rowIds())[0]).toBe(page === 1 ? 'shard-20' : 'shard-40');
    }
    expect(await (await table.rows())[0]?.text()).toContain('987');
  });

  it.each([
    ['reordered', slots(53).reverse()],
    ['inserted', [...slots(53), { ...slots(1)[0], producerId: 'inserted' }]],
    ['removed', slots(53).slice(1)],
    ['replaced', slots(53).map((slot) => ({ ...slot, producerId: `new-${slot.producerId}` }))],
    ['shrunk', slots(1)],
    ['empty', []],
  ] as const)('resets when producer identities are %s', async (_change, changed) => {
    const { fixture, table } = await render(slots(53));
    await (await table.next()).click();
    await (await table.next()).click();
    fixture.componentRef.setInput('slots', changed);
    expect(await (await table.previous()).getAttribute('aria-disabled')).toBe('true');
    expect((await table.rowIds())[0]).toBe(changed[0]?.producerId);
    expect(await (await table.summary()).text()).toBe(
      `${changed.length === 0 ? 0 : 1}–${Math.min(20, changed.length)} of ${changed.length}`,
    );
  });

  it('selects a linked producer page without reselecting it for unrelated facts', async () => {
    const { fixture, table } = await render(slots(53));
    fixture.componentRef.setInput('targetAnchor', 'producer-shard-52');
    expect(await (await table.summary()).text()).toBe('41–53 of 53');
    await (await table.previous()).click();
    fixture.componentRef.setInput('runAfterCancelEvents', 3);
    fixture.componentRef.setInput('status', 'ready');
    expect(await (await table.summary()).text()).toBe('21–40 of 53');
    fixture.componentRef.setInput('slots', slots(53));
    expect(await (await table.summary()).text()).toBe('21–40 of 53');
    fixture.componentRef.setInput('targetAnchor', 'producer-shard-45');
    expect(await (await table.summary()).text()).toBe('41–53 of 53');
    await table.chooseSize(50);
    expect(await (await table.summary()).text()).toBe('1–50 of 53');
  });

  it('lands on an anchor set before producer data arrives', async () => {
    const { fixture, table } = await render([]);
    fixture.componentRef.setInput('slots', null);
    fixture.componentRef.setInput('targetAnchor', 'producer-shard-52');
    await fixture.whenStable();
    fixture.componentRef.setInput('slots', slots(53));
    expect(await (await table.summary()).text()).toBe('41–53 of 53');
    expect(await table.rowIds()).toContain('shard-52');
  });

  it('keeps a missing anchor in bounds', async () => {
    const { fixture, table } = await render(slots(53));
    await (await table.next()).click();
    fixture.componentRef.setInput('targetAnchor', 'producer-missing');
    expect(await (await table.summary()).text()).toBe('1–20 of 53');
    fixture.componentRef.setInput('slots', []);
    expect(await (await table.summary()).text()).toBe('0–0 of 0');
    expect(await (await table.next()).getAttribute('aria-disabled')).toBe('true');
  });
});
