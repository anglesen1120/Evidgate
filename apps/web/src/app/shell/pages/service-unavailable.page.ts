import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle, HlmCardDescription } from '@evidgate/ui/card';
import { SessionStore } from '../../core/session/session.store';

@Component({
  selector: 'app-service-unavailable-page',
  imports: [HlmButton, HlmCard, HlmCardTitle, HlmCardDescription],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 font-sans text-foreground' },
  template: `<section
    hlmCard
    class="min-w-0 items-start gap-(--eg-space-4) px-(--eg-space-4) py-(--eg-space-8) [overflow-wrap:anywhere] min-[768px]:px-(--eg-space-5)"
    [attr.aria-busy]="retrying()"
    aria-labelledby="unavailable-heading"
  >
    <h1 hlmCardTitle [size]="pageHeadingSize" id="unavailable-heading" [textContent]="heading"></h1>
    <p hlmCardDescription class="m-0">{{ explanation }}</p>
    <button
      hlmBtn
      class="h-auto min-h-(--eg-control-touch) max-w-full whitespace-normal"
      type="button"
      [disabled]="retrying()"
      (click)="retry()"
    >
      {{ retrying() ? pendingLabel : action }}
    </button>
    <p class="m-0 min-h-(--eg-leading-sm) text-sm text-muted-foreground" role="status">
      {{ failed() ? failureLabel : '' }}
    </p>
  </section>`,
})
export class ServiceUnavailablePage {
  private readonly session = inject(SessionStore);

  private readonly router = inject(Router);

  readonly pageHeadingSize = 'page' as const;
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
