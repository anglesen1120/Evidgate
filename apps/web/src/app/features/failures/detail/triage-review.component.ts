import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmSelectImports } from '@evidgate/ui/select';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import {
  DesktopMutationDirective,
  DesktopRequiredNoticeComponent,
  NarrowMutationPolicy,
} from '../../../shared/ui/narrow';
import { InlineFeedbackComponent } from '../../../shared/ui/states';
import { DETAIL_COPY as c } from './failure-detail.copy';
import { checkout, detailText } from './failure-detail.fixture';
import { closureReasons, owners, triageLabels, TriageReviewStore } from './triage-review.store';

@Component({
  selector: 'app-triage-review',
  providers: [TriageReviewStore],
  imports: [
    HlmButton,
    HlmCardImports,
    HlmDialogImports,
    HlmInput,
    HlmSelectImports,
    StatusPillComponent,
    DesktopMutationDirective,
    DesktopRequiredNoticeComponent,
    InlineFeedbackComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './triage-review.component.html',
})
export class TriageReviewComponent {
  private readonly viewport = inject(NarrowMutationPolicy);
  protected readonly store = inject(TriageReviewStore);
  readonly state = input.required<string>();
  readonly classification = input.required<string>();
  protected readonly c = c;
  protected readonly text = detailText;
  protected readonly labels = triageLabels;
  protected readonly owners = owners;
  protected readonly reasons = closureReasons;
  protected readonly confirmed = computed(() => ['success', 'closed'].includes(this.state()));
  protected readonly writable = computed(
    () => !['403', '409', '412', 'closed', 'submitting', 'pending'].includes(this.state()),
  );
  protected readonly revision = computed(() =>
    this.confirmed() || this.state() === 'latest' ? '8' : '7',
  );
  protected readonly description = computed(
    () => `${detailText.previewDescription} ${this.revision()}`,
  );
  protected readonly previewSummary = computed(() => {
    const preview = this.store.preview();
    if (!preview) return '';
    return 'closure' in preview
      ? `${checkout.id} · ${preview.closure}`
      : `${checkout.id} · ${preview.label} · ${preview.owner}`;
  });
  protected showPreview(close: boolean): void {
    if (!this.writable() || !this.viewport.allowsMutation()) return;
    if (!close) this.store.previewTriage();
    else if (this.confirmed()) this.store.previewClosure();
  }
}
