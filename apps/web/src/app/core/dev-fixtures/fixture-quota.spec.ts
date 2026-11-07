import { HttpRequest } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { getUsageSummary } from '@evidgate/sdk';
import { firstValueFrom } from 'rxjs';

import { isApiRequestError } from '../http/api-error';
import { FIXTURE_BACKEND } from './fixture-backend';
import { API_BASE_URL, rejectionOf, setupFixtureBackend } from './fixture-backend.test-support';

const ORG = '018f0000-0000-7000-8000-000000000001';

describe('quota supplemental fixture backend', () => {
  afterEach(() => TestBed.resetTestingModule());

  it.each(['warn', 'blocked'] as const)(
    'serves the %s state through the SDK and gateway',
    async (state) => {
      const { gateway, toggles } = setupFixtureBackend();
      toggles.setVariant('getUsageSummary', state);
      const summary = await gateway.call((client) => getUsageSummary({ client, path: { o: ORG } }));
      const metric = summary.metrics.find((item) => item.metric === 'results');
      expect(metric).toBeDefined();
      if (!metric) throw new Error('Expected the results counter');
      expect(metric?.warned_at).not.toBeNull();
      if (state === 'blocked') expect(metric?.blocked_at).not.toBeNull();
      else expect(metric?.blocked_at).toBeNull();
    },
  );

  it('preserves a null metric and its supplied unavailable reason through the SDK', async () => {
    const { gateway, toggles } = setupFixtureBackend();
    toggles.setVariant('getUsageSummary', 'unavailable');
    const summary = await gateway.call((client) => getUsageSummary({ client, path: { o: ORG } }));
    const egress = summary.metrics.find((item) => item.metric === 'egress_gb');
    expect(egress?.used).toBeNull();
    expect(egress?.unavailable_reason).toBe(
      'storage_usage_daily.egress_bytes has no rows for this period',
    );
  });

  it('uses the existing error mapper for an unavailable summary', async () => {
    const { gateway, toggles } = setupFixtureBackend();
    toggles.setVariant('getUsageSummary', 'error');
    const error = await rejectionOf(
      gateway.call((client) => getUsageSummary({ client, path: { o: ORG } })),
    );
    expect(isApiRequestError(error)).toBe(true);
    if (!isApiRequestError(error)) throw new Error('Expected the mapped API error');
    expect(error.error.status).toBe(503);
  });

  it('does not substitute a plausible summary for an unknown variant', async () => {
    const { gateway, toggles } = setupFixtureBackend();
    toggles.setVariant('getUsageSummary', 'absent');
    const error = await rejectionOf(
      gateway.call((client) => getUsageSummary({ client, path: { o: ORG } })),
    );
    expect(isApiRequestError(error)).toBe(true);
    if (!isApiRequestError(error)) throw new Error('Expected the mapped API error');
    expect(error.error.code).toBe('fixture_not_found');
  });

  it.each([
    ['POST', `/orgs/${ORG}/usage/summary`],
    ['GET', `/orgs/${ORG}/usage/summary/extra`],
    ['GET', '/orgs//usage/summary'],
  ])('does not extend the supplemental read to %s %s', async (method, path) => {
    setupFixtureBackend();
    const backend = TestBed.inject(FIXTURE_BACKEND);
    const request = new HttpRequest(method, `${API_BASE_URL}${path}`, null);
    const error = await rejectionOf(firstValueFrom(backend.handle(request)));
    expect(error).toMatchObject({ status: 501 });
  });
});
