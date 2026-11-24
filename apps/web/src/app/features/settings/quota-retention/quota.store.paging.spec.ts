import { TestBed } from '@angular/core/testing';

import { TenantStore } from '../../../core/session/tenant.store';
import {
  metric,
  mountQuotaStore,
  project,
  serverFailure,
  summary,
} from './quota.store.test-support';

function page(id: string, next: string | null) {
  return {
    ...summary(
      metric({ used: 900, projects: [project({ project_id: id })] }),
      metric({ metric: 'ai_tokens', used: 800, projects: [project({ project_id: id })] }),
    ),
    next_cursor: next,
  };
}

describe('QuotaStore project paging', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('replaces all metric project pages together and returns through opaque cursor history', async () => {
    const { store, data, settle } = mountQuotaStore();
    TestBed.inject(TenantStore).setOrg('org-one');
    data.answer = page('first', 'second');
    store.load();
    await settle();
    expect(data.calls[0]?.limit).toBe(20);
    expect(store.projectPage()).toBe(1);
    store.previousProjectPage();
    expect(data.calls).toHaveLength(1);

    data.answer = page('middle', 'final');
    store.nextProjectPage();
    await settle();
    expect(data.calls.at(-1)?.cursor).toBe('second');
    expect(store.projectPage()).toBe(2);
    expect(
      store.usageSummary()?.metrics.map((entry) => entry.projects.map((row) => row.project_id)),
    ).toEqual([['middle'], ['middle']]);
    expect(store.usageSummary()?.metrics.map((entry) => entry.used)).toEqual([900, 800]);

    data.answer = page('last', null);
    store.nextProjectPage();
    await settle();
    expect(store.projectPage()).toBe(3);
    expect(store.hasNextProjectPage()).toBe(false);
    store.nextProjectPage();
    expect(data.calls).toHaveLength(3);

    data.answer = page('middle', 'final');
    store.previousProjectPage();
    await settle();
    expect(data.calls.at(-1)?.cursor).toBe('second');
    expect(store.projectPage()).toBe(2);
    data.answer = page('first', 'second');
    store.previousProjectPage();
    await settle();
    expect(data.calls.at(-1)?.cursor).toBeUndefined();
    expect(store.projectPage()).toBe(1);
  });

  it('retains the served summary on a later-page error and retries the failed page', async () => {
    const { store, data, settle, advance } = mountQuotaStore();
    TestBed.inject(TenantStore).setOrg('org-one');
    data.answer = page('first', 'second');
    store.load();
    await settle();
    const first = store.usageSummary();
    data.hold = true;
    store.nextProjectPage();
    await advance();
    expect(store.usageSummary()).toEqual(first);
    expect(store.projectPage()).toBe(1);
    expect(store.projectPaging()).toBe(true);
    data.fail(serverFailure());
    await settle();
    expect(store.usageSummary()).toEqual(first);
    expect(store.projectPagingError()).toBe(true);
    expect(store.loadState()).toBe('ready');
    data.hold = false;
    data.answer = page('second', null);
    store.retryProjectPage();
    await settle();
    expect(data.calls.at(-1)?.cursor).toBe('second');
    expect(store.projectPage()).toBe(2);
    expect(store.projectPagingError()).toBe(false);
  });

  it('retries a failed Previous request without discarding the current project page', async () => {
    const { store, data, settle } = mountQuotaStore();
    TestBed.inject(TenantStore).setOrg('org-one');
    data.answer = page('first', 'second');
    store.load();
    await settle();
    data.answer = page('second', null);
    store.nextProjectPage();
    await settle();
    data.failure = serverFailure();
    store.previousProjectPage();
    await settle();
    expect(store.projectPage()).toBe(2);
    expect(store.usageSummary()?.metrics[0]?.projects[0]?.project_id).toBe('second');
    expect(store.projectPagingError()).toBe(true);
    data.failure = null;
    data.answer = page('first', 'second');
    store.retryProjectPage();
    await settle();
    expect(store.projectPage()).toBe(1);
    expect(data.calls.at(-1)?.cursor).toBeUndefined();
  });

  it('refreshes a refused settings write on the selected page without resetting its size or cursor', async () => {
    const { store, data, settle, relay } = mountQuotaStore();
    TestBed.inject(TenantStore).setOrg('org-one');
    data.answer = page('first', 'second');
    store.load();
    await settle();
    store.setProjectPageSize(50);
    await settle();
    data.answer = page('second', null);
    store.nextProjectPage();
    await settle();
    relay.notify();
    await settle();
    expect(store.projectPage()).toBe(2);
    expect(store.projectPageSize()).toBe(50);
    expect(data.calls.at(-1)?.limit).toBe(50);
    expect(data.calls.at(-1)?.cursor).toBe('second');
    expect(data.calls).toHaveLength(4);
  });
  it.each([50, 100])('resets cursor history when page size becomes %s', async (size) => {
    const { store, data, settle } = mountQuotaStore();
    TestBed.inject(TenantStore).setOrg('org-one');
    data.answer = page('first', 'second');
    store.load();
    await settle();
    data.answer = page('second', null);
    store.nextProjectPage();
    await settle();
    data.answer = page('resized', null);
    store.setProjectPageSize(size);
    await settle();
    expect(data.calls.at(-1)?.limit).toBe(size);
    expect(data.calls.at(-1)?.cursor).toBeUndefined();
    expect(store.projectPage()).toBe(1);
    expect(store.projectPageSize()).toBe(size);
    store.setProjectPageSize(30);
    store.setProjectPageSize(size);
    await settle();
    expect(data.calls).toHaveLength(3);
  });

  it('clears paging and old rows when scope or usage permission changes during a page request', async () => {
    const { store, data, settle, advance, permissions } = mountQuotaStore();
    const tenant = TestBed.inject(TenantStore);
    tenant.setOrg('org-one');
    data.answer = page('private', 'second');
    store.load();
    await settle();
    data.hold = true;
    store.nextProjectPage();
    await advance();
    permissions.set(new Set());
    await advance();
    expect(store.usageSummary()).toBeNull();
    expect(store.projectPage()).toBe(1);
    data.release(page('private-second', null));
    await settle();
    expect(store.usageSummary()).toBeNull();
    tenant.setOrg('org-two');
    data.hold = false;
    data.answer = page('new-org', null);
    permissions.set(new Set(['usage:read']));
    await settle();
    expect(data.calls.at(-1)?.orgId).toBe('org-two');
    expect(data.calls.at(-1)?.cursor).toBeUndefined();
    expect(store.usageSummary()?.metrics[0]?.projects[0]?.project_id).toBe('new-org');
  });

  it('keeps empty and one-page summaries terminal and refuses a repeated forward cursor', async () => {
    const { store, data, settle } = mountQuotaStore();
    TestBed.inject(TenantStore).setOrg('org-one');
    data.answer = summary();
    store.load();
    await settle();
    store.nextProjectPage();
    expect(data.calls).toHaveLength(1);
    data.answer = page('only', null);
    store.reload();
    await settle();
    expect(store.hasNextProjectPage()).toBe(false);
    data.answer = page('first', 'repeat');
    store.reload();
    await settle();
    data.answer = page('bad', 'repeat');
    store.nextProjectPage();
    await settle();
    expect(store.projectPagingError()).toBe(true);
    expect(store.projectPage()).toBe(1);
    expect(store.usageSummary()?.metrics[0]?.projects[0]?.project_id).toBe('first');
  });
});
