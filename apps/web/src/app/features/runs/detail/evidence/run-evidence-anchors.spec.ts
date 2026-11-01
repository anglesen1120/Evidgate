import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AttemptsTableComponent } from '../attempts/attempts-table.component';
import type { AttemptRow } from '../attempts/attempt-summary';
import {
  attemptAnchorId,
  decodeEvidenceAnchor,
  evidenceHref,
  producerAnchorId,
  resolveEvidenceAnchor,
} from './run-evidence-anchors';

function mount(ids: readonly string[]): void {
  // A `<th>` outside a table is dropped by the HTML parser, so the fixture is a real table row.
  document.body.innerHTML = `<table><tbody><tr>${ids
    .map((id) => `<th id="${id}" tabindex="-1">row</th>`)
    .join('')}</tr></tbody></table>`;
}

describe('evidence anchor identity', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('builds the documented fragments for ordinary identifiers', () => {
    expect(attemptAnchorId('exec_42', 2)).toBe('attempt-exec_42-2');
    expect(producerAnchorId('shard-3')).toBe('producer-shard-3');
    expect(evidenceHref({ kind: 'attempt', executionKey: 'exec_42', attemptNo: 2 })).toBe(
      '#attempt-exec_42-2',
    );
    expect(evidenceHref({ kind: 'producer', shard: 'shard-3' })).toBe('#producer-shard-3');
  });

  it('keeps identity case-sensitive and never derives it from rendered text', () => {
    expect(attemptAnchorId('Exec', 1)).not.toBe(attemptAnchorId('exec', 1));
    expect(producerAnchorId('Shard-3')).not.toBe(producerAnchorId('shard-3'));
  });

  it('percent-encodes a reserved character once in the id and once in the fragment', () => {
    const id = attemptAnchorId('exec 42 #2', 3);
    expect(id).toBe('attempt-exec%2042%20%232-3');
    const href = evidenceHref({ kind: 'attempt', executionKey: 'exec 42 #2', attemptNo: 3 });
    expect(href).toBe('#attempt-exec%252042%2520%25232-3');
    expect(decodeEvidenceAnchor(href)).toBe(id);
  });

  it('uses the server attempt number, never an index: two attempts of one key differ', () => {
    expect(attemptAnchorId('checkout', 1)).toBe('attempt-checkout-1');
    expect(attemptAnchorId('checkout', 2)).toBe('attempt-checkout-2');
  });
});

describe('evidence anchor resolution', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('resolves an attempt fragment to the rendered row id', () => {
    mount(['attempt-exec_42-1', 'attempt-exec_42-2']);
    const target = resolveEvidenceAnchor('#attempt-exec_42-2', document);
    expect(target).not.toBeNull();
    expect(target?.id).toBe('attempt-exec_42-2');
    expect(target?.textContent).toBe('row');
  });

  it('resolves a producer fragment and accepts the fragment without its hash', () => {
    mount(['producer-shard-3']);
    expect(resolveEvidenceAnchor('#producer-shard-3', document)?.id).toBe('producer-shard-3');
    expect(resolveEvidenceAnchor('producer-shard-3', document)?.id).toBe('producer-shard-3');
  });

  it('resolves a percent-encoded identifier back to its exact DOM id', () => {
    const id = attemptAnchorId('exec 42 #2', 2);
    mount([id]);
    const href = evidenceHref({ kind: 'attempt', executionKey: 'exec 42 #2', attemptNo: 2 });
    expect(resolveEvidenceAnchor(href, document)?.id).toBe(id);
  });

  it('does not resolve an id that exists but is not an evidence anchor', () => {
    mount(['attempts-title', 'evidence-panel']);
    expect(resolveEvidenceAnchor('#attempts-title', document)).toBeNull();
    expect(resolveEvidenceAnchor('#evidence-panel', document)).toBeNull();
  });

  it('treats the fragment as data, never as a selector', () => {
    // The row renders the encoded id, so the raw bracket names nothing and the href still lands.
    mount([attemptAnchorId('a]b', 1)]);
    expect(resolveEvidenceAnchor('#attempt-a]b-1', document)).toBeNull();
    expect(
      resolveEvidenceAnchor(
        evidenceHref({ kind: 'attempt', executionKey: 'a]b', attemptNo: 1 }),
        document,
      )?.id,
    ).toBe('attempt-a%5Db-1');
  });

  it.each([
    '#',
    '',
    '#attempt-exec_42-0',
    '#attempt-exec_42-',
    '#attempt--2',
    '#producer-',
    '#row-3',
    '#attempt-exec_42-2%',
    '#%E0%A4%A',
  ])('rejects the malformed or unknown fragment %s', (fragment) => {
    mount(['attempt-exec_42-2']);
    expect(decodeEvidenceAnchor(fragment)).toBeNull();
    expect(resolveEvidenceAnchor(fragment, document)).toBeNull();
  });

  it('returns null when a well-formed anchor has no rendered target yet', () => {
    mount(['attempt-exec_42-1']);
    expect(resolveEvidenceAnchor('#attempt-exec_42-2', document)).toBeNull();
    expect(resolveEvidenceAnchor('#producer-shard-9', document)).toBeNull();
  });
});

describe('attempt row host', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [AttemptsTableComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());

  it('renders the documented id and a reason link resolves to that row', async () => {
    const rows: AttemptRow[] = [
      {
        executionKey: 'exec_42',
        attemptNo: 2,
        rawStatus: 'assertion-failed',
        normalisedStatus: 'failed',
        durationMs: 320,
        artifactCount: 1,
      },
    ];
    const fixture = TestBed.createComponent(AttemptsTableComponent);
    fixture.componentRef.setInput('attempts', rows);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;

    const header = root.querySelector('th[scope="row"]');
    expect(header?.id).toBe('attempt-exec_42-2');
    expect(header?.getAttribute('tabindex')).toBe('-1');
    const href = evidenceHref({ kind: 'attempt', executionKey: 'exec_42', attemptNo: 2 });
    expect(resolveEvidenceAnchor(href, root.ownerDocument)?.id).toBe('attempt-exec_42-2');
  });
});
