import { TestBed } from '@angular/core/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideRouter } from '@angular/router';
import { signal } from '@angular/core';
import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ChecklistReviewTabComponent } from './checklist-review-tab.component';
import { ITEM, PROJECT, PendingUiHarness, pendingRow } from './pending-changes.testing';

class ReviewBoundsHarness extends PendingUiHarness {
  override async set(selector: string, value: string) {
    const field = await this.locatorFor(selector)();
    await field.setInputValue(value);
    await field.dispatchEvent('input');
    await field.focus();
    await field.blur();
  }
  async invalid(selector: string) {
    return (await this.locatorFor(selector)()).getAttribute('aria-invalid');
  }
  async countValue(name: string) {
    return (await this.locatorFor(`#review-${name}-count app-number-value`)()).text();
  }
}

provideLayoutTestStubs();
describe('ChecklistReviewTabComponent bounded validation', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: SessionStore,
          useValue: { currentProject: signal({ id: PROJECT, org_id: 'org' }), has: () => true },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: signal(true),
            narrow: signal(false),
            allowsMutation: () => true,
          },
        },
        {
          provide: AuthoringPendingDataService,
          useValue: {
            listPending: () => Promise.resolve({ items: [pendingRow()], next_cursor: null }),
          },
        },
      ],
    });
  });

  async function setup() {
    const fixture = TestBed.createComponent(ChecklistReviewTabComponent);
    fixture.componentRef.setInput('projectId', PROJECT);
    fixture.componentRef.setInput('itemId', ITEM);
    fixture.detectChanges();
    await fixture.whenStable();
    return TestbedHarnessEnvironment.harnessForFixture(fixture, ReviewBoundsHarness);
  }

  for (const [name, limit] of [
    ['comment', 2000],
    ['reason', 500],
  ] as const) {
    it(`saturates a five-million-character ${name} paste at limit plus one and flags it`, async () => {
      const ui = await setup();
      await ui.set(`#review-${name}`, 'x'.repeat(5_000_000));
      expect(await ui.countValue(name)).toBe(new Intl.NumberFormat('en-US').format(limit + 1));
      expect(await ui.invalid(`#review-${name}`)).toBe('true');
      expect(await ui.textAt(`#review-${name}-error`)).toContain(
        new Intl.NumberFormat('en-US').format(limit),
      );
    });

    it(`counts astral ${name} characters once and accepts exactly the limit`, async () => {
      const ui = await setup();
      await ui.set(`#review-${name}`, '😀'.repeat(limit));
      expect(await ui.countValue(name)).toBe(new Intl.NumberFormat('en-US').format(limit));
      expect(await ui.invalid(`#review-${name}`)).toBeNull();
      await ui.set(`#review-${name}`, '😀'.repeat(limit + 1));
      expect(await ui.countValue(name)).toBe(new Intl.NumberFormat('en-US').format(limit + 1));
      expect(await ui.invalid(`#review-${name}`)).toBe('true');
    });
  }
});
