import { isSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { getState } from '@ngrx/signals';
import { ApiRequestError } from '../../../core/http/api-error';
import { createIntent } from '../../../core/http/intent';
import { mount, refusal, ROW, deferred, project } from './api-tokens.test-support';

describe('ApiTokensStore', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('loads metadata successfully without inventing a created timestamp', async () => {
    const h = mount();
    await h.settle();
    expect(h.store.phase()).toBe('success');
    expect(h.store.rows()).toEqual([ROW]);
    expect(h.list).toHaveBeenCalledWith('project-1', undefined, expect.any(AbortSignal));
    expect(h.store.rows()[0]).not.toHaveProperty('created_at');
  });

  it('publishes the empty state and opens the create signal from the next step', async () => {
    const h = mount(() => Promise.resolve({ items: [], next_cursor: null }));
    await h.settle();
    expect(h.store.phase()).toBe('empty');
    expect(h.store.createOpen()).toBe(false);
    h.store.openCreate();
    expect(h.store.createOpen()).toBe(true);
    h.store.closeCreate();
    expect(h.store.createOpen()).toBe(false);
  });

  it('publishes an error rather than leaving the page loading', async () => {
    const error = refusal('network');
    const h = mount(() => Promise.reject(error));
    await h.settle();
    expect(h.store.phase()).toBe('error');
    expect(h.store.loadError()).toEqual(error.error);
    expect(h.store.rows()).toEqual([]);
  });

  it('publishes forbidden on a server refusal', async () => {
    const h = mount(() => Promise.reject(refusal('forbidden')));
    await h.settle();
    expect(h.store.phase()).toBe('forbidden');
    expect(h.store.loadError()?.kind).toBe('forbidden');
    expect(h.store.rows()).toEqual([]);
  });

  it('projects metadata only and puts no full credential in any public signal', async () => {
    const full = 'credential-that-must-not-be-retained';
    const h = mount(() =>
      Promise.resolve({
        items: [{ ...ROW, token: full, secret: full }],
        next_cursor: null,
      }),
    );
    await h.settle();
    expect(JSON.stringify(getState(h.store))).not.toContain(full);
    for (const [name, value] of Object.entries(h.store)) {
      expect(name).not.toMatch(/^(token|secret)$/i);
      // A signal holding `undefined` serialises to `undefined`, which has no `toContain`.
      if (isSignal(value)) expect(JSON.stringify(value() ?? null)).not.toContain(full);
    }
    expect(h.store.rows()[0]).not.toHaveProperty('token');
    expect(h.store.rows()[0]).not.toHaveProperty('secret');
  });

  it('hides loaded rows and write controls when read permission disappears', async () => {
    const h = mount();
    await h.settle();
    h.permissions.set(new Set());
    await h.settle();
    expect(h.store.rows()).toEqual([]);
    expect(h.store.phase()).toBe('forbidden');
    h.store.openCreate();
    expect(h.store.createOpen()).toBe(false);
  });

  it('refuses to open create without api_token:write', async () => {
    const h = mount();
    h.permissions.set(new Set(['api_token:read']));
    await h.settle();
    h.store.openCreate();
    expect(h.store.createOpen()).toBe(false);
  });

  it('follows one cursor page per action, replacing rather than accumulating rows', async () => {
    const h = mount();
    h.list.mockResolvedValueOnce({ items: [ROW], next_cursor: 'next-page' });
    await h.settle();
    expect(h.store.hasNext()).toBe(true);
    h.list.mockResolvedValueOnce({ items: [{ ...ROW, id: 'row-2' }], next_cursor: null });
    h.store.next();
    await h.settle();
    expect(h.list).toHaveBeenLastCalledWith('project-1', 'next-page', expect.any(AbortSignal));
    expect(h.store.rows().map((row) => row.id)).toEqual(['row-2']);
    expect(h.store.hasNext()).toBe(false);
    h.store.next();
    expect(h.list).toHaveBeenCalledTimes(2);
  });

  it('clears the old scope immediately when switching project', async () => {
    const h = mount();
    await h.settle();
    h.projects.set([project('project-2', 'Other')]);
    await h.settle();
    expect(h.list).toHaveBeenLastCalledWith('project-2', undefined, expect.any(AbortSignal));
    expect(h.store.selectedProject()).toBe('project-2');
  });

  it('does not publish an old project answer after its read has been superseded', async () => {
    const first = deferred<{ items: (typeof ROW)[]; next_cursor: null }>();
    const h = mount(() => first.promise);
    h.fixture.detectChanges();
    await vi.waitFor(() => expect(h.list).toHaveBeenCalledTimes(1));
    h.list.mockResolvedValueOnce({ items: [{ ...ROW, id: 'new-scope' }], next_cursor: null });
    h.projects.set([project('project-2', 'Other')]);
    h.fixture.detectChanges();
    first.resolve({ items: [{ ...ROW, id: 'old-scope' }], next_cursor: null });
    await h.settle();
    expect(h.store.rows().map((row) => row.id)).toEqual(['new-scope']);
  });

  it('restores the project and the page named in the URL', async () => {
    const h = mount(undefined, {
      projects: [project('project-1'), project('project-2', 'Other')],
      query: { project: 'project-2', cursor: 'page-2' },
    });
    await h.settle();
    expect(h.store.selectedProject()).toBe('project-2');
    expect(h.list).toHaveBeenCalledWith('project-2', 'page-2', expect.any(AbortSignal));
  });

  it('ignores a cursor that arrives without the project it belongs to', async () => {
    const h = mount(undefined, { query: { cursor: 'page-2' } });
    await h.settle();
    expect(h.store.selectedProject()).toBe('project-1');
    expect(h.store.cursor()).toBeUndefined();
    expect(h.list).toHaveBeenCalledWith('project-1', undefined, expect.any(AbortSignal));
  });

  it('drops the page cursor when the selection moves to another project', async () => {
    const h = mount(undefined, {
      projects: [project('project-1'), project('project-2', 'Other')],
      query: { project: 'project-1', cursor: 'page-2' },
    });
    await h.settle();
    expect(h.store.cursor()).toBe('page-2');
    h.store.selectProject('project-2');
    await h.settle();
    expect(h.store.selectedProject()).toBe('project-2');
    // The URL still names project-1's page; applying it to project-2 would page the wrong list.
    expect(h.store.cursor()).toBeUndefined();
    expect(h.list).toHaveBeenLastCalledWith('project-2', undefined, expect.any(AbortSignal));
  });

  it('names the project a create dialog was opened for after the selection moves', async () => {
    const h = mount(undefined, { projects: [project('project-1'), project('project-2', 'Other')] });
    await h.settle();
    expect(h.store.createTarget()).toBeNull();
    h.store.openCreate();
    expect(h.store.createTarget()).toEqual({ projectId: 'project-1', projectName: 'Storefront' });
    h.projects.set([project('project-2', 'Other')]);
    await h.settle();
    // The dialog keeps the project it names instead of closing with an unrevealed secret.
    expect(h.store.createOpen()).toBe(true);
    expect(h.store.createTarget()).toEqual({ projectId: 'project-1', projectName: 'Storefront' });
    h.store.closeCreate();
    expect(h.store.createTarget()).toBeNull();
  });

  it('keeps a create refusal for the dialog it was submitted from after the selection moves', async () => {
    const h = mount(undefined, { projects: [project('project-1'), project('project-2', 'Other')] });
    await h.settle();
    h.store.openCreate();
    await h.settle();
    const failure = new ApiRequestError({
      kind: 'validation',
      status: 422,
      code: 'invalid_expiry',
      message: 'Expiry must be in the future.',
      requestId: 'create-race',
      retryable: false,
      retryAfterSeconds: null,
      details: [{ path: 'expires_at', reason: 'past' }],
    });
    h.create.mockRejectedValueOnce(failure);
    await expect(
      h.store.create(
        { name: 'Reader', expires_at: '2020-01-01T00:00:00Z', permissions: ['run:read'] },
        createIntent(),
      ),
    ).resolves.toBeNull();
    h.projects.set([project('project-2', 'Other')]);
    await h.settle();
    expect(h.store.createError()).toEqual(failure.error);
  });
});
