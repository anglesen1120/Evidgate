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
  readonly rowFacts = this.locatorForAll('[data-api-token-row] app-api-token-facts');
  readonly revokeButtons = this.locatorForAll('button[data-api-revoke]');
  readonly revokeIcon = this.locatorFor('button[data-api-revoke] hlm-icon');
  readonly detail = this.documentRootLocatorFactory().locatorForOptional('[role="dialog"]');
  async open(index = 0) {
    const trigger = (await this.inspectButtons())[index];
    if (!trigger) throw new Error('Missing token trigger');
    await trigger.focus();
    await trigger.click();
    return trigger;
  }
}

function escapeDialog(): void {
  document
    .querySelector('[role="dialog"]')
    ?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
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

  it('renders twenty metadata rows in a labelled directory and never a full credential', async () => {
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
    const table = root.querySelector('table[aria-labelledby="api-directory-title"]');
    const headings = Array.from(table?.querySelectorAll('th[scope="col"]') ?? []);
    expect(headings.map((th) => th.textContent?.trim())).toEqual(['Name', 'Token prefix', 'State']);
    expect(document.querySelector('[role="dialog"]')).toBeNull();
  });

  it('keeps never distinct from a served last-use instant and displays UTC in selected detail', async () => {
    const used = '2026-11-06T12:00:00Z';
    const { fixture, harness } = await render([ROW, { ...ROW, id: 'used', last_used_at: used }]);
    const first = await harness.open();
    expect(await (await harness.detail())?.text()).toContain('Never');
    escapeDialog();
    await fixture.whenStable();
    expect(await harness.detail()).toBeNull();
    expect(await first.isFocused()).toBe(true);
    const second = await harness.open(1);
    const detail = document.querySelector('[role="dialog"]');
    expect(detail?.querySelector('header code')?.textContent).toBe('used');
    expect(detail?.querySelector(`time[datetime="${used}"]`)?.textContent).toContain('UTC');
    for (const fact of [ROW.name, ROW.prefix, 'Created by', ROW.created_by, ...ROW.permissions]) {
      expect(detail?.textContent).toContain(fact);
    }
    escapeDialog();
    await fixture.whenStable();
    expect(await second.isFocused()).toBe(true);
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
    await harness.open();
    expect(await (await harness.detail())?.text()).toContain(name);
    expect(document.querySelector('img')).toBeNull();
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
    expect(
      root.querySelector('[data-token-page-count] app-number-value')?.textContent?.trim(),
    ).toBe('2');
    await harness.open();
    expect(await (await harness.detail())?.text()).toContain('Other');
    escapeDialog();
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

  it('keeps focus on the same unselected Inspect button when narrowing and widening', async () => {
    const { fixture, harness, wide } = await render([ROW, { ...ROW, id: 'other', name: 'Other' }]);
    const [, other] = await harness.inspectButtons();
    await other?.focus();

    for (const isWide of [false, true]) {
      wide.set(isWide);
      await fixture.whenStable();
      expect(await harness.rowFacts()).toHaveLength(0);
      const [, focused] = await harness.inspectButtons();
      expect(await focused?.isFocused()).toBe(true);
      expect(await focused?.getAttribute('aria-label')).toBe('Inspect token Other');
      expect(await harness.detail()).toBeNull();
    }
  });

  it('keeps every metadata fact reachable from the narrow token row', async () => {
    const { root, harness } = await render([ROW], false, true);
    const row = root.querySelector('[data-api-token-row]');
    expect(row?.textContent).toContain(ROW.prefix);
    expect(row?.querySelector('app-status-pill')?.textContent?.trim()).toBe('Active');
    expect(await harness.rowFacts()).toHaveLength(0);
    await harness.open();
    const detail = await (await harness.detail())?.text();
    for (const fact of [ROW.name, ROW.id, ROW.created_by, ...ROW.permissions, 'Never']) {
      expect(detail).toContain(fact);
    }
    expect(document.querySelector('[role="dialog"] time')?.getAttribute('datetime')).toBe(
      ROW.expires_at,
    );
  });

  it('gates revoke by permission, busy state and viewport', async () => {
    const { root, fixture, wide, harness } = await render(
      [ROW, { ...ROW, id: 'old', status: 'revoked' }],
      true,
    );
    const revoke = vi.fn();
    fixture.componentInstance.revoke.subscribe(revoke);
    const buttons = await harness.revokeButtons();
    expect(buttons).toHaveLength(1);
    const button = buttons[0];
    if (!button) throw new Error('Expected token Revoke action');
    expect(await button.text()).toBe('');
    expect(await button.getAttribute('aria-label')).toBe(`Revoke token: ${ROW.name}`);
    expect(await button.hasClass('row-action-danger')).toBe(true);
    expect(await (await harness.revokeIcon()).getAttribute('aria-hidden')).toBe('true');
    expect(await button.getProperty('type')).toBe('button');
    await button.focus();
    await button.click();
    expect(revoke).toHaveBeenCalledWith(ROW);
    fixture.componentRef.setInput('busy', true);
    await fixture.whenStable();
    expect(await button.getProperty('disabled')).toBe(true);
    await button.click();
    expect(revoke).toHaveBeenCalledTimes(1);
    wide.set(false);
    await fixture.whenStable();
    expect(root.querySelector('[data-api-revoke]')).toBeNull();
    for (const fact of [ROW.prefix, 'Active', 'Revoked']) expect(root.textContent).toContain(fact);
    fixture.componentRef.setInput('canWrite', false);
    wide.set(true);
    await fixture.whenStable();
    expect(root.querySelector('[data-api-revoke]')).toBeNull();
  });
});
