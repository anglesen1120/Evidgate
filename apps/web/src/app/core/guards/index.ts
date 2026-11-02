// 09.1.1-T4.1 (plan 09.1.1 §10 T4, §5): the guard surface the shell's route table composes. One
// module, because a route table should read as a list of decisions (`canMatch: [authGuard,
// permissionGuard('run:read')]`) and not as a set of import paths.
//
// Every guard here returns `boolean | UrlTree` (async where it awaits the session); none returns
// `false` for a refusal it could redirect on, and the scope guards return `false` only for an id
// the server did not list, so the wildcard route renders the 404 pattern (D-S68-Q4).

export { attemptedPath, authGuard, signInTree } from './auth.guard';
export { permissionGuard } from './permission.guard';
export { phaseGuard } from './phase.guard';
export { orgScopeGuard, projectScopeGuard } from './scope.guard';
export { landingRedirectGuard, orgLandingGuard } from './landing.guard';
