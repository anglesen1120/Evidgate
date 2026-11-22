import { ChangeDetectionStrategy, Component, computed, inject, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardContent, HlmCardHeader } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';

import { WaiverBanner } from '../../../shared/ui/annotations';
import { PageHeaderImports, SectionHeaderComponent } from '../../../shared/ui/page-header';
import {
  EmptyPatternComponent,
  InlineFeedbackComponent,
  LoadingPatternComponent,
} from '../../../shared/ui/states';
import {
  DecisionChipComponent,
  EvidenceBadgeComponent,
  OutcomePillComponent,
} from '../../../shared/ui/verdict';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { ReleaseBindingMatrixComponent } from './release-binding-matrix.component';
import { ReleaseEvidenceContextComponent } from './release-evidence-context.component';
import { RELEASE_COPY } from './release-view-copy';
import { releaseNotice, releaseState, showsReleaseSnapshot } from './release-view-state';
import {
  componentFilter,
  RELEASE_VIEW,
  type ComponentFilter,
  type ReleaseViewModel,
} from './release-view.fixture';

// Review only until release behavior lands; the real data service will replace this fixture.
@Component({
  selector: 'app-release-view-review',
  imports: [
    HlmButton,
    HlmCard,
    HlmCardContent,
    HlmCardHeader,
    HlmIcon,
    ShellIconComponent,
    PageHeaderImports,
    SectionHeaderComponent,
    EmptyPatternComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    WaiverBanner,
    DecisionChipComponent,
    EvidenceBadgeComponent,
    OutcomePillComponent,
    ReleaseBindingMatrixComponent,
    ReleaseEvidenceContextComponent,
  ],
  host: { class: 'grid min-w-0 gap-(--eg-space-6) wrap-anywhere' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './release-view-review.page.html',
})
export class ReleaseViewReviewPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly params = toSignal(this.route.queryParamMap, { requireSync: true });
  protected readonly view: ReleaseViewModel = RELEASE_VIEW;
  protected readonly copy = RELEASE_COPY;
  protected readonly state = computed(() => releaseState(this.params().get('state')));
  protected readonly filter = computed(() => componentFilter(this.params().get('component')));
  protected readonly notice = computed(() => releaseNotice(this.state()));
  protected readonly snapshot = computed(() => showsReleaseSnapshot(this.state()));
  protected readonly context = viewChild(ReleaseEvidenceContextComponent);
  protected readonly displayScope = computed(
    () => !['401', '403', '404', 'early'].includes(this.state()),
  );

  protected selectFilter(value: ComponentFilter): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParamsHandling: 'merge',
      queryParams: {
        component: value === 'all' ? null : value,
        state: this.state() === 'no-match' ? null : this.state(),
      },
    });
  }

  protected showFailure(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParamsHandling: 'merge',
      queryParams: { state: 'failure' },
    });
  }
}
