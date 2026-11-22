import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { InlineFeedbackComponent } from '../../../shared/ui/states';
import { DETAIL_COPY as c } from './failure-detail.copy';
import { checkout, checkoutEvidence, detailText } from './failure-detail.fixture';

@Component({
  selector: 'app-failure-evidence',
  imports: [RouterLink, HlmButton, HlmCardImports, InlineFeedbackComponent, StatusPillComponent],
  templateUrl: './failure-evidence.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-(--eg-space-4)' },
})
export class FailureEvidenceComponent {
  readonly state = input.required<string>();
  readonly artifactPath = input.required<readonly (string | null | undefined)[]>();
  readonly historyPath = input.required<readonly (string | null | undefined)[]>();
  readonly clearFilter = output<void>();
  protected readonly c = c;
  protected readonly r = checkout;
  protected readonly ev = checkoutEvidence;
  protected readonly text = detailText;
}
