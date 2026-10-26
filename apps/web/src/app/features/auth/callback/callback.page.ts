// 09.1.1-T5.1 (plan 09.1.1 §10 T5.1; D-S98-Q2; contracts/openapi.yaml `/v1/auth/oidc/{provider}/…`):
// the post-login landing. The API's OIDC callback sets the session cookie and 302s here with
// `?returnUrl=<validated target>`; this page re-reads the session from the server and forwards the
// caller to that target.
//
// Two rules the page keeps, and why they are structural rather than a convention:
//
// 1. It NEVER reads, logs or renders `code` or `state`. The OAuth exchange belongs to the API (the
//    provider calls the API's own callback), so those parameters do not even belong to this app; this
//    page reads exactly one query parameter (`returnUrl`) and nothing else. A `code` that reaches this
//    URL is a misconfigured provider registration, not something a client can finish.
// 2. It never stays on screen. Every outcome is a `replaceUrl` redirect — the target when a session
//    exists, `/login?reason=failed` when the probe says anonymous, `/login?reason=unavailable` when
//    the probe could not be completed — so the callback address is not left in the history and a
//    reload of it cannot replay a stale sign-in.
//
// The target is validated again here (not only where it was built): the value arrives in a URL a
// browser or a person can edit, and a tampered value becomes `/` (the landing guard then resolves the
// first org/project from the server's own lists).

import { ChangeDetectionStrategy, Component, inject, type OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';

import { SessionStore } from '../../../core/session/session.store';
import { LoadingPatternComponent } from '../../../shared/ui/states';
import { validateReturnUrl } from '../../../core/routing/return-url';

@Component({
  selector: 'app-auth-callback',
  standalone: true,
  imports: [LoadingPatternComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../../../shared/ui/states/state-panel.css', './callback.page.css'],
  template: `
    <main class="callback" aria-labelledby="auth-callback-heading">
      <h1 id="auth-callback-heading">{{ heading }}</h1>
      <p class="callback__explanation">{{ explanation }}</p>
      <app-loading-pattern [label]="loadingLabel" />
    </main>
  `,
})
export class CallbackPage implements OnInit {
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
