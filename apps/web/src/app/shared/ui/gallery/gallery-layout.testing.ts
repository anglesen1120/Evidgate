import { beforeEach, vi } from 'vitest';
import { provideLayoutTestStubs } from '../../testing/layout.testing';

/** Sonner subscribes to the OS preference; JSDOM does not provide that media query list. */
export function provideGalleryLayout(): void {
  provideLayoutTestStubs();
  beforeEach(() => {
    vi.stubGlobal('matchMedia', (media: string) => ({
      media,
      matches: false,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
    }));
  });
}
