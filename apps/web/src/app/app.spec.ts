import { ChangeDetectionStrategy, Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { App } from './app';

/** A routed component with no content: what the outlet mounts, so the root itself renders none. */
@Component({
  selector: 'app-spec-blank-route',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<span></span>',
})
class BlankRoute {}

// 09.1.1-T4.1: the root component is the router host, so the spec pins exactly that — an outlet
// and no page of its own. Routing behaviour is `app.routes.matching.spec.ts`'s subject; a route
// table here would make this spec fail for a reason it does not own.
//
// The shell runs zoneless (00 §5.6 W3, ADR-0008), so the provider mirrors `appConfig`; `ng test`
// already puts a mock platform location in the TestBed (no `provideLocationMocks()` needed, which
// moved to `@angular/common/testing` in v22 anyway). The table is a catch-all so the router has
// somewhere to match rather than reporting a navigation the spec never asked for.
describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App, BlankRoute],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: BlankRoute }]),
      ],
    }).compileComponents();
  });

  it('mounts a router outlet and renders no page itself', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    // Angular types `DebugElement.nativeElement` as `any`; the fixture host is this component's
    // own `<app-root>` element, so the DOM type is asserted once, here.
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('router-outlet')).not.toBeNull();
    expect(root.querySelector('h1')).toBeNull();
    // The page-filling box is a host class now, not a component stylesheet.
    expect(root.classList.contains('block')).toBe(true);
    expect(root.classList.contains('min-h-full')).toBe(true);
  });
});
