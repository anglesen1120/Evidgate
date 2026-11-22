import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { galleryFixture } from './studio-gallery.fixture';
import { GalleryTemplateTableComponent } from './gallery-template-table.component';

class TableHarness extends ComponentHarness {
  static hostSelector = 'app-gallery-template-table';
  readonly view = this.locatorFor('[data-gallery-view]');
  readonly views = this.locatorForAll('[data-gallery-view]');
  readonly badge = this.locatorFor('[data-template-source]');
  readonly badgeIcon = this.locatorFor('[data-template-source] hlm-icon');
  readonly injected = this.locatorForOptional('img');
  readonly selected = this.locatorForOptional('[data-template-selected]');
}

provideLayoutTestStubs();

describe('GalleryTemplateTableComponent', () => {
  it.each(galleryFixture.templates)(
    'shows $title with a labelled source and selection state',
    async (item) => {
      const fixture = TestBed.createComponent(GalleryTemplateTableComponent);
      fixture.componentRef.setInput('rows', [{ ...item, title: '<img src=x>' }]);
      const chosen = vi.fn();
      fixture.componentInstance.picked.subscribe(chosen);
      const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, TableHarness);
      expect(await (await table.host()).text()).toContain('<img src=x>');
      expect(await table.injected()).toBeNull();
      expect(await (await table.badge()).text()).toContain(
        item.source === 'team' ? 'Team template' : 'Built-in template',
      );
      expect(await (await table.badgeIcon()).getAttribute('aria-hidden')).toBe('true');
      expect(await table.selected()).toBeNull();
      const view = await table.view();
      expect(await view.getAttribute('aria-haspopup')).toBe('dialog');
      await view.click();
      expect(chosen).toHaveBeenCalledWith(item.id);
      fixture.componentRef.setInput('selected', item.id);
      expect(await table.selected()).not.toBeNull();
    },
  );

  it.each([0, 1, 2])(
    'returns focus to the retained row position after removing row %s',
    async (index) => {
      const fixture = TestBed.createComponent(GalleryTemplateTableComponent);
      fixture.componentRef.setInput('rows', galleryFixture.templates);
      const table = await TestbedHarnessEnvironment.harnessForFixture(fixture, TableHarness);
      const trigger = (await table.views())[index];
      const item = galleryFixture.templates[index];
      if (!trigger || !item) throw new Error('Missing template row');
      await trigger.click();
      fixture.componentRef.setInput(
        'rows',
        galleryFixture.templates.filter((row) => row.id !== item.id),
      );
      const remaining = await table.views();
      expect(fixture.componentInstance.focusView(item.id)).toBe(true);
      expect(await remaining[Math.min(index, remaining.length - 1)]?.isFocused()).toBe(true);
    },
  );
});
