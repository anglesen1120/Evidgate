import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmTableImports } from '@evidgate/ui/table';
import { OutcomePillComponent } from '../verdict';
import { GALLERY_COPY } from './gallery-example-copy';

@Component({
  selector: 'app-gallery-evidence-table',
  imports: [HlmTableImports, OutcomePillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <div hlmTableContainer>
      <table hlmTable>
        <caption hlmCaption>
          {{
            copy.evidenceLabel
          }}
        </caption>
        <thead hlmTHead>
          <tr hlmTr>
            <th hlmTh scope="col">{{ copy.run }}</th>
            <th hlmTh scope="col">{{ copy.outcome }}</th>
            <th hlmTh scope="col">{{ copy.duration }}</th>
          </tr>
        </thead>
        <tbody hlmTBody>
          @if (unavailableMessage(); as message) {
            <tr hlmTr>
              <td hlmTd colspan="3">{{ message }}</td>
            </tr>
          } @else {
            <tr hlmTr>
              <td hlmTd>{{ run }}</td>
              <td hlmTd><app-outcome-pill [outcome]="'has_failures'" [compact]="true" /></td>
              <td hlmTd>
                <svg
                  class="trend h-(--eg-space-5) w-(--eg-space-9) text-(--eg-accent)"
                  viewBox="0 0 40 16"
                  aria-hidden="true"
                  focusable="false"
                >
                  <polyline
                    [attr.points]="'0,14 14,11 25,12 39,2'"
                    fill="none"
                    stroke="currentColor"
                  />
                </svg>
                <span>{{ copy.trend }}</span>
              </td>
            </tr>
            <tr hlmTr>
              <td hlmTd>{{ unavailableRun }}</td>
              <td hlmTd>{{ copy.unavailable }}</td>
              <td hlmTd>—</td>
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class GalleryEvidenceTableComponent {
  readonly unavailableMessage = input.required<string | null>();
  protected readonly copy = GALLERY_COPY;
  protected readonly run = 1842;
  protected readonly unavailableRun = 1843;
}
