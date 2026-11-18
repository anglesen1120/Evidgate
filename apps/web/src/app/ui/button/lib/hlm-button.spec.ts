import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HlmButton } from './hlm-button';

@Component({
  selector: 'app-button-test-host',
  imports: [HlmButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      hlmBtn
      type="button"
      [tabIndex]="index()"
      [disabled]="disabled()"
      i18n="@@ui.button.test.button"
    >
      Button action
    </button>
    <a
      hlmBtn
      href="#destination"
      [tabIndex]="index()"
      [disabled]="disabled()"
      i18n="@@ui.button.test.anchor"
      >Link action</a
    >
  `,
})
class ButtonHost {
  readonly index = signal<number | null>(null);
  readonly disabled = signal(false);
}

class ButtonHarness extends ComponentHarness {
  static hostSelector = 'button[hlmBtn]';
  async tabIndex(): Promise<string | null> {
    return (await this.host()).getAttribute('tabindex');
  }
  async nativeDisabled(): Promise<boolean> {
    return (await this.host()).getProperty<boolean>('disabled');
  }
  async focus(): Promise<void> {
    await (await this.host()).focus();
  }
  async focused(): Promise<boolean> {
    return (await this.host()).isFocused();
  }
}

class AnchorHarness extends ButtonHarness {
  static override hostSelector = 'a[hlmBtn]';
}

function setup(index: number | null = null) {
  TestBed.configureTestingModule({
    imports: [ButtonHost],
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(ButtonHost);
  fixture.componentInstance.index.set(index);
  fixture.detectChanges();
  const loader = TestbedHarnessEnvironment.loader(fixture);
  return { fixture, host: fixture.componentInstance, loader };
}

describe('HlmButton tab order', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('preserves the default native tab order and disabled behavior for buttons and links', async () => {
    const h = setup();
    const button = await h.loader.getHarness(ButtonHarness);
    const anchor = await h.loader.getHarness(AnchorHarness);
    expect(await button.tabIndex()).toBeNull();
    expect(await anchor.tabIndex()).toBeNull();
    expect(await button.nativeDisabled()).toBe(false);
    h.host.disabled.set(true);
    expect(await button.tabIndex()).toBe('-1');
    expect(await anchor.tabIndex()).toBe('-1');
    expect(await button.nativeDisabled()).toBe(true);
    h.host.disabled.set(false);
    expect(await button.tabIndex()).toBeNull();
    expect(await anchor.tabIndex()).toBeNull();
    expect(await button.nativeDisabled()).toBe(false);
  });

  it('honors an initial controlled tab index while remaining programmatically focusable', async () => {
    const h = setup(-1);
    const button = await h.loader.getHarness(ButtonHarness);
    const anchor = await h.loader.getHarness(AnchorHarness);
    expect(await button.tabIndex()).toBe('-1');
    expect(await anchor.tabIndex()).toBe('-1');
    expect(await button.nativeDisabled()).toBe(false);
    await button.focus();
    expect(await button.focused()).toBe(true);
    h.host.index.set(0);
    expect(await button.tabIndex()).toBe('0');
    expect(await anchor.tabIndex()).toBe('0');
    h.host.disabled.set(true);
    expect(await button.tabIndex()).toBe('-1');
    expect(await button.nativeDisabled()).toBe(true);
    h.host.disabled.set(false);
    expect(await button.tabIndex()).toBe('0');
    expect(await anchor.tabIndex()).toBe('0');
    h.host.index.set(null);
    expect(await button.tabIndex()).toBeNull();
    expect(await anchor.tabIndex()).toBeNull();
  });

  it('retains a controlled negative tab index when native disabled is removed', async () => {
    const h = setup(-1);
    const button = await h.loader.getHarness(ButtonHarness);
    h.host.disabled.set(true);
    expect(await button.tabIndex()).toBe('-1');
    h.host.disabled.set(false);
    expect(await button.tabIndex()).toBe('-1');
    expect(await button.nativeDisabled()).toBe(false);
  });
});
