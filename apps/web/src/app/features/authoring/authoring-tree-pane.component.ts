import { FocusKeyManager } from '@angular/cdk/a11y';
import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  LOCALE_ID,
  afterRenderEffect,
  computed,
  inject,
  input,
  model,
  output,
  signal,
} from '@angular/core';
import type { NodeDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmIcon } from '@evidgate/ui/icon';
import { RequestStateComponent } from '../../shared/ui/states/request-state.component';
import { InlineFeedbackComponent } from '../../shared/ui/states/inline-feedback.component';
import { NumberValueComponent } from '../../shared/ui/values/number-value.component';
import { AUTHORING_TREE_COPY } from './authoring-tree.copy';
import type { AuthoringNodePage } from './authoring.store';

@Component({
  selector: 'app-authoring-tree-pane',
  imports: [
    NgTemplateOutlet,
    HlmButton,
    HlmIcon,
    RequestStateComponent,
    InlineFeedbackComponent,
    NumberValueComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './authoring-tree-pane.component.html',
  styleUrl: './authoring-tree-pane.component.css',
  host: { class: 'block min-w-0', '(keydown)': 'onKeydown($event)' },
})
export class AuthoringTreePaneComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly locale = inject(LOCALE_ID);

  readonly projectId = input.required<string>();
  readonly roots = input.required<AuthoringNodePage>();
  readonly children = input<Readonly<Record<string, AuthoringNodePage>>>({});
  readonly selectedNodeId = input<string | null>(null);
  readonly selectNode = output<string>();
  readonly expand = output<string>();
  readonly loadMore = output<string | null>();
  protected readonly copy = AUTHORING_TREE_COPY;
  /** Owned by the surface that renders the tree, so the page can tell which cap notices show. */
  readonly expanded = model<ReadonlySet<string>>(new Set<string>());
  /** The cycle guard's starting chain: the roots have no ancestor above them. */
  protected readonly noAncestors: ReadonlySet<string> = new Set<string>();
  protected readonly visible = computed(() => {
    const nodes: NodeDto[] = [];
    const visit = (items: readonly NodeDto[], ancestors: ReadonlySet<string>) => {
      for (const node of items) {
        if (ancestors.has(node.node_id)) continue;
        nodes.push(node);
        const page = this.children()[node.node_id];
        if (
          this.expanded().has(node.node_id) &&
          page?.error?.kind !== 'forbidden' &&
          page?.error?.kind !== 'not_found'
        )
          visit(page?.items ?? [], this.chain(ancestors, node.node_id));
      }
    };
    visit(this.roots().items, this.noAncestors);
    return nodes;
  });
  private readonly keys = computed(() =>
    new FocusKeyManager(
      this.visible().map((node) => ({
        focus: () => this.focusNode(node.node_id),
        getLabel: () => node.title,
      })),
    )
      .withVerticalOrientation()
      .withHomeAndEnd(),
  );
  private prefix = '';
  private typedAt = 0;
  private readonly pendingMore = signal<{
    projectId: string;
    parentId: string | null;
    loaded: number;
    control: HTMLElement;
  } | null>(null);

  constructor() {
    afterRenderEffect(() => {
      const pending = this.pendingMore();
      if (!pending) return;
      if (pending.projectId !== this.projectId()) {
        this.pendingMore.set(null);
        return;
      }
      const page = pending.parentId === null ? this.roots() : this.children()[pending.parentId];
      if (!page || page.status === 'loading') return;
      this.pendingMore.set(null);
      const { activeElement, body } = this.host.nativeElement.ownerDocument;
      if (activeElement !== pending.control && activeElement !== body) return;
      const control = this.moreControl(pending.parentId);
      if (page.nextCursor !== null && control !== null) {
        control.focus();
        return;
      }
      const first = page.items[pending.loaded];
      if (first) this.focusNode(first.node_id);
    });
  }

  protected requestMore(
    parentId: string | null,
    page: AuthoringNodePage | undefined,
    event: Event,
  ): void {
    if (!page || page.status === 'loading') return;
    const control = event.currentTarget;
    if (control instanceof HTMLElement) {
      this.pendingMore.set({
        parentId,
        projectId: this.projectId(),
        loaded: page.items.length,
        control,
      });
    }
    this.loadMore.emit(parentId);
  }

  protected canExpand(node: NodeDto): boolean {
    return node.child_count !== 0;
  }

  protected disclosureLabel(node: NodeDto): string {
    return this.expanded().has(node.node_id)
      ? $localize`:@@authoring.tree.collapse:Collapse ${node.title}:title:`
      : $localize`:@@authoring.tree.expand:Expand ${node.title}:title:`;
  }

  protected selectionLabel(node: NodeDto): string {
    return $localize`:@@authoring.tree.select:Select scope ${node.title}:title:`;
  }

  protected toggle(node: NodeDto): void {
    if (!this.canExpand(node)) return;
    const open = this.expanded().has(node.node_id);
    if (open) {
      const branch = this.rowFor(node.node_id)?.closest('li')?.querySelector('ul');
      if (branch?.contains(this.host.nativeElement.ownerDocument.activeElement)) {
        this.focusNode(node.node_id);
      }
    }
    this.expanded.update((current) => {
      const next = new Set(current);
      if (open) next.delete(node.node_id);
      else next.add(node.node_id);
      return next;
    });
    if (!open) this.expand.emit(node.node_id);
  }
  protected pageStatus(page: AuthoringNodePage | undefined): 'loading' | 'ready' | 'error' {
    if (!page || page.status === 'idle') return 'loading';
    if (page.error) return 'error';
    return page.status === 'capped' ? 'ready' : page.status;
  }

  protected retryBranch(parentId: string, page: AuthoringNodePage | undefined): void {
    if (page?.status === 'ready' && page.nextCursor) this.loadMore.emit(parentId);
    else this.expand.emit(parentId);
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.isComposing || event.altKey || event.ctrlKey || event.metaKey) return;
    const target = event.target;
    if (!(target instanceof HTMLElement)) return;
    const id = target.dataset['disclosure'] ?? target.dataset['scope'];
    if (!id) return;
    const index = this.visible().findIndex((node) => node.node_id === id);
    const node = this.visible()[index];
    if (!node) return;
    if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
      this.prefix = '';
      event.preventDefault();
      this.horizontal(node, event.key === 'ArrowRight');
    } else if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
      this.prefix = '';
      const keys = this.keys();
      keys.updateActiveItem(index);
      keys.onKeydown(event);
    } else if (event.key.length === 1 && event.key !== ' ') {
      event.preventDefault();
      this.typeAhead(event.key, index, event.timeStamp);
    }
  }

  private horizontal(node: NodeDto, right: boolean): void {
    const open = this.expanded().has(node.node_id);
    if (right) {
      if (!this.canExpand(node)) return;
      if (!open) this.toggle(node);
      else {
        const first = this.children()[node.node_id]?.items[0];
        if (first) this.focusNode(first.node_id);
      }
    } else if (open) this.toggle(node);
    else if (node.parent_id) this.focusNode(node.parent_id);
  }

  private typeAhead(key: string, index: number, time: number): void {
    const next = key.toLocaleLowerCase(this.locale).normalize('NFC');
    this.prefix = time - this.typedAt > 700 ? next : this.prefix + next;
    this.typedAt = time;
    const characters = Array.from(this.prefix);
    const prefix = characters.every((character) => character === next) ? next : this.prefix;
    const nodes = this.visible();
    for (let step = 1; step <= nodes.length; step++) {
      const node = nodes[(index + step) % nodes.length];
      if (node?.title.toLocaleLowerCase(this.locale).normalize('NFC').startsWith(prefix)) {
        this.focusNode(node.node_id);
        return;
      }
    }
  }

  /** The ancestor ids a deeper branch adds: a node already in this chain is never rendered twice. */
  protected chain(ancestors: ReadonlySet<string>, nodeId: string): ReadonlySet<string> {
    const next = new Set(ancestors);
    next.add(nodeId);
    return next;
  }

  /** Matches the attribute value itself, so an id carrying CSS syntax still finds its row. */
  private rowFor(id: string): HTMLElement | null {
    const rows = this.host.nativeElement.querySelectorAll<HTMLElement>('[data-node-id]');
    for (const row of rows) if (row.dataset['nodeId'] === id) return row;
    return null;
  }

  private moreControl(parentId: string | null): HTMLElement | null {
    const key = parentId ?? 'roots';
    const controls = this.host.nativeElement.querySelectorAll<HTMLElement>('[data-more]');
    for (const control of controls) if (control.dataset['more'] === key) return control;
    return null;
  }

  private focusNode(id: string): void {
    const row = this.rowFor(id);
    const target = row?.querySelector<HTMLElement>('[data-disclosure], [data-scope]');
    target?.focus();
    target?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }
}
