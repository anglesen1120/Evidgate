-- 0119_catalog_template_web_playwright_page.sql
--
-- WHY: `evidgate init --template web/playwright-page` reads the registry rather than a code branch, so the first packaged
-- web template is a row: a release of the files is a migration, and every generated test stays attributable to the
-- version that produced it.
--
-- SHAPE: one built-in row (`org_id IS NULL`) for the files under `templates/web/playwright-page/1/`. `body_ref` is the
-- content address of those files (manifest included) and `conformance_rules` is the manifest's own rules, copied
-- verbatim, so the registry and the files cannot disagree. The row is written as the owner role, which
-- `template_owner_backfill` already allows; `evidgate_app` keeps SELECT only and no policy moves.
--
-- BODY FENCE: for each file, in UTF-8 path order, the hash covers the path, a NUL, the decimal byte length of the
-- normalised content, a NUL, that content (UTF-8, CRLF folded to LF) and a line feed. The drift test recomputes that
-- address from the directory and compares it with the literal below, so an edited template file without a new
-- migration fails the suite instead of shipping a stale address.
--
-- LOCKS and ORDER: one insert on an existing catalogue table; `lock_timeout` bounds its wait. Deploy before anything
-- serves `evidgate init`. Rollback: delete the row by id; the packaged files stay in the repository, unreferenced.
--
-- ON CONFLICT: the fixed id and the partial unique index on built-ins make a re-run or a database reset insert nothing
-- new. A database that already holds a different built-in row for this (kind, name, version) keeps that row: this
-- migration never overwrites it and does not claim convergence, so only the drift test (shipped literal against the
-- packaged files) proves the address this migration was written for.

set local lock_timeout = '5s';

insert into public.template (id, org_id, kind, name, version, body_ref, conformance_rules)
values (
  '018f8f5e-0000-7000-8000-000000000119'::uuid,
  null,
  'web',
  'playwright-page',
  1,
  'sha256:2c5bfe93f4b2e05c4ccd7c2414f2f140ae4338441afe624d6e34775c1ac5bffe',
  $rules$[{"id":"evidgate-id-present","kind":"evidgate_annotation","file":"tests/*.spec.ts"},{"id":"no-hardcoded-credentials","kind":"forbids_secret_literal","file":"**/*.ts"},{"id":"page-object-shape","kind":"requires_pattern","file":"pages/*Page.ts","pattern":["export class \\w+Page\\b","constructor\\([^)]*page[^)]*\\)","getByRole\\("]}]$rules$::jsonb
)
on conflict (kind, name, version) where org_id is null do nothing;
