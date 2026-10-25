import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import type { CiBindingView } from '@evidgate/sdk';
import type { ApiError } from '../../../core/http/api-error';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import type { CiBindingsPhase } from './secrets.store';

export const CI_BINDINGS_COPY = {
  title: $localize`:@@settings.secrets.ci.title:CI bindings`,
  description: $localize`:@@settings.secrets.ci.description:Repository bindings and allowed CI references. Metadata only; bindings are managed during onboarding.`,
  provider: $localize`:@@settings.secrets.ci.provider:Provider`,
  status: $localize`:@@settings.secrets.ci.status:Verification state`,
  active: $localize`:@@settings.secrets.ci.active:Active`,
  pending: $localize`:@@settings.secrets.ci.pending:Pending verification`,
  disabled: $localize`:@@settings.secrets.ci.disabled:Disabled`,
  pendingHint: $localize`:@@settings.secrets.ci.pending-hint:Waiting for the first CI exchange.`,
  refs: $localize`:@@settings.secrets.ci.refs:Allowed ref patterns`,
  workflows: $localize`:@@settings.secrets.ci.workflows:Allowed workflow references`,
  activated: $localize`:@@settings.secrets.ci.activated:Activated · UTC`,
  created: $localize`:@@settings.secrets.ci.created:Created · UTC`,
  notActivated: $localize`:@@settings.secrets.ci.not-activated:Not activated`,
  none: $localize`:@@settings.secrets.ci.none:None configured`,
  showAll: $localize`:@@settings.secrets.ci.show-all:Show all`,
  showFewer: $localize`:@@settings.secrets.ci.show-fewer:Show fewer`,
  loading: $localize`:@@settings.secrets.ci.loading:Loading CI bindings…`,
  empty: $localize`:@@settings.secrets.ci.empty:No CI bindings for this project. Bindings are created during onboarding.`,
  forbidden: $localize`:@@settings.secrets.ci.forbidden:You are not permitted to view CI bindings for this project.`,
  unavailable: $localize`:@@settings.secrets.ci.unavailable:CI bindings are unavailable. Check your connection and try again.`,
  retry: $localize`:@@settings.secrets.ci.retry:Retry CI bindings`,
  more: $localize`:@@settings.secrets.ci.more:Load more CI bindings`,
  loadingMore: $localize`:@@settings.secrets.ci.loading-more:Loading more CI bindings…`,
};

/** Read-only projection: bind named CiBindingView metadata, never enumerate/spread API fields. */
@Component({
  selector: 'app-ci-binding-list',
  imports: [StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['./secrets.page.css', './ci-binding-list.component.css'],
  template: `
    <section
      data-ci-bindings
      aria-labelledby="ci-bindings-heading"
      [attr.aria-busy]="loading() || loadingMore()"
    >
      <header class="page-header">
        <h2 id="ci-bindings-heading">{{ copy.title }}</h2>
        <p>{{ copy.description }}</p>
      </header>
      @if (forbidden()) {
        <p class="failure" role="status" data-ci-forbidden>{{ copy.forbidden }}</p>
      } @else {
        @if (loading()) {
          <p role="status">{{ copy.loading }}</p>
          <div class="binding-skeleton" aria-hidden="true">
            <span></span><span></span><span></span>
          </div>
        } @else if (phase() === 'empty' || (phase() === 'ready' && rows().length === 0)) {
          <p role="status" data-ci-empty>{{ copy.empty }}</p>
        }
        @if (error() || phase() === 'error' || phase() === 'unavailable' || pagingFailure()) {
          <div class="failure" role="alert">
            @if (error(); as failure) {
              <p>{{ failure.message }}</p>
              <code>{{ failure.code }}</code>
            } @else {
              <p>{{ copy.unavailable }}</p>
            }
            <button
              type="button"
              data-ci-retry
              [disabled]="loading() || loadingMore()"
              (click)="retry.emit()"
            >
              {{ copy.retry }}
            </button>
          </div>
        }
        @if (rows().length > 0) {
          <ul class="references" [attr.aria-label]="copy.title">
            @for (row of rows(); track row.id) {
              <li data-ci-binding>
                <div class="reference-heading">
                  <h3 [title]="row.repository_full_name">{{ row.repository_full_name }}</h3>
                  <app-status-pill
                    [tone]="row.status === 'active' ? 'success' : 'neutral'"
                    [dimension]="copy.status"
                    [label]="statusLabel(row.status)"
                  />
                </div>
                @if (row.status === 'pending_verification') {
                  <p>{{ copy.pendingHint }}</p>
                }
                <dl class="metadata">
                  <div>
                    <dt>{{ copy.provider }}</dt>
                    <dd>{{ row.provider }}</dd>
                  </div>
                  <div>
                    <dt>{{ copy.activated }}</dt>
                    <dd>
                      @if (row.activated_at; as date) {
                        <time [attr.datetime]="date">{{ date }}</time>
                      } @else {
                        {{ copy.notActivated }}
                      }
                    </dd>
                  </div>
                  <div>
                    <dt>{{ copy.created }}</dt>
                    <dd>
                      <time [attr.datetime]="row.created_at">{{ row.created_at }}</time>
                    </dd>
                  </div>
                  @for (group of ['refs', 'workflows']; track group) {
                    <div class="allowlist">
                      <dt [id]="'ci-label-' + row.id + '-' + group">
                        {{ group === 'refs' ? copy.refs : copy.workflows }}
                      </dt>
                      <dd>
                        <ul
                          class="patterns"
                          [id]="'ci-patterns-' + row.id + '-' + group"
                          [class.patterns-expanded]="isExpanded(row.id, group)"
                          [attr.tabindex]="isExpanded(row.id, group) ? 0 : null"
                          [attr.aria-labelledby]="'ci-label-' + row.id + '-' + group"
                        >
                          @for (
                            pattern of patterns(row, group).slice(
                              0,
                              isExpanded(row.id, group) ? undefined : 3
                            );
                            track $index
                          ) {
                            <li>
                              <code>{{ pattern }}</code>
                            </li>
                          } @empty {
                            <li>{{ copy.none }}</li>
                          }
                        </ul>
                        @if (patterns(row, group).length > 3) {
                          <button
                            type="button"
                            data-ci-show-all
                            [attr.aria-expanded]="isExpanded(row.id, group)"
                            [attr.aria-controls]="'ci-patterns-' + row.id + '-' + group"
                            [attr.aria-describedby]="'ci-label-' + row.id + '-' + group"
                            (click)="toggle(row.id, group)"
                          >
                            {{ isExpanded(row.id, group) ? copy.showFewer : copy.showAll }}
                          </button>
                        }
                      </dd>
                    </div>
                  }
                </dl>
              </li>
            }
          </ul>
        }
        @if (hasMore() && !error() && !pagingFailure()) {
          <div class="pagination">
            <button
              type="button"
              data-ci-load-more
              [disabled]="loading() || loadingMore()"
              [attr.aria-busy]="loadingMore()"
              (click)="loadMore.emit()"
            >
              {{ loadingMore() ? copy.loadingMore : copy.more }}
            </button>
          </div>
        }
        <p class="binding-status" role="status">{{ loadingMore() ? copy.loadingMore : '' }}</p>
      }
    </section>
  `,
})
export class CiBindingListComponent {
  readonly rows = input.required<readonly CiBindingView[]>();
  readonly phase = input.required<CiBindingsPhase>();
  readonly error = input<ApiError | null>(null);
  readonly hasMore = input(false);
  readonly loadingMore = input(false);
  readonly pagingFailure = input(false);
  readonly retry = output<void>();
  readonly loadMore = output<void>();
  protected readonly copy = CI_BINDINGS_COPY;
  protected readonly loading = computed(
    () => this.phase() === 'idle' || this.phase() === 'loading',
  );
  protected readonly forbidden = computed(
    () => this.phase() === 'forbidden' || this.error()?.kind === 'forbidden',
  );
  private readonly expanded = signal<ReadonlySet<string>>(new Set());

  protected statusLabel(status: CiBindingView['status']): string {
    return status === 'active'
      ? this.copy.active
      : status === 'disabled'
        ? this.copy.disabled
        : this.copy.pending;
  }
  protected patterns(row: CiBindingView, group: string): readonly string[] {
    return group === 'refs' ? row.allowed_ref_patterns : row.allowed_workflow_refs;
  }
  protected isExpanded(id: string, group: string): boolean {
    return this.expanded().has(`${id}:${group}`);
  }
  protected toggle(id: string, group: string): void {
    const key = `${id}:${group}`;
    this.expanded.update((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }
}
