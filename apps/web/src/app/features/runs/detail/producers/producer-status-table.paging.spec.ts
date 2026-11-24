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
    expect(await (await table.previous()).getProperty('disabled')).toBe(true);
    expect(await table.rowIds()).toEqual(slots(20).map((row) => row.producerId));
    await (await table.next()).click();
    expect(await (await table.summary()).text()).toBe('21–40 of 53');
    expect((await table.rowIds())[0]).toBe('shard-20');
    await (await table.next()).click();
    expect(await (await table.summary()).text()).toBe('41–53 of 53');
    expect(await table.rowIds()).toHaveLength(13);
    expect(await (await table.next()).getProperty('disabled')).toBe(true);
    await (await table.next()).click();
    expect(await (await table.summary()).text()).toBe('41–53 of 53');
    await (await table.previous()).click();
    expect(await (await table.summary()).text()).toBe('21–40 of 53');
  });

  it.each([0, 1, 20])('shows the exact single-page range for %i producers', async (count) => {
    const { table } = await render(slots(count));
    expect(await (await table.summary()).text()).toBe(`${count === 0 ? 0 : 1}–${count} of ${count}`);
    expect(await (await table.previous()).getProperty('disabled')).toBe(true);
    expect(await (await table.next()).getProperty('disabled')).toBe(true);
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

  it('resets on replaced, reordered, filtered and shrinking producer data', async () => {
    const original = slots(53);
    const { fixture, table } = await render(original);
    for (const changed of [[...original], [...original].reverse(), original.slice(10), slots(1)]) {
      await (await table.next()).click();
      fixture.componentRef.setInput('slots', changed);
      await fixture.whenStable();
      expect(await (await table.previous()).getProperty('disabled')).toBe(true);
      expect((await table.rowIds())[0]).toBe(changed[0]?.producerId);
      expect(await (await table.summary()).text()).toBe(
        `1–${Math.min(20, changed.length)} of ${changed.length}`,
      );
    }
  });

  it('selects a linked producer page without reselecting it for unrelated facts', async () => {
    const { fixture, table } = await render(slots(53));
    fixture.componentRef.setInput('targetAnchor', 'producer-shard-52');
    expect(await (await table.summary()).text()).toBe('41–53 of 53');
    await (await table.previous()).click();
    fixture.componentRef.setInput('runAfterCancelEvents', 3);
    fixture.componentRef.setInput('status', 'ready');
    expect(await (await table.summary()).text()).toBe('21–40 of 53');
    await table.chooseSize(50);
    expect(await (await table.summary()).text()).toBe('1–50 of 53');
  });
});
