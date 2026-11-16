import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { LocaleSwitcherComponent, localeBundlePath } from './locale-switcher.component';

describe('LocaleSwitcherComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [LocaleSwitcherComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );

  it('labels both languages and requests a bundle switch when activated', async () => {
    const fixture = TestBed.createComponent(LocaleSwitcherComponent);
    const switched = vi.fn();
    fixture.componentInstance.switchRequested.subscribe(switched);
    await fixture.whenStable();
    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    expect(button.textContent?.replace(/\s+/g, ' ').trim()).toBe('EN / VI');
    expect(button.getAttribute('aria-label')).toBe(
      'EN / VI: Switch language between English and Vietnamese',
    );
    expect(button.querySelector('[lang="en"]')?.textContent).toBe('EN');
    expect(button.querySelector('[lang="vi"]')?.textContent).toBe('VI');
    button.click();
    expect(switched).toHaveBeenCalledOnce();
  });

  it('refuses switching while disabled and becomes available again', async () => {
    const fixture = TestBed.createComponent(LocaleSwitcherComponent);
    const switched = vi.fn();
    fixture.componentInstance.switchRequested.subscribe(switched);
    fixture.componentRef.setInput('disabled', true);
    await fixture.whenStable();
    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    expect(button.disabled).toBe(true);
    button.click();
    expect(switched).not.toHaveBeenCalled();
    fixture.componentRef.setInput('disabled', false);
    await fixture.whenStable();
    button.click();
    expect(switched).toHaveBeenCalledOnce();
  });
});

describe('localeBundlePath', () => {
  const route = '/orgs/org-one/projects/project-one/runs/run-one?filter=a%2Fb&page=2#evidence';

  it.each([
    ['en', 'https://evidgate.test/en/', '/vi'],
    ['vi', 'https://evidgate.test/vi/', '/en'],
    ['vi-VN', 'https://evidgate.test/review/vi/', '/review/en'],
  ])(
    'switches %s while retaining route, query, fragment and deployment prefix',
    (locale, base, prefix) => {
      expect(localeBundlePath(base, locale, route)).toBe(prefix + route);
    },
  );

  it.each(['/', '/garden/', '/preview/', '/enrichment/'])(
    'offers no sibling bundle for an unlocalized base %s',
    (path) => expect(localeBundlePath(`https://evidgate.test${path}`, 'en', route)).toBeNull(),
  );

  it('keeps the localized root available without adding an extra route segment', () => {
    expect(localeBundlePath('https://evidgate.test/en/', 'en', '/')).toBe('/vi/');
  });
});
