import { CdkTrapFocus } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
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
import { MembersStore } from './members.store';
import { MEMBERS_COPY } from './member-list.component';

@Component({
  selector: 'app-remove-dialog',
  imports: [CdkTrapFocus, RetryAfterComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './members.page.css',
  template: `
    <dialog
      #dialog
      [attr.data-members-dialog]="'remove'"
      aria-labelledby="members-remove-title"
      [attr.aria-describedby]="'members-remove-description'"
      (cancel)="cancel($event)"
      [cdkTrapFocus]="opened()"
    >
      <header>
        <h2 id="members-remove-title">{{ copy.removeTitle }}</h2>
      </header>
      <div class="dialog-body" [attr.aria-busy]="busy()">
        <p id="members-remove-description">{{ copy.removeDescription }}</p>
        <p>
          <strong>{{ member()?.invited_email }}</strong>
        </p>
        @if (store.removeState().error; as error) {
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
        <button #cancelButton type="button" (click)="close()">{{ copy.cancel }}</button
        ><button
          class="danger"
          type="button"
          data-members-submit
          [disabled]="busy() || !retryReady() || !session.has('membership:write')"
          (click)="submit()"
        >
          {{ busy() ? copy.submitting : copy.remove }}
        </button>
      </footer>
    </dialog>
  `,
})
export class RemoveDialogComponent {
  protected readonly store = inject(MembersStore);
  protected readonly session = inject(SessionStore);
  protected readonly copy = MEMBERS_COPY;
  readonly completed = output<void>();
  protected readonly opened = signal(false);
  protected readonly member = signal<MembershipView | null>(null);
  protected readonly busy = computed(() => this.store.removeState().phase === 'submitting');
  protected readonly retryReady = signal(true);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly cancelButton = viewChild.required<ElementRef<HTMLButtonElement>>('cancelButton');
  private opener: HTMLElement | null = null;
  private intent: Intent | null = null;
  private generation = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.close());
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
    this.dialog().nativeElement.showModal();
    this.cancelButton().nativeElement.focus();
  }

  close(): void {
    this.generation++;
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
  protected async submit(): Promise<void> {
    const member = this.member();
    if (
      !this.opened() ||
      !member ||
      this.busy() ||
      !this.retryReady() ||
      !this.session.has('membership:write')
    )
      return;
    this.intent ??= createIntent();
    const generation = this.generation;
    if ((await this.store.remove(member.id, this.intent)) && generation === this.generation) {
      this.close();
      this.completed.emit();
    }
  }
}
