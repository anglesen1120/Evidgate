import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { SecretReferenceListComponent } from './secret-reference-list.component';
import { base } from './secrets-page.testing';

class ReferenceListHarness extends ComponentHarness {
  static hostSelector = 'app-secret-reference-list';
  readonly search = this.locatorFor('#secret-reference-search');
  readonly inspect = this.locatorForAll('[data-secret-inspect]');
  readonly detail = this.locatorForOptional('[data-secret-detail]');
}

describe('Secret reference list and selected lifecycle', () => {
  afterEach(() => TestBed.resetTestingModule());
  async function render(narrow = false) {
    const wide = signal(!narrow);
    TestBed.configureTestingModule({
      providers: [
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
