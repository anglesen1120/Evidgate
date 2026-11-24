import { formatNumber } from '@angular/common';

export const AUTHORING_TREE_COPY = {
  title: $localize`:@@authoring.tree.title:Feature tree`,
  description: $localize`:@@authoring.tree.description:Browse manual cases by checklist and inspect their automation coverage.`,
  hierarchy: $localize`:@@authoring.tree.hierarchy:Project hierarchy`,
  hierarchyDescription: $localize`:@@authoring.tree.hierarchy-description:Expand a branch explicitly to load its children. Selecting a scope does not expand it.`,
  keyboard: $localize`:@@authoring.tree.keyboard:Tab reaches each visible control. Arrow keys, Home, End and typing move focus without selecting. Enter or Space activates a button.`,
  children: $localize`:@@authoring.tree.children:Child nodes`,
  items: $localize`:@@authoring.tree.items:Checklist items`,
  pending: $localize`:@@authoring.tree.pending:Pending`,
  pendingReason: $localize`:@@authoring.tree.pending-reason:Pending change count unavailable; this read does not supply it.`,
  current: $localize`:@@authoring.tree.current:Current scope`,
  path: $localize`:@@authoring.tree.path:Selected node path`,
  all: $localize`:@@authoring.tree.all:Project roots`,
  details: $localize`:@@authoring.tree.details:Node details`,
  nodeId: $localize`:@@authoring.tree.node-id:Node ID`,
  loading: $localize`:@@authoring.tree.loading:Loading project hierarchy…`,
  loadingChildren: $localize`:@@authoring.tree.loading-children:Loading child nodes…`,
  empty: $localize`:@@authoring.tree.empty:This project has no authoring content yet`,
  noRoots: $localize`:@@authoring.tree.no-roots:No hierarchy nodes in this project`,
  emptyDescription: $localize`:@@authoring.tree.empty-description:Import a checklist and its manual cases to build the first branch of this tree.`,
  emptyChildren: $localize`:@@authoring.tree.empty-children:No child nodes in this scope`,
  refresh: $localize`:@@authoring.tree.refresh:Refresh hierarchy`,
  stale: $localize`:@@authoring.tree.stale:Hierarchy may be stale`,
  staleDescription: $localize`:@@authoring.tree.stale-description:Refresh failed; previously loaded hierarchy data is still shown. Retry to refresh this region only.`,
  moreRoots: $localize`:@@authoring.tree.more-roots:Load more root nodes`,
  moreChildren: $localize`:@@authoring.tree.more-children:Load more child nodes`,
  browseCapped: $localize`:@@authoring.tree.browse-capped:Hierarchy list is at its load limit`,
  table: $localize`:@@authoring.tree.table:Manual cases`,
  importExport: $localize`:@@authoring.tree.import-export:Import and export`,
  categoryCases: $localize`:@@authoring.tree.category-cases:Category cases`,
  directItems: $localize`:@@authoring.tree.direct-items:Direct checklist items`,
  loadedCases: $localize`:@@authoring.tree.loaded-cases:Loaded cases`,
  loadedScope: $localize`:@@authoring.tree.loaded-scope:Loaded in this scope`,
};

/** The injected cap, grouped for the active locale; the tree renders this notice per capped page. */
export function treeBrowseCappedHelp(cap: number, locale: string): string {
  return $localize`:@@authoring.tree.browse-capped-help:Showing the first ${formatNumber(cap, locale)}:cap: nodes in this list. Open a known node link to inspect a scope outside these results.`;
}
