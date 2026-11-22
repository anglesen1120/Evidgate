import { mappingCopy } from './mapping-resolution.copy';
import type { MappingResolutionViewModel } from './mapping-resolution.view-model';

export const mappingResolutionFixture: MappingResolutionViewModel = {
  state: 'ready',
  project: 'storefront-web',
  projectKey: 'p-storefront',
  producer: 'junit-ci',
  suite: 'checkout.chrome',
  report: 'rpt-junit-62',
  run: 'run-1842',
  evidence: 'ev-3',
  digest: 'sha256:d37b…818f',
  completeness: 'incomplete',
  outcome: 'has_failures',
  decision: 'FAIL',
  rows: [
    {
      id: 'map-62',
      name: 'AddressLabel.spec.ts › formats postcode',
      state: 'unmapped',
      suggestion: mappingCopy.noCase,
      candidates: [
        {
          id: 'case-address-8 · cr-5',
          title: $localize`:@@review.mapping.case.address-postcode:Address formats postcode`,
        },
        {
          id: 'case-address-12 · cr-2',
          title: $localize`:@@review.mapping.case.address-validation:Address displays validation error`,
        },
        {
          id: 'SHOP-TC-0042 · v5',
          title: $localize`:@@review.mapping.case.confirm-order:Confirm order shows the total`,
        },
        {
          id: 'SHOP-TC-0043 · v2',
          title: $localize`:@@review.mapping.case.delivery:Required delivery address`,
        },
        {
          id: 'SHOP-TC-0051 · v3',
          title: $localize`:@@review.mapping.case.discount:Apply a discount code`,
        },
      ],
    },
    {
      id: 'map-63',
      name: 'Checkout confirmation',
      state: 'conflicted',
      suggestion: 'case-checkout-17 / case-checkout-22',
      candidates: [
        {
          id: 'case-checkout-17',
          title: $localize`:@@review.mapping.case.confirms:Checkout confirms order`,
        },
        {
          id: 'case-checkout-22',
          title: $localize`:@@review.mapping.case.suggested-identity:Suggested stable identity`,
        },
      ],
    },
    {
      id: 'map-64',
      name: 'Checkout confirms order',
      state: 'resolved',
      suggestion: 'SHOP-TC-0042 · v5',
      candidates: [],
    },
  ],
  imports: [
    {
      id: 'imp-63',
      name: 'checkout.chrome › checkout keeps cart',
      original: 'success',
      normalized: 'unknown_status',
      reason: 'unsupported_status',
      candidates: $localize`:@@review.mapping.no-stable:Unresolved · no stable case`,
    },
    {
      id: 'imp-64',
      name: 'address.unit › AddressLabel.spec.ts › formats postcode',
      original: 'success',
      normalized: 'passed',
      reason: 'no_stable_mapping',
      candidates: $localize`:@@review.mapping.no-link:No linked case`,
    },
    {
      id: 'imp-65',
      name: 'address.chrome › validates delivery address',
      original: 'failure',
      normalized: 'mapping_conflict',
      reason: 'multiple_candidates',
      candidates: 'case-address-8 / case-address-12',
    },
  ],
};
