import { DOCUMENT, NgComponentOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  LOCALE_ID,
  effect,
  inject,
  resource,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';

import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { ThemeService } from '../../../shell/layout/theme.service';
import { AuthFlow, LOCATION_NAVIGATE } from '../auth-flow';
import { LOGIN_EXTENSION_LOADER } from './login-extension';

@Component({
  selector: 'app-login-page',
  // The flow belongs to this visit so callback and expiry notices cannot retain an earlier query.
  providers: [AuthFlow],
  imports: [NgComponentOutlet, ShellIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../../shell/layout/shell-page.css', './login.page.css'],
  template: `
    <header class="login-topbar">
      <div class="login-brand">
        <app-shell-icon name="shield-check" />
        <span i18n="@@shell.layout.brand">Evidgate</span>
      </div>
      <div class="login-preferences">
        <button
          type="button"
          class="locale-toggle"
          [disabled]="auth.redirecting()"
          [attr.aria-label]="localeLabel"
          (click)="switchLocale()"
        >
          <span lang="en" i18n="@@auth.login.locale-en">EN</span>
          <span aria-hidden="true"> / </span>
          <span lang="vi" i18n="@@auth.login.locale-vi">VI</span>
        </button>
        <button
          type="button"
          class="theme-toggle"
          [attr.aria-label]="darkLabel"
          [attr.aria-pressed]="theme.isDark()"
          (click)="theme.toggle()"
        >
          <app-shell-icon [name]="theme.isDark() ? 'moon' : 'sun'" />
        </button>
      </div>
    </header>
    <main aria-labelledby="login-heading">
      <header class="login-intro">
        <h1 #loginHeading id="login-heading" tabindex="-1">{{ heading }}</h1>
        <p>{{ introduction }}</p>
      </header>
      <section class="login-card" aria-labelledby="provider-heading">
        <header>
          <h2 id="provider-heading">{{ welcome }}</h2>
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
          <button
            class="primary-provider"
            type="button"
            [disabled]="auth.redirecting()"
            (click)="auth.start('google')"
          >
            {{ google }}
          </button>
          <button type="button" [disabled]="auth.redirecting()" (click)="auth.start('github')">
            {{ github }}
          </button>
        </div>

        <p class="login-credential-note">{{ credentialNote }}</p>
        @if (extension.value(); as component) {
          <ng-container *ngComponentOutlet="component" />
        }
      </section>
      <section class="login-destination" aria-labelledby="destination-heading">
        <h2 id="destination-heading">{{ destinationHeading }}</h2>
        <p>{{ auth.returnUrl() === '/' ? defaultDestination : savedDestination }}</p>
        <p>{{ destinationPrivacy }}</p>
      </section>
    </main>
  `,
})
export class LoginPage {
  protected readonly auth = inject(AuthFlow);
  protected readonly theme = inject(ThemeService);
  private readonly document = inject(DOCUMENT);
  private readonly locale = inject(LOCALE_ID);
  private readonly navigate = inject(LOCATION_NAVIGATE);
  readonly darkLabel = $localize`:@@shell.theme.dark-mode:Dark mode`;
  readonly localeLabel = $localize`:@@auth.login.switch-language:Switch language between English and Vietnamese`;
  private readonly loadExtension = inject(LOGIN_EXTENSION_LOADER);
  protected readonly extension = resource({ loader: () => this.loadExtension() });
  private readonly router = inject(Router);
  /** The page's own focus target: `tabindex="-1"`, so it is reachable but never a Tab stop. */
  private readonly headingTarget = viewChild<ElementRef<HTMLElement>>('loginHeading');

  // Standalone auth routes do not receive the shell's client-navigation focus handoff.
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

    // Disabling the focused provider would drop focus to body while the browser is leaving.
    effect(() => {
      if (!this.auth.redirecting()) {
        return;
      }
      this.headingTarget()?.nativeElement.focus();
    });
  }

  switchLocale(): void {
    if (this.auth.redirecting()) return;
    const target = this.locale.startsWith('vi') ? 'en' : 'vi';
    const base = new URL(this.document.baseURI);
    // Localized builds are siblings; retain a deployment prefix when one is present.
    const prefix = base.pathname.replace(/(?:en|vi)\/$/, '');
    const query = new URLSearchParams({ returnUrl: this.auth.returnUrl() });
    if (this.auth.reason() !== 'none') query.set('reason', this.auth.reason());
    this.navigate(`${prefix}${target}/login?${query.toString()}`);
  }

  readonly introduction = $localize`:@@auth.login.introduction:Return to your workspace to review evidence and make informed release decisions.`;
  readonly welcome = $localize`:@@auth.login.welcome:Welcome back`;
  readonly destinationHeading = $localize`:@@auth.login.destination-heading:Your return destination`;
  readonly savedDestination = $localize`:@@auth.login.saved-destination:After sign-in, continue to your saved workspace destination. Access is checked before it opens.`;
  readonly defaultDestination = $localize`:@@auth.login.default-destination:After sign-in, continue to your workspace.`;
  readonly destinationPrivacy = $localize`:@@auth.login.destination-privacy:Private route details are not displayed on this sign-in page.`;

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
