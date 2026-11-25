import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import type { ComponentFixture } from '@angular/core/testing';

import { stubScopeBrowserApis } from '../../shell/layout/scope.fixture';
import { switchRow } from './healing-switch.test-support';
import { SwitchScopeControl } from './switch-scope-control.component';
import {
  ModeHarness,
  enterReason,
  field,
  modeTrigger,
  mount,
} from './switch-scope-control.test-support';

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

  describe('narrow containment', () => {
    function openDisable(
      fixture: ComponentFixture<SwitchScopeControl>,
      desktop: { set(value: boolean): void },
    ): void {
      desktop.set(false);
      fixture.detectChanges();
      field(fixture.nativeElement as HTMLElement, 'disable')?.click();
      fixture.detectChanges();
    }

    function confirmButton(): HTMLElement | null {
      return document.querySelector<HTMLElement>('[data-healing-confirm]');
    }

    it('offers the containment disable only at narrow width and hides the mode editor and save', () => {
      const { fixture, root, desktop } = mount({
        scope: 'tenant',
        row: switchRow('tenant', 'shadow'),
        editable: true,
      });
      expect(field(root, 'disable')).toBeNull();

      desktop.set(false);
      fixture.detectChanges();

      expect(field(root, 'disable')).not.toBeNull();
      expect(modeTrigger(root)).toBeNull();
      expect(field(root, 'save')).toBeNull();
    });

    it('does not offer containment for an already-disabled scope', () => {
      const { fixture, root, desktop } = mount({
        row: switchRow('tenant', 'disabled'),
        editable: true,
      });
      desktop.set(false);
      fixture.detectChanges();
      expect(field(root, 'disable')).toBeNull();
      desktop.set(true);
      fixture.detectChanges();
      expect(field(root, 'save')).not.toBeNull();
    });

    it.each(['desktop-only note', 'a'.repeat(2001)])(
      'preserves the desktop reason without blocking or including it in containment',
      async (draft) => {
        const { fixture, root, desktop, emitted } = mount({ editable: true });
        enterReason(root, draft);
        openDisable(fixture, desktop);
        const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, ModeHarness);
        await (await harness.confirm()).click();
        expect(emitted).toEqual([
          { mode: 'disabled', reason: null, action: 'healing-kill-switch' },
        ]);
        desktop.set(true);
        fixture.detectChanges();
        expect(root.querySelector('textarea')?.value).toBe(draft);
      },
    );

    it('opens an alertdialog with Cancel focused and sends nothing until the operator confirms', async () => {
      const { fixture, emitted, desktop } = mount({
        scope: 'tenant',
        row: switchRow('tenant', 'shadow'),
        editable: true,
      });
      openDisable(fixture, desktop);
      await fixture.whenStable();

      const dialog = document.querySelector('[role="alertdialog"]');
      expect(dialog?.querySelector('h2')?.textContent).toContain('Tenant');
      expect(dialog?.textContent).toContain('all projects in this tenant');
      expect(document.activeElement).toBe(document.querySelector('[data-healing-cancel]'));
      expect(emitted).toEqual([]);

      const confirm = confirmButton();
      confirm?.click();
      confirm?.click();
      expect(emitted).toEqual([{ mode: 'disabled', reason: null, action: 'healing-kill-switch' }]);
    });

    it('cancels on Escape with no request', async () => {
      const { fixture, emitted, desktop } = mount({
        scope: 'tenant',
        row: switchRow('tenant', 'shadow'),
        editable: true,
      });
      openDisable(fixture, desktop);
      await fixture.whenStable();

      const staleConfirm = confirmButton();
      document.activeElement?.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
      );
      staleConfirm?.click();
      fixture.detectChanges();
      await fixture.whenStable();

      expect(emitted).toEqual([]);
    });

    it('returns focus to the containment trigger after Escape and after Cancel', async () => {
      const { fixture, root, desktop } = mount({
        scope: 'tenant',
        row: switchRow('tenant', 'shadow'),
        editable: true,
      });
      openDisable(fixture, desktop);
      await fixture.whenStable();
      document.activeElement?.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
      );
      fixture.detectChanges();
      await fixture.whenStable();
      expect(document.activeElement).toBe(field(root, 'disable'));

      openDisable(fixture, desktop);
      await fixture.whenStable();
      document.querySelector<HTMLElement>('[data-healing-cancel]')?.click();
      fixture.detectChanges();
      await fixture.whenStable();
      expect(document.activeElement).toBe(field(root, 'disable'));
    });

    it('moves focus to the scope heading when a resize removed the containment trigger', async () => {
      const { fixture, root, desktop } = mount({
        scope: 'tenant',
        row: switchRow('tenant', 'shadow'),
        editable: true,
      });
      openDisable(fixture, desktop);
      await fixture.whenStable();
      desktop.set(true);
      fixture.detectChanges();
      document.querySelector<HTMLElement>('[data-healing-cancel]')?.click();
      fixture.detectChanges();
      await fixture.whenStable();
      expect(field(root, 'disable')).toBeNull();
      expect(document.activeElement).toBe(root.querySelector('#healing-switch-tenant-title'));
    });

    it('moves focus to the scope heading after a confirmed containment, not to the body', async () => {
      const { fixture, root, emitted, desktop } = mount({
        scope: 'tenant',
        row: switchRow('tenant', 'shadow'),
        editable: true,
      });
      openDisable(fixture, desktop);
      await fixture.whenStable();
      confirmButton()?.click();
      fixture.detectChanges();
      await fixture.whenStable();
      expect(emitted).toHaveLength(1);
      expect(document.activeElement).not.toBe(document.body);
      expect(document.activeElement).toBe(root.querySelector('#healing-switch-tenant-title'));
    });

    it('re-checks the kill-switch id at confirm time and sends nothing once it is revoked', () => {
      const { fixture, emitted, desktop, killSwitchAllowed } = mount({
        scope: 'tenant',
        row: switchRow('tenant', 'shadow'),
        editable: true,
      });
      openDisable(fixture, desktop);

      killSwitchAllowed.set(false);
      confirmButton()?.click();

      expect(emitted).toEqual([]);
    });

    it('still sends the confirmed disable when the viewport widens mid-dialog', () => {
      const { fixture, emitted, desktop } = mount({
        scope: 'tenant',
        row: switchRow('tenant', 'shadow'),
        editable: true,
      });
      openDisable(fixture, desktop);

      desktop.set(true);
      fixture.detectChanges();
      confirmButton()?.click();

      expect(emitted).toEqual([{ mode: 'disabled', reason: null, action: 'healing-kill-switch' }]);
    });
  });
});
