// The development-only sign-in exchange, in `core/data` because that is where the app's HTTP
// boundary lives. Only the `development`/`fixtures` login extension reaches it, through a lazily
// imported dev sign-in component, so a production build has no import edge to it and
// `/v1/dev/session` stays a marker of `apps/web/scripts/check-no-fixture-code.mjs`.
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
