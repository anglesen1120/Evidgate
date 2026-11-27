import { describe, expect, it } from 'vitest';

import { createAuthoringIntake, getAuthoringIntake, type CreateIntakeRequest } from './index.js';

const id = '018f5c1a-0000-7000-8000-000000000011';

// The generated request type is a union of one closed variant per kind, so a typed literal can
// carry exactly the members its kind allows. The `@ts-expect-error` lines fail the type-check of
// this file (not only the test run) when the generator stops enforcing a rule.
describe('authoring intake request type', () => {
  it('accepts a valid body of every kind', () => {
    const bodies: CreateIntakeRequest[] = [
      { kind: 'text', feature_node_id: id, text: 'The user resets a password by email.' },
      { kind: 'docx', feature_node_id: id, artifact_id: id, bytes: 48_213, target_language: 'vi' },
      { kind: 'pdf', feature_node_id: id, artifact_id: id, media_type: 'application/pdf' },
      { kind: 'image', artifact_id: id },
    ];
    expect(bodies.map((body) => body.kind)).toEqual(['text', 'docx', 'pdf', 'image']);
  });

  it('refuses a body with a missing or foreign member at compile time', () => {
    const refused: unknown[] = [
      // @ts-expect-error a text intake needs its text
      { kind: 'text', feature_node_id: id } satisfies CreateIntakeRequest,
      // @ts-expect-error a document intake needs its artifact
      { kind: 'pdf', feature_node_id: id } satisfies CreateIntakeRequest,
      // @ts-expect-error an image attaches to no feature node
      { kind: 'image', artifact_id: id, feature_node_id: id } satisfies CreateIntakeRequest,
      {
        kind: 'text',
        feature_node_id: id,
        text: 'x',
        // @ts-expect-error a text intake carries no artifact
        artifact_id: id,
      } satisfies CreateIntakeRequest,
      // @ts-expect-error the kind is a closed set of four
      { kind: 'spreadsheet', artifact_id: id } satisfies CreateIntakeRequest,
    ];
    expect(refused).toHaveLength(5);
  });

  it('exposes both operations', () => {
    expect(typeof createAuthoringIntake).toBe('function');
    expect(typeof getAuthoringIntake).toBe('function');
  });
});
