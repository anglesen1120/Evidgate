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
//
// 09.2.1-T2 added step 2's write path: the `onboarding-token` seed (step 2 satisfied by a token
// instead of a binding) and `seedQueryWith` / `writeVariantToggle`, which pin a create answer
// explicitly. The three create answers are their operation's default, so a spec reaches them with
// no toggle at all; `SEED_TOGGLES`'s own comment says why no seed can prove them statefully.
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
  | 'onboarding-token'
  | 'onboarding-token-used'
  | 'onboarding-unusable'
  | 'onboarding-connected'
  | 'onboarding-run-complete'
  | 'onboarding-evidence';

/**
 * The step each seed resolves to, by the plan §6 derivation:
 *
 * | seed                      | server state                                    | current step |
 * | ------------------------- | ----------------------------------------------- | ------------ |
 * | `onboarding-empty`        | no project in the org                           | 1            |
 * | `onboarding-project`      | project, no binding/token                       | 2            |
 * | `onboarding-token`        | project + one active `ingest_token`, no binding | 3            |
 * | `onboarding-connected`    | project + one active `ci_binding`, no run       | 3            |
 * | `onboarding-run-complete` | … and a `complete` run without an evidence      | 4            |
 * | `onboarding-evidence`     | … and an evidence revision on that run          | 5            |
 *
 * Step 5 stays current in `onboarding-evidence`: the contract has no project-keyed read of a
 * `policy_activation`, so the wizard reports activation as unreadable and never as done (decision
 * (b)).
 */
export const SEED_STEP: Readonly<Record<OnboardingSeed, StepNumber>> = {
  'onboarding-empty': 1,
  'onboarding-project': 2,
  'onboarding-token': 3,
  'onboarding-token-used': 3,
  'onboarding-unusable': 3,
  'onboarding-connected': 3,
  'onboarding-run-complete': 4,
  'onboarding-evidence': 5,
};

/**
 * The toggles one seed needs. Every operation the wizard reads is pinned by the seed that makes it
 * decisive. `onboarding-token` (09.2.1-T2) is the one seed whose ingest-token page carries a token
 * — the account created by step 2's token path — and it keeps the binding page empty, so it proves
 * step 2 is satisfied by *either* connect path; every other seed pins the token page empty and is
 * satisfied by the `ci_binding` alone.
 *
 * What no seed can prove is a stateful flow: the fixture backend answers a fixed document per
 * (operation, variant), so a create's answer cannot add a row to the list read that follows it.
 * The step-2 specs therefore split the behaviour — the create answers (the `ok` default of
 * `createCiBinding` / `createIngestToken` / `rotateIngestToken`, no toggle needed) prove the
 * one-shot reveal and its clearing on navigation, and `onboarding-token` proves the resume: the
 * panel re-rendered from server metadata only, with Next enabled because `done[2]` already holds.
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
  // The token path: one active token, an empty binding page, no run.
  'onboarding-token': [
    'fixture.variant.getProject=onboarding-project',
    'fixture.variant.listCiBindings=onboarding-project',
    'fixture.variant.listIngestTokens=onboarding-token',
    'fixture.variant.listRuns=onboarding-project',
  ],
  // 09.2.1-T7: the same position with the token page holding the credential metadata the health
  // panel derives from — an `active` token that was used (its `last_used_at` recorded) and, for the
  // other seed, a `revoked` one and an `expired` one. Both stay on step 3, because step 2's
  // done-condition is presence and health is not part of the derivation.
  'onboarding-token-used': [
    'fixture.variant.getProject=onboarding-project',
    'fixture.variant.listCiBindings=onboarding-project',
    'fixture.variant.listIngestTokens=onboarding-token-used',
    'fixture.variant.listRuns=onboarding-project',
  ],
  'onboarding-unusable': [
    'fixture.variant.getProject=onboarding-project',
    'fixture.variant.listCiBindings=onboarding-project',
    'fixture.variant.listIngestTokens=onboarding-unusable',
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
  return seedQueryWith(seed, []);
}

/**
 * One seed's query plus extra toggles, for a spec that wants another answer than the seed's own —
 * e.g. `seedQueryWith('onboarding-project', [writeVariantToggle('createIngestToken')])` to pin the
 * create answer a step-2 spec submits against. The seed's toggles come first, so an extra one can
 * override an operation the seed pins.
 */
export function seedQueryWith(seed: OnboardingSeed, extra: readonly string[]): string {
  return `?${[WIZARD_GRANTS, ...SEED_TOGGLES[seed], ...extra].join('&')}`;
}

/**
 * The three write operations step 2 performs, named as `contracts/openapi.yaml` names them. Each
 * one's `ok` answer is a `fixtures/web-api/` document (`ci-binding-create`, `ingest-token-create`,
 * `ingest-token-rotate`), so a spec reaches the reveal with no toggle at all: those three answers
 * are the default the backend serves, and step 2's controls are enabled by the wizard grants every
 * seed already carries (`WIZARD_GRANTS` holds `ci_binding:write` and `ingest_token:write`).
 */
export type StepTwoWrite = 'createCiBinding' | 'createIngestToken' | 'rotateIngestToken';

/**
 * A variant toggle for one step-2 write, to append to a seed's query through `seedQueryWith`. `ok`
 * is what the backend serves by default; the corpus also pins `conflict` for `createCiBinding` and
 * `rotateIngestToken` (`ci-binding-create-conflict`, `ingest-token-rotate-conflict`), so a refused
 * submit is reachable in the browser too. `createIngestToken` has no `conflict`: its operation
 * declares no 409 (contracts/openapi.yaml:2376-2397), so a token create has no refusal document.
 */
export function writeVariantToggle(write: StepTwoWrite, variant = 'ok'): string {
  return `fixture.variant.${write}=${variant}`;
}

/**
 * The wizard grants with `ci_binding:write` and `ingest_token:write` removed
 * (`permissions-list-onboarding-no-writes`): step 2's two controls render disabled, with their
 * localized reason, instead of disappearing. Pass it as an extra toggle to `seedQueryWith`, whose
 * extras come last and therefore override `WIZARD_GRANTS`.
 */
export const NO_WRITE_GRANTS_TOGGLE = 'fixture.variant.listPermissions=onboarding-no-writes';

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
 * 09.2.1-T7: a refusal answered by one of step 2's two credential lists rather than by the project
 * read. `listIngestTokens` is the read whose answer carries the token metadata the health panel
 * derives from, so its 403/404 is the case that proves a refused credential read rejects the
 * resume instead of publishing a step with an empty panel.
 */
export type CredentialListFailure = 'onboarding-forbidden' | 'onboarding-project-not-found';

/**
 * The wizard address whose ingest-token list refuses. The project reads stay on the
 * `onboarding-project` seed, so the only difference to a working step 2 is the one refused list:
 * a shell that renders a step here swallowed a failure it was contracted to report (00 §9.2).
 */
export function credentialListRefusalUrl(
  locale: ShellLocale,
  failure: CredentialListFailure,
  projectId: string,
): string {
  const query = [
    WIZARD_GRANTS,
    'fixture.variant.getProject=onboarding-project',
    'fixture.variant.listCiBindings=onboarding-project',
    `fixture.variant.listIngestTokens=${failure}`,
    'fixture.variant.listRuns=onboarding-project',
  ].join('&');
  return withVariant(onboardingPath(locale, projectId), `?${query}`);
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
 * also match the pending panel (`onboarding-step-pending`) the steps no page owns yet render — 3–5
 * since 09.2.1-T2 replaced step 2's placeholder with the connect page.
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

// ---------------------------------------------------------------------------------------------
// Step 2 (09.2.1-T2, plan §2 AC2/AC7, §8 "Secret handling"). The one-time values below are not
// typed here from memory: each is the exact string in the corpus document named above it, and the
// assertions fail loudly if the fixture and this suite ever disagree.
//
// Why these specs assert *effects* rather than the wire: the fixtures build installs the dev
// backend as Angular's `HttpBackend` (`src/app/core/dev-fixtures/fixture-backend.ts`), so the SDK's
// requests never reach the network and Playwright's `page.route`/`page.on('request')` see nothing.
// Every create path in `ConnectCiPage` ends in exactly one of two renders — the `connect-reveal`
// slot (2xx) or `connect-ci-error` (>= 400) — so "no create happened" is provable from the DOM,
// and which answer the server gave is provable from the fixture's own document.
// ---------------------------------------------------------------------------------------------

/** `fixtures/web-api/ingest-token-create/input.json` → `body.secret`. */
export const FIXTURE_TOKEN_SECRET = 'egt_FIXTURE_ONLY_not_a_real_token';

/** `fixtures/web-api/ingest-token-rotate/input.json` → `body.secret`. */
export const FIXTURE_ROTATED_TOKEN_SECRET = 'egt_FIXTURE_ONLY_not_a_real_rotated_token';

/** `fixtures/web-api/ci-binding-create/input.json` → `body.binding_challenge`. */
export const FIXTURE_BINDING_CHALLENGE = 'evg_fixture_only_binding_challenge_not_a_real_value';

// 09.2.1-T7. Step 2's connection-health panel and its CLI quickstart read the same two metadata
// rows the panel already renders, so the facts the health suite asserts are the corpus's own:
// `fixtures/web-api/ingest-tokens-list-onboarding-token-used/input.json` → `body.items[0]`.

/** That fixture's token name. The quickstart comment quotes it and the credential row titles it. */
export const TOKEN_USED_NAME = 'Release CI';
/** That fixture's prefix. The quickstart names it; it is also the rotate button's label text. */
export const TOKEN_USED_PREFIX = 'egt_fixture';
/** That fixture's `last_used_at`, rendered in the panel's `<time [attr.datetime]>`. */
export const TOKEN_USED_LAST_USED_AT = '2026-10-24T09:12:34Z';
/** The project slug of `project-detail-onboarding-project`: the quickstart's `EVIDGATE_PROJECT`. */
export const ONBOARDING_PROJECT_SLUG = 'storefront-web';

/** The step-2 page (`app-onboarding-connect-ci`), rendered for `viewStep === 2` with a project. */
export function connectCiPage(page: Page) {
  return page.locator('[data-testid="connect-ci-page"]');
}

/**
 * The one-time reveal wrapper — the single subtree allowed to hold a secret, and only while the
 * component-scoped `RevealSlot` holds one.
 */
export function revealSlot(page: Page) {
  return page.locator('[data-testid="connect-reveal"]');
}

/** The read-only control the reveal renders the one-time value in. */
export function revealValue(page: Page) {
  return page.locator('[data-testid="reveal-value"]');
}

/**
 * AC2/§8: a one-time value lives in exactly one place — the `connect-reveal` subtree — and only for
 * as long as that subtree is mounted. This is the whole-page scan behind that claim, and it looks
 * in the three places a value can hide, because a text-only scan would pass vacuously:
 *
 *   - **every attribute** of every element (a title, an `aria-label`, a `data-*`, the `[value]`
 *     binding of any input);
 *   - **every form control's `value` property** — Angular's `[value]` sets the property, not the
 *     attribute, so an input that kept a copy is invisible to an attribute scan;
 *   - **every rendered text node**, skipping `<script>`/`<style>`, whose text is source, not render.
 *
 * Plus the four out-of-document places a secret should never reach: both storages, `document.cookie`,
 * the address bar and `history.state`.
 *
 * `holders` names every element that holds the value (should be exactly the reveal's control while
 * the slot is open, and nothing at all after it closes); `outside` names those of them — and those
 * out-of-document places — that are NOT inside `connect-reveal`, which must always be empty.
 */
export async function secretFootprint(
  page: Page,
  secret: string,
): Promise<{ holders: readonly string[]; outside: readonly string[] }> {
  return page.evaluate((value) => {
    const reveal = '[data-testid="connect-reveal"]';
    const holders = new Set<string>();
    const outside = new Set<string>();
    const name = (element: Element): string => {
      const testId = element.getAttribute('data-testid');
      if (testId !== null) return `${element.tagName.toLowerCase()}[${testId}]`;
      return element.id === ''
        ? element.tagName.toLowerCase()
        : `${element.tagName.toLowerCase()}#${element.id}`;
    };
    const record = (element: Element, where: string): void => {
      holders.add(`${name(element)} ${where}`);
      if (element.closest(reveal) === null) outside.add(`${name(element)} ${where}`);
    };
    for (const element of Array.from(document.querySelectorAll('*'))) {
      if (element.tagName === 'SCRIPT' || element.tagName === 'STYLE') continue;
      for (const attribute of Array.from(element.attributes)) {
        if (attribute.value.includes(value)) record(element, attribute.name);
      }
      if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement) {
        if (element.value.includes(value)) record(element, 'value');
      }
      if (element instanceof HTMLSelectElement && element.value.includes(value)) {
        record(element, 'value');
      }
      for (const node of Array.from(element.childNodes)) {
        if (node.nodeType === Node.TEXT_NODE && (node.textContent ?? '').includes(value)) {
          record(element, 'text');
        }
      }
    }
    for (const [label, store] of [
      ['localStorage', window.localStorage],
      ['sessionStorage', window.sessionStorage],
    ] as const) {
      for (let index = 0; index < store.length; index += 1) {
        const key = store.key(index) ?? '';
        if (key.includes(value) || (store.getItem(key) ?? '').includes(value)) {
          outside.add(`${label}[${key}]`);
        }
      }
    }
    if (document.cookie.includes(value)) outside.add('document.cookie');
    if (document.location.href.includes(value)) outside.add('location.href');
    if (JSON.stringify(window.history.state ?? null).includes(value)) outside.add('history.state');
    return { holders: Array.from(holders), outside: Array.from(outside) };
  }, secret);
}

/** Asserts a one-time value appears nowhere outside the reveal slot. */
export async function expectSecretAbsent(
  page: Page,
  secret: string,
  context: string,
): Promise<void> {
  const footprint = await secretFootprint(page, secret);
  expect(footprint.outside, `${context} leaked the one-time value`).toEqual([]);
}

/**
 * Asserts the stronger half of §8 while the reveal is open: the value is held by exactly one
 * element, and it is inside the reveal slot. One holder is the whole point — a second copy in a
 * hidden field, a cache attribute or a second input would make "shown once" false even though
 * nothing leaked *outside* the slot.
 */
export async function expectSecretOnlyInReveal(
  page: Page,
  secret: string,
  context: string,
): Promise<void> {
  const footprint = await secretFootprint(page, secret);
  expect(footprint.outside, `${context} leaked the one-time value`).toEqual([]);
  expect(
    footprint.holders,
    `${context} did not hold the one-time value in exactly one place`,
  ).toHaveLength(1);
}

/** Asserts the reveal slot is closed: no wrapper, no rendered value. */
export async function expectNoReveal(page: Page): Promise<void> {
  await expect(revealSlot(page)).toHaveCount(0);
  await expect(revealValue(page)).toHaveCount(0);
}
