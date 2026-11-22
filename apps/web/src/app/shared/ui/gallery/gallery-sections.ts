export const GALLERY_SECTIONS = [
  {
    id: 'workspace',
    title: $localize`:@@shared.gallery.sheet.workspace:Workspace and navigation`,
    specimens: [
      { id: 'shell', title: $localize`:@@shared.gallery.sheet.shell:Shell, sidebar and topbar` },
      {
        id: 'breadcrumb',
        title: $localize`:@@shared.gallery.sheet.breadcrumb:Breadcrumb and avatar`,
      },
      { id: 'header', title: $localize`:@@shared.gallery.sheet.header:Page header` },
    ],
  },
  {
    id: 'evidence',
    title: $localize`:@@shared.gallery.sheet.evidence:Queue and evidence`,
    specimens: [
      { id: 'kpi', title: $localize`:@@shared.gallery.sheet.kpi:KPI card` },
      { id: 'queue', title: $localize`:@@shared.gallery.sheet.queue:Queue card, filter and sort` },
      {
        id: 'detail',
        title: $localize`:@@shared.gallery.sheet.detail:Master–detail and action footer`,
      },
      {
        id: 'failure',
        title: $localize`:@@shared.gallery.sheet.failure:Original failure and code diff`,
      },
      { id: 'chart', title: $localize`:@@shared.gallery.sheet.chart:Bar chart` },
      {
        id: 'history',
        title: $localize`:@@shared.gallery.sheet.history:Safety checks and history`,
      },
    ],
  },
  {
    id: 'forms',
    title: $localize`:@@shared.gallery.sheet.forms:Controls and forms`,
    specimens: [
      { id: 'buttons', title: $localize`:@@shared.gallery.sheet.buttons:Buttons` },
      { id: 'badges', title: $localize`:@@shared.gallery.sheet.badges:Badge and status chip` },
      { id: 'fields', title: $localize`:@@shared.gallery.sheet.fields:Input, select and textarea` },
      {
        id: 'choices',
        title: $localize`:@@shared.gallery.sheet.choices:Checkbox, radio and switch`,
      },
      { id: 'dialog', title: $localize`:@@shared.gallery.sheet.dialog:Dialog and popover` },
    ],
  },
  {
    id: 'feedback',
    title: $localize`:@@shared.gallery.sheet.feedback:Data and feedback`,
    specimens: [
      { id: 'table', title: $localize`:@@shared.gallery.sheet.table:Dense table and sparkline` },
      { id: 'tabs', title: $localize`:@@shared.gallery.sheet.tabs:Tabs and segments` },
      { id: 'tooltip', title: $localize`:@@shared.gallery.sheet.tooltip:Tooltip` },
      {
        id: 'notices',
        title: $localize`:@@shared.gallery.sheet.notices:Loading, empty, error and toast`,
      },
      { id: 'pagination', title: $localize`:@@shared.gallery.sheet.pagination:Pagination` },
    ],
  },
] as const;

export type GallerySpecimen = (typeof GALLERY_SECTIONS)[number]['specimens'][number];

export const GALLERY_STATES = [
  { id: 'hover', label: $localize`:@@shared.gallery.sheet.state-hover:Hover` },
  { id: 'focus', label: $localize`:@@shared.gallery.sheet.state-focus:Keyboard focus` },
  { id: 'active', label: $localize`:@@shared.gallery.sheet.state-active:Pressed or selected` },
  { id: 'disabled', label: $localize`:@@shared.gallery.sheet.state-disabled:Read only` },
  { id: 'loading', label: $localize`:@@shared.gallery.sheet.state-loading:Loading` },
  { id: 'empty', label: $localize`:@@shared.gallery.sheet.state-empty:No records` },
  { id: 'error', label: $localize`:@@shared.gallery.sheet.state-error:Request failed` },
  { id: 'success', label: $localize`:@@shared.gallery.sheet.state-success:Saved` },
] as const;
export type GalleryState = 'default' | (typeof GALLERY_STATES)[number]['id'];
