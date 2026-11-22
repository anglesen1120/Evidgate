import { GALLERY_COPY } from './gallery-example-copy';

export interface GalleryMember {
  readonly email: string;
  readonly name: string;
  readonly initials: string;
  readonly status: 'active' | 'pending';
  readonly projects: number;
  readonly reviewer: boolean;
}

export const SAMPLE_MEMBERS: readonly GalleryMember[] = [
  {
    email: 'linh@example.test',
    name: 'Linh Nguyễn',
    initials: 'LN',
    status: 'active',
    projects: 3,
    reviewer: false,
  },
  {
    email: 'minh@example.test',
    name: 'Minh Trần',
    initials: 'MT',
    status: 'pending',
    projects: 0,
    reviewer: true,
  },
  {
    email: 'an@example.test',
    name: 'An Lê',
    initials: 'AL',
    status: 'active',
    projects: 1,
    reviewer: true,
  },
];

export const MEMBER_COPY = {
  title: $localize`:@@shared.gallery.members.title:Members and roles`,
  specimen: $localize`:@@shared.gallery.members.specimen:Sample data. Changes apply only to this example.`,
  caption: $localize`:@@shared.gallery.members.caption:Sample members`,
  email: $localize`:@@shared.gallery.members.email:Member email`,
  status: $localize`:@@shared.gallery.roles.status:Status`,
  roles: $localize`:@@shared.gallery.members.roles:Roles`,
  projects: $localize`:@@shared.gallery.members.projects:Projects`,
  actions: $localize`:@@shared.gallery.members.actions:Actions`,
  active: $localize`:@@shared.gallery.members.active:Active`,
  pending: $localize`:@@shared.gallery.members.pending:Invite pending`,
  all: $localize`:@@shared.gallery.members.all:All`,
  viewer: $localize`:@@shared.gallery.members.viewer:Viewer`,
  reviewer: GALLERY_COPY.reviewer,
  view: $localize`:@@shared.gallery.members.view:View`,
  edit: $localize`:@@shared.gallery.members.edit:Edit`,
  viewTitle: $localize`:@@shared.gallery.members.details:Member details`,
  editTitle: $localize`:@@shared.gallery.members.edit-roles:Edit organization roles`,
  save: $localize`:@@shared.gallery.members.save:Save roles`,
  keep: $localize`:@@shared.gallery.members.keep:Keep editing`,
  discard: $localize`:@@shared.gallery.members.discard:Discard changes`,
  discardTitle: $localize`:@@shared.gallery.members.confirm:Discard unsaved changes?`,
  discardHint: $localize`:@@shared.gallery.members.discard-hint:The sample member will keep their previous roles.`,
  close: GALLERY_COPY.close,
  cancel: GALLERY_COPY.cancel,
  narrow: $localize`:@@shared.gallery.members.narrow:View only on this screen. Use a desktop to edit roles.`,
} as const;
