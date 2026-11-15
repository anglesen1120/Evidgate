import { TestBed } from '@angular/core/testing';
import {
  dialogButton,
  flush,
  mountRetention,
  refusal,
  STORED,
} from './retention-form.test-support';

function submit(root: HTMLElement): void {
  root
    .querySelector('form')
    ?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
}

describe('RetentionFormComponent confirmation lifetime', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    TestBed.resetTestingModule();
  });

  it('keeps minimum-day drafts on cancel and only stores them after confirmation', async () => {
    const h = mountRetention();
    await h.load();
    await h.edit('1', '1');
    submit(h.root);
    await h.settle();
    expect(document.querySelector('[role="alertdialog"]')).not.toBeNull();
    expect(document.querySelectorAll('[data-retention-impact]')).toHaveLength(2);
    h.http.expectNone((request) => request.method === 'PUT');
    dialogButton('[data-retention-cancel]').click();
    await h.settle();
    expect(h.store.policy()).toEqual(STORED);
    expect(await (await (await h.harness()).artifact()).getProperty('value')).toBe('1');
    expect(await (await (await h.harness()).metadata()).getProperty('value')).toBe('1');
    h.http.expectNone((request) => request.method === 'PUT');
    submit(h.root);
    await h.settle();
    dialogButton('[data-retention-confirm]').click();
    const put = await h.take('PUT');
    expect(JSON.parse(String(put.request.body))).toEqual({ artifact_days: 1, metadata_days: 1 });
    flush(put, { artifact_days: 1, metadata_days: 1 });
    await h.settle();
    expect(h.store.policy()).toEqual({ artifact_days: 1, metadata_days: 1 });
    expect(h.root.querySelector('[data-retention-saved]')).not.toBeNull();
    h.http.verify();
  });

  it.each([200, 403, 419, 422])(
    'does not close a new organization confirmation when an old write settles with %s',
    async (status) => {
      const h = mountRetention();
      await h.load();
      const saves = vi.spyOn(h.store, 'save');
      await h.edit('1', '90');
      submit(h.root);
      await h.settle();
      dialogButton('[data-retention-confirm]').click();
      const previous = await h.take('PUT');
      const previousAttempt = saves.mock.results[0];
      if (previousAttempt?.type !== 'return') throw new Error('Expected a previous save attempt');
      h.org.set({ id: 'o-2' });
      h.projects.set([{ id: 'p-2', name: 'Other organization project', org_id: 'o-2' }]);
      flush(await h.take('GET', 'p-2'), { artifact_days: 10, metadata_days: 50 });
      await h.settle();
      await h.edit('1', '50');
      submit(h.root);
      await h.settle();
      expect(document.querySelector('[role="alertdialog"]')).not.toBeNull();
      h.http.expectNone((request) => request.method === 'PUT');
      flush(
        previous,
        status === 200 ? { artifact_days: 1, metadata_days: 90 } : refusal(status),
        status,
      );
      await previousAttempt.value;
      await h.settle();
      expect(h.store.policy()).toEqual({ artifact_days: 10, metadata_days: 50 });
      expect(document.querySelector('[role="alertdialog"]')).not.toBeNull();
      h.http.expectNone((request) => request.method === 'PUT');
      dialogButton('[data-retention-confirm]').click();
      flush(await h.take('PUT', 'p-2'), { artifact_days: 1, metadata_days: 50 });
      await h.settle();
      expect(h.store.policy()).toEqual({ artifact_days: 1, metadata_days: 50 });
      h.http.verify();
    },
  );
});
