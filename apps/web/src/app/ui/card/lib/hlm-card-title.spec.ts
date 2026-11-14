import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { HlmCard } from './hlm-card';
import { HlmCardTitle, type HlmCardTitleSize } from './hlm-card-title';

@Component({
  selector: 'app-card-title-test-host',
  imports: [HlmCard, HlmCardTitle],
  template: `
    <section hlmCard [size]="cardSize">
      <h2 hlmCardTitle [size]="size()" class="consumer-title">{{ title() }}</h2>
    </section>
  `,
})
class CardTitleTestHost {
  readonly cardSize = 'sm' as const;
  readonly size = signal<HlmCardTitleSize>('default');
  readonly title = signal('Nguyễn — Kiểm thử');
}

class CardTitleHarness extends ComponentHarness {
  static hostSelector = '[hlmCardTitle]';

  async getSize(): Promise<string | null> {
    return (await this.host()).getAttribute('data-title-size');
  }

  async getTagName(): Promise<string> {
    return (await this.host()).getProperty<string>('tagName');
  }

  async hasClass(name: string): Promise<boolean> {
    return (await this.host()).hasClass(name);
  }

  async getText(): Promise<string> {
    return (await this.host()).text();
  }
}

describe('HlmCardTitle roles', () => {
  let fixture: ComponentFixture<CardTitleTestHost>;
  let harness: CardTitleHarness;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CardTitleTestHost],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(CardTitleTestHost);
    fixture.detectChanges();
    harness = await TestbedHarnessEnvironment.loader(fixture).getHarness(CardTitleHarness);
  });

  it('keeps the existing default recipe and consumer classes without assigning a heading role', async () => {
    expect(await harness.getSize()).toBe('default');
    expect(await harness.hasClass('spartan-card-title')).toBe(true);
    expect(await harness.hasClass('consumer-title')).toBe(true);
    expect(await harness.hasClass('eg-type-section-title')).toBe(false);
  });

  it.each([
    ['page', 'eg-type-page-title'],
    ['section', 'eg-type-section-title'],
    ['subsection', 'eg-type-subsection-title'],
  ] as const)(
    'applies the %s role independently of the h2 semantic level and small-card density',
    async (size, roleClass) => {
      fixture.componentInstance.size.set(size);
      expect(await harness.getSize()).toBe(size);
      expect(await harness.getTagName()).toBe('H2');
      expect(await harness.hasClass(roleClass)).toBe(true);
      expect(await harness.hasClass('consumer-title')).toBe(true);
    },
  );

  it('removes the previous role when returning to the legacy default', async () => {
    fixture.componentInstance.size.set('page');
    expect(await harness.hasClass('eg-type-page-title')).toBe(true);
    fixture.componentInstance.size.set('default');
    expect(await harness.hasClass('eg-type-page-title')).toBe(false);
    expect(await harness.hasClass('consumer-title')).toBe(true);
  });

  it('keeps long localized titles literal and complete', async () => {
    const title = 'Nguyễn — Quyền kiểm thử <img src=x> '.repeat(12);
    fixture.componentInstance.title.set(title);
    fixture.componentInstance.size.set('section');
    expect(await harness.getText()).toBe(title.trim());
  });
});
