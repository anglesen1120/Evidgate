import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, resource } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { combineLatest, distinctUntilChanged, map } from 'rxjs';
import {
  HeaderActionsDirective,
  PageHeaderComponent,
  SectionHeaderComponent,
} from '../../shared/ui/page-header';
import { RequestStateComponent } from '../../shared/ui/states/request-state.component';
import { InlineFeedbackComponent } from '../../shared/ui/states/inline-feedback.component';
import { CopyableIdentifierComponent } from '../../shared/ui/values/copyable-identifier.component';
import { AuthoringTreePaneComponent } from './authoring-tree-pane.component';
import { AuthoringCaseTableComponent } from './authoring-case-table.component';
import { AUTHORING_TREE_COPY } from './authoring-tree.copy';
import { AuthoringStore } from './authoring.store';

@Component({
  selector: 'app-authoring-page',
  imports: [
    NgTemplateOutlet,
    HlmButton,
    HlmCardImports,
    HeaderActionsDirective,
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

  protected readonly ownsPageHeading = this.route.snapshot.data['pageHeadingLevel'] === 1;
  protected readonly copy = AUTHORING_TREE_COPY;
  private readonly scope = toSignal(
    combineLatest([
      combineLatest(this.route.pathFromRoot.map((route) => route.paramMap)),
      this.route.queryParamMap,
    ]).pipe(
      map(([params, query]) => ({
        projectId:
          [...params]
            .reverse()
            .map((param) => param.get('projectId'))
            .find(Boolean) ?? null,
        nodeId: query.get('node_id'),
      })),
      distinctUntilChanged(
        (before, after) => before.projectId === after.projectId && before.nodeId === after.nodeId,
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
  /** One polite line per settled read: the scope now shown, never one line per node. */
  protected readonly announcement = computed(() => {
    if (this.denied() || this.busy()) return '';
    const failed = this.store.selectedNodeId();
    if (this.store.selectionStatus() === 'error' && failed !== null) {
      return $localize`:@@authoring.tree.announce-selection-failed:Could not load selected scope ${failed}:nodeId:`;
    }
    const selected = this.store.selectedNode();
    if (selected !== null) {
      return $localize`:@@authoring.tree.announce-selected:Selected scope ${selected.title}:title:`;
    }
    return ['ready', 'capped'].includes(this.store.roots().status) && this.store.roots().items.length > 0
      ? $localize`:@@authoring.tree.announce-roots:Scope set to all project roots`
      : '';
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
