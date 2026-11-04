import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmInput } from '@evidgate/ui/input';
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
import { createIntent, type Intent } from '../../../core/http/intent';
import { SessionStore } from '../../../core/session/session.store';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { MembersStore } from './members.store';
import { MEMBERS_COPY } from './member-list.component';
import { RoleScopePreviewComponent } from './role-scope-preview.component';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { InviteRevealComponent } from './invite-reveal.component';

@Component({
  selector: 'app-invite-dialog',
  imports: [
    HlmCheckbox,
    HlmLabel,
    HlmInput,
    HlmDialogImports,
    HlmButton,
    HlmAlertImports,
    RoleScopePreviewComponent,
    RetryAfterComponent,
    InviteRevealComponent,
  ],
  providers: [RevealSlot],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="opened() ? 'open' : 'closed'"
      [closeOnOutsidePointerEvents]="false"
      [disableClose]="true"
      [restoreFocus]="false"
      [aria-modal]="true"
      [aria-label]="revealing() ? copy.revealTitle : copy.invite"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        #dialog
        [attr.data-members-dialog]="'invite'"
        [showCloseButton]="false"
        class="max-h-[calc(100dvh-var(--eg-space-10))] overflow-y-auto wrap-anywhere [&_p]:m-0 [&_code]:font-mono"
        (keydown.escape)="cancel($event)"
      >
        @if (revealing()) {
          <app-invite-reveal (dismissed)="close()" />
        } @else {
          <form #form (submit)="submit($event, form)">
            <header hlmDialogHeader>
              <h2 hlmDialogTitle id="members-invite-title">{{ copy.invite }}</h2>
            </header>
            <div class="dialog-body grid min-w-0 gap-3 text-sm" [attr.aria-busy]="busy()">
              <label hlmLabel for="members-invite-email">{{ copy.email }}</label>
              <input
                hlmInput
                #emailInput
                id="members-invite-email"
                name="email"
                type="email"
                maxlength="320"
                required
                autocomplete="email"
                [value]="email()"
                [disabled]="locked()"
                [attr.aria-describedby]="store.inviteState().error ? 'members-invite-error' : null"
                (input)="updateEmail($event)"
              />
              <fieldset
                class="m-0 min-w-0 rounded-md border border-solid border-border p-2"
                [disabled]="locked()"
              >
                <legend>{{ copy.optionalRoles }}</legend>
                @for (role of store.roles(); track role.id) {
                  <label
                    hlmLabel
                    class="role-option flex min-h-[var(--eg-control-touch)] items-center gap-2"
                    [for]="'invite-role-' + role.id"
                  >
                    <hlm-checkbox
                      [inputId]="'invite-role-' + role.id"
                      data-members-role-option
                      [attr.data-role-id]="role.id"
                      [disabled]="locked()"
                      [checked]="selected().has(role.id)"
                      (focusin)="previewId.set(role.id)"
                      (checkedChange)="toggle(role.id, $event)"
                    />{{ role.name }}
                  </label>
                }
              </fieldset>
              <app-role-scope-preview [preview]="store.preview(previewId())" />
              @if (store.inviteState().error; as error) {
                <div
                  hlmAlert
                  [variant]="'destructive'"
                  id="members-invite-error"
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
                    (retry)="submit($event, form)"
                  />
                }
              }
            </div>
            <footer hlmDialogFooter class="mt-4">
              <button hlmBtn [variant]="'outline'" type="button" (click)="close()">
                {{ copy.cancel }}</button
              ><button
                hlmBtn
                [variant]="'default'"
                type="submit"
                data-members-submit
                [disabled]="busy() || !retryReady() || !session.has('membership:write')"
              >
                {{ busy() ? copy.submitting : copy.send }}
              </button>
            </footer>
          </form>
        }
      </hlm-dialog-content>
    </hlm-dialog>
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
  private readonly dialog = viewChild<unknown, ElementRef<HTMLElement>>('dialog', {
    read: ElementRef,
  });
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
    this.emailInput()?.nativeElement.focus();
  }

  close(): void {
    this.generation++;
    this.slot.clear();
    this.dialog()
      ?.nativeElement.querySelectorAll<HTMLInputElement>('input[readonly]')
      .forEach((control) => {
        control.value = '';
      });
    this.revealing.set(false);
    this.opened.set(false);
    this.intent = null;
    if (this.opener?.isConnected) this.opener.focus();
    this.opener = null;
  }

  protected cancel(event: Event): void {
    event.preventDefault();
    this.close();
  }

  protected updateEmail(event: Event): void {
    if (event.target instanceof HTMLInputElement) this.email.set(event.target.value);
  }
  protected toggle(id: string, checked: boolean): void {
    if (this.locked()) return;
    const roles = new Set(this.selected());
    if (checked) roles.add(id);
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
