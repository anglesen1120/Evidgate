// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: no query strings, no ids
// outside route templates): the API path template is the only thing an API span may say about
// the URL a request went to, so the table below pins both halves of the mapping — the static
// words that survive and the segments that become `:id` — plus every input the function has to
// refuse.
//
// The last test is the drift check: it reads the recorded contract fixture (the same file
// `core/http/contract-smoke.spec.ts` compares against `contracts/openapi.yaml`) and fails when
// an operation carries a static word `KNOWN_API_SEGMENTS` does not have, so adding an API
// route cannot silently turn its words into `:id`s.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { apiPathTemplate, KNOWN_API_SEGMENTS, UNPARSED_API_PATH } from './api-path-template';

const PAGE = 'https://app.example.test/shell';
const RUN_ID = ['3f2b9d54', '0d7e', '4c1a', '9a2e', '6b0f5c7d81c4'].join('-');

/** `ng test` runs with `apps/web` as the working directory (see `core/http/contract-smoke.spec.ts`). */
const FIXTURE_PATH = resolve(
  process.cwd(),
  'src/app/core/http/testing/openapi-operations.fixture.json',
);

const RE_RECORD =
  'the recorded API surface changed — re-record with ' +
  '`node apps/web/scripts/record-openapi-fixture.mjs` from the repository root and add the new ' +
  'static words to KNOWN_API_SEGMENTS';

interface RecordedFixture {
  readonly operations: readonly { readonly path: string }[];
}

interface TemplateRow {
  /** What this row proves; also the `it.each` title. */
  readonly rule: string;
  readonly url: string;
  readonly expected: string;
}

const TEMPLATE_ROWS: TemplateRow[] = [
  {
    rule: 'templates every id of a same-origin API URL',
    url: `https://app.example.test/v1/projects/${RUN_ID}/runs?expand=steps#top`,
    expected: '/v1/projects/:id/runs',
  },
  {
    rule: 'keeps the static words of the recorded surface',
    url: '/v1/projects/p-1/manifests',
    expected: '/v1/projects/:id/manifests',
  },
  {
    rule: 'keeps a trailing action word',
    url: '/v1/runs/1234567/cancel',
    expected: '/v1/runs/:id/cancel',
  },
  {
    rule: 'keeps the artifact completion word',
    url: '/v1/artifacts/a-1/complete',
    expected: '/v1/artifacts/:id/complete',
  },
  {
    rule: 'templates a short opaque segment that is not a known word',
    url: '/v1/projects/p-1/runs',
    expected: '/v1/projects/:id/runs',
  },
  {
    rule: 'reports the bare base path as it is',
    url: '/v1',
    expected: '/v1',
  },
  {
    rule: 'collapses the trailing slash of the base path',
    url: '/v1/',
    expected: '/v1',
  },
  {
    rule: 'collapses doubled slashes',
    url: '/v1//runs',
    expected: '/v1/runs',
  },
  {
    rule: 'drops a query string',
    url: '/v1/runs?x=1',
    expected: '/v1/runs',
  },
  {
    rule: 'templates a protocol-relative URL',
    url: '//other.example.test/v1/runs/1',
    expected: '/v1/runs/:id',
  },
  {
    rule: 'templates a foreign-origin API URL (the origin gate is the interceptor’s job)',
    url: 'https://other.example.test/v1/projects/x/runs',
    expected: '/v1/projects/:id/runs',
  },
  {
    rule: 'reports a page path outside the API prefix as unparsed',
    url: '',
    expected: '/v1/unparsed',
  },
  {
    rule: 'reports an operational path outside the API prefix as unparsed',
    url: 'https://app.example.test/internal/metrics',
    expected: '/v1/unparsed',
  },
  {
    rule: 'reports a near-miss prefix as unparsed',
    url: '/v1evil/runs',
    expected: '/v1/unparsed',
  },
  {
    rule: 'reports an unparsable URL as unparsed',
    url: 'http://[',
    expected: '/v1/unparsed',
  },
  {
    rule: 'does not treat an uppercase word as a known static word',
    url: '/v1/Projects/p/runs',
    expected: '/v1/:id/:id/runs',
  },
];

describe('apiPathTemplate', () => {
  it.each(TEMPLATE_ROWS)('$rule', (row: TemplateRow) => {
    expect(apiPathTemplate(row.url, PAGE)).toBe(row.expected);
  });

  it('never leaves the concrete id of the request in the template', () => {
    const template = apiPathTemplate(`/v1/projects/${RUN_ID}/runs`, PAGE);

    expect(template).toBe('/v1/projects/:id/runs');
    expect(template).not.toContain(RUN_ID);
  });

  it('reports an unparsable URL as the fixed unparsed path', () => {
    expect(UNPARSED_API_PATH).toBe('/v1/unparsed');
    expect(apiPathTemplate('http://[', PAGE)).toBe(UNPARSED_API_PATH);
  });
});

describe('KNOWN_API_SEGMENTS against the recorded contract fixture', () => {
  it('names every static segment of every recorded operation', () => {
    const fixture = JSON.parse(readFileSync(FIXTURE_PATH, 'utf8')) as RecordedFixture;
    expect(fixture.operations.length).toBeGreaterThan(0);

    const staticSegments = new Set<string>();
    for (const operation of fixture.operations) {
      for (const segment of operation.path.split('/')) {
        if (segment !== '' && segment.startsWith('{') === false) {
          staticSegments.add(segment);
        }
      }
    }

    const missing = [...staticSegments].filter((segment) => !KNOWN_API_SEGMENTS.has(segment));
    expect(missing, `${RE_RECORD} (missing: ${missing.join(', ')})`).toEqual([]);
  });

  it('templates every recorded operation into the shape the fixture declares', () => {
    const fixture = JSON.parse(readFileSync(FIXTURE_PATH, 'utf8')) as RecordedFixture;

    for (const { path } of fixture.operations) {
      // The fixture writes ids as `{p}`/`{r}`; the template writes `:id`.
      const expected = `/v1${path.replace(/\{[^}]+\}/g, ':id')}`;
      expect(apiPathTemplate(`/v1${path.replace(/\{[^}]+\}/g, 'a-1')}`, PAGE)).toBe(expected);
    }
  });
});
