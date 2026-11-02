import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';

import { DevSession } from './dev-session';

type SignInState = 'idle' | 'submitting' | 'wrong-secret' | 'unavailable';

@Component({
  selector: 'app-dev-sign-in',
  providers: [DevSession],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../../shell/layout/shell-page.css', './dev-sign-in.component.css'],
  template: `
    <details #disclosure (toggle)="expanded.set(disclosure.open)">
      <summary id="dev-sign-in-heading">{{ heading }}</summary>
      @if (expanded()) {
        <form (submit)="submit($event, secret)" aria-labelledby="dev-sign-in-heading">
          <p id="dev-sign-in-note">{{ note }}</p>
          <label for="dev-sign-in-secret">{{ secretLabel }}</label>
          <input
            #secret
            id="dev-sign-in-secret"
            type="password"
            autocomplete="off"
            required
            [readOnly]="state() === 'submitting'"
            [attr.aria-invalid]="state() === 'wrong-secret' ? 'true' : null"
            [attr.aria-describedby]="
              state() === 'wrong-secret' || state() === 'unavailable'
                ? 'dev-sign-in-note dev-sign-in-error'
                : 'dev-sign-in-note'
            "
          />
          <button type="submit" [disabled]="state() === 'submitting'">{{ heading }}</button>
          <p role="status" aria-live="polite" aria-atomic="true">{{
            state() === 'submitting' ? submitting : ''
          }}</p>
          <p id="dev-sign-in-error" class="error" role="alert" aria-atomic="true">{{
            state() === 'wrong-secret' ? wrongSecret : state() === 'unavailable' ? unavailable : ''
          }}</p>
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
