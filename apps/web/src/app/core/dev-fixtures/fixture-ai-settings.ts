import type { HttpRequest } from '@angular/common/http';
import type { AiSettingsView, AiSettingsVersion, SaveAiSettingsRequest } from '@evidgate/sdk';
import permissions from '../../../../../../contracts/permissions.json';
import initial from '../../../../../../fixtures/web-api/ai-settings-head/input.json';
import defaults from '../../../../../../fixtures/web-api/ai-settings-default/input.json';
import versionOne from '../../../../../../fixtures/web-api/ai-settings-version-1/input.json';
import versionTwo from '../../../../../../fixtures/web-api/ai-settings-version-2/input.json';
import type { FixtureEntry } from './fixture-catalog-entries';
import { findFixtureEntry } from './fixture-catalog';
import { matchFixtureRoute } from './fixture-routes';

export function aiSettingsPermissions(variant: string): FixtureEntry | undefined {
  if (variant !== 'ai-settings-write' && variant !== 'ai-settings-read') return undefined;
  const selected = permissions.permissions.filter((entry) =>
    [
      'authoring:read',
      'organization:read',
      'project:read',
      ...(variant === 'ai-settings-write' ? ['project:write'] : []),
    ].includes(entry.code),
  );
  return {
    name: variant,
    document: {
      operation_id: 'listPermissions',
      variant,
      status: 200,
      headers: {},
      latency_ms: 0,
      body: { items: selected, next_cursor: null },
    },
  };
}
export class AiSettingsFixtureScenario {
  private readonly heads = new Map<string, AiSettingsView>();
  private readonly versions = new Map<string, AiSettingsVersion[]>();
  entry(
    request: HttpRequest<unknown>,
    path: string,
    variantFor: (operation: string) => string,
  ): FixtureEntry | undefined {
    const match = /^\/projects\/([^/]+)\/ai-settings(?:\/(.*))?$/.exec(path);
    if (!match?.[1]) return undefined;
    const route = matchFixtureRoute(request.method, path);
    if (!route) return undefined;
    const variant = variantFor(route.operationId);
    const project = match[1];
    if (request.method === 'PUT')
      return this.save(request, project, variant, variantFor('getProjectAiSettings') === 'empty');
    if (variant !== 'ok') return undefined;
    const head = this.heads.get(project);
    if (!match[2])
      return head ? this.answer('getProjectAiSettings', head, 200, head.version_no) : undefined;
    if (match[2] === 'versions') {
      const versions = this.versions.get(project);
      return versions
        ? this.answer('listProjectAiSettingsVersions', {
            items: versions.map((version) => ({
              version_no: version.version_no,
              content_hash: version.content_hash,
              template: version.template,
              author_membership_id: version.author_membership_id,
              reason: version.reason,
              created_at: version.created_at,
            })),
            next_cursor: null,
          })
        : undefined;
    }
    if (!match[2].startsWith('versions/')) return undefined;
    const versionNo = Number(match[2].split('/')[1]);
    const versions = this.versions.get(project) ?? [
      versionOne.body as AiSettingsVersion,
      versionTwo.body as AiSettingsVersion,
    ];
    const version = versions.find((entry) => entry.version_no === versionNo);
    return version
      ? this.answer('getProjectAiSettingsVersion', version, 200, version.version_no)
      : findFixtureEntry('getProjectAiSettingsVersion', 'not-found');
  }
  private save(
    request: HttpRequest<unknown>,
    project: string,
    variant: string,
    empty: boolean,
  ): FixtureEntry | undefined {
    if (variant === 'conflict') {
      const head = initial.body as AiSettingsView;
      this.heads.set(project, {
        ...head,
        version_no: 3,
        settings: {
          ...head.settings,
          writing_style: 'The other fixture editor saved this guidance.',
        },
      });
      return findFixtureEntry('saveProjectAiSettings', 'conflict');
    }
    if (variant !== 'ok') return undefined;
    const head =
      this.heads.get(project) ?? ((empty ? defaults.body : initial.body) as AiSettingsView);
    if (request.headers.get('If-Match') !== '"' + head.version_no + '"')
      return findFixtureEntry('saveProjectAiSettings', 'conflict');
    const body = (
      typeof request.body === 'string' ? JSON.parse(request.body) : request.body
    ) as SaveAiSettingsRequest;
    if (!body?.settings) return undefined;
    if (JSON.stringify(body.settings) === JSON.stringify(head.settings))
      return this.answer('saveProjectAiSettings', head, 200, head.version_no);
    const next: AiSettingsView = {
      ...head,
      source: 'saved',
      settings_id: initial.body.settings_id,
      author: initial.body.author,
      created_at: initial.body.created_at,
      settings: body.settings,
      template: body.template ?? null,
      reason: body.reason ?? null,
      version_no: head.version_no + 1,
    };
    this.heads.set(project, next);
    const version: AiSettingsVersion = {
      version_no: next.version_no,
      settings_id: next.settings_id ?? '018f0000-0000-7000-8000-00000000b442',
      content_hash: next.content_hash,
      template: next.template,
      author_membership_id: next.author ?? '018f0000-0000-7000-8000-00000000b441',
      reason: next.reason,
      created_at: next.created_at ?? '2026-11-01T12:00:00Z',
      settings: next.settings,
    };
    this.versions.set(project, [
      version,
      ...(this.versions.get(project) ??
        (empty
          ? []
          : [versionTwo.body as AiSettingsVersion, versionOne.body as AiSettingsVersion])),
    ]);
    return this.answer('saveProjectAiSettings', next, 201, next.version_no);
  }
  private answer(operation: string, body: unknown, status = 200, version?: number): FixtureEntry {
    return {
      name: 'ai-settings-review-state',
      document: {
        operation_id: operation,
        variant: 'ok',
        status,
        headers: version === undefined ? {} : { ETag: '"' + version + '"' },
        latency_ms: 0,
        body,
      },
    };
  }
}
