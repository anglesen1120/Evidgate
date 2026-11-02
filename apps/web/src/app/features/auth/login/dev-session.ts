import { DestroyRef, Injectable, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import { DevSessionDataService } from '../../../core/data/dev-session-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { AuthFlow } from '../auth-flow';

@Injectable()
export class DevSession {
  private readonly data = inject(DevSessionDataService);
  private readonly session = inject(SessionStore);
  private readonly auth = inject(AuthFlow);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  async signIn(secret: string): Promise<void> {
    await firstValueFrom(this.data.open(secret).pipe(takeUntilDestroyed(this.destroyRef)));
    this.session.reauth();
    await this.session.reload();
    if (this.destroyRef.destroyed) {
      return;
    }
    if (this.session.sessionState() !== 'authenticated') {
      throw new Error('Session verification did not complete.');
    }
    await this.router.navigateByUrl(this.auth.returnUrl(), { replaceUrl: true });
  }
}
