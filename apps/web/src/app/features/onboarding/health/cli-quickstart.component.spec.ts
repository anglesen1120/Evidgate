import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import type { Mock } from 'vitest';

import { CliQuickstartComponent, buildQuickstartSnippet } from './cli-quickstart.component';
import cliManifest from '../../../../../../../packages/cli/package.json';

const cliPin = `--package @evidgate/cli@${cliManifest.version}`;

const token = { name: 'ci-main', prefix: 'egt_ab12cd' };
const binding = { repository_full_name: 'example/widgets' };

class QuickstartHarness extends ComponentHarness {
  static hostSelector = 'app-cli-quickstart';
  readonly copyButton = this.locatorFor('[data-testid="cli-quickstart-copy"]');
  readonly snippet = this.locatorFor('pre');
}

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

  it('keeps both commands and the copy action reachable by keyboard focus', async () => {
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, QuickstartHarness);
    const commands = await harness.snippet();
    await commands.focus();
    expect(await commands.isFocused()).toBe(true);
    const action = await harness.copyButton();
    await action.focus();
    expect(await action.isFocused()).toBe(true);
    expect(await action.getAttribute('type')).toBe('button');
    await action.click();
    expect(writeText).toHaveBeenCalledOnce();
    expect(status().textContent).toContain('CLI quickstart commands copied.');
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
    expect(text).toContain(`npx --yes ${cliPin} -- evidgate auth login`);
    expect(text).toContain(
      `npx --yes ${cliPin} -- evidgate quickstart --write .github/workflows/evidgate.yml`,
    );
    expect(text).not.toContain('EVIDGATE_INGEST_TOKEN=');
  });

  it('keeps the project-slug placeholder when projectRef is null but token metadata exists', () => {
    fixture.componentRef.setInput('projectRef', null);
    fixture.detectChanges();
    expect(snippet().textContent).toContain("export EVIDGATE_PROJECT='<project-slug>'");
    expect(snippet().textContent).toContain(token.name);
    expect(snippet().textContent).toContain(token.prefix);
    expect(snippet().textContent).not.toContain("export EVIDGATE_PROJECT='widgets'");
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
    expect(section.getAttribute('data-slot')).toBe('card');
    expect(copy().getAttribute('data-slot')).toBe('button');
    expect(snippet().parentElement?.getAttribute('aria-labelledby')).toBe(heading.id);
    const hintId = snippet().parentElement?.getAttribute('aria-describedby');
    expect(hintId ? root.querySelector(`[id="${hintId}"]`)?.textContent : '').toContain(
      'metadata only',
    );
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

  it('turns terminal control and bidi characters in a name into spaces', () => {
    const text = buildQuickstartSnippet({
      projectRef: 'project',
      token: { name: `ci\u001b[201~main\u202Eevil\u0085`, prefix: token.prefix },
      binding: { repository_full_name: 'acme/re\u0007po' },
    });
    expect(text).toContain('# Ingest token: ci [201~main evil');
    expect([...text].filter((char) => char !== '\n' && /[\p{Cc}\p{Cf}]/u.test(char))).toEqual([]);
  });

  it('pins both npx commands to the version in packages/cli/package.json', () => {
    expect(cliManifest.version).toMatch(/^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$/);
    const text = buildQuickstartSnippet({ projectRef: 'widgets', token, binding });
    const commands = text.split('\n').filter((line) => line.startsWith('npx '));
    expect(commands).toHaveLength(2);
    for (const command of commands) expect(command).toContain(`${cliPin} `);
    expect(text).not.toContain('--package @evidgate/cli --');
  });

  it('drops control characters from the slug so it cannot start another command line', () => {
    const hostile = buildQuickstartSnippet({
      projectRef: 'pro\u0000ject\nrm -rf /\r\u007ftail\u0085\u2028\u2029',
      token,
      binding,
    });
    const lines = hostile.split('\n');
    expect(lines).toContain("export EVIDGATE_PROJECT='projectrm -rf /tail'");
    expect(lines).not.toContain('rm -rf /');
    expect(lines.filter((line) => line.startsWith('npx '))).toHaveLength(2);
    expect(hostile.split('\n')).toHaveLength(
      buildQuickstartSnippet({ projectRef: 'widgets', token, binding }).split('\n').length,
    );
  });

  it('keeps spaces, double quotes and shell metacharacters inside one quoted value', () => {
    const text = buildQuickstartSnippet({
      projectRef: 'my project "quoted" $(touch /tmp/x) `id` & ;',
      token: null,
      binding: null,
    });
    expect(text).toContain(
      `export EVIDGATE_PROJECT='my project "quoted" $(touch /tmp/x) \`id\` & ;'`,
    );
  });
});
