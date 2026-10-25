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
import { createIntent, type Intent } from '../../../core/http/intent';
import { SessionStore } from '../../../core/session/session.store';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { MembersStore } from './members.store';
import { MEMBERS_COPY } from './member-list.component';
import { wrapDialogFocus } from './dialog-focus-wrap';
import { RoleScopePreviewComponent } from './role-scope-preview.component';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { InviteRevealComponent } from './invite-reveal.component';

@Component({
  selector: 'app-invite-dialog',
  imports: [CdkTrapFocus, RoleScopePreviewComponent, RetryAfterComponent, InviteRevealComponent],
  providers: [RevealSlot],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './members.page.css',
  template: `
    <dialog
      #dialog
      tabindex="-1"
      [attr.data-members-dialog]="'invite'"
      [attr.aria-labelledby]="revealing() ? null : 'members-invite-title'"
      [attr.aria-label]="revealing() ? copy.revealTitle : null"
      (cancel)="cancel($event)"
      (keydown.tab)="wrapFocus($event)"
      (keydown.shift.tab)="wrapFocus($event)"
      [cdkTrapFocus]="opened()"
    >
      @if (revealing()) {
        <app-invite-reveal (dismissed)="close()" />
      } @else {
        <form #form (submit)="submit($event, form)">
          <header>
            <h2 id="members-invite-title">{{ copy.invite }}</h2>
          </header>
          <div class="dialog-body" [attr.aria-busy]="busy()">
            <label for="members-invite-email">{{ copy.email }}</label>
            <input
              #emailInput
              id="members-invite-email"
              name="email"
              type="email"
              maxlength="320"
              required
              autocomplete="email"
              [value]="email()"
              [disabled]="locked()"
              (input)="updateEmail($event)"
            />
            <fieldset [disabled]="locked()">
              <legend>{{ copy.optionalRoles }}</legend>
              @for (role of store.roles(); track role.id) {
                <label class="role-option"
                  ><input
                    type="checkbox"
                    [value]="role.id"
                    [checked]="selected().has(role.id)"
                    (focus)="previewId.set(role.id)"
                    (change)="toggle(role.id, $event)"
                  />{{ role.name }}</label
                >
              }
            </fieldset>
            <app-role-scope-preview [preview]="store.preview(previewId())" />
            @if (store.inviteState().error; as error) {
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
                  (retry)="submit($event, form)"
                />
              }
            }
          </div>
          <footer>
            <button type="button" (click)="close()">{{ copy.cancel }}</button
            ><button
              class="primary"
              type="submit"
              data-members-submit
              [disabled]="busy() || !retryReady() || !session.has('membership:write')"
            >
              {{ busy() ? copy.submitting : copy.send }}
            </button>
          </footer>
        </form>
      }
    </dialog>
  `,
})
export class InviteDialogComponent {
  protected readonly store = inject(MembersStore);
  protected readonly session = inject(SessionStore);
  protected readonly copy = MEMBERS_COPY;
  private readonly slot = inject(RevealSlot);
  protected readonly revealing = signal(false);
  readonly completed = output<void>();
  protected readonly opened = signal(false);
  protected readonly email = signal('');
  protected readonly selected = signal<ReadonlySet<string>>(new Set());
  protected readonly previewId = signal('');
  private readonly inFlight = signal(false);
  protected readonly busy = computed(
    () => this.inFlight() || this.store.inviteState().phase === 'submitting',
  );
  protected readonly retryReady = signal(true);
  // Freeze a failed attempt's payload as well as its intent; closing starts a different attempt.
  protected readonly locked = computed(
    () => this.busy() || this.store.inviteState().phase === 'failed',
  );
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly emailInput = viewChild<ElementRef<HTMLInputElement>>('emailInput');
  private opener: HTMLElement | null = null;
  private intent: Intent | null = null;
  private generation = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.close());
    afterRenderEffect(() => {
      if (this.opened() && !this.revealing()) this.emailInput()?.nativeElement.focus();
    });
    effect((onCleanup) => {
      const seconds = this.store.inviteState().error?.retryAfterSeconds;
      this.retryReady.set(seconds == null || seconds <= 0);
      if (seconds != null && seconds > 0) {
        const timer = setTimeout(() => this.retryReady.set(true), seconds * 1000);
        onCleanup(() => clearTimeout(timer));
      }
    });
  }

  open(opener: HTMLElement): void {
    if (
      !this.session.has('membership:write') ||
      this.busy() ||
      this.store.removeState().phase === 'submitting'
    )
      return;
    this.generation++;
    this.opener = opener;
    this.intent = null;
    this.email.set('');
    this.selected.set(new Set());
    this.previewId.set('');
    this.store.resetMutations();
    this.slot.clear();
    this.revealing.set(false);
    this.opened.set(true);
    this.dialog().nativeElement.showModal();
    this.emailInput()?.nativeElement.focus();
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

  /** Tab/Shift+Tab wrap at the dialog's edges; see `wrapDialogFocus`. */
  protected wrapFocus(event: Event): void {
    wrapDialogFocus(event, this.dialog().nativeElement);
  }

  protected updateEmail(event: Event): void {
    if (event.target instanceof HTMLInputElement) this.email.set(event.target.value);
  }
  protected toggle(id: string, event: Event): void {
    if (!(event.target instanceof HTMLInputElement)) return;
    const roles = new Set(this.selected());
    if (event.target.checked) roles.add(id);
    else roles.delete(id);
    this.selected.set(roles);
    this.previewId.set(id);
  }
  protected async submit(event: Event | void, form: HTMLFormElement): Promise<void> {
    event?.preventDefault();
    if (
      !this.opened() ||
      this.revealing() ||
      this.busy() ||
      !this.retryReady() ||
      !this.session.has('membership:write') ||
      !form.reportValidity()
    )
      return;
    this.intent ??= createIntent();
    const generation = this.generation;
    this.inFlight.set(true);
    try {
      const issued = await this.store.invite(this.email(), [...this.selected()], this.intent);
      if (!issued) return;
      // Late responses may update the list, but must never reopen a dismissed credential.
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
