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
import type { IngestTokenView } from '@evidgate/sdk';
import { SessionStore } from '../../../core/session/session.store';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { SecretReferenceListComponent, SECRETS_COPY } from './secret-reference-list.component';
import { RotateRevealDialogComponent } from './rotate-reveal-dialog.component';
import { SecretsStore } from './secrets.store';

@Component({
  selector: 'app-secrets-page',
  imports: [
    RequestStateComponent,
    RetryAfterComponent,
    SecretReferenceListComponent,
    RotateRevealDialogComponent,
  ],
  providers: [SecretsStore, RevealSlot],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './secrets.page.css',
  template: `
    <header class="page-header">
      <h2 #heading tabindex="-1">{{ copy.title }}</h2>
      <p>{{ copy.description }}</p>
    </header>
    @if (session.projects().length > 0) {
      <div class="project-field">
        <label for="secrets-project">{{ copy.project }}</label>
        <select id="secrets-project" [value]="selectedProject()" (change)="selectProject($event)">
          @for (project of session.projects(); track project.id) {
            <option [value]="project.id">{{ project.name }}</option>
          }
        </select>
      </div>
    }
    @if (canWrite()) {
      <p class="narrow-notice">{{ copy.narrow }}</p>
    }
    <p role="status">
      @if (store.rotateState().phase === 'done') {
        {{ copy.saved }}
      }
    </p>
    @if (store.rotateState().error; as error) {
      <div class="failure" role="alert">
        <p>{{ error.message }}</p>
        <code>{{ error.code }}</code>
      </div>
      @if (error.kind === 'rate_limited' || error.kind === 'unavailable') {
        <app-retry-after
          [kind]="error.kind"
          [retryAfterSeconds]="error.retryAfterSeconds"
          [retryEligible]="rotationRetryReady() && error.retryable && canWrite() && !busy()"
          [busy]="busy()"
          [code]="error.code"
          [requestId]="error.requestId"
          (retry)="retryRotation()"
        />
      }
    }
    @if (store.loadError(); as error) {
      <div class="failure" role="alert">
        <p>{{ error.message }}</p>
        <code>{{ error.code }}</code>
      </div>
    }
    <app-request-state
      [status]="status()"
      [error]="store.loadError()"
      [empty]="!selectedProject() || store.rows().length === 0"
      [emptyTitle]="copy.emptyTitle"
      [emptyDescription]="selectedProject() ? copy.emptyDescription : copy.noProjects"
      [retryEligible]="readRetryReady() && (store.loadError()?.retryable ?? false)"
      (retry)="reload()"
    >
      <section [attr.aria-busy]="paging() || busy()">
        <app-secret-reference-list
          [rows]="visibleRows()"
          [canWrite]="canWrite()"
          [busy]="busy()"
          [blocked]="!rotationRetryReady()"
          [busyId]="rotatingId()"
          (rotate)="rotate($event)"
        />
        @if (pagingFailure()) {
          <p class="failure" role="alert">{{ copy.pagingFailure }}</p>
        }
        @if (store.loadError(); as error) {
          @if (error.kind === 'rate_limited' || error.kind === 'unavailable') {
            <app-retry-after
              [kind]="error.kind"
              [retryAfterSeconds]="error.retryAfterSeconds"
              [retryEligible]="readRetryReady() && error.retryable && !paging()"
              [busy]="paging()"
              [code]="error.code"
              [requestId]="error.requestId"
              (retry)="next()"
            />
          }
        }
        <nav class="pagination" [attr.aria-label]="copy.title">
          @if (offset() > 0) {
            <button type="button" [disabled]="paging() || busy()" (click)="previous()">
              {{ copy.previous }}
            </button>
          }
          @if (hasNext()) {
            <button
              type="button"
              data-load-more
              [disabled]="paging() || busy() || !readRetryReady()"
              [attr.aria-busy]="paging()"
              (click)="next()"
            >
              {{ cachedNext() ? copy.next : copy.more }}
            </button>
          }
        </nav>
      </section>
    </app-request-state>
    <app-rotate-reveal-dialog />
  `,
})
export class SecretsPage {
  protected readonly store = inject(SecretsStore);
  protected readonly session = inject(SessionStore);
  private readonly slot = inject(RevealSlot);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly copy = SECRETS_COPY;
  protected readonly selectedProject = signal<string | null>(null);
  protected readonly canWrite = computed(() => this.session.has('ingest_token:write'));
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
    effect((cleanup) => {
      const seconds = this.store.loadError()?.retryAfterSeconds;
      this.readRetryReady.set(seconds == null || seconds <= 0);
      if (seconds != null && seconds > 0) {
        const timer = setTimeout(() => this.readRetryReady.set(true), seconds * 1000);
        cleanup(() => clearTimeout(timer));
      }
    });
    effect((cleanup) => {
      const seconds = this.store.rotateState().error?.retryAfterSeconds;
      this.rotationRetryReady.set(seconds == null || seconds <= 0);
      if (seconds != null && seconds > 0) {
        const timer = setTimeout(() => this.rotationRetryReady.set(true), seconds * 1000);
        cleanup(() => clearTimeout(timer));
      }
    });
    this.destroyRef.onDestroy(() => this.clearScope());
  }
  protected selectProject(event: Event): void {
    if (!(event.target instanceof HTMLSelectElement)) return;
    const id = event.target.value;
    if (!this.session.projects().some((project) => project.id === id)) return;
    this.clearScope();
    this.selectedProject.set(id);
  }
  protected reload(): void {
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
  protected async rotate(target: {
    reference: IngestTokenView;
    opener: HTMLElement;
  }): Promise<void> {
    if (
      this.busy() ||
      !this.rotationRetryReady() ||
      !this.canWrite() ||
      target.reference.status !== 'active' ||
      !this.selectedProject()
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
    if (this.retryTarget && this.rotationRetryReady()) void this.rotate(this.retryTarget);
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
