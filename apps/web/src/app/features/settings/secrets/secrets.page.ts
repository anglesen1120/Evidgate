import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationStart, Router } from '@angular/router';
import type { IngestTokenView } from '@evidgate/sdk';
import { HlmAlert } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmSelectImports } from '@evidgate/ui/select';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmLabel } from '@evidgate/ui/label';
import { SessionStore } from '../../../core/session/session.store';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { SecretReferenceListComponent } from './secret-reference-list.component';
import { SECRETS_COPY } from './secrets.copy';
import { RotateConfirmDialogComponent } from './rotate-confirm-dialog.component';
import { DesktopRequiredNoticeComponent } from '../../../shared/ui/narrow/desktop-required-notice.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { RotateRevealDialogComponent } from './rotate-reveal-dialog.component';
import { SecretsStore } from './secrets.store';
import { CiBindingListComponent } from './ci-binding-list.component';

type RotationTarget = {
  readonly reference: IngestTokenView;
  readonly opener: HTMLElement;
  readonly projectId: string;
  readonly projectName: string;
};

@Component({
  selector: 'app-secrets-page',
  imports: [
    RequestStateComponent,
    RetryAfterComponent,
    SecretReferenceListComponent,
    RotateRevealDialogComponent,
    CiBindingListComponent,
    HlmAlert,
    HlmButton,
    HlmSelectImports,
    HlmCardImports,
    RotateConfirmDialogComponent,
    DesktopRequiredNoticeComponent,
    HlmLabel,
  ],
  providers: [SecretsStore, RevealSlot],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'block min-w-0 font-sans text-sm text-foreground wrap-anywhere [&_p]:m-0 [&_code]:font-mono',
  },
  templateUrl: './secrets.page.html',
})
export class SecretsPage {
  protected readonly store = inject(SecretsStore);
  protected readonly session = inject(SessionStore);
  private readonly slot = inject(RevealSlot);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly copy = SECRETS_COPY;
  protected readonly viewport = inject(NarrowMutationPolicy);
  protected readonly sectionSize = 'section' as const;
  protected readonly projectLabel = (value: unknown): string =>
    this.session.projects().find((project) => project.id === value)?.name ?? '';
  protected readonly rotationTarget = signal<RotationTarget | null>(null);
  protected readonly selectedProject = signal<string | null>(null);
  protected readonly canWrite = computed(() => this.session.has('ingest_token:write'));
  protected readonly canReadBindings = computed(() => this.session.has('project:read'));
  /** Boolean view of the project list, so a re-emitted list cannot re-run the bindings read. */
  private readonly selectedProjectListed = computed(() => {
    const id = this.selectedProject();
    return id !== null && this.session.projects().some((project) => project.id === id);
  });
  protected readonly bindingsPaging = signal(false);
  protected readonly bindingsPagingFailure = signal(false);
  protected readonly offset = signal(0);
  protected readonly visibleRows = computed(() =>
    this.store.rows().slice(this.offset(), this.offset() + 200),
  );
  protected readonly cachedNext = computed(() => this.offset() + 200 < this.store.rows().length);
  protected readonly hasNext = computed(() => this.cachedNext() || this.store.hasMore());
  protected readonly paging = signal(false);
  protected readonly pagingFailure = signal(false);
  protected readonly busy = signal(false);
  protected readonly rotatingId = signal<string | null>(null);
  protected readonly readRetryReady = signal(true);
  protected readonly rotationRetryReady = signal(true);
  protected readonly status = computed<'loading' | 'ready' | 'error'>(() => {
    if (!this.selectedProject()) return 'ready';
    const phase = this.store.phase();
    return phase === 'idle'
      ? 'loading'
      : phase === 'loading' && this.store.rows().length > 0
        ? 'ready'
        : phase;
  });
  private readonly dialog = viewChild(RotateRevealDialogComponent);
  private readonly heading = viewChild.required<ElementRef<HTMLHeadingElement>>('heading');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private generation = 0;
  private lastProject: string | null = null;
  private retryTarget: { reference: IngestTokenView; opener: HTMLElement } | null = null;

  constructor() {
    inject(Router, { optional: true })
      ?.events.pipe(takeUntilDestroyed())
      .subscribe((event) => {
        if (event instanceof NavigationStart) this.clearScope();
      });
    effect(() => {
      if (!this.canWrite()) untracked(() => this.clearScope());
    });
    effect(() => {
      const projects = this.session.projects();
      const selected = this.selectedProject();
      const valid =
        projects.find((project) => project.id === selected)?.id ?? projects[0]?.id ?? null;
      if (valid !== selected) this.selectedProject.set(valid);
      if (valid === this.lastProject) return;
      this.lastProject = valid;
      untracked(() => {
        this.clearScope();
        if (valid) void this.store.load(valid);
      });
    });
    effect(() => {
      const id = this.selectedProject();
      const permitted = this.canReadBindings();
      // Only the derived boolean is tracked: the project list re-emits on unrelated session
      // changes, and re-reading bindings for the same project would drop already loaded pages.
      const listed = this.selectedProjectListed();
      untracked(() => {
        this.bindingsPaging.set(false);
        this.bindingsPagingFailure.set(false);
        if (id && listed && permitted) void this.store.loadCiBindings(id);
        else this.store.resetCiBindings();
      });
    });
    effect((cleanup) => {
      const seconds = this.store.loadError()?.retryAfterSeconds;
      this.readRetryReady.set(seconds == null || seconds <= 0);
      if (seconds != null && seconds > 0) {
        const timer = setTimeout(
          () => this.readRetryReady.set(true),
          Math.min(seconds, 86_400) * 1000,
        );
        cleanup(() => clearTimeout(timer));
      }
    });
    effect((cleanup) => {
      const error = this.store.rotateState().error;
      const seconds = error?.kind === 'rate_limited' ? error.retryAfterSeconds : null;
      this.rotationRetryReady.set(seconds == null || seconds <= 0);
      if (seconds != null && seconds > 0) {
        const timer = setTimeout(
          () => this.rotationRetryReady.set(true),
          Math.min(seconds, 86_400) * 1000,
        );
        cleanup(() => clearTimeout(timer));
      }
    });
    this.destroyRef.onDestroy(() => this.clearScope());
  }
  protected selectProject(id: unknown): void {
    if (typeof id !== 'string' || id === this.selectedProject()) return;
    if (!this.session.projects().some((project) => project.id === id)) return;
    this.clearScope();
    this.selectedProject.set(id);
  }
  protected reload(): void {
    if (this.store.rotating()) return;
    const id = this.selectedProject();
    if (
      id &&
      this.readRetryReady() &&
      this.session.projects().some((project) => project.id === id)
    ) {
      this.clearScope();
      void this.store.load(id);
    }
  }
  protected reloadBindings(): void {
    const id = this.selectedProject();
    if (
      !id ||
      !this.canReadBindings() ||
      this.bindingsPaging() ||
      !this.session.projects().some((project) => project.id === id)
    )
      return;
    this.bindingsPagingFailure.set(false);
    void this.store.loadCiBindings(id);
  }
  protected async moreBindings(): Promise<void> {
    if (
      !this.canReadBindings() ||
      this.bindingsPaging() ||
      !this.store.hasMoreCiBindings() ||
      this.store.ciBindingsError()?.kind === 'forbidden'
    )
      return;
    const project = this.selectedProject();
    this.bindingsPaging.set(true);
    try {
      await this.store.loadMoreCiBindings();
    } catch {
      if (project === this.selectedProject()) this.bindingsPagingFailure.set(true);
    } finally {
      if (project === this.selectedProject()) this.bindingsPaging.set(false);
    }
  }
  protected requestRotation(target: { reference: IngestTokenView; opener: HTMLElement }): void {
    const projectId = this.selectedProject();
    if (!projectId || this.rotationTarget() || !this.rotationAllowed(target.reference.id)) return;
    this.rotationTarget.set({ ...target, projectId, projectName: this.projectLabel(projectId) });
  }
  protected cancelRotation(): void {
    const opener = this.rotationTarget()?.opener;
    this.rotationTarget.set(null);
    if (opener?.isConnected && !opener.matches(':disabled')) opener.focus();
    else this.heading().nativeElement.focus();
  }
  private rotationAllowed(id: string): boolean {
    return (
      this.viewport.allowsMutation() &&
      this.canWrite() &&
      !this.busy() &&
      !this.store.rotating() &&
      this.rotationRetryReady() &&
      this.session.projects().some((project) => project.id === this.selectedProject()) &&
      this.store.rows().some((row) => row.id === id && row.status === 'active')
    );
  }
  protected async confirmRotation(): Promise<void> {
    const target = this.rotationTarget();
    this.rotationTarget.set(null);
    if (
      !target ||
      target.projectId !== this.selectedProject() ||
      !this.rotationAllowed(target.reference.id)
    )
      return;
    const generation = this.generation;
    this.retryTarget = target;
    this.busy.set(true);
    this.rotatingId.set(target.reference.id);
    const result = await this.store.rotate(target.reference.id);
    if (this.destroyRef.destroyed || generation !== this.generation) return;
    this.busy.set(false);
    this.rotatingId.set(null);
    if (!result) return;
    this.offset.set(0);
    const successorId = result.successorId;
    this.slot.show({ source: 'ingest-token-rotation', value: result.value });
    this.dialog()?.open(target.opener, result.overlapEndsAt, () => {
      // Successful refresh removes the old active row's action. Return to the next Rotate action
      // (the successor), or the list heading if no action remains, never to a detached element.
      const button = Array.from(
        this.host.nativeElement.querySelectorAll<HTMLButtonElement>('[data-rotate]:enabled'),
      ).find((candidate) => candidate.dataset['referenceId'] === successorId);
      (button ?? this.heading().nativeElement).focus();
    });
  }
  protected retryRotation(): void {
    if (
      this.retryTarget &&
      this.rotationRetryReady() &&
      this.store.rotateState().error?.kind === 'rate_limited' &&
      this.store.rotateState().error?.retryable
    )
      this.requestRotation(this.retryTarget);
  }
  protected previous(): void {
    this.offset.update((value) => Math.max(0, value - 200));
    this.heading().nativeElement.focus();
  }
  protected async next(): Promise<void> {
    if (this.paging() || this.busy() || !this.readRetryReady()) return;
    const generation = this.generation;
    this.paging.set(true);
    try {
      if (!this.cachedNext()) await this.store.loadMore();
      if (generation !== this.generation) return;
      if (this.cachedNext()) this.offset.update((value) => value + 200);
      this.pagingFailure.set(false);
    } catch {
      if (generation === this.generation) this.pagingFailure.set(true);
    } finally {
      if (generation === this.generation) this.paging.set(false);
    }
  }
  private clearScope(): void {
    this.generation++;
    this.rotationTarget.set(null);
    this.dialog()?.close(false);
    this.slot.clear();
    this.store.resetRotation();
    this.retryTarget = null;
    this.busy.set(false);
    this.rotatingId.set(null);
    this.offset.set(0);
    this.paging.set(false);
    this.pagingFailure.set(false);
  }
}
