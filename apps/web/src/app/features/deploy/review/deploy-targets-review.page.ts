import { Dialog } from '@angular/cdk/dialog';
import {
  afterNextRender,
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  input,
  linkedSignal,
  signal,
  untracked,
  viewChild,
  viewChildren,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmTableImports } from '@evidgate/ui/table';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { RowActionComponent } from '../../../shared/ui/data-table/row-action.component';
import { DesktopRequiredNoticeComponent } from '../../../shared/ui/narrow/desktop-required-notice.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { HeaderActionsDirective } from '../../../shared/ui/page-header/header-actions.directive';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import {
  EmptyPatternComponent,
  ForbiddenPatternComponent,
  InlineFeedbackComponent,
  LoadingPatternComponent,
  NotFoundPatternComponent,
} from '../../../shared/ui/states';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import { DeployPromotionHistoryComponent } from './deploy-promotion-history.component';
import { copy, notices } from './deploy-targets-review.copy';
import {
  deployTargetsFixture,
  deployStates,
  type DeployState,
  type DeployTargetsViewModel,
} from './deploy-targets-review.fixture';

type RecordMode = 'view' | 'edit' | 'discard';

// This review-only page uses a snapshot so it cannot change workspace records.
// The behavior implementation will replace the fixture with its scoped data service.
@Component({
  selector: 'app-deploy-targets-review',
  imports: [
    HlmButton,
    RowActionComponent,
    HlmCard,
    HlmCardTitle,
    HlmDialogImports,
    HlmIcon,
    HlmInput,
    HlmLabel,
    DesktopRequiredNoticeComponent,
    DeployPromotionHistoryComponent,
    HlmTableImports,
    AbsoluteTimeComponent,
    StatusPillComponent,
    HeaderActionsDirective,
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
  styleUrl: './deploy-targets-review.page.css',
})
export class DeployTargetsReviewPage {
  private readonly route = inject(ActivatedRoute);
  private readonly dialogs = inject(Dialog);
  private readonly injector = inject(Injector);
  protected readonly viewport = inject(NarrowMutationPolicy);
  readonly viewModel = input<DeployTargetsViewModel>(deployTargetsFixture);
  private readonly query = toSignal(this.route.queryParamMap);
  protected readonly copy = copy;
  protected readonly displayText = escapeFormatChars;
  private readonly viewButtons = viewChildren<RowActionComponent, ElementRef<HTMLButtonElement>>(
    'viewButton',
    { read: ElementRef },
  );
  private readonly pageHeader = viewChild(PageHeaderComponent);
  private opener: HTMLElement | null = null;
  private openerIndex = 0;
  private previousField = 'target-order';
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
  protected readonly subtitle = computed(() =>
    this.visible()
      ? `${escapeFormatChars(this.viewModel().project)} · ${copy.ordered}: ${this.viewModel().targets.length}`
      : copy.intro,
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
  protected readonly history = computed(() =>
    this.state() === 'empty-history'
      ? []
      : this.viewModel().checks.filter((c) => c.targetKey === this.key()),
  );
  protected readonly metrics = computed(() => {
    const { targets, checks, project } = this.viewModel();
    const pins = [...new Set(targets.map((t) => t.policy).filter((p) => p !== null))];
    const partial = ['empty-history', 'error'].includes(this.state());
    return [
      {
        title: copy.ordered,
        value: targets.length,
        hint: escapeFormatChars(project),
        icon: 'lucideActivity',
      },
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
  protected readonly editAllowed = computed(
    () =>
      this.preview() &&
      this.viewport.canMutate() &&
      ['ready', 'validation', 'success'].includes(this.state()),
  );
  protected readonly canEdit = computed(() => this.editAllowed() && this.key() === 'production');
  private readonly recordScope = computed(() => ({
    model: this.viewModel(),
    query: this.query(),
    state: this.state(),
    key: this.key(),
  }));
  protected readonly mode = linkedSignal({
    source: () => ({ record: this.recordScope(), editable: this.canEdit() }),
    computation: (current, previous): RecordMode | null => {
      if (!previous) return null;
      if (previous.source.record === current.record) return previous.value;
      const before = previous.source.record;
      const after = current.record;
      if (
        before.model === after.model &&
        before.key === after.key &&
        before.state === after.state &&
        (previous.value === 'edit' || previous.value === 'discard') &&
        untracked(this.dirty)
      )
        return 'discard';
      return null;
    },
  });
  protected readonly order = signal<number | null>(null);
  protected readonly pin = signal('');
  protected readonly acknowledged = signal(false);
  private readonly initialOrder = computed(() =>
    this.state() === 'validation' ? 0 : (this.target()?.order ?? null),
  );
  protected readonly dirty = computed(
    () =>
      this.order() !== this.initialOrder() ||
      this.pin() !== (this.target()?.policy ?? '') ||
      this.acknowledged(),
  );

  constructor() {
    afterRenderEffect(() => {
      if (this.mode() === 'discard') {
        this.dialogs
          .getDialogById('deploy-target-record')
          ?.overlayRef.overlayElement.querySelector<HTMLElement>('#draft-keep')
          ?.focus();
      }
    });
  }

  protected openRecord(key: string, mode: 'view' | 'edit', opener: HTMLElement): void {
    if (this.mode()) {
      this.requestClose();
      return;
    }
    if (
      !this.visible() ||
      (mode === 'edit' &&
        (key !== 'production' || !this.editAllowed() || !this.viewport.allowsMutation()))
    )
      return;
    this.key.set(key);
    this.opener = opener;
    this.openerIndex = this.viewModel().targets.findIndex((target) => target.key === key);
    if (mode === 'edit') this.beginEdit();
    else this.mode.set('view');
  }

  protected beginEdit(): void {
    if (!this.canEdit() || !this.viewport.allowsMutation()) return;
    const fromView = this.mode() === 'view';
    this.order.set(this.initialOrder());
    this.pin.set(this.target()?.policy ?? '');
    this.acknowledged.set(false);
    this.previousField = 'target-order';
    this.mode.set('edit');
    if (fromView) this.focusField('target-order');
  }

  protected registerBackdrop(state: string): void {
    if (state !== 'open') return;
    // Brain's implicit dismissal cannot inspect the local draft. Use its own backdrop;
    // the CDK stream completes when this dialog closes.
    this.dialogs.getDialogById('deploy-target-record')?.backdropClick.subscribe(() => {
      this.requestClose();
    });
  }

  protected rememberField(event: FocusEvent): void {
    const field = event.target;
    if (field instanceof HTMLElement && field.id) this.previousField = field.id;
  }

  protected requestClose(event?: Event): void {
    // The release picker owns Escape while its listbox is open.
    if (
      event?.defaultPrevented ||
      (event?.target instanceof Element &&
        event.target.closest('[aria-expanded="true"][aria-haspopup="listbox"]'))
    )
      return;
    event?.preventDefault();
    if (this.mode() === 'discard') {
      this.keepEditing();
    } else if (this.mode() === 'edit' && this.dirty()) {
      this.mode.set('discard');
      this.focusField('draft-keep');
    } else {
      this.mode.set(null);
    }
  }

  protected keepEditing(): void {
    this.mode.set('edit');
    this.focusField(this.previousField);
  }

  private focusField(id: string): void {
    afterNextRender(
      () => {
        this.dialogs
          .getDialogById('deploy-target-record')
          ?.overlayRef.overlayElement.ownerDocument.getElementById(id)
          ?.focus();
      },
      { injector: this.injector },
    );
  }

  protected closed(): void {
    this.mode.set(null);
    this.order.set(null);
    this.pin.set('');
    this.acknowledged.set(false);
    const views = this.viewButtons();
    const next = this.opener?.isConnected
      ? this.opener
      : (
          views.find((button) => button.nativeElement.dataset['view'] === this.key()) ??
          views[Math.min(this.openerIndex, views.length - 1)]
        )?.nativeElement;
    if (next) next.focus();
    else this.pageHeader()?.focusTitle();
    this.opener = null;
  }
  protected readonly validOrder = computed(() => {
    const n = this.order();
    return n !== null && Number.isInteger(n) && n >= 1 && n <= 20;
  });
  protected readonly readyToPreview = computed(
    () =>
      this.canEdit() &&
      this.validOrder() &&
      !!this.pin().trim() &&
      this.acknowledged() &&
      (this.order() !== this.target()?.order || this.pin() !== (this.target()?.policy ?? '')),
  );
  protected confirm() {
    if (this.mode() !== 'edit' || !this.viewport.allowsMutation() || !this.readyToPreview()) return;
    this.mode.set(null);
    this.state.set('success');
  }
}
