import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { combineLatest } from 'rxjs';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import {
  EmptyPatternComponent,
  InlineFeedbackComponent,
  LoadingPatternComponent,
  NotFoundPatternComponent,
} from '../../../shared/ui/states';
import { DecisionChipComponent } from '../../../shared/ui/verdict/decision-chip.component';
import { LINEAGE_COPY } from './knowledge-lineage.copy';
import { lineage, lineageStates, revoked, type LineageState } from './knowledge-lineage.fixture';
import { LineageDependentsComponent } from './lineage-dependents.component';
import { RevocationEffectComponent } from './revocation-effect.component';

// Review-only until the knowledge behaviour lands. Its data service will replace the local
// lineage and dependency snapshots; the confirmation here prepares no server mutation.
@Component({
  selector: 'app-knowledge-lineage-review',
  imports: [
    RouterLink,
    HlmButton,
    HlmCardImports,
    AbsoluteTimeComponent,
    StatusPillComponent,
    PageHeaderComponent,
    EmptyPatternComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    NotFoundPatternComponent,
    DecisionChipComponent,
    LineageDependentsComponent,
    RevocationEffectComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'grid min-w-0 gap-4 wrap-anywhere [&_dl]:grid [&_dl]:gap-4 [&_dt]:text-sm [&_dt]:text-muted-foreground [&_dd]:m-0',
  },
  templateUrl: './knowledge-lineage.review.page.html',
})
export class KnowledgeLineageReviewPage {
  private readonly route = inject(ActivatedRoute);
  private readonly query = toSignal(this.route.queryParamMap);
  private readonly params = toSignal(combineLatest(this.route.pathFromRoot.map((r) => r.paramMap)));
  protected path(screen: string): string[] {
    const params = this.params() ?? [];
    const org = params.find((p) => p.has('orgId'))?.get('orgId');
    const project = params.find((p) => p.has('projectId'))?.get('projectId');
    return org && project ? ['/dev', screen, 'orgs', org, 'projects', project] : [];
  }
  protected readonly c = LINEAGE_COPY;
  protected readonly record = lineage;
  protected readonly state = computed<LineageState>(() => {
    const id = this.query()?.get('id') ?? lineage.id;
    if (id !== lineage.id) return '404';
    const raw = this.query()?.get('state') ?? 'default';
    return lineageStates.find((s) => s === raw) ?? '404';
  });
  protected readonly blocked = computed(() => revoked(this.state()));
  protected readonly candidateState = computed(() =>
    this.blocked()
      ? 'revoked'
      : this.state() === 'ineligible'
        ? 'proposed'
        : this.state() === 'applied'
          ? 'applied'
          : 'approved',
  );
}
