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

describe('RetentionFormComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('loads the selected project and displays both stored windows', async () => {
    const h = mountRetention();
    expect(h.root.textContent).toContain('Loading retention');
    await h.load();
    const form = await h.harness();
    expect(await (await form.artifact()).getProperty('value')).toBe('14');
    expect(await (await form.metadata()).getProperty('value')).toBe('90');
    expect(h.store.policy()).toEqual(STORED);
    h.http.verify();
  });

  it.each([
    ['', '90', 'retention-artifact'],
    ['', '', 'retention-artifact'],
    ['1.5', '90', 'retention-artifact'],
    ['14', '', 'retention-metadata'],
    ['30', '29', 'retention-metadata'],
  ])(
    'blocks invalid integer or ordered windows %s / %s and focuses %s',
    async (artifact, metadata, field) => {
      const h = mountRetention();
      await h.load();
      await h.edit(artifact, metadata);
      submit(h.root);
      await h.settle();
      expect(h.root.querySelector('[aria-invalid="true"]')).not.toBeNull();
      expect(document.activeElement).toBe(h.root.querySelector(`#${field}`));
      expect(
        h.root.querySelector('#retention-validation-error[role="alert"]')?.textContent,
      ).toContain('Retention was not saved');
      expect(document.querySelector('[role="dialog"]')).toBeNull();
      h.http.expectNone((request) => request.method === 'PUT');
    },
  );

  it('does not impose deployment bounds and submits larger integer windows directly', async () => {
    const h = mountRetention();
    await h.load();
    await h.edit('150', '200');
    submit(h.root);
    const request = await h.take('PUT');
    expect(JSON.parse(String(request.request.body))).toEqual({
      artifact_days: 150,
      metadata_days: 200,
    });
    expect(document.querySelector('[role="dialog"]')).toBeNull();
    flush(request, { artifact_days: 150, metadata_days: 200 });
    await h.settle();
    expect(h.root.querySelector('[data-retention-saved]')).not.toBeNull();
    expect(h.store.policy()).toEqual({ artifact_days: 150, metadata_days: 200 });
  });

  it.each([
    ['7', '90', 'artifact_days'],
    ['14', '60', 'metadata_days'],
  ])(
    'warns before lowering %s / %s and requires explicit confirmation',
    async (artifact, metadata, field) => {
      const h = mountRetention();
      await h.load();
      await h.edit(artifact, metadata);
      submit(h.root);
      await h.settle();
      const dialog = document.querySelector('[role="dialog"]');
      expect(dialog?.textContent).toContain('Review deletion impact');
      expect(dialog?.querySelector(`[data-retention-impact="${field}"]`)).not.toBeNull();
      expect(dialog?.textContent).toContain('eligible for deletion');
      h.http.expectNone((request) => request.method === 'PUT');
      dialogButton('[data-retention-confirm]').click();
      flush(await h.take('PUT'), {
        artifact_days: Number(artifact),
        metadata_days: Number(metadata),
      });
      await h.settle();
      expect(h.store.policy()).toEqual({
        artifact_days: Number(artifact),
        metadata_days: Number(metadata),
      });
    },
  );

  it('cancel preserves the edited values and leaves server truth unchanged without a PUT', async () => {
    const h = mountRetention();
    await h.load();
    await h.edit('7', '60');
    submit(h.root);
    await h.settle();
    dialogButton('[data-retention-cancel]').click();
    await h.settle();
    expect(h.store.policy()).toEqual(STORED);
    expect(await (await (await h.harness()).artifact()).getProperty('value')).toBe('7');
    expect(await (await (await h.harness()).metadata()).getProperty('value')).toBe('60');
    h.http.expectNone((request) => request.method === 'PUT');
  });

  it('ignores Escape while the confirmed write is in flight', async () => {
    const h = mountRetention();
    await h.load();
    await h.edit('7', '90');
    submit(h.root);
    await h.settle();
    dialogButton('[data-retention-confirm]').click();
    const put = await h.take('PUT');
    const content = document.querySelector<HTMLElement>('[data-slot="dialog-content"]');
    if (!content) throw new Error('Expected the deletion-impact dialog');
    const escape = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    content.dispatchEvent(escape);
    TestBed.tick();
    h.fixture.detectChanges();
    expect(escape.defaultPrevented).toBe(true);
    // The write owns the dialog: Escape must not even start its closing state.
    expect(content.getAttribute('data-state')).toBe('open');
    expect(content.isConnected).toBe(true);
    flush(put, { artifact_days: 7, metadata_days: 90 });
    await h.settle();
    expect(h.store.policy()).toEqual({ artifact_days: 7, metadata_days: 90 });
    h.http.verify();
  });

  it.each([422, 403, 404, 409])(
    'shows a %s refusal verbatim without replacing the stored windows',
    async (status) => {
      const h = mountRetention();
      await h.load();
      await h.edit('150', '200');
      submit(h.root);
      flush(await h.take('PUT'), refusal(status, 'The exact server refusal.'), status);
      await h.settle();
      expect(h.root.querySelector('[data-retention-error]')?.textContent).toContain(
        'The exact server refusal.',
      );
      expect(h.store.policy()).toEqual(STORED);
      if (status === 422) {
        expect(h.root.querySelector('#retention-artifact-error')?.textContent).toContain('120');
        expect(h.root.querySelector('#retention-artifact')?.getAttribute('aria-invalid')).toBe(
          'true',
        );
        expect(document.activeElement).toBe(h.root.querySelector('#retention-artifact'));
      }
    },
  );

  it('renders read-only values with a visible reason and no submit without write permission', async () => {
    const h = mountRetention(['project:read']);
    await h.load();
    expect(h.root.querySelector('#retention-read-only')?.textContent).toContain('Read only');
    expect(await (await (await h.harness()).artifact()).getProperty('readOnly')).toBe(true);
    expect(h.root.querySelector('[data-retention-save]')).toBeNull();
    expect(await h.store.save({ artifact_days: 30, metadata_days: 90 })).toBe(false);
    h.http.expectNone((request) => request.method === 'PUT');
  });

  it('does not read or show editable values without project read permission', async () => {
    const h = mountRetention(['project:write']);
    await h.settle();
    expect(h.root.querySelector('[data-retention-forbidden]')).not.toBeNull();
    expect(h.root.querySelector('form')).toBeNull();
    h.http.expectNone((request) => request.url.includes('/retention'));
  });

  it('shows a missing-project state without making an additional request', async () => {
    const h = mountRetention();
    await h.load();
    h.projects.set([]);
    await h.settle();
    expect(h.root.textContent).toContain('No project is available');
    expect(h.store.policy()).toBeNull();
  });
});
