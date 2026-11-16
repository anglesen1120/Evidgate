import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmButton } from '@evidgate/ui/button';
import { HlmAlertImports } from '@evidgate/ui/alert';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  output,
  signal,
} from '@angular/core';
import type { MembershipView } from '@evidgate/sdk';
import { createIntent, type Intent } from '../../../core/http/intent';
import { SessionStore } from '../../../core/session/session.store';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { MEMBERS_COPY } from './member-list.component';
import { MembersStore } from './members.store';

@Component({
  selector: 'app-cancel-invite-dialog',
  imports: [HlmDialogImports, HlmButton, HlmAlertImports, RetryAfterComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="opened() ? 'open' : 'closed'"
      [closeOnOutsidePointerEvents]="false"
      [disableClose]="true"
      [restoreFocus]="false"
      [aria-modal]="true"
      [aria-label]="copy.cancelInviteTitle"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        #dialog
        [attr.data-members-dialog]="'cancel-invite'"
        [showCloseButton]="false"
        class="max-h-[calc(100dvh-var(--eg-space-10))] overflow-y-auto wrap-anywhere [&_p]:m-0 [&_code]:font-mono"
        (keydown.escape)="cancel($event)"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle id="members-cancel-invite-title">{{ copy.cancelInviteTitle }}</h2>
        </header>
        <div class="dialog-body grid min-w-0 gap-3 text-sm" [attr.aria-busy]="busy()">
          <p hlmDialogDescription>{{ copy.cancelInviteDescription }}</p>
          <p>
            <strong>{{ member()?.invited_email }}</strong>
          </p>
          @if (store.cancelState().error; as error) {
            <div
              hlmAlert
              [variant]="'destructive'"
              class="grid gap-2 whitespace-pre-wrap"
              data-members-error
              role="alert"
            >
              <p>{{ error.message }}</p>
              <code data-members-error-code>{{ error.code }}</code>
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
            {{ copy.close }}
          </button>
          <button
            hlmBtn
            [variant]="'destructive'"
            type="button"
            data-members-submit
            [disabled]="busy() || !retryReady() || !session.has('membership:write')"
            (click)="submit()"
          >
            {{ busy() ? copy.submitting : copy.cancelInvite }}
          </button>
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class CancelInviteDialogComponent {
  protected readonly store = inject(MembersStore);
  protected readonly session = inject(SessionStore);
  protected readonly viewport = inject(NarrowMutationPolicy);
  protected readonly copy = MEMBERS_COPY;
  readonly completed = output<void>();
  protected readonly opened = signal(false);
  protected readonly member = signal<MembershipView | null>(null);
  private readonly inFlight = signal(false);
  protected readonly busy = computed(
    () => this.inFlight() || this.store.cancelState().phase === 'submitting',
  );
  protected readonly retryReady = signal(true);
  private opener: HTMLElement | null = null;
  private intent: Intent | null = null;
  private generation = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.close());
    effect((onCleanup) => {
      const seconds = this.store.cancelState().error?.retryAfterSeconds;
      this.retryReady.set(seconds == null || seconds <= 0);
      if (seconds != null && seconds > 0) {
        const timer = setTimeout(() => this.retryReady.set(true), seconds * 1000);
        onCleanup(() => clearTimeout(timer));
      }
    });
  }

  open(member: MembershipView, opener: HTMLElement): void {
    if (!this.session.has('membership:write') || member.status !== 'pending' || this.busy()) return;
    this.generation++;
    this.opener = opener;
    this.member.set(member);
    this.intent = createIntent();
    this.store.resetMutations();
    this.opened.set(true);
  }

  close(): void {
    this.generation++;
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
      !this.intent ||
      this.busy() ||
      !this.retryReady() ||
      !this.session.has('membership:write')
    )
      return;
    const generation = this.generation;
    this.inFlight.set(true);
    try {
      if (!(await this.store.cancel(member.id, this.intent))) return;
      if (generation === this.generation) this.close();
      this.completed.emit();
    } finally {
      this.inFlight.set(false);
    }
  }
}
