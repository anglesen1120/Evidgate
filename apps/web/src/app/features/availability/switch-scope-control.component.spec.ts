import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import type { ComponentFixture } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import type { HealingSwitchView } from '@evidgate/sdk';

import type { ApiError } from '../../core/http/api-error';
import { SessionStore } from '../../core/session/session.store';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { stubScopeBrowserApis } from '../../shell/layout/scope.fixture';
import { refusal, switchRow } from './healing-switch.test-support';
import { SwitchScopeControl } from './switch-scope-control.component';

class ModeHarness extends ComponentHarness {
  static hostSelector = 'app-switch-scope-control';
  readonly trigger = this.locatorFor('button[role="combobox"]');
  readonly options = this.documentRootLocatorFactory().locatorForAll('[role="option"]');

  async choose(label: string): Promise<void> {
    await (await this.trigger()).click();
    for (const option of await this.options()) {
      if ((await option.text()).trim() === label) {
        await option.click();
        return;
      }
    }
    throw new Error(`Missing mode ${label}`);
  }
}

function changeMode(fixture: ComponentFixture<SwitchScopeControl>, value: unknown): void {
  fixture.debugElement.query(By.css('hlm-select')).triggerEventHandler('valueChange', value);
}

function modeTrigger(root: HTMLElement): HTMLButtonElement | null {
  return root.querySelector<HTMLButtonElement>('button[role="combobox"]');
}

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
  const desktop = signal(true);
  const policy = {
    canMutate: desktop,
    narrow: () => !desktop(),
    allowsMutation: () => desktop(),
  };
  TestBed.configureTestingModule({
    imports: [SwitchScopeControl],
    providers: [
      provideZonelessChangeDetection(),
      { provide: NarrowMutationPolicy, useValue: policy },
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
  return { fixture, root: fixture.nativeElement as HTMLElement, emitted, desktop };
}

function scopeCard(root: HTMLElement): HTMLElement {
  const element = root.querySelector<HTMLElement>('[data-healing-editor]');
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
  let restoreBrowserApis: () => void;
  beforeEach(() => {
    restoreBrowserApis = stubScopeBrowserApis();
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    restoreBrowserApis();
    vi.unstubAllGlobals();
  });

  it('renders the system row read-only with no editor and no write control', () => {
    const { root } = mount({
      scope: 'system',
      row: switchRow('system', 'shadow', { set_by: null, reason: null }),
      editable: true,
    });

    expect(scopeCard(root).textContent).toContain('Only the platform operator');
    expect(root.querySelector('select')).toBeNull();
    expect(root.querySelector('textarea')).toBeNull();
    expect(field(root, 'save')).toBeNull();
  });

  it('defaults to shadow when no scope stores a row', () => {
    const { root } = mount({ scope: 'tenant' });
    expect(modeTrigger(root)?.textContent).toContain('Shadow');
  });

  it('offers exactly the three settable modes and selects the stored one', async () => {
    const { fixture, root } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'disabled'),
      editable: true,
    });
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, ModeHarness);
    expect(modeTrigger(root)?.textContent).toContain('Disabled');
    await (await harness.trigger()).click();
    expect(
      await Promise.all(
        (await harness.options()).map(async (option) => (await option.text()).trim()),
      ),
    ).toEqual(['Enabled', 'Shadow', 'Disabled']);
  });

  it('disables the Helm select under a blocked system scope and keeps the explanation visible', () => {
    const { fixture, root, emitted } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      blockedBy: 'system',
      editable: true,
    });

    const select = modeTrigger(root);
    if (select === null) throw new Error('the mode select did not render');
    expect(select.disabled).toBe(true);

    const note = field(root, 'note');
    expect(note?.textContent).toContain('system scope takes precedence');
    const describedBy = select.getAttribute('aria-describedby') ?? '';
    expect(describedBy.split(' ')).toContain(note?.id);

    changeMode(fixture, 'disabled');
    fixture.detectChanges();
    expect(select.textContent).toContain('Shadow');
    expect(emitted).toEqual([]);
    expect(field(root, 'save')).toBeNull();
  });

  it('names the tenant scope when it blocks the project scope', () => {
    const { root } = mount({
      scope: 'project',
      row: switchRow('project', 'shadow'),
      blockedBy: 'tenant',
      editable: true,
    });

    expect(field(root, 'note')?.textContent).toContain('tenant scope takes precedence');
    expect(modeTrigger(root)?.disabled).toBe(true);
  });

  it('removes the save control without healing:configure while keeping the modes visible', () => {
    const { root } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: false,
    });

    expect(modeTrigger(root)?.disabled).toBe(true);
    expect(field(root, 'note')?.textContent).toContain('needs permission to configure healing');
    expect(field(root, 'save')).toBeNull();
  });

  it('submits the chosen mode and the trimmed reason', async () => {
    const { fixture, root, emitted } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
    });

    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, ModeHarness);
    await harness.choose('Disabled');
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
    expect(status?.childNodes).toHaveLength(0);
    expect(status?.textContent?.trim()).toBe('');

    fixture.componentRef.setInput('saved', true);
    fixture.detectChanges();
    expect(root.querySelector('[role="status"]')?.childNodes.length).toBeGreaterThan(0);
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Switch saved');
  });

  it('labels the project scope with the current project name', () => {
    const { root } = mount({ scope: 'project', row: switchRow('project', 'shadow') });

    expect(root.querySelector('h3')?.textContent).toContain('Storefront');
  });

  it('shows the saving label and blocks the editor while the write is in flight', () => {
    const { root } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
      saving: true,
    });

    expect(field(root, 'save')?.textContent).toContain('Saving');
    expect(modeTrigger(root)?.disabled).toBe(true);
    expect(root.querySelector<HTMLTextAreaElement>('textarea')?.readOnly).toBe(true);
  });

  it('refuses a mode change while the write is in flight and restores the stored value', () => {
    const { fixture, root, emitted } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
      saving: true,
    });

    changeMode(fixture, 'disabled');
    fixture.detectChanges();

    expect(modeTrigger(root)?.textContent).toContain('Shadow');
    expect(emitted).toEqual([]);
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

    expect(root.querySelector('[data-healing-reason-too-long]')?.textContent).toContain('too long');
    expect(root.querySelector<HTMLButtonElement>('[data-healing-save]')?.disabled).toBe(true);

    field(root, 'save')?.click();
    expect(emitted).toEqual([]);
  });

  it('blocks a reason inside the code-point limit but over the serialized byte cap', () => {
    const { fixture, root, emitted } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
    });

    // 1013 four-byte emoji are 1013 code points but 4098 serialized bytes on a tenant body.
    enterReason(root, '🙂'.repeat(1013));
    fixture.detectChanges();
    expect(root.querySelector('[data-healing-reason-too-long]')?.textContent).toContain('4 KB');
    expect(root.querySelector<HTMLButtonElement>('[data-healing-save]')?.disabled).toBe(true);
    field(root, 'save')?.click();
    expect(emitted).toEqual([]);

    enterReason(root, '🙂'.repeat(1012));
    fixture.detectChanges();
    expect(root.querySelector('[data-healing-reason-too-long]')).toBeNull();
    field(root, 'save')?.click();
    expect(emitted.map((event) => [...(event.reason ?? '')].length)).toEqual([1012]);
  });

  it('replaces the server message of a 413 with the reason-specific guidance', () => {
    const { root } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
      error: refusal('payload_too_large', 413, 'request body too large').error,
    });

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(
      'The reason is too large to send. Shorten it.',
    );
    expect(root.querySelector('[role="alert"]')?.textContent).not.toContain(
      'request body too large',
    );
  });
  it('retains the draft across narrow mode and refuses a stale save without replaying it', () => {
    const { fixture, root, emitted, desktop } = mount({
      scope: 'tenant',
      row: switchRow('tenant', 'shadow'),
      editable: true,
      error: refusal('validation', 422, 'Review the stored scope.').error,
    });
    changeMode(fixture, 'disabled');
    enterReason(root, 'Giữ nguyên bản nháp trong khi đổi kích thước');
    fixture.detectChanges();
    const staleSave = field(root, 'save');
    desktop.set(false);
    staleSave?.click();
    expect(emitted).toEqual([]);
    fixture.detectChanges();
    expect(modeTrigger(root)).toBeNull();
    expect(root.querySelector('textarea')).toBeNull();
    expect(field(root, 'save')).toBeNull();
    expect(field(root, 'error')?.textContent).toContain('Review the stored scope.');
    desktop.set(true);
    fixture.detectChanges();
    expect(modeTrigger(root)?.textContent).toContain('Disabled');
    expect(root.querySelector('textarea')?.value).toBe(
      'Giữ nguyên bản nháp trong khi đổi kích thước',
    );
    expect(emitted).toEqual([]);
    field(root, 'save')?.click();
    expect(emitted).toEqual([
      { mode: 'disabled', reason: 'Giữ nguyên bản nháp trong khi đổi kích thước' },
    ]);
  });

  it.each([{ value: 'unavailable' }, { value: '' }, { value: null }, { value: ['enabled'] }])(
    'refuses an unsupported Helm value: $value',
    ({ value }) => {
      const { fixture, root, emitted } = mount({
        scope: 'tenant',
        row: switchRow('tenant', 'shadow'),
        editable: true,
      });
      changeMode(fixture, value);
      fixture.detectChanges();
      field(root, 'save')?.click();
      expect(emitted).toEqual([{ mode: 'shadow', reason: null }]);
    },
  );
});
