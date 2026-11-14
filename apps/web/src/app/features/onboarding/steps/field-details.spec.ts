import { fieldDetails, reasonFor } from './field-details';

describe('onboarding field diagnostics', () => {
  it.each([
    ['project', '/name', 'Project name'],
    ['project', 'slug', 'Project slug'],
    ['connection', '/name', 'Token name'],
    ['connection', '/repository_id', 'GitHub numeric repository ID'],
    ['connection', '/repository_full_name', 'Repository (owner/repo)'],
    ['connection', '/allowed_ref_patterns[0]', 'Allowed Git ref patterns'],
    ['connection', '/allowed_workflow_refs/0', 'Allowed workflow refs'],
    ['collection', '/policy_revision_id', 'Policy revision'],
    ['collection', '/project_id', 'Project'],
    ['collection', '/mode', 'Mode'],
    ['collection', '/hash', 'Revision hash'],
  ] as const)('names %s %s using the product label', (context, path, label) => {
    const details = fieldDetails([{ path, reason: 'Exact server reason.' }], context);
    const detail = details[0];
    expect(detail?.message).toBe(`${label}: Exact server reason.`);
    expect(detail?.path).toBe(path);
    expect(detail?.reason).toBe('Exact server reason.');
    expect(reasonFor(details, detail?.field ?? '')).toBe(detail?.message);
  });

  it.each(['/unexpected/private/path', '/constructor', '/__proto__', '/toString', ''])(
    'uses a generic label rather than exposing unknown pointer %s',
    (path) => {
      const detail = fieldDetails([{ path, reason: 'Needs correction.' }], 'connection')[0];
      expect(detail?.message).toBe('Submitted value: Needs correction.');
      expect(detail?.path).toBe(path);
    },
  );

  it('sanitizes both the lookup path and the retained server reason', () => {
    const details = fieldDetails(
      [
        { path: '/name\u0007\u202e', reason: 'Name \u0000must\u200f not embed\u2028 separators.' },
        { reason: '<img src=x> is not accepted.' },
      ],
      'project',
    );
    expect(details[0]?.message).toBe('Project name: Name must not embed separators.');
    expect(details[0]?.path).toBe('/name');
    expect(details[1]?.message).toBe('Submitted value: <img src=x> is not accepted.');
    expect(reasonFor(details, 'slug')).toBeNull();
  });

  it('keeps the first inline reason and every alert reason without changing the diagnostics', () => {
    const details = fieldDetails(
      [
        { path: '/repository_id', reason: 'First refusal.' },
        { path: '/repository_id', reason: 'Second refusal.' },
      ],
      'connection',
    );
    expect(reasonFor(details, 'repository_id')).toBe(
      'GitHub numeric repository ID: First refusal.',
    );
    expect(details.map((detail) => detail.reason)).toEqual(['First refusal.', 'Second refusal.']);
    expect(fieldDetails([], 'collection')).toEqual([]);
  });
});
