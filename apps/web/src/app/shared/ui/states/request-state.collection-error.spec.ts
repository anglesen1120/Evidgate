import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';

import type { ApiError } from '../../../core/http/api-error';
import { RequestStateComponent } from './request-state.component';

class RequestStateHarness extends ComponentHarness {
  static hostSelector = 'app-request-state';

  async text(): Promise<string> {
    return (await this.host()).text();
  }
}

const failure: ApiError = {
  kind: 'network',
  status: 0,
  code: 'network_error',
  message: 'Check your connection.',
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

describe('Failed collection reads', () => {
  it('distinguishes an unknown record count from a ready empty list without disclosing it for a denied or missing resource', async () => {
    const fixture = TestBed.createComponent(RequestStateComponent);
    const state = fixture.componentRef;
    state.setInput('tableLoading', true);
    state.setInput('status', 'error');
    state.setInput('error', failure);
    state.setInput('retryEligible', true);
    const view = await TestbedHarnessEnvironment.harnessForFixture(fixture, RequestStateHarness);
    const unknown = 'The record count is unknown.';

    // A detail page can use row-shaped loading without being a collection.
    expect(await view.text()).not.toContain(unknown);
    state.setInput('emptyTitle', 'No matching records');
    expect(await view.text()).toContain(unknown);
    expect(await view.text()).toContain('Retry');
    expect(await view.text()).not.toContain('No matching records');
    state.setInput('error', { ...failure, kind: 'rate_limited', retryAfterSeconds: 30 });
    expect(await view.text()).toContain(unknown);

    for (const kind of ['not_found', 'forbidden'] as const) {
      state.setInput('error', { ...failure, kind });
      expect(await view.text()).not.toContain(unknown);
    }
    state.setInput('status', 'ready');
    state.setInput('empty', true);
    expect(await view.text()).not.toContain(unknown);
    expect(await view.text()).toContain('No matching records');
  });
});
