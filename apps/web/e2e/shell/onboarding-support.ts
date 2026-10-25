// 09.2.1-T1 (plan 09.2.1 §2 AC1, §6, §10 T1.3/T1.4): the shared pieces of the onboarding wizard's
// real-browser suite, which runs against the FIXTURES build (`playwright.fixtures.config.ts`).
//
// Two rules, both inherited from `support.ts`:
//
// 1. **Fixtures pick the server state, never the app.** A wizard step is *derived* from server
//    resources (T63: project → binding/token → sample run → evidence revision → activation), so one
//    seed is a set of `?fixture.variant.<operationId>=<variant>` toggles rather than one document.
//    `SEED_TOGGLES` is that set, named after the `fixtures/web-api/` documents that answer it; the
//    directory comments in `fixture-catalog.ts` describe what each seed holds.
// 2. **No localized string identifies an element.** Every assertion keys on the DOM contract the
//    wizard publishes (`data-testid` values, `aria-current="step"`, `data-done`, the native
//    `disabled` attribute), so the same spec passes on the `en` and the `vi` bundle.
import { expect, type Page } from '@playwright/test';

import { localePath, ORG_ACME, type ShellLocale, withVariant } from './support';

/**
 * The project the `project-create` fixture answers `POST /v1/orgs/{o}/projects` with: the oldest
 * row of the default `projects-list` page. The wizard navigates to a plain
 * `/orgs/<org>/onboarding/<projectId>` after a successful create (no toggles), so the post-create
 * load falls back to the default reads — and only a project the default page holds resolves there.
 */
export const CREATED_PROJECT = '018f0000-0000-7000-8000-000000000011';
/** The name/slug the create fixture returns; the spec types exactly these into the form. */
export const CREATED_PROJECT_NAME = 'Storefront web';
export const CREATED_PROJECT_SLUG = 'storefront-web';

/**
 * The permission catalogue the wizard needs (`fixtures/web-api/permissions-list-onboarding`): the
 * default page holds no `project:*` code, and the route as well as the step-1 submit are gated on
 * `project:create`. Every wizard URL carries this toggle, seeds included.
 */
const WIZARD_GRANTS = 'fixture.variant.listPermissions=onboarding';

/** The five documented steps of the wizard (plan 09.2.1 §10 T1, decision (c)). */
export const STEP_NUMBERS = [1, 2, 3, 4, 5] as const;
export type StepNumber = (typeof STEP_NUMBERS)[number];

/** The derived-step seeds of `fixtures/web-api/`: each name is the variant of its documents. */
export type OnboardingSeed =
  | 'onboarding-empty'
  | 'onboarding-project'
  | 'onboarding-connected'
  | 'onboarding-run-complete'
  | 'onboarding-evidence';

/**
 * The step each seed resolves to, by the plan §6 derivation:
 *
 * | seed                    | server state                                  | current step |
 * | ----------------------- | --------------------------------------------- | ------------ |
 * | `onboarding-empty`      | no project in the org                          | 1            |
 * | `onboarding-project`    | project, no binding/token                      | 2            |
 * | `onboarding-connected`  | project + one active `ci_binding`, no run      | 3            |
 * | `onboarding-run-complete` | … and a `complete` run without an evidence   | 4            |
 * | `onboarding-evidence`   | … and an evidence revision on that run         | 5            |
 *
 * Step 5 stays current in `onboarding-evidence`: the contract has no project-keyed read of a
 * `policy_activation`, so the wizard reports activation as unreadable and never as done (decision
 * (b)).
 */
export const SEED_STEP: Readonly<Record<OnboardingSeed, StepNumber>> = {
  'onboarding-empty': 1,
  'onboarding-project': 2,
  'onboarding-connected': 3,
  'onboarding-run-complete': 4,
  'onboarding-evidence': 5,
};

/**
 * The toggles one seed needs. Every operation the wizard reads is pinned by the seed that makes it
 * decisive, and the ingest-token page is pinned empty everywhere: nothing in this corpus mints a
 * token, so step 2 can only be satisfied by the `ci_binding` (09.2.1-T2 owns the token path).
 */
const SEED_TOGGLES: Readonly<Record<OnboardingSeed, readonly string[]>> = {
  'onboarding-empty': [
    'fixture.variant.listProjects=onboarding-empty',
    // `getProject` is deliberately NOT pinned here. This seed is only reachable at the org's
    // onboarding root (an org with no project has no project address), and the step-1 create must
    // then resolve the project it just created — a project-scoped pin would answer that read with
    // `project-detail-onboarding-empty`'s 404 and flash the not-found pattern between create and
    // step 2.
  ],
  'onboarding-project': [
    'fixture.variant.getProject=onboarding-project',
    'fixture.variant.listCiBindings=onboarding-project',
    'fixture.variant.listIngestTokens=onboarding-project',
    'fixture.variant.listRuns=onboarding-project',
  ],
  'onboarding-connected': [
    'fixture.variant.getProject=onboarding-connected',
    'fixture.variant.listCiBindings=onboarding-connected',
    'fixture.variant.listIngestTokens=onboarding-connected',
    'fixture.variant.listRuns=onboarding-connected',
  ],
  // Both run seeds share the `onboarding-run` reads (binding + one `complete` run whose recorded
  // decision is `FAIL`, so the AC6 no-verdict check runs on a failing run) and differ only in
  // `getRun`: no evidence revision (step 4) versus one (step 5).
  'onboarding-run-complete': [
    'fixture.variant.getProject=onboarding-run',
    'fixture.variant.listCiBindings=onboarding-run',
    'fixture.variant.listIngestTokens=onboarding-run',
    'fixture.variant.listRuns=onboarding-run',
    'fixture.variant.getRun=onboarding-run-complete',
  ],
  'onboarding-evidence': [
    'fixture.variant.getProject=onboarding-run',
    'fixture.variant.listCiBindings=onboarding-run',
    'fixture.variant.listIngestTokens=onboarding-run',
    'fixture.variant.listRuns=onboarding-run',
    'fixture.variant.getRun=onboarding-evidence',
  ],
};

/** A progress read that refuses: the 403/404 answers of `getProject`, the project-scoped read. */
export type OnboardingFailure = 'onboarding-forbidden' | 'onboarding-project-not-found';

/**
 * The `?fixture.variant.…` query of one seed. The project reads are pinned even where the wizard is
 * opened at the org's onboarding root: the same seed then resolves identically at
 * `/orgs/<org>/onboarding/<projectId>`, which is the address a completed step navigates to.
 */
export function seedQuery(seed: OnboardingSeed): string {
  return `?${[WIZARD_GRANTS, ...SEED_TOGGLES[seed]].join('&')}`;
}

/**
 * The query of a wizard whose own project read refuses, at `/orgs/<org>/onboarding/<projectId>`.
 *
 * The refusal is pinned on `getProject` and NOT on `listProjects`: the shell's scope guard resolves
 * the org from `listProjects` first, and it answers a refusal there with the not-found page (00 §9.2
 * never distinguishes "you may not" from "it is not there") — the wizard would never mount, so the
 * pattern under test would be the shell's, not the wizard's.
 */
export function refusalQuery(failure: OnboardingFailure): string {
  return `?${[WIZARD_GRANTS, `fixture.variant.getProject=${failure}`].join('&')}`;
}

/**
 * The query of an org whose project list itself refuses. The shell answers this one: a `403` or
 * `404` on the scope read is the same not-found page (00 §9.2), so the wizard must not render at
 * all — the case exists to prove no step is shown for a read the server refused.
 */
export function orgRefusalQuery(failure: OnboardingFailure): string {
  return `?${[WIZARD_GRANTS, `fixture.variant.listProjects=${failure}`].join('&')}`;
}

/** `/orgs/:orgId/onboarding` or `/orgs/:orgId/onboarding/:projectId`, inside one locale bundle. */
export function onboardingPath(locale: ShellLocale, projectId: string | null = null): string {
  const suffix = projectId === null ? '' : `/${projectId}`;
  return localePath(locale, `/orgs/${ORG_ACME}/onboarding${suffix}`);
}

/** The wizard address of one seed, toggles included. */
export function onboardingUrl(
  locale: ShellLocale,
  seed: OnboardingSeed,
  projectId: string | null = null,
): string {
  return withVariant(onboardingPath(locale, projectId), seedQuery(seed));
}

/** The wizard address whose own project read refuses, at a project address. */
export function refusalUrl(
  locale: ShellLocale,
  failure: OnboardingFailure,
  projectId: string,
): string {
  return withVariant(onboardingPath(locale, projectId), refusalQuery(failure));
}

/** The org's onboarding root whose project list refuses (the shell answers this one). */
export function orgRefusalUrl(locale: ShellLocale, failure: OnboardingFailure): string {
  return withVariant(onboardingPath(locale), orgRefusalQuery(failure));
}

/**
 * The wizard address carrying the wizard's grants and nothing else.
 *
 * Grants are *session* state: every boot re-reads `listPermissions`, and the default catalogue page
 * holds no `project:*` code, so the onboarding route's own permission guard would refuse a reload
 * that dropped the grant toggle. A seed is *wizard* state, so a re-opened wizard carries the grants
 * and no seed at all: the step has to come from the resources the server reports.
 */
export function grantsOnlyUrl(locale: ShellLocale, projectId: string | null = null): string {
  return withVariant(onboardingPath(locale, projectId), `?${WIZARD_GRANTS}`);
}

/** One stepper item of the DOM contract. */
export function stepItem(page: Page, step: StepNumber) {
  return page.locator(`[data-testid="onboarding-step-${step}"]`);
}

/**
 * Every stepper item, scoped to the stepper itself: `[data-testid^="onboarding-step-"]` alone would
 * also match the pending panel (`onboarding-step-pending`) the steps 2–5 content renders.
 */
export function stepperItems(page: Page) {
  return page.locator('[data-testid="onboarding-stepper"] [data-testid^="onboarding-step-"]');
}

/** The stepper item the wizard is currently showing. */
export function currentStepItem(page: Page) {
  return page.locator(
    '[data-testid="onboarding-stepper"] [data-testid^="onboarding-step-"][aria-current="step"]',
  );
}

/** Next: a real `<button>`, natively `disabled` while the viewed step's done-condition is unmet. */
export function nextButton(page: Page) {
  return page.locator('[data-testid="onboarding-next"]');
}

/** Back: a real `<button>`, natively `disabled` on step 1. */
export function backButton(page: Page) {
  return page.locator('[data-testid="onboarding-back"]');
}

/** The shell's root, the one element every wizard state renders inside. */
export function shell(page: Page) {
  return page.locator('[data-testid="onboarding-shell"]');
}

/** Waits for the shell to finish loading, then asserts the wizard shows `step` and only `step`. */
export async function expectCurrentStep(page: Page, step: StepNumber): Promise<void> {
  const current = currentStepItem(page);
  await expect(current, `the wizard shows one current step`).toHaveCount(1);
  await expect(current).toHaveAttribute('data-testid', `onboarding-step-${step}`);
  await expect(stepItem(page, step)).toBeVisible();
}

/** Asserts one stepper item's `data-done` flag. */
export async function expectStepDone(page: Page, step: StepNumber, done: boolean): Promise<void> {
  await expect(stepItem(page, step), `step ${step} data-done`).toHaveAttribute(
    'data-done',
    String(done),
  );
}

/**
 * The wizard publishes five stepper items, each carrying a `data-done` flag; a state that renders
 * the T8 patterns instead still owes the reader a flag per item it does render, and never a
 * `data-done="true"` on a step the server did not confirm.
 */
export async function expectStepperShape(page: Page): Promise<void> {
  await expect(stepperItems(page)).toHaveCount(STEP_NUMBERS.length);
  for (const step of STEP_NUMBERS) {
    const flag = await stepItem(page, step).getAttribute('data-done');
    expect(flag, `step ${step} carries data-done="true|false"`).toMatch(/^(true|false)$/);
  }
}

/** No stepper item claims a done condition (used by the refusing-progress cases). */
export async function expectNoStepDone(page: Page): Promise<void> {
  await expect(
    page.locator(
      '[data-testid="onboarding-stepper"] [data-testid^="onboarding-step-"][data-done="true"]',
    ),
    'a failed progress read marked a step done',
  ).toHaveCount(0);
}

/**
 * Every `localStorage`/`sessionStorage` entry whose key or value names a step. AC1 says the resume
 * point comes from the server, so this list must stay empty across a create and a reload; the
 * theme choice (`evidgate-theme`) is the only preference the shell stores.
 */
async function stepNamedStorageEntries(page: Page): Promise<readonly string[]> {
  return page.evaluate(() => {
    const entries: string[] = [];
    for (const store of [window.localStorage, window.sessionStorage]) {
      for (let index = 0; index < store.length; index += 1) {
        const key = store.key(index) ?? '';
        const value = store.getItem(key) ?? '';
        if (/step/i.test(key) || /step/i.test(value)) {
          entries.push(`${store === window.localStorage ? 'local' : 'session'}:${key}=${value}`);
        }
      }
    }
    return entries;
  });
}

/** Asserts nothing under `localStorage`/`sessionStorage` names a wizard step. */
export async function expectNoStepInStorage(page: Page, context: string): Promise<void> {
  expect(
    await stepNamedStorageEntries(page),
    `${context} kept the wizard's resume point in browser storage`,
  ).toEqual([]);
}
