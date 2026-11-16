import {
  HlmCaption,
  HlmTable,
  HlmTableContainer,
  HlmTBody,
  HlmTd,
  HlmTFoot,
  HlmTh,
  HlmTHead,
  HlmTr,
} from './lib/hlm-table';
import { HlmTableColumn } from './lib/hlm-table-column';

export * from './lib/hlm-table';
export * from './lib/hlm-table-column';

export const HlmTableImports = [
  HlmCaption,
  HlmTableContainer,
  HlmTable,
  HlmTableColumn,
  HlmTBody,
  HlmTd,
  HlmTFoot,
  HlmTh,
  HlmTHead,
  HlmTr,
] as const;
