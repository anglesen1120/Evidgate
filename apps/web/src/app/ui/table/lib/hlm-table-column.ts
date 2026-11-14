import { Directive, input } from '@angular/core';

export type HlmTableColumnRole = 'identity' | 'status' | 'text' | 'number' | 'date' | 'actions';
export type HlmTableColumnKind = 'name' | 'identifier' | 'branch' | 'email' | 'url';
export type HlmTableColumnPriority = 'primary' | 'secondary';
export type HlmTableColumnOverflow = 'truncate' | 'wrap';

/** The same metadata styles native Helm cells and the shared CDK table. */
@Directive({
  selector: 'th[hlmTableColumn],td[hlmTableColumn],col[hlmTableColumn]',
  host: {
    '[attr.data-column-role]': 'role()',
    '[attr.data-column-kind]': 'kind()',
    '[attr.data-column-priority]': 'priority()',
    '[attr.data-column-overflow]': 'overflow()',
  },
})
export class HlmTableColumn {
  readonly role = input<HlmTableColumnRole>('text', { alias: 'hlmTableColumn' });
  readonly kind = input<HlmTableColumnKind>('name', { alias: 'columnKind' });
  readonly priority = input<HlmTableColumnPriority>('secondary', { alias: 'columnPriority' });
  readonly overflow = input<HlmTableColumnOverflow>('wrap', { alias: 'columnOverflow' });
}
