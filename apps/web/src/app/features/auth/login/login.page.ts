import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  inject,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';

import { AuthFlow } from '../auth-flow';

@Component({
  selector: 'app-login-page',
  standalone: true,
  // 09.1.1-T5 review (delta): the flow belongs to THIS page instance. A route-level `providers`
  // entry is built once per Route object in the router's environment injector, so it would keep the
  // first visit's query and resolve `ActivatedRoute` from the root chain. In the component injector
  // the route is the activation's own, and the flow (with its `pageshow` listener) is destroyed with
  // the page.
  providers: [AuthFlow],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../../shell/layout/shell-page.css', './login.page.css'],
  template: `
    <main aria-labelledby="login-heading">
      <section class="login-card">
        <header>
          <h1 #loginHeading id="login-heading" tabindex="-1">{{ heading }}</h1>
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
  private readonly router = inject(Router);
  /** The page's own focus target: `tabindex="-1"`, so it is reachable but never a Tab stop. */
  private readonly headingTarget = viewChild<ElementRef<HTMLElement>>('loginHeading');

  /**
   * F-LOGIN-01 (09.8.1-T1.2): `/login` renders OUTSIDE the shell (`app.routes.ts` spreads
   * `AUTH_ROUTES` at the top level), so the shell's path-change handoff
   * (`shell.component.ts:78-89`) never runs for it. Every client-side entry — the callback's
   * `failed`/`unavailable` redirect, the store's `expired`/`signed-out` redirect — would leave focus
   * on `body`: the top of a document the visitor did not load, with nothing said about the new page.
   *
   * `Router.navigated` is the same marker the shell uses: false while the initial document
   * navigation is still activating (a fresh load keeps the browser's own focus start, the visitor's
   * first Tab reaches the first provider button), true once any navigation has ENDed — which is
   * exactly "this page was reached from inside the app".
   */
  private readonly arrivedByNavigation = this.router.navigated;

  constructor() {
    // Reading the ref inside the effect keeps it tracked, so the pass that resolves the view query
    // re-runs it with the mounted heading.
    effect(() => {
      if (!this.arrivedByNavigation) {
        return;
      }
      this.headingTarget()?.nativeElement.focus();
    });

    // F-LOGIN-02 (09.8.1-T1.2): activating a provider disables the button that holds focus
    // (`[disabled]="auth.redirecting()"`), and a disabled element cannot keep focus — the browser
    // drops it to `body`, leaving the still-rendered page with no focus indicator at all until the
    // document leaves, and a Back return re-enters it with focus on `body`. Focus moves to the
    // page's own heading while the redirect is pending, so the ring stays visible and the browser
    // restores the same element on a Back return. The live region (`role="status"`) still announces
    // the state on its own.
    effect(() => {
      if (!this.auth.redirecting()) {
        return;
      }
      this.headingTarget()?.nativeElement.focus();
    });
  }

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
