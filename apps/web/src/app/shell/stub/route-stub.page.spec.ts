// 09.1.1-T4.1 (plan 09.1.1 §10 T4): the placeholder page. What it must prove is that the label
// comes from the registry row the route names — one source for the sidebar and the page — and that
// an id the registry does not hold still renders a titled page instead of a broken one.

import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';

import { NAV_ENTRIES } from '../nav/nav-registry';
import { RouteStubPage } from './route-stub.page';

/** Mount the page as if its lazy route carried `entryId` in `data`. */
function renderFor(entryId: string | null): HTMLElement {
  TestBed.configureTestingModule({
    imports: [RouteStubPage],
    providers: [
      provideZonelessChangeDetection(),
      // The page walks `snapshot.pathFromRoot` and keeps the closest `data.entryId`; one snapshot
      // is the whole chain for this test.
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { pathFromRoot: [{ data: { entryId } }] } },
      },
    ],
  });
  const fixture = TestBed.createComponent(RouteStubPage);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('RouteStubPage', () => {
  it('titles the page with the registry label of the entry that mounted it', () => {
    const entry = NAV_ENTRIES[0];
    if (entry === undefined) {
      throw new Error('NAV_ENTRIES is empty, so no entry can mount the stub');
    }

    const root = renderFor(entry.id);

    expect(root.querySelector('h1')?.textContent?.trim()).toBe(entry.label);
  });

  it('says the screen is not built yet and shows no feature content', () => {
    const entry = NAV_ENTRIES[0];
    if (entry === undefined) {
      throw new Error('NAV_ENTRIES is empty, so no entry can mount the stub');
    }

    const root = renderFor(entry.id);

    const paragraphs = [...root.querySelectorAll('p')].map(
      (node) => node.textContent?.trim() ?? '',
    );
    expect(paragraphs.length).toBe(2);
    // The copy is the placeholder's own; it never repeats a resource name that could confirm a
    // foreign org or project exists (D-S68-Q3).
    expect(paragraphs.join(' ')).not.toContain(entry.segment);
  });

  it('falls back to a title when no registry entry matches the route', () => {
    const root = renderFor('no-such-entry');

    const heading = root.querySelector('h1')?.textContent?.trim() ?? '';
    expect(heading).not.toBe('');
  });
});
