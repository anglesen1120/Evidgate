import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SuiteWarningListComponent } from './suite-warning-list.component';

type Warnings = readonly string[] | null | undefined;

const warningStates: readonly { label: string; warnings: Warnings }[] = [
  { label: 'items', warnings: ['A suite warning'] },
  { label: 'empty', warnings: [] },
  { label: 'null', warnings: null },
  { label: 'undefined', warnings: undefined },
];

function itemTexts(root: HTMLElement): (string | null)[] {
  return [...root.querySelectorAll('[data-testid="suite-warnings-items"] li bdi')].map(
    (item) => item.textContent,
  );
}

describe('SuiteWarningListComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [SuiteWarningListComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());

  async function render(warnings: Warnings) {
    const fixture = TestBed.createComponent(SuiteWarningListComponent);
    fixture.componentRef.setInput('warnings', warnings);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('renders warnings verbatim as text in their supplied order', async () => {
    const warnings = ['Z last alphabetically', '<b>x</b>', 'A first alphabetically'];
    const { root } = await render(warnings);

    expect(root.querySelector('[data-testid="suite-warnings"]')?.tagName).toBe('SECTION');
    expect(root.querySelector('[data-testid="suite-warnings-items"]')?.tagName).toBe('UL');
    expect(itemTexts(root)).toEqual(warnings);
    expect(root.querySelectorAll('li')).toHaveLength(warnings.length);
    expect(root.querySelector('b')).toBeNull();
    expect(root.querySelector('[data-testid="suite-warnings-unavailable"]')).toBeNull();
    expect(root.querySelector('[data-testid="suite-warnings-empty"]')).toBeNull();
  });

  it('preserves duplicates, whitespace and empty strings without filtering the list', async () => {
    const warnings = ['Repeated', '  leading and trailing  ', '', 'Repeated', 'Line one\nLine two'];
    const { root } = await render(warnings);

    expect(itemTexts(root)).toEqual(warnings);
    expect(root.querySelectorAll('li')).toHaveLength(warnings.length);
    expect(root.querySelector('[data-testid="suite-warnings-empty"]')).toBeNull();
  });

  it('renders a known empty list as no warnings rather than unavailable', async () => {
    const { root } = await render([]);

    expect(root.querySelector('[data-testid="suite-warnings-empty"]')?.textContent).toBe(
      'No warnings reported.',
    );
    expect(root.querySelector('[data-testid="suite-warnings-unavailable"]')).toBeNull();
    expect(root.querySelector('[data-testid="suite-warnings-items"]')).toBeNull();
    expect(root.querySelector('li')).toBeNull();
  });

  it.each([
    { label: 'null', warnings: null },
    { label: 'undefined', warnings: undefined },
  ])('renders $label as unavailable with a reason, never as no warnings', async ({ warnings }) => {
    const { root } = await render(warnings);
    const unavailable = root.querySelector('[data-testid="suite-warnings-unavailable"]');

    expect(unavailable?.textContent).toContain('Warnings unavailable');
    expect(unavailable?.textContent).toContain(
      'The suite revision response does not include warning details.',
    );
    expect(unavailable?.textContent).not.toContain('No warnings');
    expect(unavailable?.textContent).not.toMatch(/\b0\b/);
    expect(root.querySelector('[data-testid="suite-warnings-empty"]')).toBeNull();
    expect(root.querySelector('[data-testid="suite-warnings-items"]')).toBeNull();
    expect(root.querySelector('li')).toBeNull();
  });

  it('starts unavailable when the optional warnings input is not bound', async () => {
    const fixture = TestBed.createComponent(SuiteWarningListComponent);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="suite-warnings-unavailable"]')?.textContent).toContain(
      'The suite revision response does not include warning details.',
    );
    expect(root.querySelector('[data-testid="suite-warnings-empty"]')).toBeNull();
  });

  it('replaces the previous state when the warnings input changes', async () => {
    const { fixture, root } = await render(['Previous warning']);
    fixture.componentRef.setInput('warnings', null);
    await fixture.whenStable();

    expect(root.querySelector('[data-testid="suite-warnings-unavailable"]')).not.toBeNull();
    expect(itemTexts(root)).toEqual([]);
    expect(root.textContent).not.toContain('Previous warning');

    fixture.componentRef.setInput('warnings', []);
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="suite-warnings-empty"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="suite-warnings-unavailable"]')).toBeNull();

    fixture.componentRef.setInput('warnings', ['Current warning']);
    await fixture.whenStable();
    expect(itemTexts(root)).toEqual(['Current warning']);
    expect(root.querySelector('[data-testid="suite-warnings-empty"]')).toBeNull();
  });

  it.each(warningStates)(
    'keeps the $label state free of alert roles and verdict classes',
    async ({ warnings }) => {
      const { root } = await render(warnings);

      expect(root.querySelector('[role="alert"]')).toBeNull();
      for (const element of root.querySelectorAll('[class]')) {
        for (const className of element.classList) {
          expect(className).not.toMatch(
            /(?:^|[-_])(pass|fail|passed|failed|success|danger|verdict)(?:$|[-_])/i,
          );
        }
      }
    },
  );
  it('uses a labelled Helm card without adding keyboard stops to read-only warnings', async () => {
    const warning = 'Cảnh báo bộ kiểm thử rất dài '.repeat(30);
    const { root } = await render([warning]);
    const section = root.querySelector('[data-testid="suite-warnings"]');
    expect(section?.getAttribute('data-slot')).toBe('card');
    expect(section?.getAttribute('aria-label')).toBe('Suite warnings');
    expect(root.querySelector('h2')?.getAttribute('data-slot')).toBe('card-title');
    expect(root.querySelector('h2')?.getAttribute('data-title-size')).toBe('section');
    expect(itemTexts(root)).toEqual([warning]);
    expect(root.querySelector('button, a, input, [tabindex]')).toBeNull();
  });
});
