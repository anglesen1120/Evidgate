import { FIXTURE_CATALOG_AI_SETTINGS_ROWS } from './fixture-catalog-ai-settings-rows';
import { VALIDATOR } from './fixture-contract.support';
import { FIXTURE_ROUTES } from './fixture-routes';
import { HttpRequest } from '@angular/common/http';
import { AiSettingsFixtureScenario, aiSettingsPermissions } from './fixture-ai-settings';
import type { AiSettingsView } from '@evidgate/sdk';
import head from '../../../../../../fixtures/web-api/ai-settings-head/input.json';

describe('AI settings response contracts', () => {
  it('validates every settings response and refusal against OpenAPI', () => {
    expect(FIXTURE_CATALOG_AI_SETTINGS_ROWS.length).toBe(15);
    for (const { name, document } of FIXTURE_CATALOG_AI_SETTINGS_ROWS) {
      expect(
        VALIDATOR.validateResponse(document.operation_id, document.status, document.body).errors,
        name,
      ).toEqual([]);
      expect(FIXTURE_ROUTES.some((route) => route.operationId === document.operation_id)).toBe(
        true,
      );
    }
  });
  it('gives readable and writable review sessions exactly the intended write difference', () => {
    const read = aiSettingsPermissions('ai-settings-read')?.document.body;
    const write = aiSettingsPermissions('ai-settings-write')?.document.body;
    expect(VALIDATOR.validateResponse('listPermissions', 200, read).errors).toEqual([]);
    expect(VALIDATOR.validateResponse('listPermissions', 200, write).errors).toEqual([]);
    expect(JSON.stringify(read)).not.toContain('project:write');
    expect(JSON.stringify(write)).toContain('project:write');
  });
  it('saves submitted guidance and returns its loaded ETag on the subsequent read', () => {
    const scenario = new AiSettingsFixtureScenario();
    const settings = { ...head.body.settings, writing_style: 'New review fixture writing style.' };
    const request = new HttpRequest('PUT', '/projects/p/ai-settings', { settings });
    const saved = scenario.entry(
      request.clone({ setHeaders: { 'If-Match': '"2"', 'Idempotency-Key': 'fixture-intent' } }),
      '/projects/p/ai-settings',
      () => 'ok',
    );
    expect(saved?.document.status).toBe(201);
    expect(
      VALIDATOR.validateResponse('saveProjectAiSettings', 201, saved?.document.body).errors,
    ).toEqual([]);
    const loaded = scenario.entry(
      new HttpRequest('GET', '/projects/p/ai-settings'),
      '/projects/p/ai-settings',
      () => 'ok',
    );
    expect((loaded?.document.body as AiSettingsView).settings.writing_style).toBe(
      settings.writing_style,
    );
    expect(loaded?.document.headers['ETag']).toBe('"3"');
  });
  it('routes numeric immutable versions by version_no rather than returning the default version', () => {
    const scenario = new AiSettingsFixtureScenario();
    for (const version of [1, 2, 999]) {
      const path = '/projects/p/ai-settings/versions/' + version;
      const entry = scenario.entry(new HttpRequest('GET', path), path, () => 'ok');
      expect(entry?.document.status).toBe(version === 999 ? 404 : 200);
      if (version !== 999) expect(entry?.document.body).toMatchObject({ version_no: version });
    }
  });
  it('creates version one from the default version zero head', () => {
    const scenario = new AiSettingsFixtureScenario();
    const path = '/projects/p/ai-settings';
    const request = new HttpRequest('PUT', path, {
      settings: { ...head.body.settings, writing_style: 'First fixture settings version.' },
    });
    const entry = scenario.entry(
      request.clone({ setHeaders: { 'If-Match': '"0"', 'Idempotency-Key': 'first-version' } }),
      path,
      (operation) => (operation === 'getProjectAiSettings' ? 'empty' : 'ok'),
    );
    expect(entry?.document.status).toBe(201);
    expect((entry?.document.body as AiSettingsView).version_no).toBe(1);
    expect(
      VALIDATOR.validateResponse('saveProjectAiSettings', 201, entry?.document.body).errors,
    ).toEqual([]);
  });
});
