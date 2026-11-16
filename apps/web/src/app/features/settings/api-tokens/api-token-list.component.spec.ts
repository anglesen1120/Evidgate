import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { InteractivityChecker } from '@angular/cdk/a11y';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ApiTokenListComponent } from './api-token-list.component';
import { ROW } from './api-tokens.test-support';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import type { ApiTokenView } from '@evidgate/sdk';

class TokenListHarness extends ComponentHarness {
  static hostSelector = 'app-api-token-list';
  readonly search = this.locatorFor('#api-token-search');
  readonly inspectButtons = this.locatorForAll('[data-api-inspect]');
  readonly detail = this.locatorForOptional('[data-api-token-detail]');
}

describe('ApiTokenListComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  async function render(rows: readonly ApiTokenView[] = [ROW], writable = false, narrow = false) {
    const wide = signal(!narrow);
    TestBed.configureTestingModule({
      imports: [ApiTokenListComponent],
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
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
    const fixture = TestBed.createComponent(ApiTokenListComponent);
    fixture.componentRef.setInput('rows', rows);
    fixture.componentRef.setInput('canWrite', writable);
    fixture.detectChanges();
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, TokenListHarness);
    return { fixture, root: fixture.nativeElement as HTMLElement, harness, wide };
  }

  it('renders twenty metadata scan records and never a full credential or equal-width table', async () => {
    const full = 'credential-that-must-not-be-rendered';
    const { root } = await render(
      Array.from({ length: 20 }, (_, index) => ({
        ...ROW,
        id: `row-${index}`,
        name: `Reader ${index}`,
        token: full,
      })),
    );
    expect(root.querySelectorAll('[data-api-token-row]')).toHaveLength(20);
    expect(root.textContent).toContain('Reader 19');
    expect(root.textContent).toContain(ROW.prefix);
    expect(root.textContent).toContain('run:read');
    expect(root.outerHTML).not.toContain(full);
    expect(root.querySelector('table')).toBeNull();
    expect(root.querySelector('[data-api-token-detail]')).not.toBeNull();
  });

  it('keeps never distinct from a served last-use instant and displays UTC in selected detail', async () => {
    const used = '2026-11-06T12:00:00Z';
    const { root, harness } = await render([ROW, { ...ROW, id: 'used', last_used_at: used }]);
    expect(await (await harness.detail())?.text()).toContain('Never');
    await (await harness.inspectButtons())[1]?.click();
    const time = root.querySelector(`time[datetime="${used}"]`);
    expect(time?.textContent).toContain('UTC');
    expect(root.textContent).toContain('Created by');
    expect(root.textContent).toContain(ROW.created_by);
  });

  it.each(['active', 'expired', 'revoked'] as const)(
    'renders server %s state independently of the browser clock',
    async (status) => {
      const { root } = await render([{ ...ROW, status, expires_at: '2000-01-01T00:00:00Z' }]);
      expect(root.querySelector('[data-api-token-row]')?.getAttribute('data-state')).toBe(status);
      expect(root.querySelector('app-status-pill')?.textContent?.trim()).toBe(
        status[0]?.toUpperCase() + status.slice(1),
      );
    },
  );

  it('keeps full long untrusted names in selected detail without parsing markup', async () => {
    const name = 'Tên mã mở rộng '.repeat(30) + '<img src=x onerror=alert(1)>';
    const { root, harness } = await render([{ ...ROW, name }]);
    expect(await (await harness.detail())?.text()).toContain(name);
    expect(root.querySelector('img')).toBeNull();
    expect(root.querySelector('app-data-table-value')).not.toBeNull();
  });

  it('labels loaded-page search, supports no-match recovery and never retains filtered-out detail', async () => {
    const { root, harness } = await render([
      ROW,
      { ...ROW, id: 'other', name: 'Other', prefix: 'alternate' },
    ]);
    await (await harness.search()).setInputValue('alternate');
    await (await harness.search()).dispatchEvent('input');
    expect(root.querySelectorAll('[data-api-token-row]')).toHaveLength(1);
    expect(await (await harness.detail())?.text()).toContain('Other');
    await (await harness.search()).setInputValue('not-here');
    await (await harness.search()).dispatchEvent('input');
    expect(await harness.detail()).toBeNull();
    expect(root.textContent).toContain('No tokens match this search');
    expect(root.textContent).toContain('loaded page only');
    root.querySelector<HTMLButtonElement>('app-empty-pattern + button')?.click();
  });

  it('caps permission scan tags at two and discloses the exact remainder through Helm', async () => {
    const permissions = ['run:read', 'suite:read', 'artifact:read', 'project:read'];
    const { root, fixture } = await render([{ ...ROW, permissions }]);
    const tags = root.querySelector('app-api-token-permission-tags');
    expect(tags?.querySelectorAll('[hlmBadge]')).toHaveLength(2);
    const more = tags?.querySelector<HTMLButtonElement>('[data-api-permissions-more]');
    expect(more?.textContent?.trim()).toBe('+2');
    expect(more?.getAttribute('aria-label')).toContain('2 more permissions');
    more?.focus();
    more?.click();
    await fixture.whenStable();
    const dialog = document.querySelector('[role="dialog"]');
    for (const permission of permissions) expect(dialog?.textContent).toContain(permission);
    dialog?.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    await fixture.whenStable();
    expect(document.querySelector('[role="dialog"]')).toBeNull();
    expect(document.activeElement).toBe(more);
  });

  it('gates revoke by permission, busy state and viewport while narrow keeps every metadata fact', async () => {
    const { root, fixture, wide } = await render(
      [ROW, { ...ROW, id: 'old', status: 'revoked' }],
      true,
    );
    const revoke = vi.fn();
    fixture.componentInstance.revoke.subscribe(revoke);
    const button = root.querySelector<HTMLButtonElement>('[data-api-revoke]');
    expect(root.querySelectorAll('[data-api-revoke]')).toHaveLength(1);
    button?.click();
    expect(revoke).toHaveBeenCalledWith(ROW);
    fixture.componentRef.setInput('busy', true);
    await fixture.whenStable();
    expect(button?.disabled).toBe(true);
    button?.click();
    expect(revoke).toHaveBeenCalledTimes(1);
    wide.set(false);
    await fixture.whenStable();
    expect(root.querySelector('[data-api-revoke]')).toBeNull();
    expect(root.querySelectorAll('[data-api-token-row] app-api-token-facts')).toHaveLength(2);
    expect(root.textContent).toContain('Never');
    fixture.componentRef.setInput('canWrite', false);
    wide.set(true);
    await fixture.whenStable();
    expect(root.querySelector('[data-api-revoke]')).toBeNull();
  });
});
