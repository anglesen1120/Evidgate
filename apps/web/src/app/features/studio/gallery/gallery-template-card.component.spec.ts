import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { galleryFixture } from './studio-gallery.fixture';
import { GalleryTemplateCardComponent } from './gallery-template-card.component';

class CardHarness extends ComponentHarness {
  static hostSelector = 'app-gallery-template-card';
  readonly pick = this.locatorFor('button');
  readonly badge = this.locatorFor('[hlmBadge]');
  readonly badgeIcon = this.locatorFor('[hlmBadge] hlm-icon');
  readonly injected = this.locatorForOptional('img');
}

describe('GalleryTemplateCardComponent', () => {
  it.each(galleryFixture.templates)(
    'shows $title with a labelled source and selection state',
    async (item) => {
      const fixture = TestBed.createComponent(GalleryTemplateCardComponent);
      fixture.componentRef.setInput('item', { ...item, title: '<img src=x>' });
      fixture.componentRef.setInput('selected', false);
      const chosen = vi.fn();
      fixture.componentInstance.picked.subscribe(chosen);
      const card = await TestbedHarnessEnvironment.harnessForFixture(fixture, CardHarness);
      expect(await (await card.host()).text()).toContain('<img src=x>');
      expect(await card.injected()).toBeNull();
      expect(await (await card.badge()).text()).toContain(
        item.source === 'team' ? 'Team template' : 'Built-in template',
      );
      expect(await (await card.badgeIcon()).getAttribute('aria-hidden')).toBe('true');
      const pick = await card.pick();
      expect(await pick.getAttribute('aria-pressed')).toBe('false');
      await pick.click();
      expect(chosen).toHaveBeenCalledWith(item.id);
      fixture.componentRef.setInput('selected', true);
      expect(await pick.getAttribute('aria-pressed')).toBe('true');
    },
  );
});
