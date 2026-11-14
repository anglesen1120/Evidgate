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

function supportTrigger(root: HTMLElement): HTMLButtonElement {
  const chip = root.querySelector<HTMLButtonElement>('.support-access');
  if (!chip) throw new Error('Expected support trigger');
  const disclosure = chip.closest('details');
  if (disclosure) disclosure.open = true;
  return chip;
}

describe('AuditTableComponent', () => {
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
    const { root } = await render([
      row('actor', { actor_type: actorType as AuditEntryDto['actor_type'] }),
    ]);
    expect(root.querySelector('[data-audit-row] app-status-pill')?.textContent?.trim()).toBe(label);
  });
  it.each(['future_actor', 'constructor', '__proto__'])(
    'keeps an unknown actor type verbatim: %s',
    async (actorType) => {
      const { root } = await render([
        row('unknown', { actor_type: actorType as AuditEntryDto['actor_type'] }),
      ]);
      expect(root.querySelector('[data-audit-row] app-status-pill')?.textContent?.trim()).toBe(
        actorType,
      );
      expect(root.querySelector('[data-audit-support="true"]')).toBeNull();
    },
  );
  it('dismisses the portalled support description with Escape and restores it on refocus', async () => {
    const { fixture, root } = await render([
      row('operator', { actor_type: 'platform_operator' }),
      row('other', { metadata: { support_grant_id: 'grant-other' } }),
    ]);
    const chip = supportTrigger(root);
    chip.focus();
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(document.querySelector('[role="tooltip"]')?.textContent).toContain(
        'grant id not recorded',
      );
    });
    expect(document.activeElement).toBe(chip);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27 }));
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(document.querySelector('[role="tooltip"]')).toBeNull();
    });
    expect(document.activeElement).toBe(chip);
    chip.blur();
    chip.focus();
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(document.querySelector('[role="tooltip"]')).not.toBeNull();
    });
  });
  it('shows the support description on hover without changing the audit row', async () => {
    const { fixture, root } = await render([row('operator', { actor_type: 'platform_operator' })]);
    const chip = supportTrigger(root);
    chip.dispatchEvent(new PointerEvent('pointerenter', { pointerType: 'mouse' }));
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(document.querySelector('[role="tooltip"]')?.textContent).toContain(
        'Platform staff access',
      );
    });
    chip.dispatchEvent(new PointerEvent('pointerleave', { pointerType: 'mouse' }));
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(document.querySelector('[role="tooltip"]')).toBeNull();
    });
    expect(root.querySelectorAll('[data-audit-row]')).toHaveLength(1);
  });
  it('flags only support rows with a keyboard reachable grant description and row marker', async () => {
    const { fixture, root } = await render([
      row('ordinary'),
      row('operator', {
        actor_type: 'platform_operator',
        metadata: { support_grant_id: 'grant-42', private_note: 'not for display' },
      }),
    ]);
    const rows = Array.from(root.querySelectorAll('tbody tr'));
    expect(rows).toHaveLength(2);
    const first = rows[0];
    const second = rows[1];
    if (!first || !second) throw new Error('Expected ordinary and support rows');
    expect(first.querySelector('[data-audit-support="true"]')).toBeNull();
    expect(first.querySelector('.support-access')).toBeNull();
    expect(second.querySelector('[data-audit-support="true"]')).not.toBeNull();
    const chip = supportTrigger(root);
    expect(chip.textContent).toContain('Support access');
    expect(chip.tabIndex).toBe(0);
    const mobileDescription = chip.nextElementSibling;
    expect(mobileDescription?.textContent).toContain(
      'Platform staff access under support grant grant-42',
    );
    expect(chip.closest('details')?.open).toBe(true);
    chip.focus();
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(document.querySelector('[role="tooltip"]')?.textContent).toContain(
        'Platform staff access under support grant grant-42',
      );
    });
    expect(root.textContent).not.toContain('not for display');
    expect(root.textContent).not.toContain('expiry');
  });
  it('flags an operator without a grant key without inventing a grant or expiry', async () => {
    const { fixture, root } = await render([row('operator', { actor_type: 'platform_operator' })]);
    expect(root.querySelector('[data-audit-support="true"]')).not.toBeNull();
    expect(root.querySelector('.support-access')?.nextElementSibling?.textContent).toContain(
      'Platform staff access (grant id not recorded on this row)',
    );
    supportTrigger(root).focus();
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(document.querySelector('[role="tooltip"]')?.textContent).toContain(
        'Platform staff access (grant id not recorded on this row)',
      );
    });
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
  it('keeps exact UTC instants and role-based bounded scan values in the shared table', async () => {
    const { root } = await render([row('ordinary')]);
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe('2026-10-25T00:00:00Z');
    expect(root.querySelector('time')?.textContent).toContain('Oct 25, 2026');
    expect(root.querySelector('time')?.textContent?.replace(/\s/g, ' ')).toContain('12:00:00 AM');
    expect(root.querySelector('time')?.textContent).toContain('UTC');
    expect(root.querySelector('table')?.classList.contains('eg-table-layout')).toBe(true);
    expect(root.querySelectorAll('thead th')).toHaveLength(5);
    expect(root.querySelector('th[data-column-priority="primary"]')?.textContent?.trim()).toBe(
      'Action',
    );
    expect(root.querySelector('th[data-column-role="date"]')?.textContent?.trim()).toBe(
      'Time (UTC)',
    );
    expect(root.querySelectorAll('[role="table"]')).toHaveLength(1);
    expect(root.querySelector('app-data-table')?.textContent).toContain('Audit activity');
    expect(
      root.querySelector('td[data-column-priority="primary"] app-data-table-value'),
    ).not.toBeNull();
    expect(
      root.querySelector('td[data-column-role="identity"] app-copyable-identifier'),
    ).not.toBeNull();
    expect(root.textContent).toContain('—');
  });
  it('renders every labelled audit fact with full values in a narrow container', async () => {
    const width = vi.spyOn(Element.prototype, 'clientWidth', 'get').mockImplementation(function (
      this: Element,
    ) {
      return this.tagName === 'APP-DATA-TABLE' ? 480 : 0;
    });
    try {
      const actor = 'actor-'.repeat(40);
      const object = 'object-'.repeat(40);
      const request = 'request-'.repeat(40);
      const action = 'project.very-long-action-'.repeat(15);
      const { root } = await render([
        row('narrow', {
          actor_id: actor,
          object_id: object,
          request_id: request,
          action,
        }),
      ]);
      const records = root.querySelector('app-data-table-records');
      expect(records).not.toBeNull();
      expect(root.querySelector('table')).toBeNull();
      for (const value of [actor, object, request, action])
        expect(records?.textContent).toContain(value);
      const labels = [...root.querySelectorAll('.eg-table-fact > dt')].map(
        (label) => label.textContent,
      );
      expect(labels).toEqual(['Action', 'Actor', 'Object', 'Time (UTC)', 'Event details']);
      expect(root.querySelector('time')?.textContent).toContain('UTC');
      expect(root.querySelector('[data-audit-details]')).not.toBeNull();
    } finally {
      width.mockRestore();
    }
  });
  it.each([['not-a-date'], [''], ['2026-02-30T00:00:00Z']])(
    'invents no instant for the unusable timestamp %j',
    async (createdAt) => {
      const { root } = await render([row('unusable', { created_at: createdAt })]);
      expect(root.querySelector('time')).toBeNull();
      expect(root.querySelector('app-absolute-time')?.textContent).toContain('Time unavailable');
      expect(root.textContent).toContain('project.updated');
    },
  );
  it('renders a server instant that carries a numeric offset in UTC', async () => {
    const instant = '2026-10-25T07:30:00+07:00';
    const { root } = await render([row('offset', { created_at: instant })]);
    const time = root.querySelector('time');
    expect(time?.getAttribute('datetime')).toBe(instant);
    expect(time?.textContent).toContain('Oct 25, 2026');
    expect(time?.textContent).toContain('12:30:00');
  });
  it('keeps mobile metadata closed per row and omits empty request, support and object values', async () => {
    const { root } = await render([
      row('with-request', { request_id: 'request-visible-in-details' }),
      row('empty', { request_id: null, object_type: null, object_id: null }),
    ]);
    const rows = root.querySelectorAll('tbody tr');
    const disclosure = rows[0]?.querySelector<HTMLDetailsElement>('details');
    expect(disclosure?.open).toBe(false);
    expect(disclosure?.querySelector('summary')?.textContent).toContain('Event details');
    expect(disclosure?.textContent).toContain('request-visible-in-details');
    expect(disclosure?.textContent).not.toContain('Support access');
    if (!disclosure) throw new Error('Expected row disclosure');
    disclosure.open = true;
    expect(disclosure.open).toBe(true);
    expect(rows[1]?.querySelector('details')).toBeNull();
    expect(rows[1]?.textContent).toContain('—');
    expect(rows[1]?.querySelector('[hidden]')).toBeNull();
    expect(root.querySelectorAll('[data-audit-row]')).toHaveLength(2);
  });
});
