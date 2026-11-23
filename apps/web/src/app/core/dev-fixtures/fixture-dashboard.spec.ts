import { dashboardFixture } from './fixture-dashboard';

describe('Dashboard browser fixture scenario', () => {
  const variant = (operation: string) => (operation === 'listPermissions' ? 'dashboard' : 'ok');
  it('is opt-in and never intercepts writes or other endpoints', () => {
    expect(dashboardFixture('GET', '/projects/p/runs', () => 'ok')).toBeUndefined();
    expect(dashboardFixture('POST', '/projects/p/runs', variant)).toBeUndefined();
    expect(dashboardFixture('GET', '/projects/p/retention', variant)).toBeUndefined();
  });
  it.each([
    '/permissions',
    '/projects/p/runs',
    '/projects/p/waivers',
    '/projects/p/quarantine',
    '/projects/p/incidents',
    '/projects/p/ci-bindings',
    '/projects/p/authoring/tree',
    '/projects/p/authoring/manual-cases',
    '/projects/p/authoring/changes',
    '/orgs/o/usage/summary',
    '/orgs/o/audit',
    '/orgs/o/projects',
    '/orgs/o/memberships',
  ])('provides a concrete served body for %s', (path) => {
    const entry = dashboardFixture('GET', path, variant);
    expect(entry?.document.status).toBe(200);
    expect(entry?.document.body).toBeDefined();
  });
  it('only fails the explicitly selected widget', () => {
    const variants = (operation: string) =>
      operation === 'listPermissions'
        ? 'dashboard'
        : operation === 'listProjectWaivers'
          ? 'dashboard-error'
          : 'ok';
    expect(dashboardFixture('GET', '/projects/p/waivers', variants)?.document.status).toBe(503);
    expect(dashboardFixture('GET', '/projects/p/runs', variants)?.document.status).toBe(200);
  });
  it('honors a caller-provided list limit without affecting other scenarios', () => {
    const entry = dashboardFixture('GET', '/orgs/o/audit', variant, '/orgs/o/audit?limit=3');
    expect(entry?.document.body).toHaveProperty('next_cursor', 'dashboard-next');
    const body = entry?.document.body;
    expect(
      body && typeof body === 'object' && 'items' in body && Array.isArray(body.items)
        ? body.items.length
        : null,
    ).toBe(3);
  });
});
