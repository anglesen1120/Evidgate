import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { HlmTableImports } from '@evidgate/ui/table';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import {
  EmptyPatternComponent,
  ForbiddenPatternComponent,
  InlineFeedbackComponent,
  LoadingPatternComponent,
  NotFoundPatternComponent,
} from '../../../shared/ui/states';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { copy, notices } from './deploy-targets-review.copy';
import {
  deployTargetsFixture,
  deployStates,
  type DeployState,
  type DeployTargetsViewModel,
} from './deploy-targets-review.fixture';
// This review-only page uses a snapshot so it cannot change workspace records.
// The behavior implementation will replace the fixture with its scoped data service.
@Component({
  selector: 'app-deploy-targets-review',
  imports: [
    HlmButton,
    HlmCard,
    HlmCardTitle,
    HlmDialogImports,
    HlmIcon,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    HlmTableImports,
    AbsoluteTimeComponent,
    StatusPillComponent,
    PageHeaderComponent,
    EmptyPatternComponent,
    ForbiddenPatternComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    NotFoundPatternComponent,
    HlmCheckbox,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  templateUrl: './deploy-targets-review.page.html',
})
export class DeployTargetsReviewPage {
  private readonly route = inject(ActivatedRoute);
  protected readonly viewport = inject(NarrowMutationPolicy);
  readonly viewModel = input<DeployTargetsViewModel>(deployTargetsFixture);
  private readonly query = toSignal(this.route.queryParamMap);
  protected readonly copy = copy;
  protected readonly validationHint = notices.validation.title;
  protected readonly displayKey = computed(() =>
    this.state() === 'long' && this.key() === 'production'
      ? 'production-eu-west-checkout-primary-with-intentionally-long-name'
      : this.key(),
  );
  protected readonly state = linkedSignal<DeployState>(() => {
    const p = this.query();
    const raw = p?.get('state');
    const release = p?.get('release');
    if (raw === '403' || raw === 'denied') return 'denied';
    if (
      (p?.has('target') && !this.viewModel().targets.some((t) => t.key === p.get('target'))) ||
      (p?.has('project') &&
        ![this.viewModel().projectRef, this.viewModel().project].includes(
          p.get('project') ?? '',
        )) ||
      (release && release !== 'all' && !this.viewModel().releases.includes(release)) ||
      (release && p?.has('target') && p.get('target') !== 'production')
    )
      return '404';
    return deployStates.find((s) => s === raw) ?? this.viewModel().state;
  });
  protected readonly visible = computed(
    () => !['loading', 'empty', 'denied', '401', '404', '429'].includes(this.state()),
  );
  protected readonly preview = linkedSignal(() => this.query()?.get('section') === 'admin-preview');
  protected readonly key = linkedSignal(
    () => this.query()?.get('target') ?? this.viewModel().selectedKey,
  );
  protected readonly target = computed(() =>
    this.viewModel().targets.find((t) => t.key === this.key()),
  );
  protected readonly notice = computed(
    () => Object.entries(notices).find(([s]) => s === this.state())?.[1],
  );
  protected readonly release = linkedSignal(() =>
    this.state() === 'no-match' ? 'rel-2026.10.11' : (this.query()?.get('release') ?? 'all'),
  );
  protected readonly releaseLabel = (v: unknown) =>
    typeof v === 'string' && this.viewModel().releases.includes(v) ? v : copy.all;
  protected readonly history = computed(() =>
    this.state() === 'empty-history'
      ? []
      : this.viewModel().checks.filter((c) => c.targetKey === this.key()),
  );
  protected readonly visibleChecks = computed(() =>
    this.history().filter((c) => this.release() === 'all' || c.release === this.release()),
  );
  protected readonly metrics = computed(() => {
    const { targets, checks, project } = this.viewModel();
    const pins = [...new Set(targets.map((t) => t.policy).filter((p) => p !== null))];
    const partial = ['empty-history', 'error'].includes(this.state());
    return [
      { title: copy.ordered, value: targets.length, hint: project, icon: 'lucideActivity' },
      {
        title: copy.policies,
        value: pins.length,
        hint: pins.join(' · '),
        icon: 'lucideCircleCheck',
      },
      {
        title: copy.recorded,
        value: partial ? null : checks.length,
        hint: copy.productionHistory,
        icon: 'lucideActivity',
      },
      {
        title: copy.pendingChecks,
        value: partial ? null : checks.filter((c) => c.result === 'pending').length,
        hint: copy.noEvaluation,
        icon: 'lucideClock',
      },
    ] as const;
  });
  protected readonly tones = { blocked: 'danger', pending: 'neutral', allowed: 'success' } as const;
  protected readonly canEdit = computed(
    () =>
      this.preview() &&
      this.viewport.canMutate() &&
      this.key() === 'production' &&
      ['ready', 'validation', 'success'].includes(this.state()),
  );
  protected readonly editing = linkedSignal({
    source: () => ({ target: this.key(), state: this.state(), editable: this.canEdit() }),
    computation: () => false,
  });
  protected readonly order = signal<number | null>(null);
  protected readonly pin = signal('');
  protected readonly acknowledged = signal(false);
  protected open() {
    if (!this.canEdit() || !this.viewport.allowsMutation()) return;
    this.order.set(this.state() === 'validation' ? 0 : (this.target()?.order ?? null));
    this.pin.set(this.target()?.policy ?? '');
    this.acknowledged.set(false);
    this.editing.set(true);
  }
  protected readonly validOrder = computed(() => {
    const n = this.order();
    return n !== null && Number.isInteger(n) && n >= 1 && n <= 20;
  });
  protected confirm() {
    if (
      !this.canEdit() ||
      !this.viewport.allowsMutation() ||
      !this.editing() ||
      !this.validOrder() ||
      !this.pin().trim() ||
      !this.acknowledged()
    )
      return;
    this.editing.set(false);
    this.state.set('success');
  }
  protected setRelease(value: unknown) {
    this.release.set(
      typeof value === 'string' && this.viewModel().releases.includes(value) ? value : 'all',
    );
  }
}
