import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  linkedSignal,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
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
              data-api-first
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
              data-api-next
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
        (completed)="onRevoked()"
      />
    }
  `,
})
export class ApiTokensPage {
  protected readonly store = inject(ApiTokensStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly query = toSignal(this.route.queryParamMap);
  protected readonly copy = API_TOKENS_COPY;
  protected readonly status = computed<'loading' | 'ready' | 'error'>(() => {
    const phase = this.store.phase();
    return phase === 'loading' ? 'loading' : phase === 'error' ? 'error' : 'ready';
  });
  protected readonly retryReady = retryReadyFor(this.store.loadError);
  protected readonly writeCopy = API_TOKENS_WRITE_COPY;
  // A revoke confirmation names a row of one project's list, so a scope that moves closes it
  // instead of offering that row against another project.
  protected readonly revokeSelection = linkedSignal<ApiTokenView | null>(() => {
    this.store.scope();
    this.store.canWrite();
    return null;
  });
  protected readonly revoked = linkedSignal(() => {
    this.store.scope();
    return false;
  });
  /** The first URL write would restate what the store already read from the URL. */
  private urlWritten = false;

  constructor() {
    // The URL carries the project with its cursor. A create, a dismissed ambiguous create or a
    // revoke conflict sends the list back to page one, so the URL has to drop the cursor too;
    // otherwise a reload restores a page whose row has gone.
    effect(() => {
      const project = this.store.selectedProject();
      const cursor = this.store.cursor() ?? null;
      untracked(() => this.syncQuery(project, cursor));
    });
  }

  protected selectProject(event: Event): void {
    const target = event.target;
    if (target instanceof HTMLSelectElement) this.store.selectProject(target.value);
  }

  protected reload(): void {
    if (this.retryReady()) this.store.reload();
  }

  protected next(): void {
    this.store.next();
  }

  protected first(): void {
    this.store.setCursor(undefined);
  }

  protected onRevoked(): void {
    this.revoked.set(true);
  }

  /**
   * The project is written with the cursor: a cursor alone names a page of an unknown list, so a
   * shared or restored URL would page through another project's tokens.
   */
  private syncQuery(project: string | null, cursor: string | null): void {
    if (!this.urlWritten) {
      this.urlWritten = true;
      return;
    }
    const current = this.query();
    if (
      (current?.get('project') ?? null) === project &&
      (current?.get('cursor') ?? null) === cursor
    )
      return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { project, cursor },
      queryParamsHandling: 'merge',
    });
  }
}
