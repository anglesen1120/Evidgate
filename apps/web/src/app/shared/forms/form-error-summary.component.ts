import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { HlmAlert, HlmAlertDescription, HlmAlertTitle } from '@evidgate/ui/alert';
import { focusRefusalHeading } from '../a11y/step-focus';
import type { FormFeedback } from './form-feedback';

@Component({
  selector: 'app-form-error-summary',
  imports: [HlmAlert, HlmAlertDescription, HlmAlertTitle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    @if (feedback().summary().length > 0) {
      <section
        hlmAlert
        #summary
        [id]="summaryId()"
        [variant]="'destructive'"
        role="region"
        tabindex="-1"
        [attr.aria-labelledby]="summaryId() + '-title'"
        [attr.aria-describedby]="summaryId() + '-errors'"
        class="min-w-0 wrap-anywhere focus:outline-[length:var(--eg-focus-width)] focus:outline-ring focus:outline-offset-[var(--eg-focus-offset)]"
      >
        <h2 hlmAlertTitle [id]="summaryId() + '-title'">{{ title }}</h2>
        <ul
          hlmAlertDescription
          [id]="summaryId() + '-errors'"
          class="m-0 grid gap-[var(--eg-space-3)] pl-[var(--eg-space-7)]"
        >
          @for (error of feedback().summary(); track error.id) {
            <li>
              <a
                [href]="'#' + error.id"
                class="inline-flex min-h-[var(--eg-space-7)] items-center text-inherit underline underline-offset-2 hover:no-underline focus-visible:outline-[length:var(--eg-focus-width)] focus-visible:outline-ring focus-visible:outline-offset-[var(--eg-focus-offset)] [@media(pointer:coarse)]:min-h-[var(--eg-control-touch)]"
                (click)="focusField($event, error.id)"
                >{{ error.label }}: {{ error.message }}</a
              >
            </li>
          }
        </ul>
      </section>
    }
  `,
})
export class FormErrorSummaryComponent {
  private readonly document = inject(DOCUMENT);

  readonly summaryId = input.required<string>();
  readonly feedback = input.required<FormFeedback>();
  protected readonly title = $localize`:@@shared.forms.error-summary.title:There is a problem`;
  private readonly summary = viewChild<ElementRef<HTMLElement>>('summary');
  private readonly focusRequested = signal(false);
  private seenAttempt = 0;

  constructor() {
    let previousFeedback: FormFeedback | undefined;
    // Only an explicit failed submit owes focus; editing a correction must never move the visitor.
    effect(() => {
      const feedback = this.feedback();
      const attempt = feedback.submitAttempts();
      if (previousFeedback === feedback && attempt === this.seenAttempt) return;
      previousFeedback = feedback;
      this.seenAttempt = attempt;
      this.focusRequested.set(attempt > 0 && feedback.summary().length > 0);
    });
    focusRefusalHeading({
      request: this.focusRequested,
      heading: this.summary,
      shown: () => this.feedback().summary().length > 0,
      afterRender: true,
    });
  }

  protected focusField(event: MouseEvent, id: string): void {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey)
      return;
    const target = this.document.getElementById(id);
    if (!target) return;
    event.preventDefault();
    target.focus();
  }
}
