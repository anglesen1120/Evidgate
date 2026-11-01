// The revisions the two composition specs share: the served ready fixture and the generated grids
// that cross the matrix cell limit. `suite-composition.component.spec.ts` renders them into the
// matrix and the flat table, `suite-composition.members.component.spec.ts` owns the member cases,
// so one definition keeps both from drifting when the layout rules change.
import type { SuiteRevisionDto } from '@evidgate/sdk';
import ready from '../../../../../../../fixtures/web-api/suite-revision/input.json';

export const readyRevision = ready.body as SuiteRevisionDto;

/** A revision whose members fill a rows × columns grid, one member per cell. */
export function gridRevision(rows: number, columns: number): SuiteRevisionDto {
  const members: SuiteRevisionDto['members'] = [];
  for (let row = 0; row < rows; row += 1) {
    for (let column = 0; column < columns; column += 1) {
      members.push({
        id: `member-${row}-${column}`,
        case_revision_id: `case-${String(row).padStart(3, '0')}`,
        variant_hash: `variant-${column}`,
        params: { column },
        required: column % 2 === 0,
        ordinal: column,
      });
    }
  }
  return { ...readyRevision, members, edges: [] };
}

/**
 * A revision whose grid has `rows × columns` cells but only `placed` members, so the layout is
 * `flat` while the member list stays short enough to exhaust in one press. The stride hits every
 * row and every column while `placed` stays below their product, which is what makes the cell
 * count — not the member count — the reason for the flat layout.
 */
export function sparseGridRevision(
  rows: number,
  columns: number,
  placed: number,
): SuiteRevisionDto {
  const members: SuiteRevisionDto['members'] = [];
  for (let index = 0; index < placed; index += 1) {
    const row = index % rows;
    const column = index % columns;
    members.push({
      id: `member-${index}`,
      case_revision_id: `case-${String(row).padStart(3, '0')}`,
      variant_hash: `variant-${column}`,
      params: { column },
      required: column % 2 === 0,
      ordinal: index,
    });
  }
  return { ...readyRevision, members, edges: [] };
}
