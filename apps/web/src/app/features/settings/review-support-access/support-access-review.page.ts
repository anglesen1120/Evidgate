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
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { copy, notices } from './support-access-review.copy';
import {
  grantStatuses,
  longGrant,
  supportAccessFixture,
  supportStates,
  type GrantStatus,
  type SupportAccessViewModel,
  type SupportState,
} from './support-access-review.fixture';
// This review-only page uses a snapshot so it cannot change workspace records.
// The behavior implementation will replace the fixture with its scoped data service.
@Component({
  selector: 'app-support-access-review',
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
    CopyableIdentifierComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  templateUrl: './support-access-review.page.html',
})
export class SupportAccessReviewPage {
  private readonly route = inject(ActivatedRoute);
  protected readonly viewport = inject(NarrowMutationPolicy);
  readonly viewModel = input<SupportAccessViewModel>(supportAccessFixture);
  private readonly query = toSignal(this.route.queryParamMap);
  protected readonly copy = copy;
  protected readonly statuses = grantStatuses;
  protected readonly state = linkedSignal<SupportState>(() => {
    const raw = this.query()?.get('state');
    const requested = this.query()?.get('object');
    if (
      (requested && !this.viewModel().grants.some((g) => g.ref === requested)) ||
      (this.query()?.has('project') && this.query()?.get('project') !== 'storefront-web')
    )
      return '404';
    if (raw === 'success') return 'approved';
    return supportStates.find((s) => s === raw) ?? this.viewModel().state;
  });
  protected readonly preview = linkedSignal(() => this.query()?.get('section') === 'admin-preview');
  protected readonly directory = computed(
    () =>
      !['loading', 'empty', 'filtered', 'denied', 'error', '401', '404', '412', '429'].includes(
        this.state(),
      ),
  );
  protected readonly notice = computed(
    () =>
      Object.entries(notices).find(
        ([s]) => s === (this.state() === 'readonly' ? '403' : this.state()),
      )?.[1],
  );
  protected readonly canEdit = computed(
    () =>
      this.preview() &&
      this.viewport.canMutate() &&
      ['ready', 'approved', 'long', 'banner'].includes(this.state()),
  );
  protected readonly grants = computed(() => {
    const rows = this.viewModel().grants;
    const first = this.viewModel().selectedRef;
    switch (this.state()) {
      case 'long':
        return [...rows, longGrant];
      case 'gone':
        return rows.filter((g) => g.ref !== first);
      case 'approved':
      case 'banner':
      case 'expired':
      case 'revoked': {
        const status: GrantStatus =
          this.state() === 'revoked'
            ? 'revoked'
            : this.state() === 'expired'
              ? 'expired'
              : 'approved';
        return rows.map((g) => (g.ref === first ? { ...g, status } : g));
      }
      default:
        return rows;
    }
  });
  protected readonly metrics = computed(() =>
    grantStatuses.map((status) => ({
      status,
      count: this.grants().filter((g) => g.status === status).length,
    })),
  );
  protected readonly hints = {
    requested: copy.requestedHint,
    approved: copy.approvedHint,
    expired: copy.expiredHint,
    revoked: copy.revokedHint,
  };
  protected readonly icons = {
    requested: 'lucideClock',
    approved: 'lucideCircleCheck',
    expired: 'lucideClock',
    revoked: 'lucideOctagonX',
  } as const;
  protected readonly search = signal('');
  protected readonly filter = signal<GrantStatus | 'all'>('all');
  protected readonly filterLabel = (value: unknown) => {
    const status = grantStatuses.find((s) => s === value);
    return status ? copy[status] : copy.all;
  };
  protected readonly filtered = computed(() => {
    const term = this.search().trim().toLocaleLowerCase();
    return this.grants().filter(
      (g) =>
        (this.filter() === 'all' || g.status === this.filter()) &&
        [g.ref, g.subject, g.operator, g.project].some((v) =>
          v?.toLocaleLowerCase().includes(term),
        ),
    );
  });
  protected readonly selection = linkedSignal(
    () =>
      this.query()?.get('object') ??
      (this.state() === 'long' ? longGrant.ref : this.viewModel().selectedRef),
  );
  protected readonly selected = computed(
    () => this.filtered().find((g) => g.ref === this.selection()) ?? this.filtered()[0],
  );
  protected readonly draft = linkedSignal({
    source: () => ({ record: this.selected(), state: this.state(), editable: this.canEdit() }),
    computation: (): 'approve' | 'revoke' | null => null,
  });
  protected readonly basis = signal('');
  protected readonly reason = signal('');
  protected readonly revokedReason = linkedSignal({
    source: () => this.query(),
    computation: () => '',
  });
  protected readonly basisLabel = (v: unknown) =>
    v === 'incident' ? copy.incidentBasis : v === 'review' ? copy.adminBasis : copy.chooseBasis;
  protected setFilter(value: unknown) {
    this.filter.set(grantStatuses.find((s) => s === value) ?? 'all');
  }
  protected reset() {
    this.filter.set('all');
    this.search.set('');
    this.state.set('ready');
  }
  protected openDecision() {
    const record = this.selected();
    if (
      !this.canEdit() ||
      !this.viewport.allowsMutation() ||
      record?.ref !== this.viewModel().selectedRef
    )
      return;
    this.basis.set('');
    this.reason.set('');
    this.draft.set(
      record.status === 'requested' ? 'approve' : record.status === 'approved' ? 'revoke' : null,
    );
  }
  protected confirm() {
    const action = this.draft(),
      record = this.selected();
    if (
      !action ||
      !this.canEdit() ||
      !this.viewport.allowsMutation() ||
      record?.ref !== this.viewModel().selectedRef
    )
      return;
    if (
      action === 'approve' &&
      record.status === 'requested' &&
      ['incident', 'review'].includes(this.basis())
    )
      this.state.set('approved');
    else if (action === 'revoke' && record.status === 'approved' && this.reason().trim()) {
      this.revokedReason.set(this.reason().trim());
      this.state.set('revoked');
    }
    this.draft.set(null);
    this.basis.set('');
    this.reason.set('');
  }
}
