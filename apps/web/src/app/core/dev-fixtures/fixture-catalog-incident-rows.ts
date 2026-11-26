// The healing half of the catalog: the incident list and read the triage screens will call.

import incidentDetail from '../../../../../../fixtures/web-api/incident-detail/input.json';
import incidentsList from '../../../../../../fixtures/web-api/incidents-list/input.json';
import incidentsListEmpty from '../../../../../../fixtures/web-api/incidents-list-empty/input.json';

import type { FixtureCatalogRow } from './fixture-document';

export const FIXTURE_CATALOG_INCIDENT_ROWS: readonly FixtureCatalogRow[] = [
  { name: 'incident-detail', document: incidentDetail },
  { name: 'incidents-list', document: incidentsList },
  { name: 'incidents-list-empty', document: incidentsListEmpty },
];
