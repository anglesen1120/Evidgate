import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSkeleton } from '@evidgate/ui/skeleton';
import type { PublishedRevision } from '../../../core/data/policy-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { PolicyModeChipComponent } from './policy-mode-chip.component';
import { PolicyStore } from './policy.store';
import { PublishDialogComponent } from './publish-dialog.component';
import { RevisionDiffComponent } from './revision-diff.component';
import { RevisionListComponent } from './revision-list.component';

@Component({
  selector: 'app-policy-page',
  imports: [
    HlmAlertImports,
    HlmButton,
    HlmCardImports,
    HlmInput,
    HlmLabel,
    HlmSkeleton,
    PolicyModeChipComponent,
    RevisionListComponent,
    RevisionDiffComponent,
    PublishDialogComponent,
  ],
  providers: [PolicyStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './policy.page.html',
  host: {
    class: 'block min-w-0 text-sm text-foreground [&_p]:m-0 [&_code]:font-mono',
    '[attr.data-policy-phase]': 'store.phase()',
  },
})
export class PolicyPage {
  protected readonly store = inject(PolicyStore);
  private readonly session = inject(SessionStore);
  private readonly route = inject(ActivatedRoute);
  private readonly injector = inject(Injector);
  protected readonly canWrite = computed(() => this.session.has('policy:write'));
  protected readonly revisionId = signal(this.route.snapshot.queryParamMap.get('revision') ?? '');
  protected readonly targetId = signal<string | null>(null);
  protected readonly published = signal<PublishedRevision | null>(null);
  protected readonly paging = signal(false);
  private readonly dialog = viewChild(PublishDialogComponent);
  private readonly heading = viewChild.required<ElementRef<HTMLHeadingElement>>('heading');
  protected readonly copy = {
    title: $localize`:@@settings.policy.title:Policy`,
    description: $localize`:@@settings.policy.description:Inspect policy revisions and compare their fields. Open a revision using its identifier.`,
    revisionId: $localize`:@@settings.policy.revision-id:Revision ID`,
    open: $localize`:@@settings.policy.open:Open revision`,
    narrow: $localize`:@@settings.policy.narrow:Inspection only on narrow screens. Use a wider screen to publish a revision.`,
    publish: $localize`:@@settings.policy.publish-title:Publish revision`,
    idle: $localize`:@@settings.policy.no-selection:No revision selected. Enter a revision ID to inspect its policy.`,
    notFound: $localize`:@@settings.policy.not-found:Revision not found.`,
    loading: $localize`:@@settings.policy.loading:Loading revisions…`,
    loadFailed: $localize`:@@settings.policy.load-failed:The policy revisions could not be loaded.`,
    loadRetry: $localize`:@@settings.policy.load-retry:Retry`,
    reload: $localize`:@@settings.policy.reload:Reload revisions`,
    created: $localize`:@@settings.policy.published-new:New revision published.`,
    unchanged: $localize`:@@settings.policy.published-unchanged:Policy unchanged. Existing revision returned.`,
    revision: $localize`:@@settings.policy.revision:Revision`,
  };
  constructor() {
    if (this.revisionId()) void this.store.open(this.revisionId());
    inject(DestroyRef).onDestroy(() => this.store.reset());
    let previousOrg = this.session.currentOrg()?.id ?? null;
    let previousProject = this.session.currentProject()?.id ?? null;
    effect(() => {
      const org = this.session.currentOrg()?.id ?? null;
      const project = this.session.currentProject()?.id ?? null;
      const projects = this.session.projects();
      const openedProject = this.store.projectId();
      const changed = org !== previousOrg || project !== previousProject;
      previousOrg = org;
      previousProject = project;
      if (
        changed ||
        (openedProject !== null && !projects.some((item) => item.id === openedProject))
      ) {
        untracked(() => {
          this.dialog()?.close();
          this.published.set(null);
          this.targetId.set(null);
          this.revisionId.set('');
          this.store.reset();
        });
      }
    });
  }
  protected editRevision(event: Event): void {
    if (event.target instanceof HTMLInputElement) this.revisionId.set(event.target.value);
  }
  protected openRevision(event: Event): void {
    event.preventDefault();
    if (this.store.publishing() || this.store.phase() === 'loading') return;
    this.dialog()?.close();
    this.published.set(null);
    this.targetId.set(null);
    void this.store.open(this.revisionId());
  }
  protected openPublish(event: Event): void {
    if (!(event.currentTarget instanceof HTMLElement) || !this.canWrite()) return;
    const id = this.targetId() ?? this.store.headRevisionId();
    if (id) void this.dialog()?.open(id, event.currentTarget);
  }
  protected onPublished(result: PublishedRevision): void {
    this.published.set(result);
  }
  protected async loadMore(): Promise<void> {
    if (this.paging() || this.store.publishing()) return;
    this.paging.set(true);
    try {
      await this.store.loadMore();
    } finally {
      this.paging.set(false);
    }
  }
  protected reload(): void {
    void this.store.reload();
  }
  protected focusHeading(): void {
    afterNextRender(() => this.heading().nativeElement.focus(), { injector: this.injector });
  }
}
