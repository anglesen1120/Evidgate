import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { provideGalleryLayout } from './gallery-layout.testing';
import { UiGalleryComponent } from './ui-gallery.component';

class GalleryNavigationHarness extends ComponentHarness {
  static hostSelector = 'app-ui-gallery';
  readonly strip = this.locatorFor('main > nav');
  readonly links = this.locatorForAll('main > nav a');
  readonly headings = this.locatorForAll('main > section > h2');
}

describe('Gallery section index', () => {
  provideGalleryLayout();

  it('keeps native section anchors in one locally scrolling row with room for focus', async () => {
    const fixture = TestBed.createComponent(UiGalleryComponent);
    const gallery = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      GalleryNavigationHarness,
    );
    const strip = await gallery.strip();
    for (const name of ['flex-nowrap', 'overflow-x-auto', 'p-1', 'min-w-0']) {
      expect(await strip.hasClass(name)).toBe(true);
    }
    const links = await gallery.links();
    const ids = await Promise.all(
      (await gallery.headings()).map((heading) => heading.getAttribute('id')),
    );
    expect(await Promise.all(links.map((link) => link.getAttribute('href')))).toEqual(
      ids.map((id) => `#${id}`),
    );
    for (const link of links) {
      expect(await link.hasClass('whitespace-nowrap')).toBe(true);
      expect(await link.hasClass('shrink-0')).toBe(true);
      expect(await link.getAttribute('role')).toBeNull();
    }
  });
});
