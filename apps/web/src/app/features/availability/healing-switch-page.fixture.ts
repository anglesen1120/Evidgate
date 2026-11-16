import { computed, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { HealingSwitchResolutionView } from '@evidgate/sdk';
import { expect, vi } from 'vitest';

import { HealingSwitchDataService } from '../../core/data/healing-switch-data.service';
import { SessionStore } from '../../core/session/session.store';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { HealingSwitchPage } from './healing-switch.page';
import {
  CONFIGURE,
  FakeHealingSwitchData,
  TRIAGE,
  fakeSession,
} from './healing-switch.test-support';

export function mount(
  options: {
    codes?: readonly string[];
    withProject?: boolean;
    answer?: HealingSwitchResolutionView;
    holdReads?: boolean;
    readFailure?: Error | null;
  } = {},
) {
  const base = fakeSession(options.codes ?? [TRIAGE, CONFIGURE]);
  if (options.withProject === false) base.setProject(null);
  const session = {
    ...base,
    currentProject: computed(() => {
      const project = base.currentProject();
      return project === null ? null : { ...project, name: 'Storefront' };
    }),
  };
  const data = new FakeHealingSwitchData();
  if (options.answer !== undefined) data.answer = options.answer;
  data.holdReads = options.holdReads ?? false;
  data.readFailure = options.readFailure ?? null;
  const desktop = signal(true);
  TestBed.configureTestingModule({
    imports: [HealingSwitchPage],
    providers: [
      provideZonelessChangeDetection(),
      {
        provide: NarrowMutationPolicy,
        useValue: { canMutate: desktop, narrow: () => !desktop(), allowsMutation: () => desktop() },
      },
      { provide: SessionStore, useValue: session },
      { provide: HealingSwitchDataService, useValue: data },
    ],
  });
  const fixture = TestBed.createComponent(HealingSwitchPage);
  const root = fixture.nativeElement as HTMLElement;
  fixture.detectChanges();
  const settle = async (): Promise<void> => {
    for (let pass = 0; pass < 3; pass += 1) {
      TestBed.tick();
      fixture.detectChanges();
      await fixture.whenStable();
    }
  };
  const advance = async (): Promise<void> => {
    TestBed.tick();
    fixture.detectChanges();
    await Promise.resolve();
  };
  const until = async (predicate: () => boolean): Promise<void> => {
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(predicate()).toBe(true);
    });
  };
  return { fixture, root, data, settle, advance, until, desktop };
}

export function scopeCard(root: HTMLElement, scope: string): HTMLElement | null {
  return (
    root.querySelector<HTMLElement>(`[data-healing-editor="${scope}"]`) ??
    root.querySelector<HTMLElement>(`[data-healing-scope="${scope}"]`)
  );
}

export function saveButton(root: HTMLElement, scope: string): HTMLButtonElement | null {
  return root.querySelector<HTMLButtonElement>(
    `[data-healing-editor="${scope}"] [data-healing-save]`,
  );
}

export function selectOf(root: HTMLElement, scope: string): HTMLButtonElement | null {
  return root.querySelector<HTMLButtonElement>(
    `[data-healing-editor="${scope}"] button[role="combobox"]`,
  );
}
