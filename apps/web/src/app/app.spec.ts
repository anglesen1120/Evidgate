import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { App } from './app';

// 09.1.1-T2 ADR pending: the shell runs zoneless (00 §5.6 W3). The provider here mirrors
// appConfig so the component is exercised under the change detector it ships with.
describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  it('renders the application heading', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    // Angular types `DebugElement.nativeElement` as `any`; the fixture host is this
    // component's own `<app-root>` element, so the DOM type is asserted once, here.
    const root = fixture.nativeElement as HTMLElement;
    const heading = root.querySelector('h1');
    expect(heading?.textContent?.trim()).toBe('Evidgate');
  });
});
