// A permission read is tenant-scoped through the interceptor, so the tenant has to be published
// before the read starts. Keeping the pair in one helper makes that order impossible to forget.
import type { EffectivePermissionsSource } from './effective-permissions.source';
import type { TenantStore } from './tenant.store';

/**
 * Publishes `orgId` as the request tenant (`null` clears it) and reads the grants that tenant
 * scopes. The returned promise settles with the source's answer or its failure, unchanged.
 */
export function loadPermissionsForTenant(
  tenant: TenantStore,
  source: EffectivePermissionsSource,
  orgId: string | null,
): Promise<ReadonlySet<string>> {
  if (orgId === null) {
    tenant.clear();
  } else {
    tenant.setOrg(orgId);
  }
  return source.load();
}
