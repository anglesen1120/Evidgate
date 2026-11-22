import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { MappingDecisionComponent } from './mapping-decision.component';
import { mappingResolutionFixture } from './mapping-resolution.fixture';

class DecisionHarness extends ComponentHarness {
  static hostSelector = 'hlm-dialog-content';
  readonly submit = this.locatorFor('[data-confirm]');
  readonly cancel = this.locatorFor('[data-cancel]');
  readonly search = this.locatorFor('input[type="search"]');
  readonly candidates = this.locatorForAll('input[type="radio"]');
  readonly firstCandidate = this.locatorFor('input[type="radio"]');
  readonly text = async () => (await this.host()).text();
}

const desktop = signal(true);
async function open(mode: 'bind' | 'dismiss') {
  const fixture = TestBed.createComponent(MappingDecisionComponent);
  fixture.componentRef.setInput('row', mappingResolutionFixture.rows[0]);
  fixture.componentRef.setInput('source', 'junit-ci · checkout.chrome');
  fixture.componentRef.setInput('mode', mode);
  const confirmed = vi.fn();
  const cancelled = vi.fn();
  fixture.componentInstance.confirmed.subscribe(confirmed);
  fixture.componentInstance.cancelled.subscribe(cancelled);
  fixture.detectChanges();
  const dialog =
    await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(DecisionHarness);
  return { dialog, confirmed, cancelled };
}

describe('Mapping decision preview', () => {
  provideLayoutTestStubs();
  beforeEach(() => {
    desktop.set(true);
    TestBed.configureTestingModule({
      providers: [
        {
          provide: NarrowMutationPolicy,
          useValue: { canMutate: desktop, allowsMutation: desktop },
        },
      ],
    });
  });

  it('requires an explicit visible candidate and sends its exact revision', async () => {
    const { dialog, confirmed } = await open('bind');
    expect(await (await dialog.submit()).getProperty<boolean>('disabled')).toBe(true);
    expect(await dialog.candidates()).toHaveLength(5);
    await (await dialog.firstCandidate()).click();
    const search = await dialog.search();
    await search.setInputValue('no such project case');
    await search.dispatchEvent('input');
    expect(await dialog.candidates()).toHaveLength(0);
    expect(await dialog.text()).toContain('No project cases match');
    expect(await (await dialog.submit()).getProperty<boolean>('disabled')).toBe(true);
    await search.setInputValue('Required delivery address');
    await search.dispatchEvent('input');
    expect(await dialog.candidates()).toHaveLength(1);
    await (await dialog.firstCandidate()).click();
    expect(await dialog.text()).toContain(
      'Original failed result and raw evidence stay unchanged.',
    );
    await (await dialog.submit()).click();
    expect(confirmed).toHaveBeenCalledExactlyOnceWith({
      mode: 'bind',
      mapping: 'map-62',
      candidate: 'SHOP-TC-0043 · v2',
    });
  });

  it('can cancel without emitting a decision', async () => {
    const { dialog, confirmed, cancelled } = await open('dismiss');
    await (await dialog.cancel()).click();
    expect(confirmed).not.toHaveBeenCalled();
    expect(cancelled).toHaveBeenCalledOnce();
  });

  it('dismisses only the named mapping and never chooses a case', async () => {
    const { dialog, confirmed } = await open('dismiss');
    expect(await (await dialog.submit()).text()).toBe('Confirm dismissal preview');
    expect(await dialog.text()).toContain('AddressLabel.spec.ts › formats postcode');
    expect(await dialog.candidates()).toHaveLength(0);
    await (await dialog.submit()).click();
    expect(confirmed).toHaveBeenCalledExactlyOnceWith({
      mode: 'dismiss',
      mapping: 'map-62',
      candidate: null,
    });
  });

  it('refuses confirmation if the viewport becomes read-only after opening', async () => {
    const { dialog, confirmed } = await open('dismiss');
    desktop.set(false);
    await (await dialog.submit()).click();
    expect(confirmed).not.toHaveBeenCalled();
  });
});
