import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmTableImports } from '@evidgate/ui/table';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { LINEAGE_COPY } from './knowledge-lineage.copy';
import {
  dependencySnapshot,
  dependents,
  revoked,
  type LineageState,
} from './knowledge-lineage.fixture';

@Component({
  selector: 'app-lineage-dependents',
  imports: [HlmCardImports, HlmTableImports, StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <section hlmCard [size]="'sm'">
      <header hlmCardHeader class="border-b border-border pb-4">
        <h2 hlmCardTitle id="lineage-dependents">{{ c.dependentList }}</h2>
      </header>
      <div hlmCardContent>
        @switch (snapshot()) {
          @case ('empty') {
            <p>{{ c.none }}</p>
          }
          @case ('unknown') {
            <p>{{ c.unavailable }}</p>
          }
          @case ('known') {
            <div
              hlmTableContainer
              role="region"
              aria-labelledby="lineage-dependents"
              tabindex="0"
              class="max-w-full overflow-x-auto"
            >
              <table hlmTable>
                <caption hlmCaption class="sr-only">
                  {{
                    c.dependentList
                  }}
                </caption>
                <thead hlmTHead>
                  <tr hlmTr>
                    <th hlmTh scope="col">{{ c.candidate }}</th>
                    <th hlmTh scope="col">{{ c.caseLabel }}</th>
                    <th hlmTh scope="col">{{ c.context }}</th>
                    <th hlmTh scope="col">{{ c.stateLabel }}</th>
                  </tr>
                </thead>
                <tbody hlmTBody>
                  @for (row of rows; track row.id) {
                    <tr hlmTr>
                      <th hlmTh scope="row">
                        <strong class="block text-sm font-semibold"
                          ><bdi>{{ row.id }}</bdi></strong
                        >
                        <code class="text-xs font-normal text-muted-foreground">{{
                          row.hash
                        }}</code>
                      </th>
                      <td hlmTd>{{ row.case }}</td>
                      <td hlmTd>{{ row.context }}</td>
                      <td hlmTd>
                        <app-status-pill
                          [tone]="row.status === 'applied' ? 'info' : 'neutral'"
                          [label]="blocked() && row.status !== 'applied' ? c.flagged : row.status"
                        />
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        }
      </div>
    </section>
  `,
})
export class LineageDependentsComponent {
  readonly state = input.required<LineageState>();
  protected readonly c = LINEAGE_COPY;
  protected readonly rows = dependents;
  protected readonly snapshot = computed(() => dependencySnapshot(this.state()));
  protected readonly blocked = computed(() => revoked(this.state()));
}
