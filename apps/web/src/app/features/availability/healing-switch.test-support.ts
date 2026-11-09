// The harness the HealingSwitchStore specs share: a mounted host (the store is component-provided,
// exactly as the page provides it), a fake session whose org, project and permissions a test moves,
// and a fake data service whose reads and writes a test holds open or refuses.

import {
  ChangeDetectionStrategy,
  Component,
  computed,
  provideZonelessChangeDetection,
  signal,
  type Signal,
  type WritableSignal,
} from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type {
  HealingSwitchResolutionView,
  HealingSwitchSetV1,
  HealingSwitchView,
} from '@evidgate/sdk';

import { HealingSwitchDataService } from '../../core/data/healing-switch-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../core/http/api-error';
import type { Intent } from '../../core/http/intent';
import { SessionStore } from '../../core/session/session.store';
import { HealingSwitchStore } from './healing-switch.store';

export const TRIAGE = 'healing:triage';
export const CONFIGURE = 'healing:configure';

export function switchRow(
  scope: HealingSwitchView['scope'],
  mode: HealingSwitchView['mode'],
  changes: Partial<HealingSwitchView> = {},
): HealingSwitchView {
  return {
    scope,
    project_id: scope === 'project' ? 'p-1' : null,
    mode,
    reason: null,
    set_by: 'member-1',
    at: '2026-11-01T00:00:00Z',
    version: 2,
    ...changes,
  };
}

/** No row at any scope resolves to `shadow`: the posture that decides but never applies. */
export function resolution(
  changes: Partial<HealingSwitchResolutionView> = {},
): HealingSwitchResolutionView {
  return {
    system: null,
    tenant: null,
    project: null,
    resolved: 'shadow',
    dependency_health: { llm: null, knowledge_store: null },
    ...changes,
  };
}

export function refusal(
  kind: ApiErrorKind,
  status: number,
  message = `${kind} (test)`,
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: kind,
    message,
    requestId: null,
    retryable: kind === 'network' || kind === 'timeout',
    details: [],
    retryAfterSeconds: null,
  });
}

function storedRow(body: HealingSwitchSetV1): HealingSwitchView {
  const { mode } = body;
  return {
    scope: body.scope,
    project_id: body.scope === 'project' ? (body.project_id ?? null) : null,
    mode: mode === 'enabled' || mode === 'shadow' || mode === 'disabled' ? mode : 'disabled',
    reason: body.reason ?? null,
    set_by: 'member-1',
    at: '2026-11-02T00:00:00Z',
    version: 3,
  };
}

type HeldRead = {
  readonly project: string;
  readonly resolve: (value: HealingSwitchResolutionView) => void;
  readonly reject: (error: unknown) => void;
};

type HeldWrite = {
  readonly body: HealingSwitchSetV1;
  readonly resolve: (value: HealingSwitchView) => void;
  readonly reject: (error: unknown) => void;
};

/** The wire the store talks to, with every answer the test controls. */
export class FakeHealingSwitchData {
  readonly reads: Array<{ readonly project: string; readonly signal?: AbortSignal }> = [];
  readonly writes: Array<{ readonly body: HealingSwitchSetV1; readonly intent: Intent }> = [];
  readonly answers = new Map<string, HealingSwitchResolutionView>();
  answer = resolution();
  readFailure: Error | null = null;
  writeFailure: Error | null = null;
  holdReads = false;
  holdWrites = false;
  private readonly heldReads: HeldRead[] = [];
  private readonly heldWrites: HeldWrite[] = [];

  read(project: string, signal?: AbortSignal): Promise<HealingSwitchResolutionView> {
    this.reads.push(signal === undefined ? { project } : { project, signal });
    if (this.holdReads) {
      return new Promise<HealingSwitchResolutionView>((resolve, reject) => {
        this.heldReads.push({ project, resolve, reject });
      });
    }
    if (this.readFailure !== null) return Promise.reject(this.readFailure);
    return Promise.resolve(this.answers.get(project) ?? this.answer);
  }

  set(body: HealingSwitchSetV1, intent: Intent): Promise<HealingSwitchView> {
    this.writes.push({ body, intent });
    if (this.holdWrites) {
      return new Promise<HealingSwitchView>((resolve, reject) => {
        this.heldWrites.push({ body, resolve, reject });
      });
    }
    if (this.writeFailure !== null) return Promise.reject(this.writeFailure);
    return Promise.resolve(storedRow(body));
  }

  /** Answers one held read for `project`, so a superseded read can land after the newer one. */
  releaseRead(project: string, value?: HealingSwitchResolutionView): void {
    const index = this.heldReads.findIndex((held) => held.project === project);
    if (index === -1) return;
    const [held] = this.heldReads.splice(index, 1);
    held?.resolve(value ?? this.answers.get(held.project) ?? this.answer);
  }

  failRead(error: Error = this.readFailure ?? refusal('network', 0)): void {
    this.heldReads.shift()?.reject(error);
  }

  releaseWrite(): void {
    const held = this.heldWrites.shift();
    if (held !== undefined) held.resolve(storedRow(held.body));
  }

  failWrite(error: Error = this.writeFailure ?? refusal('network', 0)): void {
    this.heldWrites.shift()?.reject(error);
  }
}

export type FakeSession = {
  readonly currentOrg: Signal<{ id: string } | null>;
  readonly currentProject: Signal<{ id: string } | null>;
  readonly permissions: WritableSignal<ReadonlySet<string>>;
  has(code: string): boolean;
  setOrg(id: string | null): void;
  setProject(id: string | null): void;
};

export function fakeSession(codes: readonly string[] = [TRIAGE, CONFIGURE]): FakeSession {
  const org = signal<{ id: string } | null>({ id: 'o-1' });
  const project = signal<{ id: string } | null>({ id: 'p-1' });
  const permissions = signal<ReadonlySet<string>>(new Set(codes));
  return {
    currentOrg: computed(() => org()),
    currentProject: computed(() => project()),
    permissions,
    has: (code: string) => permissions().has(code),
    setOrg: (id) => org.set(id === null ? null : { id }),
    setProject: (id) => project.set(id === null ? null : { id }),
  };
}

@Component({
  template: '',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [HealingSwitchStore],
})
export class HealingHostComponent {}

export interface HealingHarness {
  readonly data: FakeHealingSwitchData;
  readonly session: FakeSession;
  readonly fixture: ComponentFixture<HealingHostComponent>;
  readonly store: InstanceType<typeof HealingSwitchStore>;
  /** Ticks change detection until the read settles; do not use while a read is held open. */
  readonly settle: () => Promise<void>;
  /** One tick and a microtask drain, for inspecting an in-flight state. */
  readonly advance: () => Promise<void>;
}

export interface MountOptions {
  readonly codes?: readonly string[];
  readonly org?: string | null;
  readonly project?: string | null;
  /** The answer the first read serves; `answers` keys a different one per project. */
  readonly answer?: HealingSwitchResolutionView;
}

export function mountHealingSwitch(options: MountOptions = {}): HealingHarness {
  const data = new FakeHealingSwitchData();
  if (options.answer !== undefined) data.answer = options.answer;
  const session = fakeSession(options.codes);
  if (options.org !== undefined) session.setOrg(options.org);
  if (options.project !== undefined) session.setProject(options.project);
  TestBed.configureTestingModule({
    imports: [HealingHostComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([]),
      { provide: HealingSwitchDataService, useValue: data },
      { provide: SessionStore, useValue: session },
    ],
  });
  const fixture = TestBed.createComponent(HealingHostComponent);
  const store = fixture.debugElement.injector.get(HealingSwitchStore);

  async function advance(): Promise<void> {
    TestBed.tick();
    fixture.detectChanges();
    await Promise.resolve();
  }

  async function settle(): Promise<void> {
    for (let pass = 0; pass < 3; pass += 1) {
      TestBed.tick();
      fixture.detectChanges();
      await fixture.whenStable();
    }
  }

  return { data, session, fixture, store, settle, advance };
}
