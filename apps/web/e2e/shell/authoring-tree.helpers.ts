import {
  localePath,
  ORG_ACME,
  PROJECT_STOREFRONT,
  type ShellLocale,
  withVariants,
} from './support';

export const PAGE = 'app-authoring-page';
export const TREE = 'app-authoring-tree-pane';
export const IDS = [101, 102, 103, 104, 105, 106, 107].map(
  (suffix) => `018f0000-0000-7000-8000-000000000${suffix}`,
);
export const ROOT = IDS[0] ?? '';
export const LEAF = IDS[6] ?? '';

export function authoringTreePath(locale: ShellLocale): string {
  return localePath(locale, `/dev/authoring-tree/orgs/${ORG_ACME}/projects/${PROJECT_STOREFRONT}`);
}

export function readOnlyTreePath(locale: ShellLocale): string {
  return withVariants(authoringTreePath(locale), [
    '?fixture.variant.listPermissions=settings-policy-readonly',
  ]);
}
