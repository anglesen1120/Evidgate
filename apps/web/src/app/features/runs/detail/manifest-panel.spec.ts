import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { stubLayoutApis } from '../../../shared/testing/layout.testing';
import { ManifestPanelComponent } from './manifest-panel.component';
import type { RunStateDto } from '@evidgate/sdk';
import ready from '../../../../../../../fixtures/web-api/run-detail/input.json';
import { manifestFromRun, UNAVAILABLE_MANIFEST, type ManifestSummary } from './manifest-summary';

/** Presentation fixture for the component-level cases (copy, escaping, string shard ids). */
const manifest: ManifestSummary = {
  manifestHash: 'sha256:' + '0123456789abcdef'.repeat(4),
  suiteRevisionId: 'suite-revision-exact',
  policyRevisionId: 'policy-revision-exact',
  shardCount: 3,
  producers: [1, 2, 3],
};
describe('ManifestPanelComponent', () => {
  // Brain's card/tab observers construct a ResizeObserver, which jsdom does not implement.
  beforeEach(() => {
    stubLayoutApis();
    TestBed.configureTestingModule({
      imports: [ManifestPanelComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    TestBed.resetTestingModule();
  });
  async function render(summary: ManifestSummary) {
    const fixture = TestBed.createComponent(ManifestPanelComponent);
    fixture.componentRef.setInput('summary', summary);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }
  it('renders every present manifest member verbatim with declared shard names', async () => {
    const { root } = await render(manifest);
    expect(root.querySelector('[data-manifest-hash] bdi')?.textContent).toBe(manifest.manifestHash);
    expect(root.querySelector('[data-manifest-suite] bdi')?.textContent).toBe(
      manifest.suiteRevisionId,
    );
    expect(root.querySelector('[data-manifest-policy] bdi')?.textContent).toBe(
      manifest.policyRevisionId,
    );
    expect(root.querySelector('[data-manifest-shards]')?.textContent?.trim()).toBe('3');
    expect([...root.querySelectorAll('li bdi')].map((node) => node.textContent)).toEqual([
      'shard-1',
      'shard-2',
      'shard-3',
    ]);
    expect(root.querySelector('app-status-pill')).toBeNull();
  });
  it('labels its Helm card and keeps the full hash selectable with polite copy feedback', async () => {
    const { root } = await render(manifest);
    const panel = root.querySelector('section');
    expect(panel?.getAttribute('data-slot')).toBe('card');
    expect(panel?.getAttribute('aria-labelledby')).toBe('manifest-title');
    expect(root.querySelector('#manifest-title')?.textContent).toBe('Manifest summary');
    expect(root.querySelector('[data-manifest-hash] bdi')?.textContent).toBe(manifest.manifestHash);
    expect(root.querySelector('[role="status"]')?.getAttribute('aria-live')).toBe('polite');
    expect(root.querySelector('button')?.classList.contains('whitespace-normal')).toBe(true);
  });
  it('renders the getRun fixture manifest verbatim, shard names as declared on the wire', async () => {
    const run = ready.body as RunStateDto;
    const { root } = await render(manifestFromRun(run));
    expect(root.querySelector('[data-manifest-hash] bdi')?.textContent).toBe(run.manifest_hash);
    expect(root.querySelector('[data-manifest-suite] bdi')?.textContent).toBe(
      run.suite_revision_id,
    );
    expect(root.querySelector('[data-manifest-policy] bdi')?.textContent).toBe(
      run.policy_revision_id,
    );
    expect(root.querySelector('[data-manifest-shards]')?.textContent?.trim()).toBe(
      String(run.shard_count),
    );
    expect([...root.querySelectorAll('li bdi')].map((node) => node.textContent)).toEqual(
      run.declared_producers,
    );
    expect(root.querySelector('app-status-pill')).toBeNull();
  });
  it('renders a missing policy revision as plain not provided while preserving present values', async () => {
    const run = { ...(ready.body as RunStateDto), policy_revision_id: null };
    const { root } = await render(manifestFromRun(run));
    expect(root.querySelector('[data-manifest-policy]')?.textContent).toContain('Not provided');
    expect(root.querySelector('app-status-pill')).toBeNull();
  });
  it('renders absent facts as plain not provided, never a status or a value-like placeholder', async () => {
    const { root } = await render(UNAVAILABLE_MANIFEST);
    expect(root.querySelector('app-status-pill')).toBeNull();
    for (const value of root.querySelectorAll('dd'))
      expect(value.textContent).toContain('Not provided');
    expect(root.querySelector('button')).toBeNull();
    expect(root.textContent).not.toContain('PASS');
  });
  it('distinguishes known zero shards and an empty declaration from unavailable', async () => {
    const { root } = await render({ ...manifest, shardCount: 0, producers: [] });
    expect(root.querySelector('[data-manifest-shards]')?.textContent?.trim()).toBe('0');
    expect(root.querySelector('[data-manifest-producers]')?.textContent).toContain(
      'No producers declared',
    );
  });
  it('preserves string shard declarations and escapes format characters without parsing markup', async () => {
    const { root } = await render({
      ...manifest,
      suiteRevisionId: 'suite\u202e<script>',
      producers: ['1', 'shard-7'],
    });
    expect([...root.querySelectorAll('li bdi')].map((node) => node.textContent)).toEqual([
      'shard-1',
      'shard-7',
    ]);
    expect(root.textContent).toContain('suite⟨U+202E⟩<script>');
    expect(root.querySelector('script')).toBeNull();
  });
  it('copies the full hash and announces success', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const { root, fixture } = await render(manifest);
    root.querySelector('button')!.click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenCalledWith(manifest.manifestHash);
    expect(root.querySelector('[role="status"]')?.textContent).toBe('Manifest hash copied');
  });
  it('keeps the copy button enabled and focused while the copy is in flight', async () => {
    let settle!: () => void;
    // The deferred resolver by hand: `Promise.withResolvers` is ES2024 and this project
    // compiles against the ES2022 lib.
    const writeText = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          settle = resolve;
        }),
    );
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const { root, fixture } = await render(manifest);
    const button = root.querySelector('button')!;
    button.focus();
    expect(document.activeElement).toBe(button);

    button.click();
    await Promise.resolve();
    // The result is announced by the live region, so the control that carries the visitor's place
    // must stay focusable: `disabled` here would drop focus to `body` for the rest of the copy.
    expect(writeText).toHaveBeenCalledTimes(1);
    expect(button.disabled).toBe(false);
    await fixture.whenStable();
    expect(button.getAttribute('aria-busy')).toBe('true');
    expect(button.getAttribute('data-slot')).toBe('button');
    expect(document.activeElement).toBe(button);

    // A second activation starts nothing; that guard is the handler's, not the attribute's.
    button.click();
    await Promise.resolve();
    expect(writeText).toHaveBeenCalledTimes(1);

    settle();
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toBe('Manifest hash copied');
    expect(button.getAttribute('aria-busy')).toBe('false');
    expect(document.activeElement).toBe(button);
  });
  it('escapes format characters in the displayed hash but copies the exact raw hash', async () => {
    const hash = 'sha256:abc\u202edef';
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const { root, fixture } = await render({ ...manifest, manifestHash: hash });
    const displayed = root.querySelector('[data-manifest-hash] bdi');
    expect(displayed?.textContent).toBe('sha256:abc⟨U+202E⟩def');
    expect(displayed?.textContent).not.toContain('\u202e');
    root.querySelector('button')!.click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenCalledWith(hash);
  });
  it('offers manual copying when clipboard access fails and clears feedback for a new summary', async () => {
    vi.stubGlobal('navigator', {
      clipboard: { writeText: vi.fn().mockRejectedValue(new Error('denied')) },
    });
    const { root, fixture } = await render(manifest);
    root.querySelector('button')!.click();
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Copy failed.');
    expect(root.querySelector('[data-manifest-hash] bdi')?.textContent).toBe(manifest.manifestHash);
    fixture.componentRef.setInput('summary', UNAVAILABLE_MANIFEST);
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toBe('');
  });
});
