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
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import type { NodeDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmIcon } from '@evidgate/ui/icon';
import { RequestStateComponent } from '../../shared/ui/states/request-state.component';
import { NumberValueComponent } from '../../shared/ui/values/number-value.component';
import { AUTHORING_TREE_COPY } from './authoring-tree.copy';
import type { AuthoringNodePage } from './authoring.store';

@Component({
  selector: 'app-authoring-tree-pane',
  imports: [NgTemplateOutlet, HlmButton, HlmIcon, RequestStateComponent, NumberValueComponent],
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
  protected readonly expanded = linkedSignal({
    source: this.projectId,
    computation: () => new Set<string>(),
  });
  protected readonly visible = computed(() => {
    const nodes: NodeDto[] = [];
    const visit = (items: readonly NodeDto[]) => {
      for (const node of items) {
        nodes.push(node);
        const page = this.children()[node.node_id];
        if (
          this.expanded().has(node.node_id) &&
          page?.error?.kind !== 'forbidden' &&
          page?.error?.kind !== 'not_found'
        )
          visit(page?.items ?? []);
      }
    };
    visit(this.roots().items);
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
      const first = page.items[pending.loaded];
      if (!first) return;
      const active = document.activeElement;
      if (active !== pending.control && !(active === document.body && !pending.control.isConnected))
        return;
      this.focusNode(first.node_id);
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
      const branch = this.host.nativeElement.querySelector(`#authoring-children-${node.node_id}`);
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
    return page.status;
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

  private focusNode(id: string): void {
    const row = this.host.nativeElement.querySelector<HTMLElement>(`[data-node-id="${id}"]`);
    const target = row?.querySelector<HTMLElement>('[data-disclosure], [data-scope]');
    target?.focus();
    target?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }
}
