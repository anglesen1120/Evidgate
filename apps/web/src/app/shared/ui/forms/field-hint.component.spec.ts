import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HlmInput } from '@evidgate/ui/input';
import { FieldHintComponent } from './field-hint.component';

@Component({
  imports: [HlmInput, FieldHintComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(keydown.escape)': 'escaped.set(true)' },
  template: `
    <app-field-hint [descriptionId]="'code-help'" [label]="label" [text]="guidance()">
      <label for="code">{{ label }}</label>
    </app-field-hint>
    <input hlmInput id="code" [aria-describedby]="'code-help code-error'" />
    <p id="code-error">{{ correction }}</p>
  `,
})
class Host {
  readonly label = 'Action code';
  readonly correction = 'Invalid code.';
  readonly escaped = signal(false);
  readonly guidance = signal<string | undefined>('Use the exact code.');
}

class FieldHarness extends ComponentHarness {
  static hostSelector = 'app-field-hint';
  readonly trigger = this.locatorFor('button');
  readonly description = this.locatorFor('[popover]');
  readonly control = this.documentRootLocatorFactory().locatorFor('#code');
  readonly optionalTrigger = this.locatorForOptional('button');
}

async function setup() {
  TestBed.configureTestingModule({ imports: [Host] });
  const fixture = TestBed.createComponent(Host);
  const field = await TestbedHarnessEnvironment.loader(fixture).getHarness(FieldHarness);
  const panel = TestbedHarnessEnvironment.getNativeElement(
    await field.description(),
  ) as HTMLElement;
  // jsdom lacks the native top layer. Adapt only this fixture's panel; the browser spec
  // exercises real Enter/Space activation, visibility and the accessible description.
  for (const method of ['showPopover', 'hidePopover'] as const) {
    if (typeof panel[method] !== 'function')
      Object.defineProperty(panel, method, { value: vi.fn() });
  }
  return { field, host: fixture.componentInstance };
}

describe('FieldHintComponent', () => {
  it('preserves the input description and validation reference while collapsed and expanded', async () => {
    const { field } = await setup();
    const trigger = await field.trigger();
    const description = await field.description();
    const input = await field.control();
    expect(await trigger.getAttribute('aria-label')).toBe('Hint: Action code');
    expect(await trigger.getAttribute('type')).toBe('button');
    expect(await trigger.getAttribute('aria-controls')).toBe(await description.getAttribute('id'));
    expect((await description.getProperty<string>('textContent')).trim()).toBe(
      'Use the exact code.',
    );
    expect(await input.getAttribute('aria-describedby')).toBe('code-help code-error');
    await trigger.focus();
    expect(await trigger.getAttribute('aria-expanded')).toBe('false');
    await trigger.click();
    expect(await trigger.getAttribute('aria-expanded')).toBe('true');
    await trigger.sendKeys(TestKey.ESCAPE);
    expect(await trigger.getAttribute('aria-expanded')).toBe('false');
    expect(await trigger.isFocused()).toBe(true);
    expect(await input.getAttribute('aria-describedby')).toBe('code-help code-error');
  });

  it('handles Escape inside the panel without passing it to an enclosing dialog', async () => {
    const { field, host } = await setup();
    const trigger = await field.trigger();
    const panel = await field.description();
    await trigger.click();
    await panel.focus();
    expect(await panel.isFocused()).toBe(true);
    await panel.sendKeys(TestKey.ESCAPE);
    expect(await trigger.getAttribute('aria-expanded')).toBe('false');
    expect(await trigger.isFocused()).toBe(true);
    expect(host.escaped()).toBe(false);
  });

  it('removes the disclosure when optional guidance goes away', async () => {
    const { field, host } = await setup();
    await (await field.trigger()).click();
    host.guidance.set(undefined);
    expect(await field.optionalTrigger()).toBeNull();
    host.guidance.set('Updated guidance.');
    expect(await (await field.trigger()).getAttribute('aria-expanded')).toBe('false');
  });

  it('dismisses on an outside click but not a click inside the note', async () => {
    const { field } = await setup();
    const trigger = await field.trigger();
    await trigger.click();
    await (await field.description()).click();
    expect(await trigger.getAttribute('aria-expanded')).toBe('true');
    await (await field.control()).click();
    expect(await trigger.getAttribute('aria-expanded')).toBe('false');
  });
});
