import { TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { NodeDto } from '@evidgate/sdk';
import type { ApiError } from '../../core/http/api-error';
import type { AuthoringNodePage } from './authoring.store';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { AuthoringTreePaneComponent } from './authoring-tree-pane.component';
import { AuthoringTreeHarness } from './authoring-tree.testing';

const root: NodeDto = {
  node_id: '018f0000-0000-7000-8000-000000000101',
  parent_id: null,
  kind: 'module',
  title: 'Thanh toán',
  position: 0,
  version: 1,
  child_count: 1,
  item_count: 2,
};
const child: NodeDto = {
  ...root,
  node_id: '018f0000-0000-7000-8000-000000000102',
  parent_id: root.node_id,
  kind: 'feature',
  title: 'Địa chỉ giao hàng rất dài để kiểm tra cách xuống dòng tự nhiên',
  child_count: 0,
  item_count: 0,
};
const second: NodeDto = {
  ...root,
  node_id: '018f0000-0000-7000-8000-000000000103',
  title: 'Tài khoản',
  position: 1,
  child_count: 0,
};
const page = (items: NodeDto[]) => ({
  items,
  nextCursor: null,
  status: 'ready' as const,
  error: null,
});

describe('AuthoringTreePaneComponent', () => {
  provideLayoutTestStubs();
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [AuthoringTreePaneComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );

  async function render() {
    const fixture = TestBed.createComponent(AuthoringTreePaneComponent);
    fixture.componentRef.setInput('projectId', 'project');
    fixture.componentRef.setInput('roots', page([root, second]));
    fixture.componentRef.setInput('children', { [root.node_id]: page([child]) });
    fixture.detectChanges();
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      AuthoringTreeHarness,
    );
    return { fixture, harness };
  }

  it('uses nested native lists and never expands merely on focus or selection', async () => {
    const { fixture, harness } = await render();
    const expand = vi.fn();
    const select = vi.fn();
    fixture.componentInstance.expand.subscribe(expand);
    fixture.componentInstance.selectNode.subscribe(select);
    const scope = await harness.scope(root.node_id);
    await scope.focus();
    await scope.click();
    expect(select).toHaveBeenCalledWith(root.node_id);
    expect(expand).not.toHaveBeenCalled();
    expect(await harness.visibleIds()).toEqual([root.node_id, second.node_id]);
    expect(await harness.treeRoles()).toHaveLength(0);
    await (await harness.disclosure(root.node_id)).click();
    expect(expand).toHaveBeenCalledWith(root.node_id);
    expect(await harness.visibleIds()).toEqual([root.node_id, child.node_id, second.node_id]);
    expect(await harness.nestedLists()).toHaveLength(1);
  });

  it('moves focus with arrows and Home/End without selecting or fetching hidden branches', async () => {
    const { fixture, harness } = await render();
    const select = vi.fn();
    fixture.componentInstance.selectNode.subscribe(select);
    const first = await harness.disclosure(root.node_id);
    await first.focus();
    await first.sendKeys(TestKey.RIGHT_ARROW);
    expect(await first.getAttribute('aria-expanded')).toBe('true');
    await first.sendKeys(TestKey.RIGHT_ARROW);
    const childScope = await harness.scope(child.node_id);
    expect(await childScope.isFocused()).toBe(true);
    await childScope.sendKeys(TestKey.DOWN_ARROW);
    const last = await harness.scope(second.node_id);
    expect(await last.isFocused()).toBe(true);
    await last.sendKeys(TestKey.HOME);
    expect(await first.isFocused()).toBe(true);
    await first.sendKeys(TestKey.END);
    expect(await last.isFocused()).toBe(true);
    await last.sendKeys(TestKey.UP_ARROW);
    expect(await childScope.isFocused()).toBe(true);
    await childScope.sendKeys(TestKey.LEFT_ARROW);
    expect(await first.isFocused()).toBe(true);
    expect(select).not.toHaveBeenCalled();
  });

  it('returns focus to the parent disclosure before hiding a focused descendant', async () => {
    const { harness } = await render();
    const parent = await harness.disclosure(root.node_id);
    await parent.click();
    await (await harness.scope(child.node_id)).focus();
    await parent.click();
    expect(await parent.isFocused()).toBe(true);
    expect(await parent.getAttribute('aria-expanded')).toBe('false');
    expect(await harness.visibleIds()).toEqual([root.node_id, second.node_id]);
  });

  it('cycles repeated locale-aware type-ahead and starts a new prefix after an arrow key', async () => {
    const { harness } = await render();
    const first = await harness.disclosure(root.node_id);
    const last = await harness.scope(second.node_id);
    await first.focus();
    await first.sendKeys('t');
    expect(await last.isFocused()).toBe(true);
    await last.sendKeys('t');
    expect(await first.isFocused()).toBe(true);
    await first.sendKeys(TestKey.RIGHT_ARROW);
    await first.sendKeys('đ');
    expect(await (await harness.scope(child.node_id)).isFocused()).toBe(true);
  });

  it('shows direct child/checklist counts and unavailable pending, never a guessed case total', async () => {
    const { fixture, harness } = await render();
    fixture.componentRef.setInput('selectedNodeId', root.node_id);
    const text = await harness.text();
    expect(text).toContain('Child nodes');
    expect(text).toContain('Checklist items');
    expect(text).toContain('Pending change count unavailable');
    expect(await (await harness.scope(root.node_id)).getAttribute('aria-current')).toBe('location');
    expect(text).not.toContain('0 pending');
  });

  it('keeps child loading and cursor controls inside the expanded branch', async () => {
    const { fixture, harness } = await render();
    fixture.componentRef.setInput('children', {
      [root.node_id]: { ...page([]), nextCursor: 'next', status: 'loading' },
    });
    await (await harness.disclosure(root.node_id)).click();
    expect(await harness.loading()).not.toBeNull();
    fixture.componentRef.setInput('children', {
      [root.node_id]: { ...page([child]), nextCursor: 'next' },
    });
    const more = vi.fn();
    fixture.componentInstance.loadMore.subscribe(more);
    await (await harness.more(root.node_id)).click();
    expect(more).toHaveBeenCalledWith(root.node_id);
  });

  it('ignores control shortcuts and composing input without expanding or selecting', async () => {
    const { fixture, harness } = await render();
    const expand = vi.fn();
    const select = vi.fn();
    fixture.componentInstance.expand.subscribe(expand);
    fixture.componentInstance.selectNode.subscribe(select);
    const first = await harness.disclosure(root.node_id);
    await first.focus();
    await first.sendKeys({ control: true }, 't');
    await first.dispatchEvent('keydown', { key: 't', isComposing: true });
    expect(await first.isFocused()).toBe(true);
    expect(expand).not.toHaveBeenCalled();
    expect(select).not.toHaveBeenCalled();
  });

  it('retries a failed continuation without re-reading or hiding its loaded branch', async () => {
    const { fixture, harness } = await render();
    const failure: ApiError = {
      kind: 'unavailable',
      status: 503,
      code: 'unavailable',
      message: 'Fixture branch failed',
      requestId: null,
      retryable: true,
      retryAfterSeconds: null,
      details: [],
    };
    const retained: AuthoringNodePage = {
      items: [child],
      nextCursor: 'following',
      status: 'ready',
      error: failure,
    };
    fixture.componentRef.setInput('children', { [root.node_id]: retained });
    await (await harness.disclosure(root.node_id)).click();
    const more = vi.fn();
    fixture.componentInstance.loadMore.subscribe(more);
    await (await harness.errorRetry()).click();
    expect(more).toHaveBeenCalledWith(root.node_id);
    expect(await harness.visibleIds()).toEqual([root.node_id, child.node_id, second.node_id]);
  });

  it('does not expose retained descendant identities after a branch loses permission', async () => {
    const { fixture, harness } = await render();
    const denied: ApiError = {
      kind: 'forbidden',
      status: 403,
      code: 'forbidden',
      message: 'Fixture branch denied',
      requestId: null,
      retryable: false,
      retryAfterSeconds: null,
      details: [],
    };
    fixture.componentRef.setInput('children', {
      [root.node_id]: { ...page([child]), status: 'error', error: denied },
    });
    await (await harness.disclosure(root.node_id)).click();
    expect(await harness.forbidden()).not.toBeNull();
    expect(await harness.visibleIds()).toEqual([root.node_id, second.node_id]);
    expect(await harness.text()).not.toContain(child.title);
  });

  it('uses the root continuation without assigning it to a child scope', async () => {
    const { fixture, harness } = await render();
    fixture.componentRef.setInput('roots', {
      ...page([root, second]),
      nextCursor: 'root-following',
    });
    const more = vi.fn();
    fixture.componentInstance.loadMore.subscribe(more);
    await (await harness.more('roots')).click();
    expect(more).toHaveBeenCalledWith(null);
  });

  it('moves keyboard focus to the first appended child when its load-more action disappears', async () => {
    const { fixture, harness } = await render();
    const extra = {
      ...child,
      node_id: '018f0000-0000-7000-8000-000000000104',
      title: 'Additional child',
    };
    fixture.componentRef.setInput('children', {
      [root.node_id]: { ...page([child]), nextCursor: 'following' },
    });
    await (await harness.disclosure(root.node_id)).click();
    fixture.componentInstance.loadMore.subscribe(() => {
      fixture.componentRef.setInput('children', {
        [root.node_id]: { ...page([child]), nextCursor: 'following', status: 'loading' },
      });
    });
    const more = await harness.more(root.node_id);
    await more.focus();
    await more.click();
    fixture.componentRef.setInput('children', { [root.node_id]: page([child, extra]) });
    expect(await (await harness.scope(extra.node_id)).isFocused()).toBe(true);
  });

  it('restores focus to a load-more control that survives the append', async () => {
    const { fixture, harness } = await render();
    const extra = {
      ...child,
      node_id: '018f0000-0000-7000-8000-000000000105',
      title: 'Further child',
    };
    fixture.componentRef.setInput('children', {
      [root.node_id]: { ...page([child]), nextCursor: 'following' },
    });
    await (await harness.disclosure(root.node_id)).click();
    fixture.componentInstance.loadMore.subscribe(() => {
      fixture.componentRef.setInput('children', {
        [root.node_id]: { ...page([child]), nextCursor: 'following', status: 'loading' },
      });
    });
    const more = await harness.more(root.node_id);
    await more.focus();
    await more.click();
    await more.blur();
    fixture.componentRef.setInput('children', {
      [root.node_id]: { ...page([child, extra]), nextCursor: 'following' },
    });
    expect(await (await harness.more(root.node_id)).isFocused()).toBe(true);
    expect(await (await harness.scope(extra.node_id)).isFocused()).toBe(false);
  });

  it('skips a node whose id is already in its ancestor chain', async () => {
    const { fixture, harness } = await render();
    const branch = {
      ...root,
      node_id: '018f0000-0000-7000-8000-000000000106',
      parent_id: root.node_id,
      title: 'Cycle branch',
      child_count: 1,
    };
    fixture.componentRef.setInput('children', {
      [root.node_id]: page([branch]),
      [branch.node_id]: page([root]),
    });
    await (await harness.disclosure(root.node_id)).click();
    await (await harness.disclosure(branch.node_id)).click();
    expect(await harness.visibleIds()).toEqual([root.node_id, branch.node_id, second.node_id]);
  });

  it('finds rows whose ids carry CSS syntax and keeps their keyboard focus', async () => {
    const { fixture, harness } = await render();
    const odd = {
      ...root,
      node_id: 'odd"id#1 .node',
      title: 'Odd id',
      child_count: 1,
    };
    const nested = { ...child, parent_id: odd.node_id };
    fixture.componentRef.setInput('roots', page([odd, second]));
    fixture.componentRef.setInput('children', { [odd.node_id]: page([nested]) });
    const disclosure = await harness.disclosure(odd.node_id);
    await disclosure.focus();
    await disclosure.sendKeys(TestKey.RIGHT_ARROW);
    expect(await disclosure.getAttribute('aria-expanded')).toBe('true');
    await disclosure.sendKeys(TestKey.RIGHT_ARROW);
    expect(await (await harness.scope(nested.node_id)).isFocused()).toBe(true);
    await (await harness.scope(nested.node_id)).sendKeys(TestKey.LEFT_ARROW);
    expect(await disclosure.isFocused()).toBe(true);
    await disclosure.sendKeys(TestKey.LEFT_ARROW);
    expect(await disclosure.getAttribute('aria-expanded')).toBe('false');
  });
});
