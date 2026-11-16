// The API owns the OAuth exchange. This landing reads only the validated return destination,
// reloads the session and replaces history with the destination or a sign-in failure notice.

import { ChangeDetectionStrategy, Component, inject, type OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmCardTitle } from '@evidgate/ui/card';

import { SessionStore } from '../../../core/session/session.store';
import { LoadingPatternComponent } from '../../../shared/ui/states';
import { validateReturnUrl } from '../../../core/routing/return-url';

@Component({
  selector: 'app-auth-callback',
  imports: [LoadingPatternComponent, HlmCardTitle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <main
      class="callback box-border grid min-h-dvh content-center justify-items-center gap-(--eg-space-4) bg-card px-(--eg-space-5) py-(--eg-space-8) text-center font-sans text-foreground [overflow-wrap:anywhere]"
      aria-labelledby="auth-callback-heading"
    >
      <h1 hlmCardTitle [size]="pageHeadingSize" id="auth-callback-heading">
        {{ heading }}
      </h1>
      <p
        class="callback__explanation m-0 max-w-[calc(2*var(--eg-rail-width))] text-sm text-muted-foreground"
      >
        {{ explanation }}
      </p>
      <app-loading-pattern [label]="loadingLabel" />
    </main>
  `,
})
export class CallbackPage implements OnInit {
  readonly pageHeadingSize = 'page' as const;
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly session = inject(SessionStore);

  readonly heading = $localize`:@@auth.callback.heading:Finishing sign-in`;
  readonly explanation = $localize`:@@auth.callback.explanation:Checking your session, then taking you back.`;
  readonly loadingLabel = $localize`:@@auth.callback.loading:Checking your session…`;

  ngOnInit(): void {
    // Nothing awaits this: the page only redirects, and a navigation the router itself cancels is
    // not an error to surface.
    void this.complete().catch(() => undefined);
  }

  /** The whole landing: probe, then one redirect. Exported for the spec. */
  async complete(): Promise<void> {
    const target = validateReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl'));
    try {
      await this.session.reload();
    } catch {
      // `reload()` rejects only for `unavailable`: the probe could not be completed, so the caller
      // is offered the sign-in page instead of a workspace the app cannot vouch for.
      await this.toLogin('unavailable');
      return;
    }
    if (this.session.sessionState() === 'authenticated') {
      await this.router.navigateByUrl(target, { replaceUrl: true });
      return;
    }
    // Anonymous: the cookie was not set (a cancelled or failed exchange, or a latched sign-out).
    await this.toLogin('failed');
  }

  private async toLogin(reason: 'unavailable' | 'failed'): Promise<void> {
    await this.router.navigate(['/login'], { queryParams: { reason }, replaceUrl: true });
  }
}
