import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ManifestPanelComponent } from './manifest-panel.component';
import { UNAVAILABLE_MANIFEST, type ManifestSummary } from './manifest-summary';

/** Component presentation fixture only: these members do NOT exist on RunStateDto. */
const manifest: ManifestSummary = {
  manifestHash: 'sha256:' + '0123456789abcdef'.repeat(4),
  suiteRevisionId: 'suite-revision-exact',
  policyRevisionId: 'policy-revision-exact',
  shardCount: 3,
  producers: [1, 2, 3],
};
describe('ManifestPanelComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [ManifestPanelComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
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
  it('renders each absent member as the shared unavailable state, never a value-like placeholder', async () => {
    const { root } = await render(UNAVAILABLE_MANIFEST);
    expect(root.querySelectorAll('app-status-pill')).toHaveLength(5);
    for (const value of root.querySelectorAll('dd'))
      expect(value.textContent).toContain('Status unavailable');
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
