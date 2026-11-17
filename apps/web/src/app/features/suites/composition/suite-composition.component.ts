import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  linkedSignal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { HlmAlert } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmSkeleton } from '@evidgate/ui/skeleton';
import { HlmTableImports } from '@evidgate/ui/table';
import { LIST_CHUNK } from './suite-composition.model';
import { SuiteCompositionStore } from './suite-composition.store';
import { SuiteWarningListComponent } from './suite-warning-list.component';

@Component({
  selector: 'app-suite-composition',
  imports: [
    SuiteWarningListComponent,
    HlmAlert,
    HlmButton,
    HlmCard,
    HlmCardTitle,
    HlmSkeleton,
    HlmTableImports,
  ],
  providers: [SuiteCompositionStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './suite-composition.component.html',
  host: {
    '[attr.data-suite-composition-status]': 'store.status()',
    class:
      'block min-w-0 font-sans text-sm text-foreground wrap-anywhere [&_dl]:m-0 [&_dd]:m-0 [&_dd]:min-w-0 [&_dt]:text-muted-foreground [&_p]:m-0',
  },
})
export class SuiteCompositionComponent {
  protected readonly store = inject(SuiteCompositionStore);

  private readonly params = toSignal(inject(ActivatedRoute).paramMap);

  protected readonly pageHeadingSize = 'page' as const;
  protected readonly sectionHeadingSize = 'section' as const;
  private readonly stateHeading = viewChild<ElementRef<HTMLHeadingElement>>('stateHeading');
  protected readonly copy = {
    variantHash: $localize`:@@suites.composition.variant-hash:Variant hash`,
    title: $localize`:@@suites.composition.suite-composition:Suite composition`,
    revision: $localize`:@@suites.composition.revision:Revision`,
    status: $localize`:@@suites.composition.status:Status`,
    hash: $localize`:@@suites.composition.revision-hash:Revision hash`,
    published: $localize`:@@suites.composition.this-published-revision-is-immutable-and-read-on:This published revision is immutable and read-only.`,
    draft: $localize`:@@suites.composition.this-draft-is-shown-read-only:This draft is shown read-only.`,
    strategy: $localize`:@@suites.composition.shard-strategy:Shard strategy`,
    strategyReadOnly: $localize`:@@suites.composition.strategy-is-read-only-in-this-view:Strategy is read-only in this view.`,
    noSharding: $localize`:@@suites.composition.no-sharding:No sharding`,
    shardCount: $localize`:@@suites.composition.shard-count:Shard count`,
    balance: $localize`:@@suites.composition.balance-by:Balance by`,
    duration: $localize`:@@suites.composition.duration:Duration`,
    count: $localize`:@@suites.composition.member-count:Member count`,
    noBalance: $localize`:@@suites.composition.no-balancing-specified:No balancing specified`,
    strategyUnavailable: $localize`:@@suites.composition.strategy-unavailable-this-revision-has-an-unreco:Strategy unavailable: this revision has an unrecognized shard strategy.`,
    matrix: $localize`:@@suites.composition.composition-matrix:Composition matrix`,
    caption: $localize`:@@suites.composition.case-revisions-by-variant-each-populated-cell-sh:Case revisions by variant. Each populated cell shows its member identifier, requirement and ordinal.`,
    caseRevision: $localize`:@@suites.composition.case-revision:Case revision`,
    noParameters: $localize`:@@suites.composition.no-parameters:No parameters`,
    member: $localize`:@@suites.composition.member:Member`,
    required: $localize`:@@suites.composition.required:Required`,
    optional: $localize`:@@suites.composition.optional:Optional`,
    ordinal: $localize`:@@suites.composition.ordinal:Ordinal`,
    noMember: $localize`:@@suites.composition.no-member:No member`,
    variantParams: $localize`:@@suites.composition.variant-params:Variant parameters`,
    flatMatrixNote: $localize`:@@suites.composition.flat-matrix-note:This revision has more cells than a matrix can show, so its members are listed as a flat table.`,
    duplicateMembers: (count: number) =>
      $localize`:@@suites.composition.repeated-variant-cells:Repeated variant members: ${count}:count:; the matrix keeps only the first member of each cell.`,
    showMore: $localize`:@@suites.composition.show-more:Show more`,
    membersShown: (shown: number, total: number) =>
      $localize`:@@suites.composition.members-shown:Showing ${shown}:shown: of ${total}:total: members.`,
    dependenciesShown: (shown: number, total: number) =>
      $localize`:@@suites.composition.dependencies-shown:Showing ${shown}:shown: of ${total}:total: dependencies.`,
    dependencies: $localize`:@@suites.composition.dependencies:Dependencies`,
    dependencyNote: $localize`:@@suites.composition.each-child-member-depends-on-the-parent-member-s:Each child member depends on the parent member shown beside it.`,
    parent: $localize`:@@suites.composition.parent-member:Parent member`,
    child: $localize`:@@suites.composition.child-member:Child member`,
    noDependencies: $localize`:@@suites.composition.no-dependencies-declared:No dependencies declared`,
    emptyTitle: $localize`:@@suites.composition.no-members-in-this-revision:No members in this revision`,
    emptyBody: $localize`:@@suites.composition.this-revision-has-no-members-so-there-is-no-comp:This revision has no members, so there is no composition matrix to display.`,
    loadingTitle: $localize`:@@suites.composition.loading-suite-composition:Loading suite composition`,
    loadingBody: $localize`:@@suites.composition.reading-the-pinned-members-variants-and-dependen:Reading the pinned members, variants and dependencies.`,
    notFoundTitle: $localize`:@@suites.composition.suite-revision-not-found:Suite revision not found`,
    notFoundBody: $localize`:@@suites.composition.this-revision-does-not-exist-or-is-not-available:This revision does not exist or is not available in this project. Check the revision link.`,
    forbiddenTitle: $localize`:@@suites.composition.suite-revision-access-denied:Suite revision access denied`,
    forbiddenBody: $localize`:@@suites.composition.you-do-not-have-permission-to-view-this-revision:You do not have permission to view this revision. Ask a project administrator for access.`,
    unavailableTitle: $localize`:@@suites.composition.suite-composition-unavailable:Suite composition unavailable`,
    unavailableBody: $localize`:@@suites.composition.the-suite-service-is-temporarily-unavailable-rel:The suite service is temporarily unavailable. Reload this page later.`,
    errorTitle: $localize`:@@suites.composition.suite-composition-could-not-be-loaded:Suite composition could not be loaded`,
    errorBody: $localize`:@@suites.composition.the-revision-could-not-be-read-check-your-connec:The revision could not be read. Check your connection and reload this page.`,
  };
  protected readonly state = computed(() => {
    switch (this.store.status()) {
      case 'idle':
      case 'loading':
        return { title: this.copy.loadingTitle, body: this.copy.loadingBody };
      case 'not-found':
        return { title: this.copy.notFoundTitle, body: this.copy.notFoundBody };
      case 'forbidden':
        return { title: this.copy.forbiddenTitle, body: this.copy.forbiddenBody };
      case 'unavailable':
        return { title: this.copy.unavailableTitle, body: this.copy.unavailableBody };
      case 'error':
        return { title: this.copy.errorTitle, body: this.copy.errorBody };
      case 'ready':
        // The heading names the screen whatever the revision holds; an empty revision says so in
        // the matrix section, so this heading never has to carry a second meaning.
        return { title: this.copy.title, body: '' };
    }
  });

  /**
   * How many rows each chunked list shows. Both derive from the served view, so a new revision
   * starts again at one chunk instead of inheriting how far the previous one was opened.
   */
  protected readonly memberLimit = linkedSignal({
    source: () => this.store.view(),
    computation: () => LIST_CHUNK,
  });
  protected readonly dependencyLimit = linkedSignal({
    source: () => this.store.view(),
    computation: () => LIST_CHUNK,
  });
  protected readonly visibleMembers = computed(() =>
    (this.store.view()?.flatMembers ?? []).slice(0, this.memberLimit()),
  );
  protected readonly visibleDependencies = computed(() =>
    (this.store.view()?.dependencies ?? []).slice(0, this.dependencyLimit()),
  );
  protected readonly listChunk = LIST_CHUNK;

  // Growing the window never moves focus: the button that was pressed stays in place, and the
  // count region announces the new total. Once every row is shown the control stays mounted and
  // says so with `aria-disabled` — removing it would drop the focus a keyboard user just used.
  protected showMoreMembers(): void {
    const total = this.store.view()?.flatMembers.length ?? 0;
    if (this.visibleMembers().length >= total) return;
    this.memberLimit.update((limit) => limit + LIST_CHUNK);
  }

  protected showMoreDependencies(): void {
    const total = this.store.view()?.dependencies.length ?? 0;
    if (this.visibleDependencies().length >= total) return;
    this.dependencyLimit.update((limit) => limit + LIST_CHUNK);
  }

  constructor() {
    effect(() => {
      const revisionId = this.params()?.get('revisionId');
      // An address that names no revision has nothing to read: the screen must show its not-found
      // state rather than hold the loading presentation forever.
      untracked(() => (revisionId ? void this.store.load(revisionId) : this.store.markMissing()));
    });
    // Focus follows the committed state, after its heading exists in the DOM. A render effect
    // avoids racing asynchronous reads or scheduling focus on a heading that has been removed.
    afterRenderEffect(() => {
      this.store.status();
      this.stateHeading()?.nativeElement.focus();
    });
  }
}
