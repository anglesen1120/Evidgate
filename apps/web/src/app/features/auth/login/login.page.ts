import { DOCUMENT, NgComponentOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  LOCALE_ID,
  computed,
  effect,
  inject,
  resource,
  viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import {
  HlmCard,
  HlmCardContent,
  HlmCardFooter,
  HlmCardHeader,
  HlmCardTitle,
} from '@evidgate/ui/card';

import {
  InlineFeedbackComponent,
  type InlineFeedbackTone,
} from '../../../shared/ui/states/inline-feedback.component';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { ThemeService } from '../../../shell/layout/theme.service';
import {
  LocaleSwitcherComponent,
  localeBundlePath,
} from '../../../shell/layout/locale-switcher.component';
import { AuthFlow, LOCATION_NAVIGATE } from '../auth-flow';
import { LOGIN_EXTENSION_LOADER } from './login-extension';

interface LoginNotice {
  readonly tone: InlineFeedbackTone;
  readonly title: string;
  readonly description?: string;
}

@Component({
  selector: 'app-login-page',
  // The flow belongs to this visit so callback and expiry notices cannot retain an earlier query.
  providers: [AuthFlow],
  imports: [
    NgComponentOutlet,
    ShellIconComponent,
    LocaleSwitcherComponent,
    InlineFeedbackComponent,
    HlmButton,
    HlmCard,
    HlmCardHeader,
    HlmCardTitle,
    HlmCardContent,
    HlmCardFooter,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'box-border flex min-h-dvh min-w-0 flex-col bg-background font-sans text-foreground',
  },
  templateUrl: './login.page.html',
})
export class LoginPage {
  protected readonly auth = inject(AuthFlow);

  private readonly theme = inject(ThemeService);

  private readonly document = inject(DOCUMENT);

  private readonly locale = inject(LOCALE_ID);

  private readonly navigate = inject(LOCATION_NAVIGATE);

  private readonly loadExtension = inject(LOGIN_EXTENSION_LOADER);

  private readonly router = inject(Router);

  protected readonly isDark = this.theme.isDark;
  protected readonly pageHeadingSize = 'page' as const;
  protected readonly subsectionHeadingSize = 'subsection' as const;
  private readonly localeBase = this.document.baseURI;
  readonly canSwitchLocale = localeBundlePath(this.localeBase, this.locale, '/login') !== null;
  readonly darkLabel = $localize`:@@shell.theme.dark-mode:Dark mode`;
  protected readonly extension = resource({ loader: () => this.loadExtension() });
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

  protected toggleTheme(): void {
    this.theme.toggle();
  }

  switchLocale(): void {
    if (this.auth.redirecting()) return;
    const query = new URLSearchParams({ returnUrl: this.auth.returnUrl() });
    if (this.auth.reason() !== 'none') query.set('reason', this.auth.reason());
    const destination = localeBundlePath(
      this.localeBase,
      this.locale,
      `/login?${query.toString()}`,
    );
    if (destination !== null) this.navigate(destination);
  }

  protected readonly organizationSignIn = $localize`:@@auth.login.organization-sign-in:Organization sign-in`;
  readonly welcome = $localize`:@@auth.login.welcome:Welcome back`;
  readonly destinationHeading = $localize`:@@auth.login.destination-heading:Your return destination`;
  readonly savedDestination = $localize`:@@auth.login.saved-destination:After sign-in, continue to your saved workspace destination. Access is checked before it opens.`;
  readonly defaultDestination = $localize`:@@auth.login.default-destination:After sign-in, continue to your workspace.`;
  readonly destinationPrivacy = $localize`:@@auth.login.destination-privacy:Private route details are not displayed on this sign-in page.`;

  readonly heading = $localize`:@@auth.login.heading:Sign in to Evidgate`;
  protected readonly accountGuidance = $localize`:@@auth.login.organization-account:Use your organization account. Your workspace membership is checked after sign-in.`;
  readonly google = $localize`:@@auth.login.google:Continue with Google`;
  readonly github = $localize`:@@auth.login.github:Continue with GitHub`;
  readonly credentialNote = $localize`:@@auth.login.organization-access:Access is managed by your organization. No local password is required.`;
  readonly redirecting = $localize`:@@auth.login.redirecting:Opening your sign-in provider…`;
  readonly expired = $localize`:@@auth.login.expired:Your session expired. Sign in again to continue.`;
  readonly signedOut = $localize`:@@auth.login.signed-out:Signed out on this tab`;
  readonly signedOutDetail = $localize`:@@auth.login.signed-out-detail:Your browser session may still be valid. Server sign-out is not available yet.`;
  readonly unavailable = $localize`:@@auth.login.unavailable:Sign-in is temporarily unavailable. Choose a provider to try again.`;
  readonly failed = $localize`:@@auth.login.failed:Sign-in failed. Choose a provider to try again.`;

  protected readonly notice = computed<LoginNotice | null>(() => {
    if (this.auth.redirecting()) return { tone: 'info', title: this.redirecting };
    switch (this.auth.reason()) {
      case 'expired':
        return { tone: 'info', title: this.expired };
      case 'signed-out':
        return { tone: 'info', title: this.signedOut, description: this.signedOutDetail };
      case 'unavailable':
        return { tone: 'destructive', title: this.unavailable };
      case 'failed':
        return { tone: 'destructive', title: this.failed };
      case 'none':
        return null;
    }
  });
}
