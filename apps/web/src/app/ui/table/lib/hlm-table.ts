import { Directive } from '@angular/core';
import { classes } from '@evidgate/ui/utils';

@Directive({
  selector: 'div[hlmTableContainer]',
  exportAs: 'hlmTableContainer',
  host: { 'data-slot': 'table-container' },
})
export class HlmTableContainer {
  constructor() {
    classes(() => 'spartan-table-container');
  }
}

@Directive({
  selector: 'table[hlmTable]',
  exportAs: 'hlmTable',
  host: { 'data-slot': 'table' },
})
export class HlmTable {
  constructor() {
    classes(() => 'spartan-table');
  }
}

@Directive({
  selector: 'thead[hlmTHead],thead[hlmTableHeader]',
  exportAs: 'hlmTHead',
  host: { 'data-slot': 'table-header' },
})
export class HlmTHead {
  constructor() {
    classes(() => 'spartan-table-header');
  }
}

@Directive({
  selector: 'tbody[hlmTBody],tbody[hlmTableBody]',
  exportAs: 'hlmTBody',
  host: { 'data-slot': 'table-body' },
})
export class HlmTBody {
  constructor() {
    classes(() => 'spartan-table-body');
  }
}

@Directive({
  selector: 'tfoot[hlmTFoot],tfoot[hlmTableFooter]',
  exportAs: 'hlmTFoot',
  host: { 'data-slot': 'table-footer' },
})
export class HlmTFoot {
  constructor() {
    classes(() => 'spartan-table-footer');
  }
}

@Directive({
  selector: 'tr[hlmTr],tr[hlmTableRow]',
  exportAs: 'hlmTr',
  host: { 'data-slot': 'table-row' },
})
export class HlmTr {
  constructor() {
    classes(() => 'spartan-table-row has-aria-expanded:bg-muted/50');
  }
}

@Directive({
  selector: 'th[hlmTh],th[hlmTableHead]',
  exportAs: 'hlmTh',
  host: { 'data-slot': 'table-head' },
})
export class HlmTh {
  constructor() {
    classes(() => 'spartan-table-head');
  }
}

@Directive({
  selector: 'td[hlmTd],td[hlmTableCell]',
  exportAs: 'hlmTd',
  host: { 'data-slot': 'table-cell' },
})
export class HlmTd {
  constructor() {
    classes(() => 'spartan-table-cell');
  }
}

@Directive({
  selector: 'caption[hlmCaption],caption[hlmTableCaption]',
  exportAs: 'hlmCaption',
  host: { 'data-slot': 'table-caption' },
})
export class HlmCaption {
  constructor() {
    classes(() => 'spartan-table-caption');
  }
}
