import { InteractivityChecker } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { injectBrnDialogContext } from '@spartan-ng/brain/dialog';
import { HlmDialogClose } from './lib/hlm-dialog-close';
import { HlmDialogService } from './lib/hlm-dialog.service';

@Component({
  selector: 'app-dialog-template-host',
  imports: [HlmDialogClose],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ng-template #content let-context>
      <p>{{ label() }}: {{ context.message }}</p>
      <button class="accept" (click)="context.close('accepted')" i18n="@@dialog.testAccept">
        Accept
      </button>
      <button class="dismiss" hlmDialogClose i18n="@@dialog.testDismiss">Dismiss</button>
    </ng-template>
  `,
})
class DialogTemplateHost {
  readonly content = viewChild.required<TemplateRef<unknown>>('content');
  readonly label = signal('Template content');
}

@Component({
  selector: 'app-dialog-component-content',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<p>{{ context.message }}</p>',
})
class DialogComponentContent {
  readonly context = injectBrnDialogContext<{ message: string }>();
}

describe('HlmDialogService', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DialogTemplateHost, DialogComponentContent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    // jsdom has no layout; preserve CDK focus behavior apart from visibility detection.
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  function dialog(): HTMLElement {
    const root = document.querySelector<HTMLElement>('[role="dialog"]');
    if (!root) throw new Error('Expected an open dialog');
    return root;
  }

  function button(selector: string): HTMLButtonElement {
    const element = dialog().querySelector<HTMLButtonElement>(selector);
    if (!element) throw new Error(`Expected dialog button ${selector}`);
    return element;
  }

  it('renders a real TemplateRef with host bindings, implicit context and close results', async () => {
    const fixture = TestBed.createComponent(DialogTemplateHost);
    await fixture.whenStable();
    const ref = TestBed.inject(HlmDialogService).open<string>(fixture.componentInstance.content(), {
      context: { message: 'Review changes' },
      contentClass: 'template-content',
      closeLabel: 'Close review',
      ariaLabel: 'Review changes',
    });
    const closed = vi.fn();
    ref.closed$.subscribe(closed);
    await fixture.whenStable();

    expect(dialog().textContent).toContain('Template content: Review changes');
    expect(
      dialog().querySelector('hlm-dialog-content')?.classList.contains('template-content'),
    ).toBe(true);
    expect(button('.spartan-dialog-close').textContent).toContain('Close review');
    fixture.componentInstance.label.set('Updated content');
    await fixture.whenStable();
    expect(dialog().textContent).toContain('Updated content: Review changes');

    button('.accept').click();
    await fixture.whenStable();
    expect(closed).toHaveBeenCalledWith('accepted');
    expect(document.querySelector('[role="dialog"]')).toBeNull();
  });

  it('provides the dialog injector to template close directives without the built-in close button', async () => {
    const fixture = TestBed.createComponent(DialogTemplateHost);
    await fixture.whenStable();
    const ref = TestBed.inject(HlmDialogService).open(fixture.componentInstance.content(), {
      context: { message: 'Dismiss changes' },
      showCloseButton: false,
      ariaLabel: 'Dismiss changes',
    });
    const closed = vi.fn();
    ref.closed$.subscribe(closed);
    await fixture.whenStable();

    expect(dialog().querySelector('.spartan-dialog-close')).toBeNull();
    button('.dismiss').click();
    await fixture.whenStable();
    expect(closed).toHaveBeenCalledTimes(1);
    expect(document.querySelector('[role="dialog"]')).toBeNull();
  });

  it('continues rendering component content with injected context and built-in dismissal', async () => {
    const fixture = TestBed.createComponent(DialogTemplateHost);
    await fixture.whenStable();
    TestBed.inject(HlmDialogService).open(DialogComponentContent, {
      context: { message: 'Component content' },
      ariaLabel: 'Component content',
    });
    await fixture.whenStable();

    expect(dialog().querySelector('app-dialog-component-content')?.textContent).toBe(
      'Component content',
    );
    button('.spartan-dialog-close').click();
    await fixture.whenStable();
    expect(document.querySelector('[role="dialog"]')).toBeNull();
  });
});
