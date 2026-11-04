import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { HlmSelectImports } from '../index';

@Component({
  selector: 'app-select-trigger-test',
  imports: [HlmSelectImports],
  template: `
    <label for="decision-trigger" i18n="@@selectTriggerTest.decision">Decision</label>
    <hlm-select [value]="selected()" (valueChange)="selected.set($event)">
      <hlm-select-trigger [buttonId]="'decision-trigger'" [aria-describedby]="description()">
        <hlm-select-value />
      </hlm-select-trigger>
      <hlm-select-content *hlmSelectPortal [aria-label]="'Decision'" class="animate-none">
        <hlm-select-item [value]="'pending'" i18n="@@selectTriggerTest.pending"
          >Not evaluated</hlm-select-item
        >
        <hlm-select-item [value]="'PASS'" i18n="@@selectTriggerTest.pass">PASS</hlm-select-item>
      </hlm-select-content>
    </hlm-select>
    <p id="decision-hint" i18n="@@selectTriggerTest.noEvaluation">No live evaluation yet.</p>
  `,
})
class SelectTriggerTestHost {
  readonly description = signal<string | null>('decision-hint');
  readonly selected = signal<unknown>('pending');
}

describe('HlmSelectTrigger', () => {
  provideLayoutTestStubs();
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SelectTriggerTestHost],
      providers: [provideZonelessChangeDetection()],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  async function render() {
    const fixture = TestBed.createComponent(SelectTriggerTestHost);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const trigger = root.querySelector<HTMLButtonElement>('#decision-trigger');
    if (!trigger) throw new Error('Missing select trigger');
    return { fixture, root, trigger };
  }

  it('associates and removes description text on the focusable button, not just the host', async () => {
    const { fixture, root, trigger } = await render();
    expect(trigger.getAttribute('aria-describedby')).toBe('decision-hint');
    expect(root.querySelector('#decision-hint')?.textContent).toBe('No live evaluation yet.');
    expect(trigger.labels?.item(0)?.textContent).toBe('Decision');
    fixture.componentInstance.description.set(null);
    await fixture.whenStable();
    expect(trigger.hasAttribute('aria-describedby')).toBe(false);
  });

  it('retains keyboard opening and Escape dismissal with the described trigger focused', async () => {
    const { fixture, trigger } = await render();
    trigger.focus();
    trigger.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }),
    );
    await fixture.whenStable();
    expect(trigger.getAttribute('aria-expanded')).toBe('true');
    expect(document.querySelector('[role="listbox"]')?.getAttribute('aria-label')).toBe('Decision');
    trigger.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    await fixture.whenStable();
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(trigger);
    expect(fixture.componentInstance.selected()).toBe('pending');
    expect(trigger.getAttribute('aria-describedby')).toBe('decision-hint');
  });
});
