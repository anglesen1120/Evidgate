import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { SessionStore } from '../../core/session/session.store';

@Component({
  selector: 'app-service-unavailable-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: '../layout/shell-page.css',
  template: `<section [attr.aria-busy]="retrying()">
    <h1>{{ heading }}</h1>
    <p>{{ explanation }}</p>
    <button type="button" [disabled]="retrying()" (click)="retry()">
      {{ retrying() ? pendingLabel : action }}
    </button>
    <p role="status">{{ failed() ? failureLabel : '' }}</p>
  </section>`,
})
export class ServiceUnavailablePage {
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  readonly retrying = signal(false);
  readonly failed = signal(false);
  readonly heading = $localize`:@@shell.page.unavailable.heading:Workspace unavailable`;
  readonly explanation = $localize`:@@shell.page.unavailable.explanation:We could not load your workspace. Try again.`;
  readonly action = $localize`:@@shell.page.unavailable.action:Try again`;
  readonly pendingLabel = $localize`:@@shell.page.unavailable.pending:Trying again…`;
  readonly failureLabel = $localize`:@@shell.page.unavailable.failed:The workspace is still unavailable. Try again later.`;

  async retry(): Promise<void> {
    if (this.retrying()) return;
    this.retrying.set(true);
    this.failed.set(false);
    try {
      await this.session.reload();
      await this.router.navigateByUrl('/');
    } catch {
      this.failed.set(true);
    } finally {
      this.retrying.set(false);
    }
  }
}
