import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { SecretReferenceListComponent } from './secret-reference-list.component';
import { base } from './secrets-page.testing';
import { ROTATION_CLOCK } from './rotation-reminder-chip.component';

class ReferenceListHarness extends ComponentHarness {
  static hostSelector = 'app-secret-reference-list';
  readonly search = this.locatorFor('#secret-reference-search');
  readonly inspect = this.locatorForAll('[data-secret-inspect]');
  readonly detail = this.locatorForOptional('[data-secret-detail]');
  readonly counts = this.locatorForAll('[data-page-count] dd app-number-value');
  readonly filters = this.locatorForAll('[role="group"] button[aria-pressed]');
}

describe('Secret reference list and selected lifecycle', () => {
  afterEach(() => TestBed.resetTestingModule());
  async function render(narrow = false) {
    const wide = signal(!narrow);
    TestBed.configureTestingModule({
      providers: [
        { provide: ROTATION_CLOCK, useValue: () => new Date('2026-11-01T00:00:00Z') },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: wide,
            narrow: computed(() => !wide()),
            allowsMutation: () => wide(),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(SecretReferenceListComponent);
    fixture.componentRef.setInput('rows', [
      base,
      {
        ...base,
        id: 'second',
        name: 'Tên tham chiếu mở rộng '.repeat(20),
        prefix: 'alternate',
        expires_at: null,
        overlap_ends_at: null,
        last_used_at: null,
      },
    ]);
    fixture.componentRef.setInput('canWrite', true);
    fixture.detectChanges();
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      ReferenceListHarness,
    );
    return { fixture, root: fixture.nativeElement as HTMLElement, harness, wide };
  }

  it('summarizes the loaded page independently of the directory filters', async () => {
    const { fixture, harness } = await render();
    fixture.componentRef.setInput('rows', [
      {
        ...base,
        id: 'near',
        name: 'Production',
        status: 'active',
        expires_at: '2026-11-15T00:00:00Z',
      },
      { ...base, id: 'past', status: 'expired' },
      { ...base, id: 'revoked', status: 'revoked' },
    ]);
    await fixture.whenStable();
    const counts = async () => Promise.all((await harness.counts()).map((node) => node.text()));
    expect(await counts()).toEqual(['3', '1', '1', '1']);
    const filters = await harness.filters();
    const labels = await Promise.all(filters.map((filter) => filter.text()));
    expect(labels.map((label) => label.replace(/\s*(\d+)$/, ':$1'))).toEqual([
      'All states:3',
      'Active:1',
      'Rotated:0',
      'Revoked:1',
      'Expired:1',
    ]);
    const [all, active] = filters;
    if (!all || !active) throw new Error('Expected all and active status filters');
    await active.click();
    expect(await active.getAttribute('aria-pressed')).toBe('true');
    expect(await harness.inspect()).toHaveLength(1);
    expect(await counts()).toEqual(['3', '1', '1', '1']);
    await all.click();
    await (await harness.search()).setInputValue('Production');
    await (await harness.search()).dispatchEvent('input');
    expect(await harness.inspect()).toHaveLength(1);
    expect(await counts()).toEqual(['3', '1', '1', '1']);
    fixture.componentRef.setInput('rows', [{ ...base, status: 'revoked' }]);
    expect(await counts()).toEqual(['1', '0', '0', '0']);
  });

  it('selects a complete metadata lifecycle with explicit missing values and UTC instants', async () => {
    const { root, harness } = await render();
    expect(root.querySelectorAll('[data-secret-reference]')).toHaveLength(2);
    expect(root.querySelector('time[datetime]')?.textContent).toContain('UTC');
    await (await harness.inspect())[1]?.click();
    const detail = await (await harness.detail())?.text();
    expect(detail).toContain('Tên tham chiếu mở rộng '.repeat(20).trim());
    expect(detail).toContain('No expiry');
    expect(detail).toContain('No overlap window');
    expect(detail).toContain('Not used yet');
    expect(detail).toContain('alternate');
  });

  it('keeps local filters usable and removes stale selected facts for no-match results', async () => {
    const { root, harness, fixture } = await render();
    await (await harness.search()).setInputValue('alternate');
    await (await harness.search()).dispatchEvent('input');
    expect(root.querySelectorAll('[data-secret-reference]')).toHaveLength(1);
    expect(await (await harness.detail())?.text()).toContain('alternate');
    await (await harness.search()).setInputValue('not-loaded');
    await (await harness.search()).dispatchEvent('input');
    expect(await harness.detail()).toBeNull();
    expect(root.textContent).toContain('No references match these filters');
    expect(root.textContent).toContain('loaded page only');
    root.querySelector<HTMLButtonElement>('app-empty-pattern + button')?.click();
    await fixture.whenStable();
    expect(root.querySelectorAll('[data-secret-reference]')).toHaveLength(2);
  });

  it('renders labelled stacked lifecycle facts on narrow screens and never emits a mutation after resize', async () => {
    const { root, fixture, wide } = await render();
    const emitted = vi.fn();
    fixture.componentInstance.rotate.subscribe(emitted);
    wide.set(false);
    await fixture.whenStable();
    expect(root.querySelector('[data-rotate]')).toBeNull();
    const records = root.querySelectorAll('[data-secret-reference] app-secret-reference-facts');
    expect(records).toHaveLength(2);
    for (const record of records) {
      expect(record.querySelectorAll('dt')).toHaveLength(7);
      expect(record.textContent).toContain('Resolution scope');
    }
    expect(emitted).not.toHaveBeenCalled();
    expect(root.querySelectorAll('[data-secret-inspect]')).toHaveLength(2);
  });
});
