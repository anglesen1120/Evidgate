import { HeaderActionsDirective } from './header-actions.directive';
import { PageHeaderComponent } from './page-header.component';
import { SectionHeaderComponent } from './section-header.component';

export * from './header-actions.directive';
export * from './page-header.component';
export * from './section-header.component';

export const PageHeaderImports = [PageHeaderComponent, HeaderActionsDirective] as const;
export const SectionHeaderImports = [SectionHeaderComponent, HeaderActionsDirective] as const;
