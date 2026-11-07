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
import { SessionStore } from '../../../core/session/session.store';
import { ConnectStepRevealComponent } from '../../../shared/ui/reveal/connect-step-reveal.component';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { ApiTokensStore } from './api-tokens.store';
import { API_TOKENS_WRITE_COPY } from './api-tokens-write.copy';
import { tokenPermissionChoices } from './api-token-permissions';
import { ApiTokenWriteErrorComponent } from './api-token-write-error.component';
import { retryReadyFor } from './api-token-retry';

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
              [attr.aria-invalid]="!expires() || !!store.createError()"
              [attr.aria-describedby]="
                store.createError() ? 'api-expiry-required api-create-error' : 'api-expiry-required'
              "
              (input)="updateExpiry($event)"
            />
            <p
              id="api-expiry-required"
              class="m-0 text-sm text-muted-foreground"
              [attr.role]="!expires() ? 'status' : null"
            >
              {{ copy.required }}
            </p>
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
                (click)="close()"
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
  protected readonly valid = computed(
    () =>
      this.store.canWrite() && !!this.name().trim() && !!this.expires() && this.selected().size > 0,
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
    this.intent ??= createIntent();
    const answer = await this.store.create(
      {
        name: this.name().trim(),
        expires_at: new Date(this.expires() + 'Z').toISOString(),
        permissions: [...this.selected()],
      },
      this.intent,
    );
    if (answer && !this.destroyRef.destroyed && this.opened() && this.store.createOpen()) {
      this.slot.show({ source: 'api-token', value: answer.token });
    }
  }
  protected escape(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    if (!this.revealing() && !this.store.creating()) this.close();
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
