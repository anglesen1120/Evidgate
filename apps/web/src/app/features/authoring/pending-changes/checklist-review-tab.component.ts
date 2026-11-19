import { DOCUMENT } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  linkedSignal,
  resource,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { SessionStore } from '../../../core/session/session.store';
import { SectionHeaderComponent } from '../../../shared/ui/page-header/section-header.component';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { ForbiddenPatternComponent } from '../../../shared/ui/states/forbidden-pattern.component';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { DesktopMutationDirective } from '../../../shared/ui/narrow/desktop-mutation.directive';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { createFormFeedback } from '../../../shared/forms/form-feedback';
import { FieldFeedbackComponent } from '../../../shared/forms/field-feedback.component';
import { FormErrorSummaryComponent } from '../../../shared/forms/form-error-summary.component';
import { codePoints } from '../editor/editor.model';
import { PENDING_COPY } from './pending-changes.copy';
import { PENDING_UI, operationLabel } from './pending-changes.ui-copy';
import { PendingChangesStore } from './pending-changes.store';
import { PendingConfirmComponent } from './pending-confirm.component';

@Component({
  selector: 'app-checklist-review-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [PendingChangesStore],
  imports: [
    FormsModule,
    RouterLink,
    HlmButton,
    HlmInput,
    HlmLabel,
    SectionHeaderComponent,
    RequestStateComponent,
    ForbiddenPatternComponent,
    InlineFeedbackComponent,
    RetryAfterComponent,
    DesktopMutationDirective,
    StatusPillComponent,
    CopyableIdentifierComponent,
    NumberValueComponent,
    AbsoluteTimeComponent,
    PendingConfirmComponent,
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
  ],
  templateUrl: './checklist-review-tab.component.html',
  styleUrl: './checklist-review-tab.component.css',
  host: { '(focusin)': 'rememberFocus($event)' },
})
export class ChecklistReviewTabComponent {
  protected readonly store = inject(PendingChangesStore);
  protected readonly policy = inject(NarrowMutationPolicy);
  private readonly session = inject(SessionStore);
  private readonly document = inject(DOCUMENT);
  readonly projectId = input.required<string>();
  readonly itemId = input.required<string>();
  protected readonly copy = PENDING_COPY;
  protected readonly ui = PENDING_UI;
  protected readonly operationLabel = operationLabel;
  private readonly scope = computed(() => ({ project: this.projectId(), item: this.itemId() }));
  protected readonly comment = linkedSignal({ source: this.scope, computation: () => '' });
  protected readonly reason = linkedSignal({ source: this.scope, computation: () => '' });
  protected readonly commentLimit = 2000;
  protected readonly reasonLimit = 500;
  protected readonly commentLength = computed(() => codePoints(this.comment(), this.commentLimit));
  protected readonly reasonLength = computed(() => codePoints(this.reason(), this.reasonLimit));
  private readonly commentTooLong = $localize`:@@authoring.pending-changes.comment-too-long:Use no more than 2,000 characters for the comment.`;
  private readonly reasonTooLong = $localize`:@@authoring.pending-changes.reason-too-long:Use no more than 500 characters for the reason.`;
  protected readonly queued = linkedSignal({ source: this.scope, computation: () => false });
  protected readonly consent = signal<{ project: string; item: string; reason: string } | null>(
    null,
  );
  protected readonly load = resource({
    params: () => (this.store.canRead() && this.itemId() ? this.scope() : undefined),
    loader: async ({ params }) => {
      await this.store.init(params.project, { targetId: params.item });
      return params;
    },
  });
  // The server scopes the read, but an ignored filter or an empty item id
  // must never render another checklist item's queued changes.
  protected readonly rows = computed(() => {
    const item = this.itemId();
    return item ? this.store.rows().filter((row) => row.target_id === item) : [];
  });
  protected readonly queueLink = computed(() => [
    '/orgs',
    this.session.currentProject()?.org_id ?? '',
    'projects',
    this.projectId(),
    'pending-changes',
  ]);
  // An empty item id never reads the queue (an empty scope would list every item).
  protected readonly status = computed<'loading' | 'ready' | 'error'>(() =>
    !this.itemId() || this.store.queueStatus() === 'ready'
      ? 'ready'
      : this.store.queueStatus() === 'error'
        ? 'error'
        : 'loading',
  );
  private readonly rejectedReason = linkedSignal({
    source: this.scope,
    computation: (): string | null => null,
  });
  protected readonly reasonRefused = computed(
    () =>
      this.reason() === this.rejectedReason() &&
      this.store.enqueueError()?.kind === 'validation' &&
      this.store.enqueueError()?.details.some((detail) => detail.path === '/reason'),
  );
  protected readonly feedback = linkedSignal({
    source: this.scope,
    computation: () =>
      createFormFeedback([
        {
          id: 'review-comment',
          label: this.copy.commentLabel,
          hint: this.ui.commentHint,
          describedBy: 'review-comment-count',
          error: () =>
            this.retryKind() === 'delete'
              ? null
              : this.commentLength() > this.commentLimit
                ? this.commentTooLong
                : this.comment().trim()
                  ? null
                  : this.ui.commentRequired,
        },
        {
          id: 'review-reason',
          label: this.ui.queueReason,
          describedBy: 'review-reason-count',
          error: () =>
            this.reasonLength() > this.reasonLimit
              ? this.reasonTooLong
              : this.reasonRefused()
                ? this.ui.reasonRejected
                : null,
        },
      ]),
  });
  protected readonly retryKind = signal<'comment' | 'delete'>('comment');
  protected readonly quota = computed(
    () =>
      this.store
        .enqueueError()
        ?.details.some((detail) => detail.reason === 'pending_limit_reached') ||
      this.store.enqueueError()?.code === 'quota_exceeded',
  );
  protected readonly deniedWrite = computed(() => this.store.enqueueError()?.kind === 'forbidden');
  protected readonly blocked = computed(
    () =>
      !this.store.canWrite() ||
      this.store.queueing() ||
      !this.store.retryReady() ||
      this.deniedWrite(),
  );
  protected readonly retryFailure = computed(() => {
    const error = this.store.enqueueError();
    return error?.kind === 'rate_limited' || error?.kind === 'unavailable' ? error : null;
  });
  private readonly inspection = viewChild<ElementRef<HTMLElement>>('inspection');
  private focused: HTMLElement | null = null;
  constructor() {
    afterRenderEffect(() => {
      if (!this.policy.canMutate()) {
        if (this.consent()) this.consent.set(null);
        if (
          this.focused &&
          !this.focused.isConnected &&
          this.document.activeElement === this.document.body
        )
          this.inspection()?.nativeElement.focus();
      }
    });
  }
  protected rememberFocus(event: FocusEvent): void {
    this.focused = event.target instanceof HTMLElement ? event.target : null;
  }
  protected async queueComment(): Promise<void> {
    this.queued.set(false);
    this.retryKind.set('comment');
    if (!this.policy.allowsMutation() || this.blocked() || !this.feedback().submit()) return;
    const scope = this.scope();
    const reason = this.reason();
    const saved = await this.store.queueComment(scope.item, this.comment(), reason || undefined);
    if (scope !== this.scope()) return;
    this.rejectedReason.set(reason);
    if (saved) {
      this.comment.set('');
      this.queued.set(true);
      this.feedback().reset();
    } else if (this.reasonRefused()) this.feedback().submit();
  }
  protected openDelete(): void {
    if (!this.policy.allowsMutation() || this.blocked()) return;
    this.retryKind.set('delete');
    if (!this.feedback().submit()) return;
    this.consent.set({ ...this.scope(), reason: this.reason() });
  }
  protected async confirmDelete(): Promise<void> {
    const consent = this.consent();
    this.consent.set(null);
    if (
      !consent ||
      !this.policy.allowsMutation() ||
      this.blocked() ||
      consent.project !== this.projectId() ||
      consent.item !== this.itemId()
    )
      return;
    this.queued.set(false);
    this.retryKind.set('delete');
    const saved = await this.store.queueDelete(consent.item, consent.reason || undefined);
    if (consent.project !== this.projectId() || consent.item !== this.itemId()) return;
    this.rejectedReason.set(consent.reason);
    if (saved) {
      this.queued.set(true);
      this.feedback().reset();
    } else if (this.reasonRefused()) this.feedback().submit();
  }
}
