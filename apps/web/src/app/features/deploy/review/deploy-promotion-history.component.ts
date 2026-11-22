import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { HlmTableImports } from '@evidgate/ui/table';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { EmptyPatternComponent, InlineFeedbackComponent } from '../../../shared/ui/states';
import { copy, notices } from './deploy-targets-review.copy';
import type { PromotionCheckViewModel } from './deploy-targets-review.fixture';

@Component({
  selector: 'app-deploy-promotion-history',
  imports: [
    HlmLabel,
    HlmSelectImports,
    HlmTableImports,
    AbsoluteTimeComponent,
    StatusPillComponent,
    EmptyPatternComponent,
    InlineFeedbackComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-3 border-t border-border pt-4' },
  templateUrl: './deploy-promotion-history.component.html',
})
export class DeployPromotionHistoryComponent {
  readonly checks = input.required<readonly PromotionCheckViewModel[]>();
  readonly readError = input.required<boolean>();
  readonly releases = input.required<readonly string[]>();
  readonly release = model.required<string>();
  protected readonly copy = copy;
  protected readonly error = notices.error;
  protected readonly tones = { blocked: 'danger', pending: 'neutral', allowed: 'success' } as const;
  protected readonly columns = [
    copy.check,
    copy.release,
    copy.result,
    copy.decision,
    copy.policy,
    copy.reasons,
  ];
  protected readonly visibleChecks = computed(() =>
    this.release() === 'all'
      ? this.checks()
      : this.checks().filter((c) => c.release === this.release()),
  );
  protected readonly releaseLabel = (v: unknown) =>
    typeof v === 'string' && this.releases().includes(v) ? v : copy.all;

  protected setRelease(value: unknown): void {
    this.release.set(typeof value === 'string' && this.releases().includes(value) ? value : 'all');
  }
}
