import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestBed } from '@angular/core/testing';
import type { ComponentFixture } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import type { HealingSwitchView } from '@evidgate/sdk';

import type { ApiError } from '../../core/http/api-error';
import { SessionStore } from '../../core/session/session.store';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { SwitchScopeControl } from './switch-scope-control.component';

export class ModeHarness extends ComponentHarness {
  static hostSelector = 'app-switch-scope-control';
  readonly trigger = this.locatorFor('button[role="combobox"]');
  readonly options = this.documentRootLocatorFactory().locatorForAll('[role="option"]');
  readonly confirm = this.documentRootLocatorFactory().locatorFor(
    '[role="alertdialog"] [data-healing-confirm]',
  );

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

export function changeMode(fixture: ComponentFixture<SwitchScopeControl>, value: unknown): void {
  fixture.debugElement.query(By.css('hlm-select')).triggerEventHandler('valueChange', value);
}

export function modeTrigger(root: HTMLElement): HTMLButtonElement | null {
  return root.querySelector<HTMLButtonElement>('button[role="combobox"]');
}

export function mount(inputs: {
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
  const killSwitchAllowed = signal(true);
  const policy = {
    canMutate: desktop,
    narrow: () => !desktop(),
    allowsMutation: (action?: string) =>
      desktop() || (action === 'healing-kill-switch' && killSwitchAllowed()),
    canMutateFor: (action?: string) =>
      desktop() || (action === 'healing-kill-switch' && killSwitchAllowed()),
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
  const emitted: { mode: string; reason: string | null; action?: string | undefined }[] = [];
  fixture.componentInstance.submitted.subscribe((event) => emitted.push(event));
  return {
    fixture,
    root: fixture.nativeElement as HTMLElement,
    emitted,
    desktop,
    killSwitchAllowed,
  };
}

export function scopeCard(root: HTMLElement): HTMLElement {
  const element = root.querySelector<HTMLElement>('[data-healing-editor]');
  if (element === null) throw new Error('the scope card did not render');
  return element;
}

export function field(root: HTMLElement, hook: string): HTMLElement | null {
  return root.querySelector<HTMLElement>(`[data-healing-${hook}]`);
}

export function enterReason(root: HTMLElement, text: string): void {
  const reason = root.querySelector<HTMLTextAreaElement>('textarea');
  if (reason === null) throw new Error('the reason field did not render');
  reason.value = text;
  reason.dispatchEvent(new Event('input'));
}
