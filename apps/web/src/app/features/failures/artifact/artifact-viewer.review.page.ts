import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { combineLatest } from 'rxjs';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmTableImports } from '@evidgate/ui/table';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { PageHeaderImports } from '../../../shared/ui/page-header';
import {
  EmptyPatternComponent,
  InlineFeedbackComponent,
  LoadingPatternComponent,
  NotFoundPatternComponent,
} from '../../../shared/ui/states';
import { DecisionChipComponent } from '../../../shared/ui/verdict/decision-chip.component';
import { ARTIFACT_COPY } from './artifact-viewer.copy';
import { artifactSnapshot, artifacts, checkout, sandbox } from './artifact-viewer.fixture';
import { ArtifactPreviewComponent } from './artifact-preview.component';

// Review-only until the viewer behaviour lands. Its data service will replace these pinned
// metadata fixtures; live bytes belong on the separate viewer origin, never in this page.
@Component({
  selector: 'app-artifact-viewer-review',
  imports: [
    RouterLink,
    HlmButton,
    HlmCardImports,
    HlmIcon,
    HlmTableImports,
    AbsoluteTimeComponent,
    StatusPillComponent,
    PageHeaderImports,
    EmptyPatternComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    NotFoundPatternComponent,
    DecisionChipComponent,
    ArtifactPreviewComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'grid min-w-0 gap-4 text-sm wrap-anywhere [&_dl]:grid [&_dl]:gap-4 [&_dt]:text-xs [&_dt]:text-muted-foreground [&_dd]:m-0 [&_dd]:font-medium',
  },
  templateUrl: './artifact-viewer.review.page.html',
})
export class ArtifactViewerReviewPage {
  private readonly route = inject(ActivatedRoute);
  private readonly query = toSignal(this.route.queryParamMap);
  private readonly params = toSignal(combineLatest(this.route.pathFromRoot.map((r) => r.paramMap)));
  protected path(screen: string): string[] {
    const params = this.params() ?? [];
    const org = params.find((p) => p.has('orgId'))?.get('orgId');
    const project = params.find((p) => p.has('projectId'))?.get('projectId');
    return org && project ? ['/dev', screen, 'orgs', org, 'projects', project] : [];
  }
  protected readonly c = ARTIFACT_COPY;
  protected readonly official = checkout;
  protected readonly related = artifacts;
  protected readonly studio = sandbox;
  protected readonly isStudio = computed(() => this.query()?.get('id') === sandbox.id);
  protected readonly snapshot = computed(() =>
    artifactSnapshot(this.query()?.get('id') ?? null, this.query()?.get('state') ?? null),
  );
}
