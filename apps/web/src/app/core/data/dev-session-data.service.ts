// `core/data` is one of the two directories that own HTTP, so the development-only sign-in
// exchange lives here rather than in the feature that uses it. The feature never imports
// `HttpClient`; it calls this service like any other data service.
//
// Nothing in production imports this file — only `features/auth/login/login-extension.development`
// reaches it, through the dev sign-in component — so the production bundle tree-shakes it and the
// `/v1/dev/session` path stays a marker of `apps/web/scripts/check-no-fixture-code.mjs`.
//
// The response is 204 with the session cookie on `Set-Cookie`, so the request itself is the whole
// result and the body is `void`. The route is XSRF-exempt by contract (it sets the antiforgery pair
// rather than authenticating with a token), so nothing here depends on that header, and the relative
// `/v1` path is the page origin: `withCredentials` stays exactly as the feature call had it.
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { type Observable } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class DevSessionDataService {
  private readonly http = inject(HttpClient);

  /**
   * `POST /v1/dev/session` for `secret`. The observable is returned unsubscribed so the caller
   * keeps its own lifetime: the dev sign-in component cancels the exchange through its
   * `DestroyRef` exactly as before.
   */
  open(secret: string): Observable<void> {
    return this.http.post<void>('/v1/dev/session', { secret }, { withCredentials: true });
  }
}
