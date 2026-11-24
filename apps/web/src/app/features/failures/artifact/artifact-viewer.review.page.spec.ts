import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it } from 'vitest';
import { ArtifactViewerReviewPage } from './artifact-viewer.review.page';

class PageHarness extends ComponentHarness {
  static hostSelector = 'app-artifact-viewer-review';
  readonly text = async () => (await this.host()).text();
  readonly find = (selector: string) => this.locatorForOptional(selector)();
  readonly metadata = this.locatorFor('table a[aria-label="Metadata · art-shot-92"]');
  readonly metadataIcon = this.locatorFor('table a[aria-label="Metadata · art-shot-92"] hlm-icon');
}

class PreviewDialogHarness extends ComponentHarness {
  static hostSelector = '[role="dialog"]';
  readonly text = async () => (await this.host()).text();
  readonly embeddedContent = this.locatorForOptional(
    'iframe, object, embed, img[src], a[download]',
  );
}

async function mount(state: string, id?: string) {
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'orgs/:orgId/projects/:projectId', component: ArtifactViewerReviewPage },
      ]),
    ],
  }).compileComponents();
  const query = new URLSearchParams(id === undefined ? { state } : { state, id });
  const { fixture } = await RouterTestingHarness.create(`/orgs/org/projects/project?${query}`);
  const page = await TestbedHarnessEnvironment.loader(fixture).getHarness(PageHarness);
  return { page, overlays: TestbedHarnessEnvironment.documentRootLoader(fixture) };
}

describe('ArtifactViewerReviewPage', () => {
  it.each([
    ['default', 'Standard · redacted'],
    ['loading', 'Loading pinned evidence'],
    ['empty', 'No artifact attached'],
    ['filtered', 'No matching artifacts'],
    ['error', 'Unable to refresh'],
    ['403', 'Permissions changed'],
    ['404', 'Not found'],
    ['401', 'Session expired'],
    ['429', 'Retry-After: 30 s'],
    ['412', 'rv-12 → rv-13'],
    ['reloaded', 'Review revision rv-13'],
    ['trace', 'Ready trace'],
    ['restricted', 'Sensitive · restricted'],
    ['scan', 'Scan pending'],
    ['pending_upload', 'Pending upload'],
    ['uploaded', 'Uploaded · not ready'],
    ['rejected', 'Checksum mismatch'],
    ['pending_delete', 'Pending deletion'],
    ['deleted', 'Deleted by retention policy'],
    ['gated', 'Content gated'],
    ['large', 'Too large for the bounded viewer'],
    ['refresh', 'Authorization refresh failed'],
    ['historical', 'ev-2 is historical'],
  ])('shows the %s snapshot', async (state, expected) => {
    const { page } = await mount(state);
    expect(await page.text()).toContain(expected);
  });
  it('keeps the original failed checkout and its pinned decision independent of the retry', async () => {
    const { page } = await mount('default', 'art-shot-92');
    const text = await page.text();
    for (const value of [
      'att-1842-01',
      'case-checkout-17',
      'run-1842',
      'ev-3',
      'has_failures',
      'FAIL',
      'sha256:ad72…f18b',
    ])
      expect(text).toContain(value);
    expect(text).not.toMatch(/inc-92|\bPASS\b/);
    expect(await page.find('app-decision-chip hlm-icon')).not.toBeNull();
    expect(await (await page.find('app-page-header p'))?.text()).toContain(
      'art-shot-92 · image/png · 248 KiB · ev-3',
    );
    expect(
      await (await page.find('table a[aria-current="page"]'))?.getAttribute('aria-label'),
    ).toContain('art-shot-92');
    const metadata = await page.metadata();
    expect(await metadata.text({ exclude: '[popover]' })).toBe('');
    expect(await metadata.getAttribute('href')).toContain('id=art-shot-92');
    expect(await (await page.metadataIcon()).getAttribute('aria-hidden')).toBe('true');
    await metadata.focus();
    expect(await metadata.isFocused()).toBe(true);
    expect(await page.find('iframe, object, embed, img[src], a[download]')).toBeNull();
  });
  it('does not substitute official metadata for unavailable Studio content', async () => {
    const { page } = await mount('default', 'ev-s1');
    expect(await page.text()).toContain('Sandbox artifact ev-s1 unavailable');
    expect(await page.text()).toContain('run-sandbox-88');
    expect(await page.text()).not.toMatch(/run-1842|art-shot-92|248 KiB|FAIL/);
    expect(await page.find('app-page-header a, table a[aria-current]')).toBeNull();
  });
  it('keeps a pending upload neutral and closes both content actions', async () => {
    const { page } = await mount('pending_upload');
    expect(
      await (await page.find('app-artifact-preview section > div .status-pill--neutral'))?.text(),
    ).toContain('Pending upload');
    expect(
      await page.find('app-artifact-preview section > div .status-pill--neutral hlm-icon'),
    ).not.toBeNull();
    expect(await page.find('[data-inspect], [data-download]')).toBeNull();
  });
  it('opens an illustrative capture without loading bytes or activating a download', async () => {
    const { page, overlays } = await mount('default');
    await (await page.find('[data-inspect]'))?.click();
    const dialog = await overlays.getHarness(PreviewDialogHarness);
    expect(await dialog.text()).toContain('Illustrative capture with redacted fields');
    expect(await dialog.text()).toContain('art-shot-92');
    expect(await dialog.embeddedContent()).toBeNull();
  });
  it.each(['art-dom-93', 'art-log-94', 'art-trace-91'])(
    'does not open %s on a review reload',
    async (id) => {
      const { page } = await mount('reloaded', id);
      expect(await page.find('[data-inspect], [data-download]')).toBeNull();
      expect(await page.text()).toContain(id);
    },
  );
  it('never substitutes a different requested record', async () => {
    const { page } = await mount('default', 'missing');
    expect(await page.text()).toContain('Not found');
    expect(await page.text()).not.toContain('art-shot-92');
  });
});
