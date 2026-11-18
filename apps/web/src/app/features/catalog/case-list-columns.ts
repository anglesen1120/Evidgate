// Open contract gaps: CaseDto.version is an ETag token, not an immutable case revision.
// Owner is a membership identifier; no owner display name is supplied by this read.
export const CATALOG_OMITTED_COLUMNS = [
  { column: 'revision', missingField: 'CaseDto.current_revision_id' },
  { column: 'platform', missingField: 'CaseDto.platform' },
  { column: 'level', missingField: 'CaseDto.level' },
  { column: 'purpose', missingField: 'CaseDto.purpose' },
  { column: 'rename history', missingField: 'CaseDto.renames (only CaseDetailDto has renames)' },
  { column: 'linked manual case', missingField: 'CaseDto.linked_manual_case_id' },
  { column: 'script status', missingField: 'CaseDto.script_status' },
] as const;
