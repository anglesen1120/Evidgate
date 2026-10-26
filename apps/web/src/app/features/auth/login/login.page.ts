import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { AuthFlow } from '../auth-flow';

@Component({
  selector: 'app-login-page',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../../shell/layout/shell-page.css', './login.page.css'],
  template: `
    <main aria-labelledby="login-heading">
      <section class="login-card">
        <header>
          <h1 id="login-heading">{{ heading }}</h1>
          <p>{{ explanation }}</p>
        </header>

        <!-- Keep the live region mounted and outside the busy controls. -->
        <div
          class="login-notice"
          [class.login-notice--info]="auth.redirecting() || auth.reason() === 'expired'"
          [class.login-notice--success]="!auth.redirecting() && auth.reason() === 'signed-out'"
          [class.login-notice--error]="
            !auth.redirecting() && (auth.reason() === 'failed' || auth.reason() === 'unavailable')
          "
          role="status"
          [attr.aria-live]="'polite'"
          aria-atomic="true"
        >
          @if (auth.redirecting()) {
            <p>{{ redirecting }}</p>
          } @else {
            @switch (auth.reason()) {
              @case ('expired') {
                <p>{{ expired }}</p>
              }
              @case ('signed-out') {
                <p>{{ signedOut }}</p>
                <p>{{ signedOutDetail }}</p>
              }
              @case ('unavailable') {
                <p>{{ unavailable }}</p>
              }
              @case ('failed') {
                <p>{{ failed }}</p>
              }
            }
          }
        </div>

        <div class="login-providers" [attr.aria-busy]="auth.redirecting()">
          <button type="button" [disabled]="auth.redirecting()" (click)="auth.start('google')">
            {{ google }}
          </button>
          <button type="button" [disabled]="auth.redirecting()" (click)="auth.start('github')">
            {{ github }}
          </button>
        </div>

        <p class="login-credential-note">{{ credentialNote }}</p>
      </section>
    </main>
  `,
})
export class LoginPage {
  protected readonly auth = inject(AuthFlow);

  readonly heading = $localize`:@@auth.login.heading:Sign in to Evidgate`;
  readonly explanation = $localize`:@@auth.login.explanation:Choose your provider to continue.`;
  readonly google = $localize`:@@auth.login.google:Continue with Google`;
  readonly github = $localize`:@@auth.login.github:Continue with GitHub`;
  readonly credentialNote = $localize`:@@auth.login.credential-note:Your provider account is your only sign-in credential. Evidgate does not ask for a separate email or password.`;
  readonly redirecting = $localize`:@@auth.login.redirecting:Opening your sign-in provider…`;
  readonly expired = $localize`:@@auth.login.expired:Your session expired. Sign in again to continue.`;
  readonly signedOut = $localize`:@@auth.login.signed-out:Signed out on this tab`;
  readonly signedOutDetail = $localize`:@@auth.login.signed-out-detail:Your browser session may still be valid. Server sign-out is not available yet.`;
  readonly unavailable = $localize`:@@auth.login.unavailable:Sign-in is temporarily unavailable. Choose a provider to try again.`;
  readonly failed = $localize`:@@auth.login.failed:Sign-in failed. Choose a provider to try again.`;
}
