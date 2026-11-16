import { ChangeDetectionStrategy, Component, input } from '@angular/core';

// Static Lucide geometry avoids the string-to-innerHTML sink used by ng-icons.
@Component({
  selector: 'hlm-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'aria-hidden': 'true', class: 'inline-flex shrink-0' },
  template: `
    <svg
      [attr.viewBox]="'0 0 24 24'"
      [attr.fill]="'none'"
      [attr.stroke]="'currentColor'"
      [attr.stroke-width]="2"
      [attr.stroke-linecap]="'round'"
      [attr.stroke-linejoin]="'round'"
      [attr.focusable]="'false'"
      class="size-full"
    >
      @switch (name()) {
        @case ('lucideActivity') {
          <path
            [attr.d]="'M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2'"
          ></path>
        }
        @case ('lucideCheck') {
          <path [attr.d]="'M20 6 9 17l-5-5'"></path>
        }
        @case ('lucideChevronDown') {
          <path [attr.d]="'m6 9 6 6 6-6'"></path>
        }
        @case ('lucideChevronLeft') {
          <path [attr.d]="'m15 18-6-6 6-6'"></path>
        }
        @case ('lucideChevronRight') {
          <path [attr.d]="'m9 18 6-6-6-6'"></path>
        }
        @case ('lucideChevronUp') {
          <path [attr.d]="'m18 15-6-6-6 6'"></path>
        }
        @case ('lucideCircle') {
          <circle [attr.cx]="'12'" [attr.cy]="'12'" [attr.r]="'10'"></circle>
        }
        @case ('lucideCircleCheck') {
          <circle [attr.cx]="'12'" [attr.cy]="'12'" [attr.r]="'10'"></circle>
          <path [attr.d]="'m9 12 2 2 4-4'"></path>
        }
        @case ('lucideClock') {
          <path [attr.d]="'M12 6v6l4 2'"></path>
          <circle [attr.cx]="'12'" [attr.cy]="'12'" [attr.r]="'10'"></circle>
        }
        @case ('lucideCopy') {
          <rect
            [attr.width]="14"
            [attr.height]="14"
            [attr.x]="8"
            [attr.y]="8"
            [attr.rx]="2"
            [attr.ry]="2"
          ></rect>
          <path [attr.d]="'M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2'"></path>
        }
        @case ('lucideInfo') {
          <circle [attr.cx]="'12'" [attr.cy]="'12'" [attr.r]="'10'"></circle>
          <path [attr.d]="'M12 16v-4'"></path>
          <path [attr.d]="'M12 8h.01'"></path>
        }
        @case ('lucideLoader2') {
          <path [attr.d]="'M21 12a9 9 0 1 1-6.219-8.56'"></path>
        }
        @case ('lucideMaximize2') {
          <path [attr.d]="'M15 3h6v6'"></path>
          <path [attr.d]="'m21 3-7 7'"></path>
          <path [attr.d]="'m3 21 7-7'"></path>
          <path [attr.d]="'M9 21H3v-6'"></path>
        }
        @case ('lucideOctagonX') {
          <path [attr.d]="'m15 9-6 6'"></path>
          <path
            [attr.d]="'M2.586 16.726A2 2 0 0 1 2 15.312V8.688a2 2 0 0 1 .586-1.414l4.688-4.688A2 2 0 0 1 8.688 2h6.624a2 2 0 0 1 1.414.586l4.688 4.688A2 2 0 0 1 22 8.688v6.624a2 2 0 0 1-.586 1.414l-4.688 4.688a2 2 0 0 1-1.414.586H8.688a2 2 0 0 1-1.414-.586z'"
          ></path>
          <path [attr.d]="'m9 9 6 6'"></path>
        }
        @case ('lucideTriangleAlert') {
          <path
            [attr.d]="'m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3'"
          ></path>
          <path [attr.d]="'M12 9v4'"></path>
          <path [attr.d]="'M12 17h.01'"></path>
        }
        @case ('lucideX') {
          <path [attr.d]="'M18 6 6 18'"></path>
          <path [attr.d]="'m6 6 12 12'"></path>
        }
      }
    </svg>
  `,
})
export class HlmIcon {
  readonly name = input.required<
    | 'lucideActivity'
    | 'lucideCheck'
    | 'lucideChevronDown'
    | 'lucideChevronLeft'
    | 'lucideChevronRight'
    | 'lucideChevronUp'
    | 'lucideCircle'
    | 'lucideCircleCheck'
    | 'lucideClock'
    | 'lucideCopy'
    | 'lucideInfo'
    | 'lucideLoader2'
    | 'lucideMaximize2'
    | 'lucideOctagonX'
    | 'lucideTriangleAlert'
    | 'lucideX'
  >();
}
