export const mappingStates = [
  {
    key: 'ready',
    label: $localize`:@@review.mapping.states.ready.title:Default`,
    title: $localize`:@@review.mapping.states.ready.title:Default`,
    description: '',
  },
  {
    key: 'loading',
    label: $localize`:@@review.mapping.states.loading.title:Loading`,
    title: $localize`:@@review.mapping.states.loading.title:Loading`,
    description: $localize`:@@review.mapping.states.loading.body:Reading the current scope. No change is being submitted.`,
  },
  {
    key: 'empty',
    label: $localize`:@@review.mapping.states.empty.option:No resources`,
    title: $localize`:@@review.mapping.states.empty.title:No unresolved mappings`,
    description: $localize`:@@review.mapping.states.empty.body:Every supplied identity already has a decision. This does not imply a passing run.`,
  },
  {
    key: 'filtered',
    label: $localize`:@@review.mapping.states.filtered.option:No matches`,
    title: $localize`:@@review.mapping.states.filtered.title:No results match these filters`,
    description: $localize`:@@review.mapping.states.filtered.body:Your selection is retained.`,
  },
  {
    key: 'error',
    label: $localize`:@@review.mapping.states.error.title:Request failed`,
    title: $localize`:@@review.mapping.states.error.title:Request failed`,
    description: $localize`:@@review.mapping.states.error.body:The request did not complete. Retry the same scope; no changes have been applied.`,
  },
  {
    key: 'denied',
    label: $localize`:@@review.mapping.states.denied.option:403 · Not authorized`,
    title: $localize`:@@review.mapping.states.denied.title:Not authorized`,
    description: $localize`:@@review.mapping.states.denied.body:Ask an administrator for access to this scope.`,
  },
  {
    key: 'missing',
    label: $localize`:@@review.mapping.states.missing.option:404 · Not found`,
    title: $localize`:@@review.mapping.states.missing.title:Not found`,
    description: $localize`:@@review.mapping.states.missing.body:This resource is unavailable.`,
  },
  {
    key: 'expired',
    label: $localize`:@@review.mapping.states.expired.option:401 · Session expired`,
    title: $localize`:@@review.mapping.states.expired.title:Sign in again`,
    description: $localize`:@@review.mapping.states.expired.body:Your safe return route is retained. No draft will be submitted.`,
  },
  {
    key: 'limited',
    label: $localize`:@@review.mapping.states.limited.option:429 · Retry later`,
    title: $localize`:@@review.mapping.states.limited.title:Too many requests`,
    description: $localize`:@@review.mapping.states.limited.body:Retry-After: 30 s (server fixture). No automatic retry.`,
  },
  {
    key: 'conflict',
    label: $localize`:@@review.mapping.states.conflict.option:412 · Linked edit conflict`,
    title: $localize`:@@review.mapping.states.conflict.title:Someone else changed this — reload`,
    description: $localize`:@@review.mapping.states.conflict.body:Separate linked edit example only: rv-12 → rv-13; candidate changed. The draft case-address-8 is retained. This is not a mapping state-guard response.`,
  },
  {
    key: 'pending',
    label: $localize`:@@review.mapping.states.pending.title:Submitting · awaiting server response`,
    title: $localize`:@@review.mapping.states.pending.title:Submitting · awaiting server response`,
    description: $localize`:@@review.mapping.states.pending.body:No mapping or source result has changed. Duplicate submissions are disabled.`,
  },
  {
    key: 'unavail',
    label: $localize`:@@review.mapping.states.unavail.title:Proposed case unavailable`,
    title: $localize`:@@review.mapping.states.unavail.title:Proposed case unavailable`,
    description: $localize`:@@review.mapping.states.unavail.body:map-63 · No candidate may be inferred across projects.`,
  },
  {
    key: 'pickerload',
    label: $localize`:@@review.mapping.states.pickerload.title:Loading project cases`,
    title: $localize`:@@review.mapping.states.pickerload.title:Loading project cases`,
    description: $localize`:@@review.mapping.states.pickerload.body:map-62 · Bind is disabled until candidates arrive.`,
  },
  {
    key: 'pickerempty',
    label: $localize`:@@review.mapping.states.pickerempty.title:No project cases match`,
    title: $localize`:@@review.mapping.states.pickerempty.title:No project cases match`,
    description: $localize`:@@review.mapping.states.pickerempty.body:map-62 · Adjust the search or return to the catalog. No fuzzy match is selected.`,
  },
  {
    key: 'pickererror',
    label: $localize`:@@review.mapping.states.pickererror.title:Could not load project cases`,
    title: $localize`:@@review.mapping.states.pickererror.title:Could not load project cases`,
    description: $localize`:@@review.mapping.states.pickererror.body:req-map-31 · No case was selected and no mapping was changed.`,
  },
  {
    key: 'guard',
    label: $localize`:@@review.mapping.states.guard.title:409 · Already resolved`,
    title: $localize`:@@review.mapping.states.guard.title:409 · Already resolved`,
    description: $localize`:@@review.mapping.states.guard.body:State guard: already resolved. Reload the row. This is not a 412 revision conflict.`,
  },
  {
    key: 'mapped',
    label: $localize`:@@review.mapping.states.mapped.title:Mapped · supplied server history`,
    title: $localize`:@@review.mapping.states.mapped.title:Mapped · supplied server history`,
    description: $localize`:@@review.mapping.states.mapped.body:map-64 → SHOP-TC-0042 v5. Original result remains failed. No item was automatically matched.`,
  },
  {
    key: 'long',
    label: $localize`:@@review.mapping.states.long.title:Long producer identity`,
    title: $localize`:@@review.mapping.states.long.title:Long producer identity`,
    description: $localize`:@@review.mapping.states.long.body:AddressLabel.spec.ts › checkout.chrome › địa_chỉ_giao_hàng › nhập_mã_bưu_chính_không_hợp_lệ › 訂正後の郵便番号を再検証`,
  },
  {
    key: 'readonly',
    label: $localize`:@@review.mapping.states.readonly.title:Read-only permission`,
    title: $localize`:@@review.mapping.states.readonly.title:Read-only permission`,
    description: '',
  },
] as const;

export type MappingState = (typeof mappingStates)[number]['key'];
