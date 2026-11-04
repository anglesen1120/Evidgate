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
import { HlmAlert, HlmAlertDescription } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardHeader, HlmCardTitle, HlmCardDescription } from '@evidgate/ui/card';

import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { ThemeService } from '../../../shell/layout/theme.service';
import { AuthFlow, LOCATION_NAVIGATE } from '../auth-flow';
import { LOGIN_EXTENSION_LOADER } from './login-extension';

@Component({
  selector: 'app-login-page',
  // The flow belongs to this visit so callback and expiry notices cannot retain an earlier query.
  providers: [AuthFlow],
  imports: [
    NgComponentOutlet,
    ShellIconComponent,
    HlmAlert,
    HlmAlertDescription,
    HlmButton,
    HlmCard,
    HlmCardHeader,
    HlmCardTitle,
    HlmCardDescription,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'box-border flex min-h-dvh min-w-0 flex-col bg-background font-sans text-foreground',
  },
  template: `
    <header
      class="login-topbar box-border flex min-h-(--eg-space-12) flex-wrap items-center justify-between gap-(--eg-space-4) border-0 border-b-(length:--eg-border-width) border-solid border-border bg-card px-(--eg-space-4) py-(--eg-space-3) min-[421px]:px-(--eg-space-7)"
    >
      <div
        class="login-brand flex items-center gap-(--eg-space-3) font-(family-name:--eg-font-display) text-lg font-(weight:--eg-weight-bold)"
      >
        <app-shell-icon class="text-primary" [name]="'shield-check'" />
        <span i18n="@@shell.layout.brand">Evidgate</span>
      </div>
      <div class="login-preferences flex items-center gap-(--eg-space-3)">
        @if (canSwitchLocale) {
          <button
            type="button"
            hlmBtn
            [variant]="'outline'"
            class="locale-toggle min-h-(--eg-control-touch)"
            [disabled]="auth.redirecting()"
            [attr.aria-label]="localeLabel"
            (click)="switchLocale()"
          >
            <span lang="en" i18n="@@auth.login.locale-en">EN</span>
            <span aria-hidden="true"> / </span>
            <span lang="vi" i18n="@@auth.login.locale-vi">VI</span>
          </button>
        }
        <button
          type="button"
          hlmBtn
          [variant]="'outline'"
          [size]="'icon'"
          class="theme-toggle min-h-(--eg-control-touch) min-w-(--eg-control-touch) aria-pressed:bg-accent aria-pressed:text-accent-foreground"
          [attr.aria-label]="darkLabel"
          [attr.aria-pressed]="theme.isDark()"
          (click)="theme.toggle()"
        >
          <app-shell-icon [name]="theme.isDark() ? 'moon' : 'sun'" />
        </button>
      </div>
    </header>
    <main
      class="box-border mx-auto grid w-full min-w-0 max-w-[calc(2*var(--eg-rail-width))] grid-cols-1 items-start gap-(--eg-space-7) p-(--eg-space-4) min-[421px]:p-(--eg-space-7) min-[900px]:my-auto min-[900px]:max-w-[calc(18*var(--eg-space-12))] min-[900px]:grid-cols-2 min-[900px]:gap-(--eg-space-8) min-[900px]:py-(--eg-space-10)"
      aria-labelledby="login-heading"
    >
      <header
        class="login-intro grid gap-(--eg-space-5) [overflow-wrap:anywhere] min-[900px]:py-(--eg-space-7)"
      >
        <h1
          #loginHeading
          class="m-0 font-(family-name:--eg-font-display) text-xl font-(weight:--eg-weight-bold) focus-visible:outline-solid focus-visible:outline-(length:--eg-focus-width) focus-visible:outline-offset-(--eg-focus-offset) focus-visible:outline-ring min-[900px]:text-2xl"
          id="login-heading"
          tabindex="-1"
          [textContent]="heading"
        ></h1>
        <p class="m-0 text-sm text-muted-foreground">{{ introduction }}</p>
      </header>
      <section
        hlmCard
        class="login-card min-w-0 gap-(--eg-space-5) p-(--eg-space-4) [overflow-wrap:anywhere] min-[421px]:p-(--eg-space-7) min-[900px]:col-start-2 min-[900px]:row-span-2"
        aria-labelledby="provider-heading"
      >
        <header hlmCardHeader class="gap-(--eg-space-3) p-0">
          <h2
            hlmCardTitle
            class="m-0 font-(family-name:--eg-font-display) text-lg"
            id="provider-heading"
            [textContent]="welcome"
          ></h2>
          <p hlmCardDescription class="m-0">{{ explanation }}</p>
        </header>

        <!-- Keep the live region mounted and outside the busy controls. -->
        <div
          class="login-notice min-h-(--eg-leading-sm) min-w-0"
          role="status"
          [attr.aria-live]="'polite'"
          aria-atomic="true"
        >
          @if (auth.redirecting() || auth.reason() !== 'none') {
            <div
              hlmAlert
              [variant]="
                !auth.redirecting() &&
                (auth.reason() === 'failed' || auth.reason() === 'unavailable')
                  ? 'destructive'
                  : 'default'
              "
              [attr.role]="null"
            >
              <div hlmAlertDescription class="gap-(--eg-space-3) [&>p]:m-0">
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
            </div>
          }
        </div>

        <div
          class="login-providers grid min-w-0 gap-(--eg-space-4)"
          [attr.aria-busy]="auth.redirecting()"
        >
          <button
            hlmBtn
            class="primary-provider h-auto min-h-(--eg-control-touch) w-full whitespace-normal"
            type="button"
            [disabled]="auth.redirecting()"
            (click)="auth.start('google')"
          >
            {{ google }}
          </button>
          <button
            hlmBtn
            [variant]="'outline'"
            class="h-auto min-h-(--eg-control-touch) w-full whitespace-normal"
            type="button"
            [disabled]="auth.redirecting()"
            (click)="auth.start('github')"
          >
            {{ github }}
          </button>
        </div>

        <p
          class="login-credential-note m-0 border-0 border-t-(length:--eg-border-width) border-solid border-border pt-(--eg-space-3) text-sm text-muted-foreground"
        >
          {{ credentialNote }}
        </p>
        @if (extension.value(); as component) {
          <ng-container *ngComponentOutlet="component" />
        }
      </section>
      <section
        hlmCard
        class="login-destination min-w-0 gap-(--eg-space-5) p-(--eg-space-4) [overflow-wrap:anywhere] min-[421px]:p-(--eg-space-7) min-[900px]:col-start-1"
        aria-labelledby="destination-heading"
      >
        <h2
          hlmCardTitle
          class="m-0 font-(family-name:--eg-font-display) text-lg"
          id="destination-heading"
          [textContent]="destinationHeading"
        ></h2>
        <p class="m-0 text-sm text-muted-foreground">
          {{ auth.returnUrl() === '/' ? defaultDestination : savedDestination }}
        </p>
        <p class="m-0 text-sm text-muted-foreground">{{ destinationPrivacy }}</p>
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
  private readonly localeBase = new URL(this.document.baseURI).pathname;
  private readonly localeSegment = /(?:^|\/)(?:en|vi)\/$/.exec(this.localeBase);
  readonly canSwitchLocale = this.localeSegment !== null;
  readonly darkLabel = $localize`:@@shell.theme.dark-mode:Dark mode`;
  readonly localeLabel = $localize`:@@auth.login.switch-language:EN / VI: Switch language between English and Vietnamese`;
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
    if (this.auth.redirecting() || this.localeSegment === null) return;
    const target = this.locale.startsWith('vi') ? 'en' : 'vi';
    // Localized builds are siblings; retain a deployment prefix when one is present.
    const prefix = this.localeBase.slice(0, this.localeSegment.index);
    const query = new URLSearchParams({ returnUrl: this.auth.returnUrl() });
    if (this.auth.reason() !== 'none') query.set('reason', this.auth.reason());
    this.navigate(`${prefix}/${target}/login?${query.toString()}`);
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
