import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AuditEntryDto } from '@evidgate/sdk';
import { AuditTableComponent } from './audit-table.component';

function row(id: string, changes: Partial<AuditEntryDto> = {}): AuditEntryDto {
  return {
    id,
    seq: 1,
    actor_type: 'user',
    actor_id: 'member1',
    action: 'project.updated',
    object_type: null,
    object_id: null,
    request_id: 'request1',
    prev_hash: 'previous',
    entry_hash: 'entry',
    created_at: '2026-10-25T00:00:00Z',
    ...changes,
  };
}

describe('AuditTableComponent (AC8)', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [AuditTableComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());
  async function render(rows: AuditEntryDto[]) {
    const fixture = TestBed.createComponent(AuditTableComponent);
    fixture.componentRef.setInput('rows', rows);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }
  it.each([
    ['user', 'User'],
    ['ci_producer', 'CI producer'],
    ['agent', 'Agent'],
    ['platform_operator', 'Platform staff'],
    ['system', 'System'],
  ])('renders the localized actor label for %s', async (actorType, label) => {
    const { root } = await render([row('actor', { actor_type: actorType })]);
    expect(root.querySelector('tbody td app-status-pill')?.textContent?.trim()).toBe(label);
  });
  it.each(['future_actor', 'constructor', '__proto__'])(
    'keeps an unknown actor type verbatim: %s',
    async (actorType) => {
      const { root } = await render([row('unknown', { actor_type: actorType })]);
      expect(root.querySelector('tbody td app-status-pill')?.textContent?.trim()).toBe(actorType);
      expect(root.querySelector('[data-audit-support="true"]')).toBeNull();
    },
  );
  it('dismisses the support description with Escape and restores it on refocus', async () => {
    const { fixture, root } = await render([
      row('operator', { actor_type: 'platform_operator' }),
      row('other', { metadata: { support_grant_id: 'grant-other' } }),
    ]);
    const chip = root.querySelector<HTMLElement>('.support-access')!;
    const description = root.querySelector<HTMLElement>('#audit-support-operator')!;
    const other = root.querySelector<HTMLElement>('#audit-support-other')!;
    chip.focus();
    await fixture.whenStable();
    expect(document.activeElement).toBe(chip);
    expect(description.hidden).toBe(false);
    chip.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await fixture.whenStable();
    expect(description.hidden).toBe(false);
    chip.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();
    expect(description.hidden).toBe(true);
    expect(document.activeElement).toBe(chip);
    expect(other.hidden).toBe(false);
    chip.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    await fixture.whenStable();
    expect(description.hidden).toBe(true);
    chip.blur();
    chip.focus();
    await fixture.whenStable();
    expect(description.hidden).toBe(false);
    expect(chip.getAttribute('aria-describedby')).toBe(description.id);
  });
  it('keeps Escape dismissal until hover re-entry, not movement inside the chip', async () => {
    const { fixture, root } = await render([row('operator', { actor_type: 'platform_operator' })]);
    const chip = root.querySelector<HTMLElement>('.support-access')!;
    const description = root.querySelector<HTMLElement>('#audit-support-operator')!;
    chip.dispatchEvent(new MouseEvent('mouseenter'));
    await fixture.whenStable();
    expect(description.hidden).toBe(false);
    description.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
    await fixture.whenStable();
    expect(description.hidden).toBe(false);
    chip.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();
    expect(description.hidden).toBe(true);
    description.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
    chip.dispatchEvent(new MouseEvent('mousemove'));
    await fixture.whenStable();
    expect(description.hidden).toBe(true);
    chip.dispatchEvent(new MouseEvent('mouseleave'));
    chip.dispatchEvent(new MouseEvent('mouseenter'));
    await fixture.whenStable();
    expect(description.hidden).toBe(false);
  });
  it('flags only support rows with a keyboard reachable grant description and row marker', async () => {
    const { root } = await render([
      row('ordinary'),
      row('operator', {
        actor_type: 'platform_operator',
        metadata: { support_grant_id: 'grant-42', private_note: 'not for display' },
      }),
    ]);
    const rows = Array.from(root.querySelectorAll('tbody tr'));
    expect(rows).toHaveLength(2);
    const first = rows[0]!;
    const second = rows[1]!;
    expect(first.getAttribute('data-audit-support')).not.toBe('true');
    expect(first.querySelector('.support-access')).toBeNull();
    expect(second.getAttribute('data-audit-support')).toBe('true');
    const chip = second.querySelector<HTMLElement>('.support-access')!;
    expect(chip.textContent).toContain('Support access');
    expect(chip.tabIndex).toBe(0);
    const description = root.querySelector(`[id="${chip.getAttribute('aria-describedby')}"]`);
    expect(description?.textContent).toBe('Platform staff access under support grant grant-42');
    expect(root.textContent).not.toContain('not for display');
    expect(root.textContent).not.toContain('expiry');
  });
  it('flags an operator without a grant key without inventing a grant or expiry', async () => {
    const { root } = await render([row('operator', { actor_type: 'platform_operator' })]);
    expect(root.querySelector('[data-audit-support="true"]')).not.toBeNull();
    expect(root.querySelector('[role="tooltip"]')?.textContent).toBe(
      'Platform staff access (grant id not recorded on this row)',
    );
  });
  it('flags a support grant on another actor type and preserves full long values as text', async () => {
    const long = '<b>' + 'actor-'.repeat(80);
    const { root } = await render([
      row('long', {
        actor_id: long,
        action: long,
        object_id: long,
        metadata: { support_grant_id: 'grant-user' },
      }),
    ]);
    expect(root.querySelector('[data-audit-support="true"]')).not.toBeNull();
    expect(root.textContent).toContain(long);
    expect(root.querySelector('b')).toBeNull();
  });
  it('renders null objects as em dashes and absolute timestamps in a semantic scrollable table', async () => {
    const { root } = await render([row('ordinary')]);
    expect(root.querySelector('tbody td:nth-of-type(3)')?.textContent).toContain('—');
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe('2026-10-25T00:00:00Z');
    expect(root.querySelector('time')?.textContent).toBe('2026-10-25T00:00:00Z');
    expect(root.querySelector('caption')?.textContent).toContain('Audit activity');
    expect(root.querySelectorAll('thead th[scope="col"]')).toHaveLength(6);
    expect(root.querySelector<HTMLElement>('[role="region"]')?.tabIndex).toBe(0);
  });
});
