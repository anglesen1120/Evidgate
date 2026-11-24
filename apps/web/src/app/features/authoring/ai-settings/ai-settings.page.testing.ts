import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal, provideZonelessChangeDetection, type WritableSignal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import type { AiSettingsView, AiSettingsVersion } from '@evidgate/sdk';
import { vi, type Mock } from 'vitest';
import { AiSettingsDataService } from '../../../core/data/ai-settings-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { AiSettingsPage } from './ai-settings.page';
import { AI_WRITE } from './ai-settings.model';
import { SettingsHarness } from './ai-settings.testing';
import headDocument from '../../../../../../../fixtures/web-api/ai-settings-head/input.json';
import versionDocument from '../../../../../../../fixtures/web-api/ai-settings-version-1/input.json';

export interface AiSettingsPageSetup {
  writable: WritableSignal<boolean>;
  readable: WritableSignal<boolean>;
  project: WritableSignal<{ id: string } | null>;
  data: { read: Mock; save: Mock; versions: Mock; version: Mock; templates: Mock };
  mount: () => Promise<{ fixture: ComponentFixture<AiSettingsPage>; harness: SettingsHarness }>;
}

export function setupAiSettingsPage(): AiSettingsPageSetup {
  const head = headDocument.body as AiSettingsView;
  const version = versionDocument.body as AiSettingsVersion;
  const writable = signal(true);
  const readable = signal(true);
  const project = signal<{ id: string } | null>({ id: 'p1' });
  const data = {
    read: vi.fn().mockResolvedValue({ view: head, etag: '"2"' }),
    save: vi.fn().mockResolvedValue({ view: head, etag: '"2"', appended: false }),
    versions: vi.fn().mockResolvedValue({ items: [version], next_cursor: null }),
    version: vi.fn().mockResolvedValue(version),
    templates: vi.fn().mockResolvedValue({ items: [], next_cursor: null }),
  };
  TestBed.configureTestingModule({
    imports: [AiSettingsPage],
    providers: [
      provideZonelessChangeDetection(),
      { provide: ActivatedRoute, useValue: { snapshot: { data: { pageHeadingLevel: 1 } } } },
      { provide: AiSettingsDataService, useValue: data },
      {
        provide: SessionStore,
        useValue: {
          currentOrg: signal({ id: 'o1' }),
          currentProject: project,
          has: (code: string) => (code === AI_WRITE ? writable() : readable()),
        },
      },
      {
        provide: NarrowMutationPolicy,
        useValue: { canMutate: signal(true), narrow: signal(false), allowsMutation: () => true },
      },
    ],
  });
  async function mount() {
    const fixture = TestBed.createComponent(AiSettingsPage);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, SettingsHarness);
    return { fixture, harness };
  }
  return { writable, readable, project, data, mount };
}
