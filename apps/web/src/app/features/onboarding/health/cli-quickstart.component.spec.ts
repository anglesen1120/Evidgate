import { provideZonelessChangeDetection } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import type { Mock } from 'vitest';

import { CliQuickstartComponent, buildQuickstartSnippet } from './cli-quickstart.component';

const token = { name: 'ci-main', prefix: 'egt_ab12cd' };
const binding = { repository_full_name: 'example/widgets' };

// Default/empty: metadata or setup guidance; busy: guarded Copy; error: manual copy;
// success: polite confirmation; overflow: wrapping, selectable pre/code in both themes.
describe('CliQuickstartComponent', () => {
  let fixture: ComponentFixture<CliQuickstartComponent>;
  let root: HTMLElement;
  let writeText: Mock;
  let originalClipboard: PropertyDescriptor | undefined;

  const snippet = () => root.querySelector<HTMLElement>('[data-testid="cli-quickstart-snippet"]')!;
  const copy = () => root.querySelector<HTMLButtonElement>('[data-testid="cli-quickstart-copy"]')!;
  const status = () =>
    root.querySelector<HTMLElement>('[data-testid="cli-quickstart-copy-status"]')!;

  beforeEach(async () => {
    originalClipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    await TestBed.configureTestingModule({
      imports: [CliQuickstartComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(CliQuickstartComponent);
    fixture.componentRef.setInput('projectRef', 'widgets');
    fixture.componentRef.setInput('token', token);
    fixture.componentRef.setInput('binding', binding);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  afterEach(() => {
    fixture.destroy();
    if (originalClipboard) {
      Object.defineProperty(navigator, 'clipboard', originalClipboard);
    } else {
      Reflect.deleteProperty(navigator, 'clipboard');
    }
    vi.restoreAllMocks();
  });

  it('renders and copies only name/prefix metadata, never any secret value in scope (T7.2)', async () => {
    // Assemble secret-shaped values at runtime so the secret-literal gate remains meaningful.
    const secret = ['egt', crypto.randomUUID().replaceAll('-', '').repeat(2)].join('_');
    const jwt = [
      btoa(JSON.stringify({ alg: 'HS256', typ: 'JWT' })),
      btoa(JSON.stringify({ sub: 'runtime-test' })),
      btoa(crypto.randomUUID()),
    ]
      .map((part) => part.replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, ''))
      .join('.');
    const challenge = ['binding', crypto.randomUUID(), 'challenge'].join('-');
    const tokenWithUnlistedFields = { ...token, secret, jwt };
    const bindingWithUnlistedFields = { ...binding, binding_challenge: challenge };
    fixture.componentRef.setInput('token', tokenWithUnlistedFields);
    fixture.componentRef.setInput('binding', bindingWithUnlistedFields);
    fixture.detectChanges();

    expect(snippet().textContent).toBe(
      buildQuickstartSnippet({
        projectRef: 'widgets',
        token: tokenWithUnlistedFields,
        binding: bindingWithUnlistedFields,
      }),
    );
    for (const value of [secret, jwt, challenge]) {
      expect(root.textContent).not.toContain(value);
      expect(root.outerHTML).not.toContain(value);
    }
    expect(root.textContent).toContain(token.name);
    expect(root.textContent).toContain(token.prefix);
    copy().click();
    await Promise.resolve();
    fixture.detectChanges();
    expect(writeText).toHaveBeenCalledExactlyOnceWith(snippet().textContent);
    const copied = writeText.mock.calls[0]![0] as string;
    for (const value of [secret, jwt, challenge]) {
      expect(copied).not.toContain(value);
      expect(root.textContent).not.toContain(value);
    }
    expect(copied).toContain(token.name);
    expect(copied).toContain(token.prefix);
  });

  it('sets non-secret CI configuration and the documented auth and quickstart commands', () => {
    const text = snippet().textContent;
    expect(text).toContain("export EVIDGATE_API_URL='<your-evidgate-api-url>'");
    expect(text).toContain("export EVIDGATE_PROJECT='widgets'");
    expect(text).toContain('non-secret CI variables');
    expect(text).toContain('EVIDGATE_INGEST_TOKEN from your CI masked secret store');
    expect(text).toContain('npx --yes --package @evidgate/cli -- evidgate auth login');
    expect(text).toContain('evidgate quickstart --write .github/workflows/evidgate.yml');
    expect(text).not.toContain('EVIDGATE_INGEST_TOKEN=');
  });

  it('renders the OIDC path when the token is null and references the challenge only by variable name', () => {
    fixture.componentRef.setInput('token', null);
    fixture.detectChanges();
    expect(snippet().textContent).toContain(binding.repository_full_name);
    expect(snippet().textContent).toContain('EVIDGATE_BINDING_CHALLENGE repository variable');
    expect(snippet().textContent).toContain('id-token: write');
    expect(snippet().textContent).not.toContain(token.prefix);
    expect(snippet().textContent).not.toContain('EVIDGATE_BINDING_CHALLENGE=');
  });

  it('renders token metadata without a binding', () => {
    fixture.componentRef.setInput('binding', null);
    fixture.detectChanges();
    expect(snippet().textContent).toContain(token.name);
    expect(snippet().textContent).toContain(token.prefix);
    expect(snippet().textContent).not.toContain('EVIDGATE_BINDING_CHALLENGE');
  });

  it('uses a project-slug placeholder and create-token guidance when no credentials exist', () => {
    fixture.componentRef.setInput('projectRef', null);
    fixture.componentRef.setInput('token', null);
    fixture.componentRef.setInput('binding', null);
    fixture.detectChanges();
    expect(snippet().textContent).toContain("export EVIDGATE_PROJECT='<project-slug>'");
    expect(snippet().textContent).toContain('Create an ingest token first');
    expect(snippet().textContent).not.toContain(token.name);
    expect(snippet().textContent).not.toContain('EVIDGATE_BINDING_CHALLENGE');
  });

  it('announces successful copying in a persistent polite live region', async () => {
    copy().click();
    await Promise.resolve();
    fixture.detectChanges();
    expect(status().textContent?.trim()).toBe('CLI quickstart commands copied.');
    expect(status().getAttribute('aria-live')).toBe('polite');
    expect(status().getAttribute('aria-atomic')).toBe('true');
    expect(status().textContent).not.toContain(token.prefix);
  });

  it('keeps the commands selectable and focuses them with manual-copy guidance after rejection', async () => {
    writeText.mockRejectedValue(new Error('clipboard denied'));
    const original = snippet().textContent;
    copy().click();
    await Promise.resolve();
    fixture.detectChanges();
    expect(status().textContent).toContain('Select the commands above and copy them manually.');
    expect(snippet().textContent).toBe(original);
    expect(document.activeElement).toBe(snippet().parentElement);
    expect(copy().getAttribute('aria-disabled')).toBe('false');
    expect(status().textContent).not.toContain('clipboard denied');
  });

  it('offers the same manual-copy fallback when the Clipboard API is unavailable', async () => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
    copy().click();
    await Promise.resolve();
    fixture.detectChanges();
    expect(status().textContent).toContain('Copy was unavailable.');
    expect(snippet().textContent).toContain(token.prefix);
    expect(writeText).not.toHaveBeenCalled();
  });

  it('uses an h3-named section and a descriptive, unique copy button name', () => {
    const section = root.querySelector('section')!;
    const heading = root.querySelector('h3')!;
    expect(root.dataset['testid']).toBe('cli-quickstart');
    expect(section.getAttribute('aria-labelledby')).toBe(heading.id);
    expect(copy().textContent?.trim()).toBe('Copy CLI quickstart commands');
    expect(copy().type).toBe('button');
    expect(snippet().tagName).toBe('CODE');
    expect(snippet().parentElement?.tagName).toBe('PRE');
    expect(snippet().parentElement?.tabIndex).toBe(0);
  });

  it('guards concurrent copies without moving focus or disabling the native button', async () => {
    let finish!: () => void;
    // ES2022 lib does not expose Promise.withResolvers; follow the existing reveal specs.
    writeText.mockReturnValue(
      new Promise<void>((resolve) => {
        finish = resolve;
      }),
    );
    copy().focus();
    copy().click();
    fixture.detectChanges();
    copy().click();
    expect(writeText).toHaveBeenCalledTimes(1);
    expect(copy().getAttribute('aria-busy')).toBe('true');
    expect(copy().getAttribute('aria-disabled')).toBe('true');
    expect(copy().disabled).toBe(false);
    expect(document.activeElement).toBe(copy());
    finish();
    await Promise.resolve();
    fixture.detectChanges();
    expect(copy().getAttribute('aria-busy')).toBe('false');
  });

  it('clears stale success when inputs change and ignores superseded clipboard results', async () => {
    copy().click();
    await Promise.resolve();
    fixture.detectChanges();
    expect(status().textContent?.trim()).not.toBe('');
    let finish!: () => void;
    writeText.mockReturnValue(
      new Promise<void>((resolve) => {
        finish = resolve;
      }),
    );
    copy().click();
    fixture.componentRef.setInput('projectRef', 'replacement');
    fixture.detectChanges();
    finish();
    await Promise.resolve();
    fixture.detectChanges();
    expect(status().textContent?.trim()).toBe('');
    expect(snippet().textContent).toContain("EVIDGATE_PROJECT='replacement'");
    expect(copy().getAttribute('aria-disabled')).toBe('false');
  });
});

describe('buildQuickstartSnippet', () => {
  it('keeps metadata on comment lines and quotes the project as shell data', () => {
    const text = buildQuickstartSnippet({
      projectRef: "project'quoted",
      token: { name: 'ci-main\nnot-a-command', prefix: token.prefix },
      binding: null,
    });
    expect(text).toContain(`export EVIDGATE_PROJECT='project'"'"'quoted'`);
    expect(text).toContain('# Ingest token: ci-main not-a-command');
    expect(text).not.toContain('\nnot-a-command');
  });
});
