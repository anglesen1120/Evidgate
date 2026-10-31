import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { SuiteCompositionStore } from './suite-composition.store';
import { SuiteWarningListComponent } from './suite-warning-list.component';

@Component({
  selector: 'app-suite-composition',
  imports: [SuiteWarningListComponent],
  providers: [SuiteCompositionStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './suite-composition.component.html',
  styleUrl: './suite-composition.component.css',
  host: { '[attr.data-suite-composition-status]': 'store.status()' },
})
export class SuiteCompositionComponent {
  protected readonly store = inject(SuiteCompositionStore);
  private readonly params = toSignal(inject(ActivatedRoute).paramMap);
  private readonly stateHeading = viewChild<ElementRef<HTMLHeadingElement>>('stateHeading');
  protected readonly copy = {
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
        return {
          title: this.store.view()?.rows.length === 0 ? this.copy.emptyTitle : this.copy.title,
          body: '',
        };
    }
  });

  constructor() {
    effect(() => {
      const revisionId = this.params()?.get('revisionId');
      if (revisionId) untracked(() => void this.store.load(revisionId));
    });
    // Focus follows the committed state, after its heading exists in the DOM. A render effect
    // avoids racing asynchronous reads or scheduling focus on a heading that has been removed.
    afterRenderEffect(() => {
      this.store.status();
      this.stateHeading()?.nativeElement.focus();
    });
  }
}
