import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';

import { DevSession } from './dev-session';

type SignInState = 'idle' | 'submitting' | 'wrong-secret' | 'unavailable';

@Component({
  selector: 'app-dev-sign-in',
  providers: [DevSession],
  imports: [HlmButton, HlmInput, HlmLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'block min-w-0 border-0 border-t-(length:--eg-border-width) border-solid border-border pt-(--eg-space-5)',
  },
  template: `
    <details #disclosure (toggle)="expanded.set(disclosure.open)">
      <summary
        id="dev-sign-in-heading"
        class="min-h-(--eg-control-touch) cursor-pointer content-center text-base font-(weight:--eg-weight-semibold) [overflow-wrap:anywhere] hover:underline focus-visible:outline-solid focus-visible:outline-(length:--eg-focus-width) focus-visible:outline-ring active:text-primary"
      >
        {{ heading }}
      </summary>
      @if (expanded()) {
        <form
          class="grid min-w-0 gap-(--eg-space-3) pt-(--eg-space-3)"
          (submit)="submit($event, secret)"
          aria-labelledby="dev-sign-in-heading"
          [attr.aria-busy]="state() === 'submitting'"
        >
          <p class="m-0 text-sm text-muted-foreground" id="dev-sign-in-note">{{ note }}</p>
          <label hlmLabel for="dev-sign-in-secret">{{ secretLabel }}</label>
          <input
            hlmInput
            class="min-h-(--eg-control-touch) w-full min-w-0 read-only:bg-muted"
            #secret
            id="dev-sign-in-secret"
            type="password"
            autocomplete="off"
            required
            [readOnly]="state() === 'submitting'"
            [attr.aria-invalid]="state() === 'wrong-secret' ? 'true' : null"
            [aria-describedby]="
              state() === 'wrong-secret' || state() === 'unavailable'
                ? 'dev-sign-in-note dev-sign-in-error'
                : 'dev-sign-in-note'
            "
          />
          <button
            hlmBtn
            [variant]="'outline'"
            class="h-auto min-h-(--eg-control-touch) whitespace-normal"
            type="submit"
            [disabled]="state() === 'submitting'"
          >
            {{ heading }}
          </button>
          <p
            class="m-0 min-h-(--eg-leading-sm) text-sm text-muted-foreground"
            role="status"
            [attr.aria-live]="'polite'"
            aria-atomic="true"
          >
            {{ state() === 'submitting' ? submitting : '' }}
          </p>
          <p
            id="dev-sign-in-error"
            class="error m-0 min-h-(--eg-leading-sm) rounded-sm text-sm not-empty:bg-(--eg-danger-bg) not-empty:p-(--eg-space-4) not-empty:text-(--eg-danger-fg)"
            role="alert"
            aria-atomic="true"
          >
            {{
              state() === 'wrong-secret'
                ? wrongSecret
                : state() === 'unavailable'
                  ? unavailable
                  : ''
            }}
          </p>
        </form>
      }
    </details>
  `,
})
export class DevSignInComponent {
  private readonly session = inject(DevSession);
  protected readonly state = signal<SignInState>('idle');
  protected readonly expanded = signal(false);

  readonly heading = $localize`:@@auth.dev.heading:Dev sign-in`;
  readonly note = $localize`:@@auth.dev.note:Development only. Use the configured dev-login secret instead of a provider account.`;
  readonly secretLabel = $localize`:@@auth.dev.secret:Dev-login secret`;
  readonly submitting = $localize`:@@auth.dev.submitting:Signing in and checking your session…`;
  readonly wrongSecret = $localize`:@@auth.dev.wrong-secret:The dev-login secret is incorrect. Try again.`;
  readonly unavailable = $localize`:@@auth.dev.unavailable:Dev sign-in is not enabled. Check the development server configuration.`;

  async submit(event: Event, input: HTMLInputElement): Promise<void> {
    event.preventDefault();
    if (this.state() === 'submitting' || input.value.length === 0) {
      return;
    }
    this.state.set('submitting');
    const attempt = this.session.signIn(input.value);
    // Keep the secret only in the request, not in component state or the rendered form.
    input.value = '';
    try {
      await attempt;
      this.state.set('idle');
    } catch (error: unknown) {
      this.state.set(
        error instanceof HttpErrorResponse && error.status === 401 ? 'wrong-secret' : 'unavailable',
      );
    }
  }
}
