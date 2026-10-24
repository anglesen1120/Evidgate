import { TestBed } from '@angular/core/testing';
import { ThemeService } from './theme.service';

describe('ThemeService', () => {
  beforeEach(() => {
    localStorage.removeItem('evidgate-theme');
    document.documentElement.removeAttribute('data-theme');
    TestBed.configureTestingModule({});
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    localStorage.removeItem('evidgate-theme');
    document.documentElement.removeAttribute('data-theme');
  });

  it('leaves data-theme unset when no explicit choice exists', () => {
    document.documentElement.setAttribute('data-theme', 'light');
    TestBed.inject(ThemeService);
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });

  it.each(['light', 'dark'] as const)('applies stored %s', (theme) => {
    localStorage.setItem('evidgate-theme', theme);
    const service = TestBed.inject(ThemeService);
    expect(document.documentElement.getAttribute('data-theme')).toBe(theme);
    expect(service.isDark()).toBe(theme === 'dark');
  });

  it('toggles the resolved theme and persists the explicit choice', () => {
    localStorage.setItem('evidgate-theme', 'light');
    const service = TestBed.inject(ThemeService);
    service.toggle();
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem('evidgate-theme')).toBe('dark');
    service.toggle();
    expect(localStorage.getItem('evidgate-theme')).toBe('light');
  });

  it('resolves an OS dark preference without persisting it; toggle chooses light', () => {
    vi.stubGlobal(
      'matchMedia',
      vi.fn(() => ({
        matches: true,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    );
    const service = TestBed.inject(ThemeService);
    expect(service.isDark()).toBe(true);
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
    expect(localStorage.getItem('evidgate-theme')).toBeNull();
    service.toggle();
    expect(localStorage.getItem('evidgate-theme')).toBe('light');
  });

  it('ignores invalid storage and remains usable if storage throws', () => {
    localStorage.setItem('evidgate-theme', 'invalid');
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });
    const service = TestBed.inject(ThemeService);
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
    expect(() => service.toggle()).not.toThrow();
    expect(document.documentElement.getAttribute('data-theme')).not.toBeNull();
  });
});
