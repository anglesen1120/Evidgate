import {
  ChangeDetectionStrategy,
  afterNextRender,
  ElementRef,
  Injector,
  viewChild,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink, type ParamMap } from '@angular/router';
import { combineLatest } from 'rxjs';
import { focusWaitingLine } from '../../../shared/a11y/step-focus';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmLabel } from '@evidgate/ui/label';
import {
  HlmSelect,
  HlmSelectContent,
  HlmSelectItem,
  HlmSelectPortal,
  HlmSelectTrigger,
  HlmSelectValue,
} from '@evidgate/ui/select';
import { StatusPillComponent } from '../../../shared/ui/chips';
import { DesktopRequiredNoticeComponent, NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { PageHeaderComponent } from '../../../shared/ui/page-header';
import {
  EmptyPatternComponent,
  ErrorPatternComponent,
  ForbiddenPatternComponent,
  LoadingPatternComponent,
  NotFoundPatternComponent,
} from '../../../shared/ui/states';
import {
  DecisionChipComponent,
  EvidenceBadgeComponent,
  OutcomePillComponent,
} from '../../../shared/ui/verdict';
import type { ApiError } from '../../../core/http/api-error';
import { MappingDecisionComponent } from './mapping-decision.component';
import { mappingCopy } from './mapping-resolution.copy';
import { mappingResolutionFixture } from './mapping-resolution.fixture';
import { mappingStates } from './mapping-review-states';
import type {
  MappingPreview,
  MappingResolutionViewModel,
  MappingRowViewModel,
} from './mapping-resolution.view-model';

type MappingFilter = 'all' | MappingRowViewModel['state'];
const filters: readonly { key: MappingFilter; label: string }[] = [
  { key: 'all', label: mappingCopy.all },
  { key: 'unmapped', label: mappingCopy.unmapped },
  { key: 'conflicted', label: mappingCopy.conflicted },
  { key: 'resolved', label: mappingCopy.history },
];

function requestedRecord(
  query: ParamMap | undefined,
  model: MappingResolutionViewModel,
): string | null {
  if (!query) return null;
  const scope: Record<string, readonly string[]> = {
    project: [model.project, model.projectKey],
    run: [model.run],
    evidence: [model.evidence],
    evaluation: [model.decision],
  };
  if (
    Object.entries(scope).some(
      ([key, values]) => query.has(key) && !values.includes(query.get(key) ?? ''),
    )
  )
    return 'unavailable';
  if (
    [
      'attempt',
      'artifact',
      'candidate',
      'incident',
      'object',
      'proposal',
      'release',
      'revision',
      'generated',
    ].some((key) => query.has(key))
  )
    return 'unavailable';
  const id = query.get('mapping');
  const requestedCase = query.get('case');
  if (requestedCase !== null) {
    const matching = model.rows.find(
      (row) =>
        row.state === 'conflicted' &&
        row.candidates.some((candidate) => candidate.id === requestedCase),
    );
    return matching && (id === null || id === matching.id) ? matching.id : 'unavailable';
  }
  return id === null ||
    model.rows.some((row) => row.id === id) ||
    model.imports.some((row) => row.id === id)
    ? id
    : 'unavailable';
}

// Review-only while mapping decisions are not wired. The behavior work replaces the
// frozen presentation fixture with the scoped data service.
@Component({
  selector: 'app-mapping-resolution-review',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    HlmButton,
    HlmCard,
    HlmCardTitle,
    HlmIcon,
    HlmLabel,
    HlmSelect,
    HlmSelectContent,
    HlmSelectItem,
    HlmSelectPortal,
    HlmSelectTrigger,
    HlmSelectValue,
    StatusPillComponent,
    DesktopRequiredNoticeComponent,
    PageHeaderComponent,
    EmptyPatternComponent,
    ErrorPatternComponent,
    ForbiddenPatternComponent,
    LoadingPatternComponent,
    NotFoundPatternComponent,
    DecisionChipComponent,
    EvidenceBadgeComponent,
    OutcomePillComponent,
    MappingDecisionComponent,
  ],
  host: { class: 'block min-w-0' },
  templateUrl: './mapping-resolution.page.html',
})
export class MappingResolutionPage {
  private readonly route = inject(ActivatedRoute);
  protected readonly router = inject(Router);
  protected readonly policy = inject(NarrowMutationPolicy);
  private readonly injector = inject(Injector);
  private readonly stateHeading = viewChild<ElementRef<HTMLElement>>('stateHeading');
  readonly viewModel = input<MappingResolutionViewModel>(mappingResolutionFixture);
  protected readonly copy = mappingCopy;
  protected readonly states = mappingStates;
  protected readonly filters = filters;
  private readonly query = toSignal(this.route.queryParamMap);
  private readonly ancestors = toSignal(
    combineLatest(this.route.pathFromRoot.map((route) => route.paramMap)),
  );
  protected readonly catalog = computed(() => {
    const maps = this.ancestors() ?? [];
    const org = maps.find((params) => params.has('orgId'))?.get('orgId');
    const project = maps.find((params) => params.has('projectId'))?.get('projectId');
    return org && project ? ['/dev/catalog-list', 'orgs', org, 'projects', project] : null;
  });
  protected readonly exact = computed(() => requestedRecord(this.query(), this.viewModel()));
  protected readonly imported = computed(() =>
    this.viewModel().imports.find((row) => row.id === this.exact()),
  );
  private readonly requestedState = computed(() => {
    const raw = this.query()?.get('state');
    return raw === 'success'
      ? 'mapped'
      : (this.states.find((item) => item.key === raw)?.key ?? this.viewModel().state);
  });
  protected readonly state = linkedSignal(() => this.requestedState());
  protected readonly sheet = computed(
    () => this.states.find((value) => value.key === this.state()) ?? this.states[0],
  );
  protected readonly visibleQueue = computed(
    () => this.state() === 'ready' || this.state() === 'readonly',
  );
  protected readonly filter = linkedSignal<MappingFilter>(() => {
    const linked = this.viewModel().rows.find((row) => row.id === this.exact());
    const requested = this.query()?.get('filter') ?? this.query()?.get('state');
    return linked?.state ?? filters.find((item) => item.key === requested)?.key ?? 'all';
  });
  protected readonly records = computed(() =>
    this.viewModel().rows.filter(
      (row) =>
        (!this.exact() || row.id === this.exact()) &&
        (this.filter() === 'all' ? row.state !== 'resolved' : row.state === this.filter()),
    ),
  );
  protected readonly selected = linkedSignal(() => this.records().at(0));
  protected readonly mode = linkedSignal((): MappingPreview['mode'] | null => {
    this.selected();
    this.state();
    return null;
  });
  protected readonly preview = linkedSignal((): MappingPreview | null => {
    this.query();
    return null;
  });
  protected readonly canDecide = computed(
    () =>
      this.state() === 'ready' && this.selected()?.state !== 'resolved' && this.policy.canMutate(),
  );
  protected readonly stateLabel = (value: unknown) =>
    this.states.find((item) => item.key === value)?.label ?? '';
  protected readonly filterLabel = (value: unknown) =>
    filters.find((item) => item.key === value)?.label ?? '';
  protected readonly error = computed<ApiError>(() => ({
    kind: 'unexpected',
    status: 500,
    code: this.sheet().title,
    message: this.sheet().description,
    requestId: 'req-26-31',
    retryable: true,
    retryAfterSeconds: null,
    details: [],
  }));
  protected readonly metrics = computed(() => {
    const vm = this.viewModel();
    const unresolved = vm.rows.filter((row) => row.state !== 'resolved');
    return [
      {
        key: 'queue',
        label: $localize`:@@review.mapping.unresolved:Unresolved`,
        value: unresolved.length,
        detail: $localize`:@@review.mapping.queue-counts:1 unmapped · 1 conflicted`,
        icon: 'lucideClock',
      },
      {
        key: 'producer',
        label: this.copy.producer,
        value: vm.producer,
        detail: vm.suite,
        icon: 'lucideActivity',
      },
      {
        key: 'report',
        label: this.copy.report,
        value: vm.report,
        detail: vm.run + ' · GitHub Actions',
        icon: 'lucideCopy',
      },
      {
        key: 'evidence',
        label: $localize`:@@review.mapping.frozen-evidence:Frozen evidence`,
        value: vm.evidence,
        detail: $localize`:@@review.mapping.immutable:Immutable source`,
        icon: 'lucideCircleCheck',
      },
    ] as const;
  });
  protected readonly facts = computed(() => {
    const vm = this.viewModel();
    return [
      [this.copy.producerSuite, vm.producer + ' / ' + vm.suite],
      [this.copy.original, this.copy.attempt],
      [this.copy.suggestion, this.selected()?.suggestion],
      [this.copy.frozen, vm.evidence],
      [this.copy.runReport, vm.run + ' / ' + vm.report],
      [this.copy.digest, vm.digest],
    ];
  });

  protected changeFilter(key: unknown): void {
    if (!filters.some((item) => item.key === key)) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        mapping: null,
        case: null,
        filter: key,
        state: this.state() === 'readonly' ? 'readonly' : null,
      },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  protected setState(key: unknown): void {
    const next = this.states.find((item) => item.key === key);
    if (!next) return;
    this.preview.set(null);
    this.state.set(next.key);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { state: next.key === 'ready' ? null : next.key },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  protected openDecision(mode: MappingPreview['mode']): void {
    if (this.canDecide() && this.policy.allowsMutation()) this.mode.set(mode);
  }

  protected confirm(value: MappingPreview): void {
    if (
      !this.canDecide() ||
      !this.policy.allowsMutation() ||
      this.mode() !== value.mode ||
      this.selected()?.id !== value.mapping
    )
      return;
    this.preview.set(value);
    this.mode.set(null);
    this.state.set('pending');
    afterNextRender(() => focusWaitingLine(this.stateHeading), { injector: this.injector });
  }
}
