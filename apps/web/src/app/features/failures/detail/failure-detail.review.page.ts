import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { combineLatest } from 'rxjs';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { PageHeaderImports } from '../../../shared/ui/page-header';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import {
  EmptyPatternComponent,
  LoadingPatternComponent,
  InlineFeedbackComponent,
  NotFoundPatternComponent,
} from '../../../shared/ui/states';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { DETAIL_COPY as c } from './failure-detail.copy';
import { address, checkout, detailNotice, detailText } from './failure-detail.fixture';
import { FailureEvidenceComponent } from './failure-evidence.component';
import { TriageReviewComponent } from './triage-review.component';

// Review-only: this evidence snapshot must not submit a real triage decision.
// The behavior task will replace the fixture with its project-scoped data service.
@Component({
  selector: 'app-failure-detail-review',
  imports: [
    RouterLink,
    HlmButton,
    HlmCardImports,
    HlmIcon,
    PageHeaderImports,
    StatusPillComponent,
    EmptyPatternComponent,
    LoadingPatternComponent,
    InlineFeedbackComponent,
    NotFoundPatternComponent,
    RetryAfterComponent,
    FailureEvidenceComponent,
    TriageReviewComponent,
  ],
  templateUrl: './failure-detail.review.page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-(--eg-space-5) text-sm wrap-anywhere' },
})
export class FailureDetailReviewPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly params = toSignal(
    combineLatest(this.route.pathFromRoot.map((r) => r.paramMap)),
    { requireSync: true },
  );
  protected readonly query = toSignal(this.route.queryParamMap, { requireSync: true });
  protected readonly scope = computed(() => {
    const maps = this.params();
    return [
      'orgs',
      maps.find((p) => p.has('orgId'))?.get('orgId'),
      'projects',
      maps.find((p) => p.has('projectId'))?.get('projectId'),
    ];
  });
  protected readonly state = computed(() => this.query().get('state') ?? 'default');
  protected readonly c = c;
  protected readonly text = detailText;
  protected readonly id = computed(
    () => this.query().get('id') ?? (this.state() === 'eligible' ? address.id : checkout.id),
  );
  protected readonly record = computed(() =>
    this.id() === checkout.id ? checkout : this.id() === address.id ? address : null,
  );
  protected readonly notice = computed(() => detailNotice(this.state()));
  protected readonly tiles = computed(
    () =>
      [
        { label: c.completeness, value: c.incomplete, hint: c.collection, icon: 'lucideCircle' },
        { label: c.outcome, value: c.failed, hint: c.ci, icon: 'lucideOctagonX' },
        { label: c.rev, value: 'ev-3', hint: c.pinned, icon: 'lucideActivity' },
        { label: c.incident, value: this.id(), hint: c.incidentScope, icon: 'lucideInfo' },
      ] as const,
  );
  protected readonly inbox = computed(() => ['/dev/failure-inbox', ...this.scope()]);
  protected readonly history = computed(() => ['/dev/investigation-history', ...this.scope()]);
  protected readonly shadow = computed(() => ['/dev/shadow-detail', ...this.scope()]);
  protected readonly artifact = computed(() => ['/dev/artifact-viewer', ...this.scope()]);
  protected reload(state: 'latest' | 'default' = 'default'): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { state },
      queryParamsHandling: 'merge',
    });
  }
}
