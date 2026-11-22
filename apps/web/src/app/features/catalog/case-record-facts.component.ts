import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import type { CaseDto } from '@evidgate/sdk';
import { AbsoluteTimeComponent } from '../../shared/time/absolute-time.component';
import { CopyableIdentifierComponent } from '../../shared/ui/values/copyable-identifier.component';
import { escapeFormatChars } from '../../shared/ui/values/ref-name-display';
import { CATALOG_RECORD } from './case-record.copy';

@Component({
  selector: 'app-case-record-facts',
  imports: [AbsoluteTimeComponent, CopyableIdentifierComponent, DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `@for (group of groups(); track group.label) {
    <h3 class="mt-(--eg-space-4) mb-(--eg-space-2) font-semibold">{{ group.label }}</h3>
    <dl
      class="m-0 grid grid-cols-1 items-baseline gap-x-(--eg-space-5) gap-y-(--eg-space-2) min-[600px]:grid-cols-[minmax(0,1fr)_minmax(0,1.8fr)]"
    >
      @for (fact of group.facts; track fact.key) {
        <dt class="text-muted-foreground">{{ fact.label }}</dt>
        <dd class="m-0 min-w-0 wrap-anywhere">
          @if (fact.value === null || fact.value === undefined || fact.value === '') {
            {{ copy.missingValue }}
          } @else if (fact.kind === 'time') {
            <app-absolute-time [value]="asText(fact.value)" />
          } @else if (fact.kind === 'id') {
            <app-copyable-identifier [value]="asText(fact.value)" [label]="fact.label" />
          } @else if (typeof fact.value === 'number') {
            <span class="block text-end tabular-nums">{{ fact.value | number }}</span>
          } @else {
            <bdi>{{ display(fact.value) }}</bdi>
          }
        </dd>
      }
    </dl>
  }`,
})
export class CaseRecordFactsComponent {
  readonly row = input.required<CaseDto>();
  readonly ownerName = input.required<string | null>();
  protected readonly copy = CATALOG_RECORD;
  protected readonly display = escapeFormatChars;
  protected readonly asText = String;
  protected readonly facts = computed(() => {
    const r = this.row();
    return [
      { key: 'name', label: this.copy.name, value: r.name },
      { key: 'id', label: this.copy.id, value: r.id, kind: 'id' },
      { key: 'annotation', label: this.copy.annotation, value: r.annotation_id, kind: 'id' },
      { key: 'org', label: this.copy.org, value: r.org_id, kind: 'id' },
      { key: 'project', label: this.copy.project, value: r.project_id, kind: 'id' },
      { key: 'framework', label: this.copy.framework, value: r.framework },
      { key: 'language', label: this.copy.language, value: r.language },
      { key: 'path', label: this.copy.path, value: r.file_path },
      { key: 'owner', label: this.copy.owner, value: this.ownerName() },
      { key: 'ownerId', label: this.copy.ownerId, value: r.owner_membership_id, kind: 'id' },
      { key: 'labels', label: this.copy.labels, value: r.labels.join(', ') },
      { key: 'requirement', label: this.copy.requirement, value: r.requirement_ref },
      { key: 'first', label: this.copy.first, value: r.first_seen_commit, kind: 'id' },
      { key: 'last', label: this.copy.last, value: r.last_seen_commit, kind: 'id' },
      { key: 'created', label: this.copy.created, value: r.created_at, kind: 'time' },
      { key: 'updated', label: this.copy.updated, value: r.updated_at, kind: 'time' },
      { key: 'version', label: this.copy.version, value: r.version },
    ];
  });
  protected readonly groups = computed(() => {
    const facts = this.facts();
    const history = facts.findIndex((fact) => fact.key === 'first');
    return [
      { label: this.copy.summary, facts: facts.slice(0, history) },
      { label: this.copy.history, facts: facts.slice(history) },
    ];
  });
}
