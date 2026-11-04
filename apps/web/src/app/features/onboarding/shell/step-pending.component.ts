import { ChangeDetectionStrategy, Component } from '@angular/core';
import { HlmAlertImports } from '@evidgate/ui/alert';

@Component({
  selector: 'app-onboarding-step-pending',
  imports: [HlmAlertImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section hlmAlert role="note" class="border-dashed" data-testid="onboarding-step-pending">
      <p hlmAlertTitle>{{ unavailable }}</p>
      <p hlmAlertDescription>{{ retained }}</p>
    </section>
  `,
})
export class StepPendingComponent {
  protected readonly unavailable = $localize`:@@onboarding.pending.title:This step is not available yet`;
  protected readonly retained = $localize`:@@onboarding.pending.description:Your setup progress is read from the server. Viewing a step does not complete it.`;
}
