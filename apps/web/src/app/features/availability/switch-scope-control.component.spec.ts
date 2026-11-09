import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { HealingSwitchView } from '@evidgate/sdk';

import type { ApiError } from '../../core/http/api-error';
import { SessionStore } from '../../core/session/session.store';
import { refusal, switchRow } from './healing-switch.test-support';
import { SwitchScopeControl } from './switch-scope-control.component';

function mount(inputs: {
  scope?: 'system' | 'tenant' | 'project';
  row?: HealingSwitchView | null;
  blockedBy?: 'system' | 'tenant' | 'project' | null;
  editable?: boolean;
  saving?: boolean;
  error?: ApiError | null;
  saved?: boolean;
}) {
  // The page names the project scope after the current project, so the fake session carries a
  // name; the shared fake session keeps ids only and is not enough for that label.
  const currentProject = signal({ id: 'p-1', name: 'Storefront' });
  TestBed.configureTestingModule({
    imports: [SwitchScopeControl],
    providers: [
      provideZonelessChangeDetection(),
      {
        provide: SessionStore,
        useValue: { currentProject, has: () => true },
      },
    ],
  });
  const fixture = TestBed.createComponent(SwitchScopeControl);
  fixture.componentRef.setInput('scope', inputs.scope ?? 'tenant');
  fixture.componentRef.setInput('row', inputs.row ?? null);
  fixture.componentRef.setInput('blockedBy', inputs.blockedBy ?? null);
  fixture.componentRef.setInput('editable', inputs.editable ?? false);
  fixture.componentRef.setInput('saving', inputs.saving ?? false);
  fixture.componentRef.setInput('error', inputs.error ?? null);
  fixture.componentRef.setInput('saved', inputs.saved ?? false);
  fixture.detectChanges();
  const emitted: { mode: string; reason: string | null }[] = [];
  fixture.componentInstance.submitted.subscribe((event) => emitted.push(event));
  return { fixture, root: fixture.nativeElement as HTMLElement, emitted };
}

function scopeCard(root: HTMLElement): HTMLElement {
  const element = root.querySelector<HTMLElement>('[data-healing-scope]');
  if (element === null) throw new Error('the scope card did not render');
  return element;
}

function field(root: HTMLElement, hook: string): HTMLElement | null {
  return root.querySelector<HTMLElement>(`[data-healing-${hook}]`);
}

function enterReason(root: HTMLElement, text: string): void {
  const reason = root.querySelector<HTMLTextAreaElement>('textarea');
  if (reason === null) throw new Error('the reason field did not render');
  reason.value = text;
  reason.dispatchEvent(new Event('input'));
}

describe('SwitchScopeControl', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('renders the system row read-only with no editor and no write control', () => {
    const { root } = mount({
      scope: 'system',
      row: switchRow('system', 'shadow', { set_by: null, reason: null }),
      editable: true,
    });

    expect(scopeCard(root).textContent).toContain('Only the platform operator');
    expect(field(root, 'stored-mode')?.textContent).toContain('Shadow');
    expect(field(root, 'set-by')?.textContent?.trim()).toBe('Hidden');
    expect(field(root, 'reason')?.textContent?.trim()).toBe('Hidden');
    expect(root.querySelector('select')).toBeNull();
    expect(root.querySelector('textarea')).toBeNull();
    expect(field(root, 'save')).toBeNull();
  });

  it('shows a stored tenant row with the dash for a value nobody recorded', () => {
    const { root } = mount({ scope: 'tenant', row: switchRow('tenant', 'shadow') });

    expect(field(root, 'reason')?.textContent?.trim()).toBe('—');
  });

  it('offers exactly the three settable modes and selects the stored one', () => {
    const { root } = mount({ scope: 'tenant', row: switchRow('tenant', 'disabled') });

    const select = root.querySelector<HTMLSelectElement>('select');
    expect(select).not.toBeNull();
    expect([...(select?.options ?? [])].map((option) => option.value)).toEqual([
      'enabled',
      'shadow',
      'disabled',
    ]);
    expect(select?.value).toBe('disabled');
  });

  it('keeps the editor visible but disabled under a blocked system scope and names it', () => {
    const { root } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      blockedBy: 'system',
      editable: true,
    });

    const select = root.querySelector<HTMLSelectElement>('select');
    expect(select?.disabled).toBe(true);
    expect(field(root, 'save')).toBeNull();
    expect(field(root, 'note')?.textContent).toContain('system scope takes precedence');
    expect(select?.getAttribute('aria-describedby')).toContain(
      field(root, 'note')?.id ?? 'missing',
    );
  });

  it('names the tenant scope when it blocks the project scope', () => {
    const { root } = mount({
      scope: 'project',
      row: switchRow('project', 'shadow'),
      blockedBy: 'tenant',
      editable: true,
    });

    expect(field(root, 'note')?.textContent).toContain('tenant scope takes precedence');
    expect(root.querySelector<HTMLSelectElement>('select')?.disabled).toBe(true);
  });

  it('removes the save control without healing:configure while keeping the modes visible', () => {
    const { root } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: false,
    });

    expect(root.querySelector<HTMLSelectElement>('select')?.disabled).toBe(true);
    expect(field(root, 'note')?.textContent).toContain('needs permission to configure healing');
    expect(field(root, 'save')).toBeNull();
  });

  it('submits the chosen mode and the trimmed reason', () => {
    const { fixture, root, emitted } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
    });

    const select = root.querySelector<HTMLSelectElement>('select');
    if (select === null) throw new Error('the mode select did not render');
    select.value = 'disabled';
    select.dispatchEvent(new Event('change'));
    const reason = root.querySelector<HTMLTextAreaElement>('textarea');
    if (reason === null) throw new Error('the reason field did not render');
    reason.value = '  Holding repairs during the release  ';
    reason.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    field(root, 'save')?.click();

    expect(emitted).toEqual([{ mode: 'disabled', reason: 'Holding repairs during the release' }]);
  });

  it('sends null when the reason is blank', () => {
    const { fixture, root, emitted } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
    });

    const reason = root.querySelector<HTMLTextAreaElement>('textarea');
    if (reason === null) throw new Error('the reason field did not render');
    reason.value = '   ';
    reason.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    field(root, 'save')?.click();

    expect(emitted).toEqual([{ mode: 'shadow', reason: null }]);
  });

  it('renders a write refusal verbatim', () => {
    const { root } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
      error: refusal('validation', 422, 'You cannot configure this tenant.').error,
    });

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(
      'You cannot configure this tenant.',
    );
  });

  it('reports a saved switch with role=status', () => {
    const { root } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      saved: true,
    });

    expect(root.querySelector('[role="status"]')?.textContent).toContain('Switch saved');
  });

  it('keeps the saved status region mounted before the write answers', () => {
    const { fixture, root } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
    });

    const status = root.querySelector('[role="status"]');
    expect(status).not.toBeNull();
    expect(status?.textContent?.trim()).toBe('');

    fixture.componentRef.setInput('saved', true);
    fixture.detectChanges();
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Switch saved');
  });

  it('labels the project scope with the current project name', () => {
    const { root } = mount({ scope: 'project', row: switchRow('project', 'shadow') });

    expect(root.querySelector('h2')?.textContent).toContain('Storefront');
  });

  it('shows the saving label and blocks the editor while the write is in flight', () => {
    const { root } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
      saving: true,
    });

    expect(field(root, 'save')?.textContent).toContain('Saving');
    expect(root.querySelector<HTMLSelectElement>('select')?.disabled).toBe(true);
    expect(root.querySelector<HTMLTextAreaElement>('textarea')?.readOnly).toBe(true);
  });

  it.each([
    ['1001 emoji', '🙂'.repeat(1001), 1001],
    ['1999 ASCII and one emoji', 'a'.repeat(1999) + '🙂', 2000],
  ] as const)('submits a valid code-point reason intact: %s', (_label, reason, count) => {
    const { fixture, root, emitted } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
    });

    enterReason(root, reason);
    fixture.detectChanges();
    expect(root.querySelector('textarea')?.hasAttribute('maxlength')).toBe(false);
    expect(root.querySelector('[data-healing-reason-too-long]')).toBeNull();

    field(root, 'save')?.click();
    const sent = emitted[0]?.reason ?? '';
    expect([...sent].length).toBe(count);
    expect(sent).toBe(reason);
  });

  it('blocks a reason above 2000 code points with a visible message', () => {
    const { fixture, root, emitted } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
    });

    enterReason(root, 'a'.repeat(2001));
    fixture.detectChanges();

    expect(root.querySelector('[data-healing-reason-too-long]')?.textContent).toContain(
      'longer than 2000 characters',
    );
    expect(root.querySelector<HTMLButtonElement>('[data-healing-save]')?.disabled).toBe(true);

    field(root, 'save')?.click();
    expect(emitted).toEqual([]);
  });
});
