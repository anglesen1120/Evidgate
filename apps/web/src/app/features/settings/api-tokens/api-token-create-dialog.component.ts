import { DOCUMENT } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { createIntent, type Intent } from '../../../core/http/intent';
import type { ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { ConnectStepRevealComponent } from '../../../shared/ui/reveal/connect-step-reveal.component';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { ApiTokensStore } from './api-tokens.store';
import { API_TOKENS_WRITE_COPY } from './api-tokens-write.copy';
import { tokenPermissionChoices } from './api-token-permissions';
import { ApiTokenWriteErrorComponent } from './api-token-write-error.component';
import { retryReadyFor } from './api-token-retry';

/** Reasons a 422 can give for the expiry field (00 §9.2 field paths). */
const EXPIRY_REFUSALS: Record<string, true> = { past: true, out_of_range: true, required: true };

/**
 * A `datetime-local` value carries no zone and the field is labelled UTC, so read it as UTC.
 * Returns `null` for an empty or impossible value, which `toISOString` would throw on.
 */
export function expiryInstant(value: string): number | null {
  const instant = value ? Date.parse(`${value}Z`) : Number.NaN;
  return Number.isFinite(instant) ? instant : null;
}

function refusesExpiry(failure: ApiError): boolean {
  return failure.details.some(
    (detail) => detail.path === 'expires_at' && EXPIRY_REFUSALS[detail.reason] === true,
  );
}

@Component({
  selector: 'app-api-token-create-dialog',
  imports: [
    HlmDialogImports,
    HlmButton,
    HlmInput,
    HlmLabel,
    HlmCheckbox,
    ConnectStepRevealComponent,
    ApiTokenWriteErrorComponent,
  ],
  providers: [RevealSlot],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="opened() ? 'open' : 'closed'"
      [closeOnOutsidePointerEvents]="false"
      [disableClose]="true"
      [restoreFocus]="false"
      [aria-label]="revealing() ? copy.revealTitle : copy.create"
      (closed)="finishClose()"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        #dialog
        [showCloseButton]="false"
        data-api-create-dialog
        class="max-h-[calc(100dvh-var(--eg-space-10))] overflow-y-auto wrap-anywhere"
        (keydown.escape)="escape($event)"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle>{{ revealing() ? copy.revealTitle : copy.create }}</h2>
        </header>
        @if (slot.current(); as entry) {
          <app-connect-step-reveal
            [heading]="copy.revealTitle"
            [value]="entry.value"
            [warning]="copy.warning"
            [copyLabel]="copy.copy"
            [copiedLabel]="copy.copied"
            [dismissLabel]="copy.dismiss"
            (dismissed)="close()"
          />
        } @else {
          <form
            #form
            (submit)="submit($event, form)"
            class="grid min-w-0 gap-3"
            [attr.aria-busy]="store.creating()"
          >
            <label hlmLabel for="api-create-name">{{ copy.name }}</label>
            <input
              hlmInput
              #nameControl
              id="api-create-name"
              name="name"
              required
              maxlength="120"
              autocomplete="off"
              [value]="name()"
              [disabled]="locked()"
              [attr.aria-describedby]="store.createError() ? 'api-create-error' : null"
              (input)="updateName($event)"
            />
            <label hlmLabel for="api-create-expiry">{{ copy.expires }}</label>
            <input
              hlmInput
              id="api-create-expiry"
              name="expires_at"
              type="datetime-local"
              required
              [value]="expires()"
              [disabled]="locked()"
              [attr.min]="minExpiry"
              [attr.aria-invalid]="expiryInvalid() || !!store.createError()"
              [attr.aria-describedby]="
                (expiryRefused()
                  ? 'api-expiry-required api-expiry-refusal'
                  : 'api-expiry-required') + (store.createError() ? ' api-create-error' : '')
              "
              (input)="updateExpiry($event)"
            />
            <p
              id="api-expiry-required"
              class="m-0 text-sm text-muted-foreground"
              [attr.role]="expiryInvalid() ? 'status' : null"
            >
              {{ copy.required }}
            </p>
            @if (expiryRefused()) {
              <p id="api-expiry-refusal" role="alert" class="m-0 text-sm" data-api-expiry-refusal>
                {{ expiryRefusal() }}
              </p>
            }
            <fieldset class="m-0 min-w-0 rounded-md border border-border p-3" [disabled]="locked()">
              <legend>{{ copy.permissions }}</legend>
              <p class="m-0 mb-2 text-sm text-muted-foreground">{{ copy.permissionHint }}</p>
              @for (code of choices(); track code) {
                <label
                  hlmLabel
                  class="flex min-h-[var(--eg-control-touch)] items-center gap-2 wrap-anywhere"
                  [for]="'api-permission-' + code"
                >
                  <hlm-checkbox
                    [inputId]="'api-permission-' + code"
                    [checked]="selected().has(code)"
                    [disabled]="locked()"
                    (checkedChange)="toggle(code, $event)"
                  />{{ code }}
                </label>
              }
            </fieldset>
            <div id="api-create-error">
              <app-api-token-write-error [error]="store.createError()" />
            </div>
            @if (uncertain()) {
              <p role="alert" class="m-0">{{ copy.uncertain }}</p>
            }
            <footer hlmDialogFooter class="mt-3">
              <button
                hlmBtn
                [variant]="'outline'"
                type="button"
                [disabled]="store.creating()"
                (click)="dismiss()"
              >
                {{ uncertain() ? copy.inspect : copy.cancel }}
              </button>
              <button
                hlmBtn
                type="submit"
                data-api-create-submit
                [disabled]="!valid() || store.creating() || !retryReady() || uncertain()"
              >
                {{ store.creating() ? copy.submitting : copy.create }}
              </button>
            </footer>
          </form>
        }
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class ApiTokenCreateDialogComponent {
  protected readonly store = inject(ApiTokensStore);
  private readonly session = inject(SessionStore);
  protected readonly slot = inject(RevealSlot);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  protected readonly copy = API_TOKENS_WRITE_COPY;
  protected readonly opened = signal(true);
  protected readonly name = signal('');
  protected readonly expires = signal('');
  /** The expiry value a 422 refused, so an unchanged resubmit cannot repeat the same refusal. */
  protected readonly rejectedExpiry = signal<string | null>(null);
  /** The server's own words for that refusal, shown beside the field with its maximum. */
  protected readonly expiryRefusal = signal<string | null>(null);
  /** Set when a submit finds the expiry already past; cleared by the next edit of the field. */
  protected readonly elapsed = signal(false);
  // The picker floor and the live check share the open instant so the field and its minimum agree;
  // submit re-checks against the clock, since a dialog can stay open past the chosen expiry.
  private readonly openedAtMs = Date.now();
  protected readonly minExpiry = new Date(this.openedAtMs).toISOString().slice(0, 16);
  protected readonly expiryInvalid = computed(() => {
    const instant = expiryInstant(this.expires());
    return instant === null || instant <= this.openedAtMs || this.elapsed();
  });
  protected readonly choices = computed(() =>
    tokenPermissionChoices(this.session.permissions(), (code) => this.session.has(code)),
  );
  protected readonly selected = linkedSignal({
    source: this.choices,
    computation: (codes, previous): ReadonlySet<string> =>
      new Set([...(previous?.value ?? [])].filter((code) => codes.includes(code))),
  });
  protected readonly revealing = computed(() => this.slot.current() !== null);
  protected readonly uncertain = computed(() => {
    const kind = this.store.createError()?.kind;
    return kind === 'network' || kind === 'timeout';
  });
  protected readonly locked = computed(
    () => this.store.creating() || !!this.store.createError()?.retryable || this.uncertain(),
  );
  protected readonly expiryRefused = computed(
    () => this.expiryRefusal() !== null && this.expires() === this.rejectedExpiry(),
  );
  protected readonly valid = computed(
    () =>
      this.store.canWrite() &&
      !!this.name().trim() &&
      !this.expiryInvalid() &&
      this.expires() !== this.rejectedExpiry() &&
      this.selected().size > 0,
  );
  protected readonly retryReady = retryReadyFor(this.store.createError);
  private readonly dialog = viewChild<unknown, ElementRef<HTMLElement>>('dialog', {
    read: ElementRef,
  });
  private readonly nameControl = viewChild<ElementRef<HTMLInputElement>>('nameControl');
  private readonly opener =
    this.document.activeElement instanceof HTMLElement ? this.document.activeElement : null;
  private intent: Intent | null = null;

  constructor() {
    this.store.resetCreateError();
    const off = this.session.onSessionEnd(() => this.close());
    this.destroyRef.onDestroy(() => {
      off();
      this.clearValue();
      this.store.closeCreate();
    });
    afterRenderEffect(() => {
      if (this.opened() && !this.revealing()) this.nameControl()?.nativeElement.focus();
    });
  }

  protected updateName(event: Event): void {
    if (event.target instanceof HTMLInputElement && !this.locked()) {
      this.name.set(event.target.value);
      this.changed();
    }
  }
  protected updateExpiry(event: Event): void {
    if (event.target instanceof HTMLInputElement && !this.locked()) {
      this.expires.set(event.target.value);
      this.rejectedExpiry.set(null);
      this.expiryRefusal.set(null);
      this.elapsed.set(false);
      this.changed();
    }
  }
  protected toggle(code: string, checked: boolean): void {
    if (this.locked() || !this.choices().includes(code)) return;
    const selected = new Set(this.selected());
    if (checked) selected.add(code);
    else selected.delete(code);
    this.selected.set(selected);
    this.changed();
  }
  private changed(): void {
    this.intent = null;
    this.store.resetCreateError();
  }
  protected async submit(event: Event, form: HTMLFormElement): Promise<void> {
    event.preventDefault();
    if (
      !this.opened() ||
      this.revealing() ||
      this.store.creating() ||
      !this.valid() ||
      !this.retryReady() ||
      this.uncertain() ||
      !form.reportValidity()
    )
      return;
    // `valid()` already refuses an unparsable value; re-reading it here keeps `toISOString` from
    // throwing a RangeError if the two ever disagree.
    const instant = expiryInstant(this.expires());
    if (instant === null) return;
    if (instant <= Date.now()) {
      this.elapsed.set(true);
      return;
    }
    this.intent ??= createIntent();
    const answer = await this.store.create(
      {
        name: this.name().trim(),
        expires_at: new Date(instant).toISOString(),
        permissions: [...this.selected()],
      },
      this.intent,
    );
    if (answer && !this.destroyRef.destroyed && this.opened() && this.store.createOpen()) {
      this.slot.show({ source: 'api-token', value: answer.token });
      return;
    }
    const failure = this.store.createError();
    if (!answer && failure && refusesExpiry(failure)) {
      this.rejectedExpiry.set(this.expires());
      this.expiryRefusal.set(failure.message);
    }
  }
  protected escape(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    if (!this.revealing() && !this.store.creating()) this.close();
  }
  /** An ambiguous create may have committed, so the list is re-read before this dialog goes away. */
  protected dismiss(): void {
    if (this.uncertain()) this.store.reloadFirst();
    this.close();
  }
  close(): void {
    this.clearValue();
    this.opened.set(false);
  }
  private clearValue(): void {
    this.dialog()
      ?.nativeElement.querySelectorAll<HTMLInputElement>('input[readonly]')
      .forEach((control) => {
        control.value = '';
        control.removeAttribute('value');
      });
    this.slot.clear();
  }
  protected finishClose(): void {
    this.store.closeCreate();
    if (this.opener?.isConnected && !this.opener.matches(':disabled')) this.opener.focus();
    else this.document.querySelector<HTMLElement>('app-api-tokens-page h2')?.focus();
  }
}
