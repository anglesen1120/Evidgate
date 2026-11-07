import { ChangeDetectionStrategy, Component, computed, inject, linkedSignal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { NotFoundPatternComponent } from '../../../shared/ui/states/not-found-pattern.component';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { ApiTokenListComponent } from './api-token-list.component';
import { API_TOKENS_COPY } from './api-tokens.copy';
import { ApiTokensStore } from './api-tokens.store';
import type { ApiTokenView } from '@evidgate/sdk';
import { ApiTokenCreateDialogComponent } from './api-token-create-dialog.component';
import { ApiTokenRevokeDialogComponent } from './api-token-revoke-dialog.component';
import { API_TOKENS_WRITE_COPY } from './api-tokens-write.copy';
import { retryReadyFor } from './api-token-retry';

@Component({
  selector: 'app-api-tokens-page',
  imports: [
    HlmButton,
    HlmInput,
    HlmLabel,
    RequestStateComponent,
    NotFoundPatternComponent,
    ApiTokenListComponent,
    ApiTokenCreateDialogComponent,
    ApiTokenRevokeDialogComponent,
  ],
  providers: [ApiTokensStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 text-sm text-foreground wrap-anywhere' },
  template: `
    <header class="mb-4 grid min-w-0 gap-2">
      <h2 tabindex="-1" class="m-0 font-[family-name:var(--eg-font-display)] text-2xl">
        {{ copy.title }}
      </h2>
      <p class="m-0 text-muted-foreground">{{ copy.description }}</p>
    </header>
    @if (store.session.projects().length > 0) {
      <div class="mb-4 grid gap-2">
        <label hlmLabel for="api-tokens-project">{{ copy.project }}</label>
        <select
          hlmInput
          id="api-tokens-project"
          class="min-h-[var(--eg-control-touch)]"
          [value]="store.selectedProject()"
          (change)="selectProject($event)"
        >
          @for (project of store.session.projects(); track project.id) {
            <option [value]="project.id">{{ project.name }}</option>
          }
        </select>
      </div>
    }
    @if (store.canWrite() && store.scope() && store.phase() !== 'forbidden') {
      <p class="text-muted-foreground md:hidden">{{ copy.narrow }}</p>
      @if (store.phase() !== 'empty') {
        <button
          hlmBtn
          type="button"
          class="mb-4 hidden min-h-[var(--eg-control-touch)] md:inline-flex"
          data-api-create
          [disabled]="store.creating() || store.revoking()"
          (click)="store.openCreate()"
        >
          {{ copy.create }}
        </button>
      }
    }
    @if (store.phase() === 'forbidden') {
      <app-not-found-pattern />
    } @else {
      <app-request-state
        [status]="status()"
        [error]="store.loadError()"
        [empty]="store.phase() === 'empty'"
        [emptyTitle]="copy.emptyTitle"
        [emptyDescription]="store.selectedProject() ? copy.emptyDescription : copy.noProjects"
        [retryEligible]="retryReady() && (store.loadError()?.retryable ?? false)"
        (retry)="reload()"
      >
        <app-api-token-list
          [rows]="store.rows()"
          [canWrite]="store.canWrite()"
          [busy]="store.creating() || store.revoking()"
          (revoke)="revokeSelection.set($event)"
        />
        <nav class="mt-3 flex flex-wrap gap-2" [attr.aria-label]="copy.pages">
          @if (store.cursor()) {
            <button
              hlmBtn
              [variant]="'outline'"
              type="button"
              class="min-h-[var(--eg-control-touch)]"
              (click)="first()"
            >
              {{ copy.first }}
            </button>
          }
          @if (store.hasNext()) {
            <button
              hlmBtn
              [variant]="'outline'"
              type="button"
              class="min-h-[var(--eg-control-touch)]"
              (click)="next()"
            >
              {{ copy.next }}
            </button>
          }
        </nav>
      </app-request-state>
      @if (store.phase() === 'empty' && store.canWrite() && store.scope()) {
        <button
          hlmBtn
          type="button"
          class="mt-3 hidden min-h-[var(--eg-control-touch)] md:inline-flex"
          data-api-create
          [disabled]="store.creating() || store.revoking()"
          (click)="store.openCreate()"
        >
          {{ copy.create }}
        </button>
      }
    }
    <p role="status">{{ revoked() ? writeCopy.revoked : '' }}</p>
    @if (store.createOpen()) {
      <app-api-token-create-dialog />
    }
    @if (store.canWrite() && revokeSelection(); as row) {
      <app-api-token-revoke-dialog
        [row]="row"
        (dismissed)="revokeSelection.set(null)"
        (completed)="revoked.set(true)"
      />
    }
  `,
})
export class ApiTokensPage {
  protected readonly store = inject(ApiTokensStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  protected readonly copy = API_TOKENS_COPY;
  protected readonly status = computed<'loading' | 'ready' | 'error'>(() => {
    const phase = this.store.phase();
    return phase === 'loading' ? 'loading' : phase === 'error' ? 'error' : 'ready';
  });
  protected readonly retryReady = retryReadyFor(this.store.loadError);
  protected readonly writeCopy = API_TOKENS_WRITE_COPY;
  protected readonly revokeSelection = linkedSignal<unknown, ApiTokenView | null>({
    source: () => ({ scope: this.store.scope(), writable: this.store.canWrite() }),
    computation: () => null,
  });
  protected readonly revoked = linkedSignal(() => {
    this.store.scope();
    return false;
  });

  protected selectProject(event: Event): void {
    const target = event.target;
    if (target instanceof HTMLSelectElement) {
      this.store.selectProject(target.value);
      this.updateCursor();
    }
  }

  protected reload(): void {
    if (this.retryReady()) this.store.reload();
  }

  protected next(): void {
    this.store.next();
    this.updateCursor(this.store.cursor());
  }

  protected first(): void {
    this.store.setCursor(undefined);
    this.updateCursor();
  }

  private updateCursor(cursor?: string): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { cursor: cursor ?? null },
      queryParamsHandling: 'merge',
    });
  }
}
