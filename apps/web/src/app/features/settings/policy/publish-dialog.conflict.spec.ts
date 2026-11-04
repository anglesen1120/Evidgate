import { TestBed } from '@angular/core/testing';
import type { PolicyRevisionDto } from '@evidgate/sdk';
import {
  conflict,
  configurePublishDialog,
  edit,
  FakePolicyStore,
  render,
  revision,
  waitForConflict,
} from './publish-dialog.test-support';

describe('PublishDialogComponent conflict comparison', () => {
  let store: FakePolicyStore;
  beforeEach(() => {
    store = new FakePolicyStore();
    configurePublishDialog(store, () => true);
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });
  it('shows the head body as server values for added, removed and changed rows', async () => {
    const headBody: PolicyRevisionDto = {
      ...revision,
      id: 'current-head',
      body: { flaky_budget: 2, collection_timeout_minutes: 30 },
    };
    store.loadBody.mockResolvedValueOnce(revision).mockResolvedValueOnce(headBody);
    store.publish.mockResolvedValue({
      kind: 'conflict',
      error: conflict,
      currentRevisionId: 'current-head',
    });
    const { fixture, root, textarea, submit } = await render();
    edit(textarea, JSON.stringify({ flaky_budget: 1, late_window_minutes: 15 }, null, 2));
    submit.click();
    const shared = await waitForConflict(fixture, root);
    expect(store.loadBody).toHaveBeenLastCalledWith('current-head');
    const fields = [...shared.querySelectorAll('.field')];
    expect(fields).toHaveLength(3);
    const field = (path: string): Element => {
      const found = fields.find((item) => item.querySelector('h3')?.textContent?.includes(path));
      if (found === undefined) throw new Error(`no conflict field for ${path}`);
      return found;
    };
    expect(field('/flaky_budget').querySelector('.local-value')?.textContent).toContain('1');
    expect(field('/flaky_budget').querySelector('.server-value')?.textContent).toContain('2');
    expect(field('/late_window_minutes').querySelector('.server-value')?.textContent).toMatch(/\S/);
    expect(field('/late_window_minutes').querySelector('.local-value')?.textContent).toContain(
      '15',
    );
    expect(
      field('/collection_timeout_minutes').querySelector('.server-value')?.textContent,
    ).toContain('30');
    expect(field('/collection_timeout_minutes').querySelector('.local-value')?.textContent).toMatch(
      /\S/,
    );
  });
  it('falls back to the envelope fields when the head body cannot be loaded', async () => {
    store.loadBody
      .mockResolvedValueOnce(revision)
      .mockRejectedValueOnce(new Error('head read refused'));
    store.publish.mockResolvedValue({
      kind: 'conflict',
      error: conflict,
      currentRevisionId: 'current-head',
    });
    const { fixture, root, submit } = await render();
    submit.click();
    const shared = await waitForConflict(fixture, root);
    expect(store.loadBody).toHaveBeenLastCalledWith('current-head');
    const fields = [...shared.querySelectorAll('.field')];
    expect(fields).toHaveLength(1);
    expect(fields[0]?.querySelector('.local-value')?.textContent).toMatch(/\S/);
    expect(fields[0]?.querySelector('.server-value')?.textContent).toMatch(/\S/);
    shared.querySelector<HTMLButtonElement>('button.reload')?.click();
    await fixture.whenStable();
    expect(store.reload).toHaveBeenCalledTimes(1);
    expect(store.publish).toHaveBeenCalledTimes(1);
  });
  it('caps the conflict comparison at a hundred field rows', async () => {
    const wideBody = Object.fromEntries(
      Array.from({ length: 150 }, (_unused, index) => [`field_${index}`, index]),
    ) as PolicyRevisionDto['body'];
    store.loadBody
      .mockResolvedValueOnce(revision)
      .mockResolvedValueOnce({ ...revision, id: 'current-head', body: wideBody });
    store.publish.mockResolvedValue({
      kind: 'conflict',
      error: conflict,
      currentRevisionId: 'current-head',
    });
    const { fixture, root, textarea, submit } = await render();
    edit(textarea, '{}');
    submit.click();
    const shared = await waitForConflict(fixture, root);
    expect([...shared.querySelectorAll('.field')]).toHaveLength(100);
  });
  it('renders every row of an array change even when the rows share one pointer', async () => {
    store.loadBody.mockResolvedValueOnce(revision).mockResolvedValueOnce({
      ...revision,
      id: 'current-head',
      body: { required_suites: ['checkout', 'search', 'storefront-catalog'] },
    });
    store.publish.mockResolvedValue({
      kind: 'conflict',
      error: conflict,
      currentRevisionId: 'current-head',
    });
    const { fixture, root, textarea, submit } = await render();
    edit(textarea, JSON.stringify({ required_suites: ['checkout'] }));
    submit.click();
    const shared = await waitForConflict(fixture, root);
    const fields = [...shared.querySelectorAll('.field')];
    expect(fields).toHaveLength(2);
    for (const field of fields) {
      expect(field.querySelector('h3')?.textContent).toContain('/required_suites');
    }
  });
  it('falls back to the envelope fields when the head body has no differing field', async () => {
    store.loadBody
      .mockResolvedValueOnce(revision)
      .mockResolvedValueOnce({ ...revision, id: 'current-head' });
    store.publish.mockResolvedValue({
      kind: 'conflict',
      error: conflict,
      currentRevisionId: 'current-head',
    });
    const { fixture, root, submit } = await render();
    submit.click();
    const shared = await waitForConflict(fixture, root);
    expect([...shared.querySelectorAll('.field')]).toHaveLength(1);
    expect(store.publish).toHaveBeenCalledTimes(1);
  });
});
