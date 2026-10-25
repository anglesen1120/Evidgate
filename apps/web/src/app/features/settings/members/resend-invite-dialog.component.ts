import { CdkTrapFocus } from '@angular/cdk/a11y';
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
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { wrapDialogFocus } from './dialog-focus-wrap';
import { InviteRevealComponent } from './invite-reveal.component';
import { MEMBERS_COPY } from './member-list.component';
import { MembersStore } from './members.store';

@Component({
  selector: 'app-resend-invite-dialog',
  imports: [CdkTrapFocus, RetryAfterComponent, InviteRevealComponent],
  providers: [RevealSlot],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './members.page.css',
  template: `
    <dialog
      #dialog
      tabindex="-1"
      [attr.data-members-dialog]="'resend'"
      [attr.aria-labelledby]="revealing() ? null : 'members-resend-title'"
      [attr.aria-label]="revealing() ? copy.revealTitle : null"
      [attr.aria-describedby]="revealing() ? null : 'members-resend-description'"
      (cancel)="cancel($event)"
      (keydown.tab)="wrapFocus($event)"
      (keydown.shift.tab)="wrapFocus($event)"
      [cdkTrapFocus]="opened()"
    >
      @if (revealing()) {
        <app-invite-reveal (dismissed)="close()" />
      } @else {
        <header>
          <h2 id="members-resend-title">{{ copy.resendTitle }}</h2>
        </header>
        <div class="dialog-body" [attr.aria-busy]="busy()">
          <p id="members-resend-description">{{ copy.resendDescription }}</p>
          <p>
            <strong>{{ member()?.invited_email }}</strong>
          </p>
          @if (store.resendState().error; as error) {
            <div data-members-error role="alert">
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
        <footer>
          <button #cancelButton type="button" (click)="close()">{{ copy.cancel }}</button>
          <button
            class="primary"
            type="button"
            data-members-submit
            [disabled]="busy() || !retryReady() || !session.has('membership:write')"
            (click)="submit()"
          >
            {{ busy() ? copy.submitting : copy.resend }}
          </button>
        </footer>
      }
    </dialog>
  `,
})
export class ResendInviteDialogComponent {
  protected readonly store = inject(MembersStore);
  protected readonly session = inject(SessionStore);
  protected readonly copy = MEMBERS_COPY;
  private readonly slot = inject(RevealSlot);
  readonly completed = output<void>();
  protected readonly opened = signal(false);
  protected readonly revealing = signal(false);
  protected readonly member = signal<MembershipView | null>(null);
  private readonly inFlight = signal(false);
  protected readonly busy = computed(
    () => this.inFlight() || this.store.resendState().phase === 'submitting',
  );
  protected readonly retryReady = signal(true);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly cancelButton = viewChild<ElementRef<HTMLButtonElement>>('cancelButton');
  private opener: HTMLElement | null = null;
  private intent: Intent | null = null;
  private generation = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.close());
    afterRenderEffect(() => {
      if (this.opened() && !this.revealing()) this.cancelButton()?.nativeElement.focus();
    });
    effect((onCleanup) => {
      const seconds = this.store.resendState().error?.retryAfterSeconds;
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
    this.slot.clear();
    this.revealing.set(false);
    this.store.resetMutations();
    this.opened.set(true);
    this.dialog().nativeElement.showModal();
    this.cancelButton()?.nativeElement.focus();
  }

  close(): void {
    this.generation++;
    this.slot.clear();
    this.dialog()
      .nativeElement.querySelectorAll<HTMLInputElement>('input[readonly]')
      .forEach((control) => {
        control.value = '';
      });
    this.revealing.set(false);
    this.opened.set(false);
    this.dialog().nativeElement.close();
    this.intent = null;
    if (this.opener?.isConnected) this.opener.focus();
    this.opener = null;
  }

  protected cancel(event: Event): void {
    event.preventDefault();
    this.close();
  }

  protected wrapFocus(event: Event): void {
    wrapDialogFocus(event, this.dialog().nativeElement);
  }

  protected async submit(): Promise<void> {
    const member = this.member();
    if (
      !this.opened() ||
      this.revealing() ||
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
      const issued = await this.store.resend(member.id, this.intent);
      if (!issued) return;
      if (generation === this.generation) {
        if (issued.inviteToken !== null)
          this.slot.show({ source: 'invite-token', value: issued.inviteToken });
        this.revealing.set(true);
      }
      this.completed.emit();
    } finally {
      this.inFlight.set(false);
    }
  }
}
