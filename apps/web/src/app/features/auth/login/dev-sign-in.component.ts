import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';

import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { DevSession } from './dev-session';

type SignInState = 'idle' | 'submitting' | 'wrong-secret' | 'unavailable' | 'failed';

@Component({
  selector: 'app-dev-sign-in',
  providers: [DevSession],
  imports: [HlmButton, HlmInput, HlmLabel, InlineFeedbackComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'block min-w-0 border-0 border-t-(length:--eg-border-width) border-solid border-border pt-(--eg-space-3)',
  },
  template: `
    <details #disclosure (toggle)="expanded.set(disclosure.open)">
      <summary
        id="dev-sign-in-heading"
        class="min-h-(--eg-control-touch) cursor-pointer content-center rounded-(--eg-radius-sm) text-sm font-(weight:--eg-weight-semibold) text-foreground wrap-anywhere hover:text-primary hover:underline focus-visible:outline-solid focus-visible:outline-(length:--eg-focus-width) focus-visible:outline-offset-(--eg-focus-offset) focus-visible:outline-ring active:text-primary"
      >
        {{ heading }}
      </summary>
      @if (expanded()) {
        <form
          class="grid min-w-0 gap-(--eg-space-4) rounded-(--eg-radius-md) bg-(--eg-surface-raised) p-(--eg-space-5) text-foreground"
          (submit)="submit($event, secret)"
          aria-labelledby="dev-sign-in-heading"
          [attr.aria-busy]="state() === 'submitting'"
        >
          <p class="m-0 text-sm text-muted-foreground" id="dev-sign-in-note">{{ note }}</p>
          <div class="grid min-w-0 gap-(--eg-space-3)">
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
                errorMessage() ? 'dev-sign-in-note dev-sign-in-error' : 'dev-sign-in-note'
              "
            />
          </div>
          <button
            hlmBtn
            [variant]="'outline'"
            class="h-auto min-h-(--eg-control-touch) whitespace-normal"
            type="submit"
            [disabled]="state() === 'submitting'"
          >
            {{ heading }}
          </button>
        </form>
        <!-- Keep the live regions outside the busy form so its pending state is announced immediately. -->
        <div
          class="mt-(--eg-space-3)"
          [class.sr-only]="state() !== 'submitting'"
          role="status"
          [attr.aria-live]="'polite'"
          aria-atomic="true"
        >
          @if (state() === 'submitting') {
            <app-inline-feedback [title]="submitting" [announcement]="'off'" />
          }
        </div>
        <div
          id="dev-sign-in-error"
          class="mt-(--eg-space-3)"
          [class.sr-only]="!errorMessage()"
          role="alert"
          aria-atomic="true"
        >
          @if (errorMessage(); as message) {
            <app-inline-feedback [tone]="'destructive'" [title]="message" [announcement]="'off'" />
          }
        </div>
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
  readonly failed = $localize`:@@auth.dev.failed:Sign-in could not be completed or your session could not be verified. Check your connection and try again.`;

  protected readonly errorMessage = computed(() => {
    switch (this.state()) {
      case 'wrong-secret':
        return this.wrongSecret;
      case 'unavailable':
        return this.unavailable;
      case 'failed':
        return this.failed;
      default:
        return null;
    }
  });

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
      if (error instanceof HttpErrorResponse && error.url?.endsWith('/v1/dev/session')) {
        this.state.set(
          error.status === 401 ? 'wrong-secret' : error.status === 404 ? 'unavailable' : 'failed',
        );
      } else {
        this.state.set('failed');
      }
    }
  }
}
