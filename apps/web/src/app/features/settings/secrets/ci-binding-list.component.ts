import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import type { CiBindingView } from '@evidgate/sdk';
import { HlmAlert } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmSkeleton } from '@evidgate/ui/skeleton';
import type { ApiError } from '../../../core/http/api-error';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
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
  imports: [StatusPillComponent, HlmAlert, HlmButton, HlmCard, HlmSkeleton, AbsoluteTimeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'mt-8 block min-w-0 text-sm text-foreground wrap-anywhere [&_p]:m-0 [&_h2]:m-0 [&_h3]:m-0 [&_dd]:m-0 [&_dt]:text-xs [&_dt]:text-muted-foreground [&_code]:font-mono',
  },
  template: `
    <section
      data-ci-bindings
      aria-labelledby="ci-bindings-heading"
      [attr.aria-busy]="loading() || loadingMore()"
    >
      <header class="mb-4 grid min-w-0 gap-2">
        <h2
          #heading
          id="ci-bindings-heading"
          tabindex="-1"
          class="font-[family-name:var(--eg-font-display)] text-lg focus-visible:outline-[length:var(--eg-focus-width)] focus-visible:outline-ring focus-visible:outline-offset-[var(--eg-focus-offset)]"
        >
          {{ copy.title }}
        </h2>
        <p class="text-muted-foreground">{{ copy.description }}</p>
      </header>
      @if (forbidden()) {
        <p
          hlmAlert
          [variant]="'destructive'"
          class="bg-[var(--eg-danger-bg)]"
          role="status"
          data-ci-forbidden
        >
          {{ copy.forbidden }}
        </p>
      } @else {
        @if (loading()) {
          <p role="status">{{ copy.loading }}</p>
          <div hlmCard class="binding-skeleton gap-3 p-4" aria-hidden="true">
            <span hlmSkeleton class="h-6"></span><span hlmSkeleton class="h-6"></span
            ><span hlmSkeleton class="h-6"></span>
          </div>
        } @else if (phase() === 'empty' || (phase() === 'ready' && rows().length === 0)) {
          <p role="status" data-ci-empty>{{ copy.empty }}</p>
        }
        @if (error() || phase() === 'error' || phase() === 'unavailable' || pagingFailure()) {
          <div
            hlmAlert
            [variant]="'destructive'"
            class="my-2 bg-[var(--eg-danger-bg)]"
            role="alert"
          >
            @if (error(); as failure) {
              <p>{{ failure.message }}</p>
              <code>{{ failure.code }}</code>
            } @else {
              <p>{{ copy.unavailable }}</p>
            }
            <button
              hlmBtn
              [variant]="'outline'"
              class="mt-2"
              type="button"
              data-ci-retry
              [disabled]="loading() || loadingMore()"
              (click)="requestRetry($event)"
            >
              {{ copy.retry }}
            </button>
          </div>
        }
        @if (rows().length > 0) {
          <ul class="m-0 grid list-none gap-3 p-0" [attr.aria-label]="copy.title">
            @for (row of rows(); track row.id) {
              <li hlmCard class="min-w-0 gap-3 p-3 md:p-4" data-ci-binding>
                <div class="flex flex-wrap items-start gap-2 md:items-center">
                  <h3
                    class="min-w-0 basis-full text-base md:basis-auto md:grow"
                    [title]="row.repository_full_name"
                  >
                    {{ row.repository_full_name }}
                  </h3>
                  <app-status-pill
                    [tone]="row.status === 'active' ? 'success' : 'neutral'"
                    [dimension]="copy.status"
                    [label]="statusLabel(row.status)"
                  />
                </div>
                @if (row.status === 'pending_verification') {
                  <p>{{ copy.pendingHint }}</p>
                }
                <dl class="m-0 grid gap-3 md:grid-cols-2 [&>div]:min-w-0">
                  <div>
                    <dt>{{ copy.provider }}</dt>
                    <dd>{{ row.provider }}</dd>
                  </div>
                  <div>
                    <dt>{{ copy.activated }}</dt>
                    <dd>
                      @if (row.activated_at; as date) {
                        <app-absolute-time [value]="date" />
                      } @else {
                        {{ copy.notActivated }}
                      }
                    </dd>
                  </div>
                  <div>
                    <dt>{{ copy.created }}</dt>
                    <dd>
                      <app-absolute-time [value]="row.created_at" />
                    </dd>
                  </div>
                  @for (group of ['refs', 'workflows']; track group) {
                    <div class="col-span-full">
                      <dt [id]="'ci-label-' + row.id + '-' + group">
                        {{ group === 'refs' ? copy.refs : copy.workflows }}
                      </dt>
                      <dd>
                        <ul
                          class="patterns m-0 grid min-w-0 list-none gap-1 p-0 focus-visible:outline-[length:var(--eg-focus-width)] focus-visible:outline-ring focus-visible:outline-offset-[var(--eg-focus-offset)]"
                          [id]="'ci-patterns-' + row.id + '-' + group"
                          [class]="
                            isExpanded(row.id, group)
                              ? 'max-h-[calc(var(--eg-space-12)*3)] overflow-y-auto'
                              : ''
                          "
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
                              <code
                                class="inline-block max-w-full rounded-sm bg-muted px-1 py-[var(--eg-space-1)] text-muted-foreground"
                                >{{ pattern }}</code
                              >
                            </li>
                          } @empty {
                            <li>{{ copy.none }}</li>
                          }
                        </ul>
                        @if (patterns(row, group).length > 3) {
                          <button
                            hlmBtn
                            [variant]="'outline'"
                            class="mt-2"
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
          <div class="mt-3 flex flex-wrap items-center gap-2">
            <button
              hlmBtn
              [variant]="'outline'"
              type="button"
              data-ci-load-more
              [disabled]="loading() || loadingMore()"
              [attr.aria-busy]="loadingMore()"
              (click)="requestLoadMore($event)"
            >
              {{ loadingMore() ? copy.loadingMore : copy.more }}
            </button>
          </div>
        }
      }
      <!-- Outside the state branch: the live region must exist in every phase, forbidden included. -->
      <p class="empty:hidden" role="status">{{ loadingMore() ? copy.loadingMore : '' }}</p>
    </section>
  `,
})
export class CiBindingListComponent {
  private readonly document = inject(DOCUMENT);

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
  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');
  /** The control whose click started the read; it may be removed before the read settles. */
  private focusAnchor: HTMLElement | null = null;
  private readonly restoreFocusPending = signal(false);

  constructor() {
    // A settled Load more or Retry can remove the control that had focus (the last page, or the
    // error block), which drops focus to the document body. Run after the DOM update, and only
    // move focus when the control is really gone, so a surviving control keeps the user's focus.
    afterRenderEffect(() => {
      const busy = this.loading() || this.loadingMore();
      // A fast response can coalesce loading true → false before the child renders.
      const pending = this.restoreFocusPending();
      untracked(() => {
        if (busy || !pending) return;
        this.restoreFocusPending.set(false);
        const anchor = this.focusAnchor;
        this.focusAnchor = null;
        if (anchor?.isConnected) return;
        // Focus is only "lost" when the browser dropped it to the document: any element still
        // holding focus was put there deliberately, so never yank it back into this section.
        const active = this.document.activeElement;
        if (active && active !== this.document.body && active !== this.document.documentElement)
          return;
        this.heading().nativeElement.focus();
      });
    });
  }

  protected requestLoadMore(event: Event): void {
    if (this.loading() || this.loadingMore()) return;
    this.armFocusRestore(event);
    this.loadMore.emit();
  }
  protected requestRetry(event: Event): void {
    if (this.loading() || this.loadingMore()) return;
    this.armFocusRestore(event);
    this.retry.emit();
  }
  private armFocusRestore(event: Event): void {
    this.focusAnchor = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
    this.restoreFocusPending.set(true);
  }

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
