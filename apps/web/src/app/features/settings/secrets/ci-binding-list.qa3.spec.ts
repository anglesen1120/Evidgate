import { TestBed } from '@angular/core/testing';
import type { CiBindingView } from '@evidgate/sdk';
import bindings from '../../../../../../../fixtures/web-api/ci-bindings-list-settings/input.json';
import { CiBindingListComponent } from './ci-binding-list.component';

const rows = bindings.body.items as CiBindingView[];
const SECRET = 'tok_QA3_SECRET_NEVER_RENDER';

describe('CiBindingListComponent negatives (QA S86)', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [CiBindingListComponent] });
  });

  afterEach(() => TestBed.resetTestingModule());

  function render(data: readonly CiBindingView[], phase: 'ready' | 'loading' = 'ready') {
    const fixture = TestBed.createComponent(CiBindingListComponent);
    fixture.componentRef.setInput('rows', data);
    fixture.componentRef.setInput('phase', phase);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('never renders a secret-shaped field smuggled into a row (template binds named fields only)', () => {
    const smuggled = rows.map(
      (row) => ({ ...row, token: SECRET, signing_secret: SECRET }) as unknown as CiBindingView,
    );
    const root = render(smuggled);
    expect(root.textContent ?? '').not.toContain(SECRET);
    expect(root.innerHTML).not.toContain(SECRET);
  });

  it('renders zero rows for an empty ready list without throwing (boundary: empty)', () => {
    const root = render([]);
    expect(root.querySelectorAll('[data-ci-binding]')).toHaveLength(0);
  });

  it('renders zero rows while loading an empty list (boundary: loading)', () => {
    const root = render([], 'loading');
    expect(root.querySelectorAll('[data-ci-binding]')).toHaveLength(0);
  });
});
