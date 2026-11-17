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
import {
  createFormFeedback,
  FieldFeedbackComponent,
  FormErrorSummaryComponent,
  FormRequiredNoteComponent,
  type FieldFeedback,
} from '../../../shared/forms';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
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
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
    FormRequiredNoteComponent,
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
          <form novalidate (submit)="submit($event)">
            <header hlmDialogHeader>
              <h2 hlmDialogTitle id="members-invite-title">{{ copy.invite }}</h2>
            </header>
            <div class="dialog-body grid min-w-0 gap-3 text-sm" [attr.aria-busy]="busy()">
              <app-form-error-summary
                [summaryId]="'members-invite-validation-summary'"
                [feedback]="feedback"
              />
              <app-form-required-note />
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
                [aria-describedby]="emailDescribedBy()"
                [attr.aria-invalid]="emailField.ariaInvalid()"
                [forceInvalid]="emailField.invalid()"
                (blur)="emailField.blur()"
                (input)="updateEmail($event)"
              />
              <app-field-feedback [field]="emailField" />
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
                <div class="sr-only" id="members-invite-error-label">{{ copy.inviteRefused }}</div>
                <div
                  #errorSummary
                  hlmAlert
                  [variant]="'destructive'"
                  id="members-invite-error"
                  class="grid gap-2 whitespace-pre-wrap"
                  data-members-error
                  role="group"
                  tabindex="-1"
                  aria-labelledby="members-invite-error-label"
                  [attr.aria-describedby]="'members-invite-error-message members-invite-error-code'"
                >
                  <p id="members-invite-error-message">{{ error.message }}</p>
                  <code id="members-invite-error-code" data-members-error-code>{{
                    error.code
                  }}</code>
                </div>
                <p class="text-sm text-muted-foreground">
                  {{
                    editableRefusal()
                      ? copy.correctInvitation
                      : error.retryable
                        ? copy.fixedInvitation
                        : copy.restartInvitation
                  }}
                </p>
                @if (error.kind === 'rate_limited' || error.kind === 'unavailable') {
                  <app-retry-after
                    [kind]="error.kind"
                    [retryAfterSeconds]="error.retryAfterSeconds"
                    [retryEligible]="retryReady() && error.retryable"
                    [busy]="busy()"
                    [code]="error.code"
                    [requestId]="error.requestId"
                    (retry)="submit($event)"
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
                [disabled]="
                  busy() ||
                  !retryReady() ||
                  (!!store.inviteState().error && !store.inviteState().error?.retryable) ||
                  !session.has('membership:write')
                "
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

  protected readonly viewport = inject(NarrowMutationPolicy);

  private readonly slot = inject(RevealSlot);

  protected readonly copy = MEMBERS_COPY;
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
  protected readonly editableRefusal = computed(() => {
    const error = this.store.inviteState().error;
    return (
      error?.kind === 'validation' || (error?.kind === 'conflict' && error.code === 'invite_exists')
    );
  });
  // Only an authoritative, correctable refusal permits a different payload and a new intent.
  protected readonly locked = computed(
    () => this.busy() || (this.store.inviteState().phase === 'failed' && !this.editableRefusal()),
  );
  private readonly focusRequest = signal<{ generation: number; error: boolean } | null>(null);
  private readonly errorSummary = viewChild<ElementRef<HTMLElement>>('errorSummary');
  private readonly dialog = viewChild<unknown, ElementRef<HTMLElement>>('dialog', {
    read: ElementRef,
  });
  private readonly emailInput = viewChild<ElementRef<HTMLInputElement>>('emailInput');
  protected readonly feedback = createFormFeedback([
    {
      id: 'members-invite-email',
      label: this.copy.email,
      error: () => this.emailCorrection(),
    },
  ]);
  protected readonly emailField = this.requireEmailField();
  protected readonly emailDescribedBy = computed(() => {
    const feedbackId = this.emailField.describedBy();
    const refusalId = this.store.inviteState().error ? 'members-invite-error' : null;
    return feedbackId && refusalId ? `${feedbackId} ${refusalId}` : (feedbackId ?? refusalId);
  });
  private opener: HTMLElement | null = null;
  private intent: Intent | null = null;
  private generation = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.close());
    afterRenderEffect(() => {
      const request = this.focusRequest();
      if (!request || request.generation !== this.generation || !this.opened() || this.revealing())
        return;
      const target = request.error
        ? this.errorSummary()?.nativeElement
        : this.emailInput()?.nativeElement;
      if (!target || (target instanceof HTMLInputElement && target.disabled)) return;
      target.focus();
      this.focusRequest.set(null);
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
    this.feedback.reset();
    this.selected.set(new Set());
    this.previewId.set('');
    this.store.resetMutations();
    this.slot.clear();
    this.revealing.set(false);
    this.opened.set(true);
    this.focusRequest.set({ generation: this.generation, error: false });
  }

  close(): void {
    this.generation++;
    this.focusRequest.set(null);
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
    if (this.locked() || !(event.target instanceof HTMLInputElement)) return;
    if (event.target.value === this.email()) return;
    this.beginCorrection();
    this.email.set(event.target.value);
  }
  protected toggle(id: string, checked: boolean): void {
    if (this.locked()) return;
    if (this.selected().has(id) === checked) return;
    this.beginCorrection();
    const roles = new Set(this.selected());
    if (checked) roles.add(id);
    else roles.delete(id);
    this.selected.set(roles);
    this.previewId.set(id);
  }
  private beginCorrection(): void {
    if (!this.editableRefusal()) return;
    this.intent = null;
    this.store.resetMutations();
  }
  private requireEmailField(): FieldFeedback {
    const field = this.feedback.fields[0];
    if (!field) throw new Error('Missing invitation email feedback');
    return field;
  }

  private emailCorrection(): string | null {
    const email = this.email();
    if (!email.trim()) return this.copy.emailRequired;
    if (email.length > 320) return this.copy.emailTooLong;
    const input = this.emailInput()?.nativeElement;
    return !input || input.validity.typeMismatch ? this.copy.emailInvalid : null;
  }

  protected async submit(event: Event | void): Promise<void> {
    event?.preventDefault();
    if (
      !this.opened() ||
      !this.viewport.allowsMutation() ||
      this.revealing() ||
      this.busy() ||
      !this.retryReady() ||
      (!!this.store.inviteState().error && !this.store.inviteState().error?.retryable) ||
      !this.session.has('membership:write')
    )
      return;
    if (!this.feedback.submit()) return;
    this.intent ??= createIntent();
    const generation = this.generation;
    this.inFlight.set(true);
    try {
      const issued = await this.store.invite(this.email(), [...this.selected()], this.intent);
      if (!issued) {
        if (this.opened() && generation === this.generation)
          this.focusRequest.set({ generation, error: true });
        return;
      }
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
