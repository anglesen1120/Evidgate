import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import {
  HlmCard,
  HlmCardContent,
  HlmCardDescription,
  HlmCardHeader,
  HlmCardTitle,
} from '@evidgate/ui/card';
import { SessionStore } from '../../core/session/session.store';

@Component({
  selector: 'app-service-unavailable-page',
  imports: [HlmButton, HlmCard, HlmCardContent, HlmCardDescription, HlmCardHeader, HlmCardTitle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 font-sans text-foreground' },
  template: `<section
    hlmCard
    class="min-w-0 gap-(--eg-space-5) py-(--eg-space-6)"
    [attr.aria-busy]="retrying()"
    aria-labelledby="unavailable-heading"
  >
    <header hlmCardHeader>
      <h1
        hlmCardTitle
        [size]="pageHeadingSize"
        id="unavailable-heading"
        [textContent]="heading"
      ></h1>
      <p hlmCardDescription class="max-w-prose text-sm">{{ explanation }}</p>
    </header>
    <div hlmCardContent class="grid justify-items-start gap-(--eg-space-3)">
      <button
        hlmBtn
        class="h-auto min-h-(--eg-control-touch) max-w-full whitespace-normal"
        type="button"
        [disabled]="retrying()"
        [attr.aria-busy]="retrying()"
        (click)="retry()"
      >
        {{ retrying() ? pendingLabel : action }}
      </button>
      <p
        class="m-0 min-h-(--eg-leading-sm) max-w-prose text-sm text-muted-foreground"
        role="status"
      >
        {{ failed() ? failureLabel : '' }}
      </p>
    </div>
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
