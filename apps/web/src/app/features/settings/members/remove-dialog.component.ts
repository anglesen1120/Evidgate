import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmButton } from '@evidgate/ui/button';
import { HlmAlertImports } from '@evidgate/ui/alert';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterRenderEffect,
  computed,
  effect,
  inject,
  output,
  signal,
  viewChild,
} from '@angular/core';
import type { MembershipView } from '@evidgate/sdk';
import { createIntent, type Intent } from '../../../core/http/intent';
import { SessionStore } from '../../../core/session/session.store';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { MembersStore } from './members.store';
import { MEMBERS_COPY } from './member-list.component';

@Component({
  selector: 'app-remove-dialog',
  imports: [HlmDialogImports, HlmButton, HlmAlertImports, RetryAfterComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="opened() ? 'open' : 'closed'"
      [closeOnOutsidePointerEvents]="false"
      [disableClose]="true"
      [restoreFocus]="false"
      [aria-modal]="true"
      [aria-label]="copy.removeTitle"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        #dialog
        [attr.data-members-dialog]="'remove'"
        [showCloseButton]="false"
        class="max-h-[calc(100dvh-var(--eg-space-10))] overflow-y-auto wrap-anywhere [&_p]:m-0 [&_code]:font-mono"
        (keydown.escape)="cancel($event)"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle id="members-remove-title">{{ copy.removeTitle }}</h2>
        </header>
        <div class="dialog-body grid min-w-0 gap-3 text-sm" [attr.aria-busy]="busy()">
          <p hlmDialogDescription>{{ copy.removeDescription }}</p>
          <p>
            <strong>{{ member()?.invited_email }}</strong>
          </p>
          @if (store.removeState().error; as error) {
            <div
              #errorSummary
              hlmAlert
              [variant]="'destructive'"
              class="grid gap-2 whitespace-pre-wrap"
              data-members-error
              role="group"
              tabindex="-1"
              [attr.aria-label]="copy.removeRefused"
              [attr.aria-describedby]="'members-remove-error-message members-remove-error-code'"
            >
              <p id="members-remove-error-message">{{ error.message }}</p>
              <code id="members-remove-error-code" data-members-error-code>{{ error.code }}</code>
            </div>
            @if (error.kind === 'rate_limited' || error.kind === 'unavailable') {
              <app-retry-after
                [kind]="error.kind"
                [retryAfterSeconds]="error.retryAfterSeconds"
                [retryEligible]="retryReady() && error.retryable"
                [busy]="busy()"
                [code]="error.code"
                [requestId]="error.requestId"
                (retry)="submit()"
              />
            }
          }
        </div>
        <footer hlmDialogFooter class="mt-4">
          <button hlmBtn [variant]="'outline'" #cancelButton type="button" (click)="close()">
            {{ copy.cancel }}</button
          ><button
            hlmBtn
            [variant]="'destructive'"
            type="button"
            data-members-submit
            [disabled]="busy() || !retryReady() || !session.has('membership:write')"
            (click)="submit()"
          >
            {{ busy() ? copy.submitting : copy.remove }}
          </button>
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class RemoveDialogComponent {
  protected readonly store = inject(MembersStore);
  protected readonly session = inject(SessionStore);
  protected readonly viewport = inject(NarrowMutationPolicy);
  protected readonly copy = MEMBERS_COPY;
  readonly completed = output<void>();
  protected readonly opened = signal(false);
  protected readonly member = signal<MembershipView | null>(null);
  protected readonly busy = computed(() => this.store.removeState().phase === 'submitting');
  protected readonly retryReady = signal(true);
  private opener: HTMLElement | null = null;
  private intent: Intent | null = null;
  private generation = 0;
  private readonly focusGeneration = signal<number | null>(null);
  private readonly errorSummary = viewChild<ElementRef<HTMLElement>>('errorSummary');

  constructor() {
    inject(DestroyRef).onDestroy(() => this.close());
    afterRenderEffect(() => {
      const generation = this.focusGeneration();
      if (generation === null || generation !== this.generation || !this.opened()) return;
      const target = this.errorSummary()?.nativeElement;
      if (!target) return;
      target.focus();
      this.focusGeneration.set(null);
    });
    effect((onCleanup) => {
      const seconds = this.store.removeState().error?.retryAfterSeconds;
      this.retryReady.set(seconds == null || seconds <= 0);
      if (seconds != null && seconds > 0) {
        const timer = setTimeout(() => this.retryReady.set(true), seconds * 1000);
        onCleanup(() => clearTimeout(timer));
      }
    });
  }

  open(member: MembershipView, opener: HTMLElement): void {
    if (
      !this.session.has('membership:write') ||
      this.busy() ||
      this.store.inviteState().phase === 'submitting'
    )
      return;
    this.generation++;
    this.opener = opener;
    this.member.set(member);
    this.intent = null;
    this.store.resetMutations();
    this.opened.set(true);
  }

  close(): void {
    this.generation++;
    this.focusGeneration.set(null);
    this.opened.set(false);
    this.intent = null;
    if (this.opener?.isConnected) this.opener.focus();
    this.opener = null;
  }

  protected cancel(event: Event): void {
    event.preventDefault();
    this.close();
  }

  protected async submit(): Promise<void> {
    const member = this.member();
    if (
      !this.opened() ||
      !this.viewport.allowsMutation() ||
      !member ||
      this.busy() ||
      !this.retryReady() ||
      !this.session.has('membership:write')
    )
      return;
    this.intent ??= createIntent();
    const generation = this.generation;
    if (!(await this.store.remove(member.id, this.intent))) {
      if (this.opened() && generation === this.generation) this.focusGeneration.set(generation);
      return;
    }
    // A success is a fact about the list, not about this dialog: announce it even when the dialog was
    // closed while the removal was in flight, so the page still confirms it. Only the attempt that is
    // still open closes itself; a late refusal renders inside the closed dialog and never reopens it.
    if (generation === this.generation) this.close();
    this.completed.emit();
  }
}
