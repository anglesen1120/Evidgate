import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RowActionComponent } from './row-action.component';

@Component({
  selector: 'app-row-action-test',
  imports: [RowActionComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      appRowAction
      #trigger
      [label]="label()"
      [attr.aria-label]="label()"
      [disabled]="disabled()"
      [tone]="tone()"
      (click)="clicks = clicks + 1; opener = trigger.nativeElement"
    ></button>
    <a appRowAction href="/projects" [label]="label()" [attr.aria-label]="label()"></a>
  `,
})
class Host {
  readonly label = signal('View Dev Project');
  readonly disabled = signal(false);
  readonly tone = signal<'neutral' | 'danger'>('neutral');
  clicks = 0;
  opener: HTMLElement | null = null;
}

class ActionHarness extends ComponentHarness {
  static hostSelector = 'button[appRowAction]';
  readonly icon = this.locatorFor('hlm-icon');
  async name() {
    return (await this.host()).getAttribute('aria-label');
  }
  async click() {
    await (await this.host()).click();
  }
  async focus() {
    await (await this.host()).focus();
    await (await this.host()).dispatchEvent('focus');
  }
  async focused() {
    return (await this.host()).isFocused();
  }
  async disabled() {
    return (await this.host()).getProperty<boolean>('disabled');
  }
  async hasClass(name: string) {
    return (await this.host()).hasClass(name);
  }
}

class LinkHarness extends ActionHarness {
  static override hostSelector = 'a[appRowAction]';
  async href() {
    return (await this.host()).getAttribute('href');
  }
  async type() {
    return (await this.host()).getAttribute('type');
  }
}

class TooltipHarness extends ComponentHarness {
  static hostSelector = '[role="tooltip"]';
  async text() {
    return (await this.host()).text();
  }
}

async function setup() {
  TestBed.configureTestingModule({
    imports: [Host],
    providers: [provideZonelessChangeDetection()],
  });
  const fixture = TestBed.createComponent(Host);
  await fixture.whenStable();
  const action = await TestbedHarnessEnvironment.loader(fixture).getHarness(ActionHarness);
  return { fixture, host: fixture.componentInstance, action };
}

describe('RowActionComponent', () => {
  it('renders a named native icon button with the compact Helm recipe and keyboard focus', async () => {
    const { action, host } = await setup();
    expect(await action.name()).toBe('View Dev Project');
    expect(await action.hasClass('spartan-button-size-icon-sm')).toBe(true);
    expect(await action.hasClass('spartan-button-variant-ghost')).toBe(true);
    expect(await (await action.icon()).getAttribute('aria-hidden')).toBe('true');
    await action.focus();
    expect(await action.focused()).toBe(true);
    await action.click();
    expect(host.clicks).toBe(1);
  });

  it('updates the contextual name and danger tone without executing an action', async () => {
    const { action, host } = await setup();
    host.label.set('Remove Dev Project');
    host.tone.set('danger');
    expect(await action.name()).toBe('Remove Dev Project');
    expect(await action.hasClass('row-action-danger')).toBe(true);
    expect(host.clicks).toBe(0);
  });

  it('shows the contextual tooltip on keyboard focus', async () => {
    const { fixture, action } = await setup();
    await action.focus();
    await vi.waitFor(async () => {
      const tooltip =
        await TestbedHarnessEnvironment.documentRootLoader(fixture).getHarness(TooltipHarness);
      expect(await tooltip.text()).toContain('View Dev Project');
    });
  });

  it('preserves native disabled semantics', async () => {
    const { action, host } = await setup();
    host.disabled.set(true);
    expect(await action.disabled()).toBe(true);
    await action.click();
    expect(host.clicks).toBe(0);
  });

  it('exposes the native opener from template references for focus restoration', async () => {
    const { action, host } = await setup();
    await action.click();
    expect(host.opener?.tagName).toBe('BUTTON');
    host.opener?.focus();
    expect(await action.focused()).toBe(true);
  });

  it('preserves anchor navigation semantics and the same contextual icon recipe', async () => {
    const { fixture } = await setup();
    const link = await TestbedHarnessEnvironment.loader(fixture).getHarness(LinkHarness);
    expect(await link.href()).toBe('/projects');
    expect(await link.type()).toBeNull();
    expect(await link.name()).toBe('View Dev Project');
    expect(await link.hasClass('spartan-button-size-icon-sm')).toBe(true);
    await link.focus();
    expect(await link.focused()).toBe(true);
  });
});
