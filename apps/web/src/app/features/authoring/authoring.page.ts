import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  linkedSignal,
  LOCALE_ID,
  resource,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import type { NodeDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { combineLatest, distinctUntilChanged, map } from 'rxjs';
import { RETAINED_ROWS_CAP } from '../../shared/keyset-retention';
import {
  HeaderActionsDirective,
  PageHeaderComponent,
  SectionHeaderComponent,
} from '../../shared/ui/page-header';
import { RequestStateComponent } from '../../shared/ui/states/request-state.component';
import { InlineFeedbackComponent } from '../../shared/ui/states/inline-feedback.component';
import { EmptyPatternComponent } from '../../shared/ui/states/empty-pattern.component';
import { NumberValueComponent } from '../../shared/ui/values/number-value.component';
import { CopyableIdentifierComponent } from '../../shared/ui/values/copyable-identifier.component';
import { ShellIconComponent } from '../../shell/layout/shell-icon.component';
import { AuthoringTreePaneComponent } from './authoring-tree-pane.component';
import { AuthoringCaseTableComponent } from './authoring-case-table.component';
import { AUTHORING_TREE_COPY, treeBrowseCappedHelp } from './authoring-tree.copy';
import { AuthoringStore } from './authoring.store';

@Component({
  selector: 'app-authoring-page',
  imports: [
    NgTemplateOutlet,
    RouterLink,
    EmptyPatternComponent,
    NumberValueComponent,
    HlmButton,
    HlmCardImports,
    HeaderActionsDirective,
    ShellIconComponent,
    PageHeaderComponent,
    SectionHeaderComponent,
    RequestStateComponent,
    InlineFeedbackComponent,
    CopyableIdentifierComponent,
    AuthoringTreePaneComponent,
    AuthoringCaseTableComponent,
  ],
  providers: [AuthoringStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './authoring.page.html',
  styleUrl: './authoring.page.css',
  host: { class: 'block min-w-0 text-foreground' },
})
export class AuthoringPage {
  protected readonly store = inject(AuthoringStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly locale = inject(LOCALE_ID);
  private readonly cap = inject(RETAINED_ROWS_CAP);

  protected readonly ownsPageHeading = this.route.snapshot.data['pageHeadingLevel'] === 1;
  protected readonly copy = {
    ...AUTHORING_TREE_COPY,
    browseCappedHelp: treeBrowseCappedHelp(this.cap, this.locale),
  };
  private readonly scope = toSignal(
    combineLatest([
      combineLatest(this.route.pathFromRoot.map((route) => route.paramMap)),
      this.route.queryParamMap,
    ]).pipe(
      map(([params, query]) => ({
        orgId: params.map((param) => param.get('orgId')).find(Boolean) ?? null,
        projectId:
          [...params]
            .reverse()
            .map((param) => param.get('projectId'))
            .find(Boolean) ?? null,
        nodeId: query.get('node_id'),
      })),
      distinctUntilChanged(
        (before, after) =>
          before.orgId === after.orgId &&
          before.projectId === after.projectId &&
          before.nodeId === after.nodeId,
      ),
    ),
  );
  private readonly request = resource({
    params: () => this.scope(),
    loader: async ({ params }) => {
      if (params.projectId) await this.store.init(params.projectId, params.nodeId);
    },
  });
  protected readonly busy = computed(
    () => this.store.treeStatus() === 'loading' || this.request.isLoading(),
  );
  protected readonly denied = computed(() =>
    ['forbidden', 'not_found'].includes(this.store.treeError()?.kind ?? ''),
  );
  protected readonly hasContent = computed(
    () =>
      !this.denied() &&
      (this.store.roots().items.length > 0 || this.store.roots().nextCursor !== null),
  );
  protected readonly viewStatus = computed<'loading' | 'ready' | 'error'>(() => {
    if (this.denied()) return 'error';
    if (this.hasContent()) return 'ready';
    const status = this.store.treeStatus();
    return status === 'idle' ? 'loading' : status;
  });
  protected readonly selectedPath = computed(() => {
    if (this.denied()) return [];
    const node = this.store.selectedNode();
    const path = this.store.path();
    return node && !path.some((ancestor) => ancestor.node_id === node.node_id)
      ? [...path, node]
      : path;
  });
  private readonly cases = viewChild(AuthoringCaseTableComponent);
  protected readonly importRoute = computed(() => {
    const scope = this.scope();
    return scope?.orgId && scope.projectId && !this.denied()
      ? ['/orgs', scope.orgId, 'projects', scope.projectId, 'authoring', 'import-export']
      : null;
  });
  protected readonly projectEmpty = computed(
    () =>
      this.viewStatus() === 'ready' &&
      !this.hasContent() &&
      !this.store.treeError() &&
      !this.store.selectedNodeId() &&
      this.cases()?.emptyProject() === true,
  );
  protected readonly metrics = computed(() => {
    const metrics: { label: string; count: number; scope: string }[] = [];
    if (this.denied() || this.projectEmpty()) return metrics;
    const node = this.store.selectedNode();
    if (node && this.store.selectionStatus() === 'ready' && !this.store.stale()) {
      metrics.push({ label: this.copy.directItems, count: node.item_count, scope: node.title });
    }
    const loaded = this.cases()?.loadedInventory();
    if (loaded && loaded.nodeId === (this.scope()?.nodeId ?? null)) {
      const category =
        loaded.nodeId && (node?.kind === 'category' || node?.kind === 'sub_category');
      metrics.push({
        label: loaded.partial
          ? this.copy.loadedCases
          : category
            ? this.copy.categoryCases
            : this.copy.table,
        count: loaded.count,
        scope: this.copy.loadedScope,
      });
    }
    return metrics;
  });

  protected openImport(): void {
    const route = this.importRoute();
    if (route) void this.router.navigate(route);
  }
  /**
   * One polite line per settled read: the scope now shown, then every cap notice on screen. A
   * cap is inserted with its message, so it is spoken here rather than by the notice itself.
   */
  protected readonly announcement = computed(() => {
    if (this.denied() || this.busy()) return '';
    return [this.scopeLine(), this.capLine()].filter((line) => line !== '').join(' ');
  });
  /** Which branches the pane has open, so only a cap whose notice renders is announced. */
  protected readonly expanded = linkedSignal<string | null, ReadonlySet<string>>({
    source: () => this.store.projectId(),
    computation: () => new Set<string>(),
  });
  private readonly scopeLine = computed(() => {
    const failed = this.store.selectedNodeId();
    if (this.store.selectionStatus() === 'error' && failed !== null) {
      return $localize`:@@authoring.tree.announce-selection-failed:Could not load selected scope ${failed}:nodeId:`;
    }
    const selected = this.store.selectedNode();
    if (selected !== null) {
      return $localize`:@@authoring.tree.announce-selected:Selected scope ${selected.title}:title:`;
    }
    return ['ready', 'capped'].includes(this.store.roots().status) &&
      this.store.roots().items.length > 0
      ? $localize`:@@authoring.tree.announce-roots:Scope set to all project roots`
      : '';
  });
  private readonly capLine = computed(() =>
    this.store.roots().status === 'capped' || this.cappedBranchOnScreen()
      ? `${this.copy.browseCapped}. ${this.copy.browseCappedHelp}`
      : '',
  );
  /** Mirrors the tree's render walk: only a branch under an unbroken chain of expanded rows shows. */
  private readonly cappedBranchOnScreen = computed(() => {
    const children = this.store.children();
    const expanded = this.expanded();
    const visit = (items: readonly NodeDto[], ancestors: ReadonlySet<string>): boolean => {
      for (const node of items) {
        // A childless node renders no children at all, whatever a stale page holds, so its cap
        // notice is off screen and must not be announced.
        if (ancestors.has(node.node_id) || node.child_count === 0) continue;
        if (!expanded.has(node.node_id)) continue;
        const page = children[node.node_id];
        if (page?.error?.kind === 'forbidden' || page?.error?.kind === 'not_found') continue;
        if (page?.status === 'capped') return true;
        const chain = new Set(ancestors).add(node.node_id);
        if (visit(page?.items ?? [], chain)) return true;
      }
      return false;
    };
    return visit(this.store.roots().items, new Set<string>());
  });

  protected select(id: string | null): void {
    const nodeId = this.store.selectNode(id);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParamsHandling: 'merge',
      queryParams: { node_id: nodeId, cursor: null },
    });
  }

  protected retry(): void {
    if (!this.busy()) void this.store.retryTree();
  }

  protected expand(id: string): void {
    void this.store.expand(id);
  }

  protected loadMore(id: string | null): void {
    void this.store.loadMoreChildren(id);
  }
}
