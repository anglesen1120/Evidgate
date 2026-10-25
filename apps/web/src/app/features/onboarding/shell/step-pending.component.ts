import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-onboarding-step-pending',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pending" data-testid="onboarding-step-pending">
      <p>{{ unavailable }}</p>
      <p>{{ retained }}</p>
    </section>
  `,
  styles: `
    .pending {
      padding: var(--eg-space-7);
      border: var(--eg-border-width) dashed var(--eg-border);
      border-radius: var(--eg-radius-md);
      background: var(--eg-surface);
      color: var(--eg-text-muted);
      font-size: var(--eg-text-sm);
      line-height: var(--eg-leading-sm);
    }
    p {
      margin: var(--eg-space-0);
    }
    p + p {
      margin-block-start: var(--eg-space-3);
    }
  `,
})
export class StepPendingComponent {
  protected readonly unavailable = $localize`:@@onboarding.pending.title:This step is not available yet`;
  protected readonly retained = $localize`:@@onboarding.pending.description:Your setup progress is read from the server. Viewing a step does not complete it.`;
}
