import { TestBed } from '@angular/core/testing';
import axe from 'axe-core';
import { expect, vi } from 'vitest';

import exceptionsDocument from '../../../../e2e/a11y-exceptions.json';
import {
  AXE_JSDOM_RUN_OPTIONS,
  formatViolations,
  unexceptedViolations,
  type A11yException,
} from '../../../../e2e/a11y/axe-config';
import { stubScopeBrowserApis } from '../../shell/layout/scope.fixture';
import { TRIAGE, refusal, resolution, switchRow } from './healing-switch.test-support';
import { mount, scopeCard, saveButton, selectOf } from './healing-switch-page.fixture';

const A11Y_EXCEPTIONS: readonly A11yException[] = exceptionsDocument.exceptions;

describe('HealingSwitchPage', () => {
  let restoreBrowserApis: () => void;
  beforeEach(() => {
    restoreBrowserApis = stubScopeBrowserApis();
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    restoreBrowserApis();
    vi.unstubAllGlobals();
  });

  it('refuses the screen without healing:triage and never reads the switch', async () => {
    const { root, data, settle } = mount({ codes: ['project:read'] });
    await settle();

    expect(root.querySelector('[data-healing-forbidden]')?.textContent).toContain(
      'do not have permission to view the healing switch',
    );
    expect(data.reads).toHaveLength(0);
  });

  it('asks for a project instead of reading a switch without one', async () => {
    const { root, data, settle } = mount({ withProject: false });
    await settle();

    expect(root.querySelector('[data-healing-no-project]')).not.toBeNull();
    expect(data.reads).toHaveLength(0);
  });

  it('reserves a loading status until the resolution answers', async () => {
    const { root, data, settle, advance } = mount({ holdReads: true });
    await advance();
    expect(root.querySelector('[role="status"][aria-busy="true"]')?.textContent).toContain(
      'Loading the healing switch',
    );

    data.releaseRead('p-1', resolution());
    await settle();
    expect(root.querySelector('[data-healing-resolved]')).not.toBeNull();
  });

  it('renders the three scopes in system, tenant, project order', async () => {
    const { root, settle } = mount({
      answer: resolution({
        system: switchRow('system', 'shadow', { set_by: null, reason: null }),
        tenant: switchRow('tenant', 'shadow'),
        project: switchRow('project', 'enabled'),
      }),
    });
    await settle();

    const scopes = [...root.querySelectorAll('[data-healing-scope]')].map((element) =>
      element.getAttribute('data-healing-scope'),
    );
    expect(scopes).toEqual(['system', 'tenant', 'project']);
  });

  it('renders the system scope read-only even for a configurer', async () => {
    const { root, settle } = mount({
      answer: resolution({
        system: switchRow('system', 'disabled', { set_by: null, reason: null }),
      }),
    });
    await settle();

    const system = scopeCard(root, 'system');
    expect(system?.querySelector('[data-healing-system-note]')?.textContent).toContain(
      'Only the platform operator',
    );
    expect(system?.querySelector('select')).toBeNull();
    expect(saveButton(root, 'system')).toBeNull();
  });

  it('states the default shadow posture when no scope stores a switch', async () => {
    const { root, settle } = mount();
    await settle();

    expect(root.querySelector('[data-healing-resolved]')?.textContent).toContain(
      'No scope stores a switch, so healing stays in the default shadow mode.',
    );
    expect(root.querySelector('[data-healing-resolved-mode]')?.textContent).toContain('Shadow');
  });

  it('names the deciding scope when a disabled tenant row takes precedence', async () => {
    const { root, settle } = mount({
      answer: resolution({
        tenant: switchRow('tenant', 'disabled'),
        resolved: 'disabled',
        dependency_health: { llm: 'ok', knowledge_store: 'ok' },
      }),
    });
    await settle();

    expect(root.querySelector('[data-healing-resolved-mode]')?.textContent).toContain('Disabled');
    expect(root.querySelector('[data-healing-resolved]')?.textContent).toContain(
      'The tenant scope decides this.',
    );
  });

  it('renders null dependency observations as not observed, never as healthy', async () => {
    const { root, settle } = mount();
    await settle();

    const values = [...root.querySelectorAll('[data-healing-health]')].map((element) =>
      element.textContent?.trim(),
    );
    expect(values).toEqual(['Not observed', 'Not observed']);
  });

  it('labels known dependency observations', async () => {
    const { root, settle } = mount({
      answer: resolution({
        dependency_health: { llm: 'ok', knowledge_store: 'degraded' },
      }),
    });
    await settle();

    const health = [...root.querySelectorAll<HTMLElement>('[data-healing-health]')];
    expect(health.map((element) => element.textContent?.trim())).toEqual(['Healthy', 'Degraded']);
  });

  it.each([
    ['ok', 'Healthy'],
    ['healthy', 'Healthy'],
    ['degraded', 'Degraded'],
    ['unavailable', 'Unavailable'],
  ] as const)('maps the observation %s to %s', async (value, label) => {
    const { root, settle } = mount({
      answer: resolution({ dependency_health: { llm: value, knowledge_store: null } }),
    });
    await settle();

    expect(root.querySelector('[data-healing-health="llm"]')?.textContent?.trim()).toBe(label);
    expect(root.querySelector('[data-healing-health="knowledge-store"]')?.textContent?.trim()).toBe(
      'Not observed',
    );
  });

  it('shows an unrecognised observation verbatim under a label that says so', async () => {
    const { root, settle } = mount({
      answer: resolution({
        dependency_health: { llm: 'warming-up', knowledge_store: 'surprising' },
      }),
    });
    await settle();

    const llm = root.querySelector<HTMLElement>('[data-healing-health="llm"]');
    const wrapper = llm?.querySelector('[data-healing-health-unrecognised]');
    expect(wrapper).not.toBeNull();
    expect(wrapper?.textContent).toContain('Unrecognised value:');
    expect(wrapper?.querySelector('[data-healing-health-value]')?.textContent?.trim()).toBe(
      'warming-up',
    );
    expect(
      root.querySelector(
        '[data-healing-health="knowledge-store"] [data-healing-health-unrecognised]',
      )?.textContent,
    ).toContain('surprising');
  });

  it('disables both lower scopes under a disabled system row and names it', async () => {
    const { root, settle } = mount({
      answer: resolution({
        system: switchRow('system', 'disabled', { set_by: null, reason: null }),
        project: switchRow('project', 'enabled'),
        resolved: 'disabled',
      }),
    });
    await settle();

    expect(selectOf(root, 'tenant')?.disabled).toBe(true);
    expect(selectOf(root, 'project')?.disabled).toBe(true);
    expect(saveButton(root, 'tenant')).toBeNull();
    expect(saveButton(root, 'project')).toBeNull();
    expect(scopeCard(root, 'tenant')?.querySelector('[data-healing-note]')?.textContent).toContain(
      'system scope takes precedence',
    );
    expect(scopeCard(root, 'project')?.querySelector('[data-healing-note]')?.textContent).toContain(
      'system scope takes precedence',
    );
  });

  it('disables the project scope under a disabled tenant row and names the tenant', async () => {
    const { root, settle } = mount({
      answer: resolution({ tenant: switchRow('tenant', 'disabled'), resolved: 'disabled' }),
    });
    await settle();

    expect(selectOf(root, 'tenant')?.disabled).toBe(false);
    expect(selectOf(root, 'project')?.disabled).toBe(true);
    expect(scopeCard(root, 'project')?.querySelector('[data-healing-note]')?.textContent).toContain(
      'tenant scope takes precedence',
    );
  });

  it('renders an unavailable resolution read-only and offers no unavailable mode', async () => {
    const { root, settle } = mount({ answer: resolution({ resolved: 'unavailable' }) });
    await settle();

    expect(root.querySelector('[data-healing-unavailable-posture]')?.textContent).toContain(
      'Healing is unavailable.',
    );
    expect(root.querySelectorAll('select')).toHaveLength(0);
    expect(selectOf(root, 'tenant')?.disabled).toBe(true);
    expect(selectOf(root, 'project')?.disabled).toBe(true);
    expect(saveButton(root, 'tenant')).toBeNull();
  });

  it('keeps every scope read-only without healing:configure', async () => {
    const { root, settle } = mount({
      codes: [TRIAGE],
      answer: resolution({
        tenant: switchRow('tenant', 'shadow'),
        project: switchRow('project', 'enabled'),
      }),
    });
    await settle();

    expect(saveButton(root, 'tenant')).toBeNull();
    expect(saveButton(root, 'project')).toBeNull();
    expect(selectOf(root, 'tenant')?.disabled).toBe(true);
    expect(scopeCard(root, 'tenant')?.querySelector('[data-healing-note]')?.textContent).toContain(
      'needs permission to configure healing',
    );
  });

  it('labels the project scope with the current project name', async () => {
    const { root, settle } = mount();
    await settle();

    expect(root.querySelector('[data-healing-editor="project"] h3')?.textContent).toContain(
      'Storefront',
    );
  });

  it('renders a read refusal with the server message and reloads on request', async () => {
    const { root, data, settle, until } = mount({
      readFailure: refusal('forbidden', 403, 'Healing triage is not granted here.'),
    });
    await settle();

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(
      'Healing triage is not granted here.',
    );
    data.readFailure = null;
    root.querySelector<HTMLButtonElement>('[data-healing-reload]')?.click();
    await until(() => data.reads.length === 2);
    await settle();

    expect(root.querySelector('[data-healing-resolved]')).not.toBeNull();
  });

  it('renders a foreign project as not found without a scope hint', async () => {
    const { root, settle } = mount({
      readFailure: refusal('not_found', 404, 'Project not found.'),
    });
    await settle();

    expect(root.querySelector('[data-healing-not-found]')).not.toBeNull();
    expect(root.querySelector('[data-healing-scope]')).toBeNull();
    expect(root.querySelector('[role="alert"]')).toBeNull();
    expect(root.textContent).not.toContain('Project not found.');
  });

  it('renders a write refusal on the scope that was refused', async () => {
    const { root, data, settle } = mount({
      answer: resolution({ tenant: switchRow('tenant', 'shadow') }),
    });
    data.writeFailure = refusal('forbidden', 403, 'Healing configuration is not granted.');
    await settle();

    saveButton(root, 'tenant')?.click();
    await settle();

    expect(scopeCard(root, 'tenant')?.querySelector('[role="alert"]')?.textContent).toContain(
      'Healing configuration is not granted.',
    );
    expect(scopeCard(root, 'project')?.querySelector('[role="alert"]')).toBeNull();
  });

  it('explains a 413 on the scope with the reason-specific guidance', async () => {
    const { root, data, settle } = mount({
      answer: resolution({ tenant: switchRow('tenant', 'shadow') }),
    });
    data.writeFailure = refusal('payload_too_large', 413, 'request body too large');
    await settle();

    saveButton(root, 'tenant')?.click();
    await settle();

    const alert = scopeCard(root, 'tenant')?.querySelector('[role="alert"]');
    expect(alert?.textContent).toContain('The reason is too large to send. Shorten it.');
    expect(alert?.textContent).not.toContain('request body too large');
  });

  it('refetches the resolution after a 412 and shows its message', async () => {
    const { root, data, settle, until } = mount({
      answer: resolution({ tenant: switchRow('tenant', 'shadow') }),
    });
    data.writeFailure = refusal('precondition_failed', 412, 'The switch changed; review it.');
    await settle();
    expect(data.reads).toHaveLength(1);

    saveButton(root, 'tenant')?.click();
    await until(() => data.reads.length === 2);
    await settle();

    expect(scopeCard(root, 'tenant')?.querySelector('[role="alert"]')?.textContent).toContain(
      'The switch changed; review it.',
    );
  });

  it('keeps the controls and saved status mounted while the save reload runs', async () => {
    const { root, data, settle, until } = mount({
      answer: resolution({ tenant: switchRow('tenant', 'shadow') }),
    });
    await settle();
    const tenant = scopeCard(root, 'tenant');
    data.holdReads = true;

    saveButton(root, 'tenant')?.click();
    await until(() => data.reads.length === 2);

    expect(scopeCard(root, 'tenant')).toBe(tenant);
    expect(scopeCard(root, 'tenant')?.querySelector('[data-healing-saved]')?.textContent).toContain(
      'Switch saved',
    );
    expect(root.querySelector('[role="status"][aria-busy="true"]')).toBeNull();

    data.releaseRead('p-1', resolution({ tenant: switchRow('tenant', 'disabled') }));
    await settle();
    expect(scopeCard(root, 'tenant')).toBe(tenant);
    expect(scopeCard(root, 'tenant')?.querySelector('[data-healing-saved]')).not.toBeNull();
  });

  it('keeps the 412 message mounted while the reload it triggered is in flight', async () => {
    const { root, data, settle, until } = mount({
      answer: resolution({ tenant: switchRow('tenant', 'shadow') }),
    });
    data.writeFailure = refusal('precondition_failed', 412, 'The switch changed; review it.');
    await settle();
    const tenant = scopeCard(root, 'tenant');
    data.holdReads = true;

    saveButton(root, 'tenant')?.click();
    await until(() => data.reads.length === 2);

    expect(scopeCard(root, 'tenant')).toBe(tenant);
    expect(scopeCard(root, 'tenant')?.querySelector('[role="alert"]')?.textContent).toContain(
      'The switch changed; review it.',
    );

    data.releaseRead('p-1', resolution({ tenant: switchRow('tenant', 'enabled') }));
    await settle();
    expect(scopeCard(root, 'tenant')).toBe(tenant);
    expect(scopeCard(root, 'tenant')?.querySelector('[role="alert"]')?.textContent).toContain(
      'The switch changed; review it.',
    );
  });

  it('leaves the busy state when a read fails with a non-API error', async () => {
    const { root, data, settle, until } = mount({
      readFailure: new Error('the response did not parse'),
    });
    await settle();

    expect(root.querySelector('[role="status"][aria-busy="true"]')).toBeNull();
    expect(root.querySelector('[data-healing-read-error] [role="alert"]')?.textContent).toContain(
      'The healing switch could not be loaded.',
    );

    data.readFailure = null;
    root.querySelector<HTMLButtonElement>('[data-healing-reload]')?.click();
    await until(() => data.reads.length === 2);
    await settle();
    expect(root.querySelector('[data-healing-resolved]')).not.toBeNull();
  });

  it('has zero WCAG 2.2 AA violations in jsdom', async () => {
    const { root, settle } = mount({
      answer: resolution({
        system: switchRow('system', 'disabled', { set_by: null, reason: null }),
        tenant: switchRow('tenant', 'shadow'),
      }),
    });
    await settle();
    document.body.append(root);
    try {
      const result = await axe.run(root, AXE_JSDOM_RUN_OPTIONS);
      expect(root.querySelectorAll('button[role="combobox"]:disabled')).toHaveLength(2);
      expect(result.passes.some((rule) => rule.id === 'button-name')).toBe(true);
      expect(formatViolations(unexceptedViolations(result.violations, A11Y_EXCEPTIONS))).toEqual(
        [],
      );
    } finally {
      root.remove();
    }
  }, 30_000);
});
