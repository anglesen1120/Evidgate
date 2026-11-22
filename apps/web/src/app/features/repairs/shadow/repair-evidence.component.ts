import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmTableImports } from '@evidgate/ui/table';
import { StatusPillComponent } from '../../../shared/ui/chips';
import { InlineFeedbackComponent } from '../../../shared/ui/states';
import {
  DecisionChipComponent,
  EvidenceBadgeComponent,
  OutcomePillComponent,
} from '../../../shared/ui/verdict';
import { REPAIR } from './repair-review.fixture';
import type { RepairReviewContext } from './repair-review-context';
import { SHADOW_COPY } from './shadow.copy';

type EvidencePanel = 'original' | 'diff' | 'result' | 'validation' | 'provenance';

@Component({
  selector: 'app-repair-evidence',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  imports: [
    RouterLink,
    HlmButton,
    HlmCardImports,
    HlmTableImports,
    StatusPillComponent,
    InlineFeedbackComponent,
    DecisionChipComponent,
    EvidenceBadgeComponent,
    OutcomePillComponent,
  ],
  templateUrl: './repair-evidence.component.html',
  styleUrl: './repair-panels.css',
})
export class RepairEvidenceComponent {
  readonly panel = input.required<EvidencePanel>();
  readonly context = input.required<RepairReviewContext>();
  protected readonly copy = SHADOW_COPY;
  protected readonly record = REPAIR;
  protected readonly checks = [
    [this.copy.semantics, this.copy.role, this.copy.passed],
    [this.copy.match, '1', this.copy.passed],
    [this.copy.region, 'checkout', this.copy.passed],
    [this.copy.postcondition, this.copy.reachable, this.copy.passed],
    [this.copy.hashCheck, REPAIR.assertionHash, this.copy.notSufficient],
    [this.copy.control, this.copy.broken, this.copy.expected],
  ] as const;
  protected readonly validationNote = computed(() => {
    const state = this.context().state();
    switch (state) {
      case 'missing':
      case 'negative':
      case 'stale':
      case 'expired':
      case 'proposed':
      case 'running':
        return this.copy[state];
      default:
        return this.copy.noCurrent;
    }
  });
}
