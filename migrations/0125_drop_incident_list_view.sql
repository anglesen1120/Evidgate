-- 0125_drop_incident_list_view.sql
--
-- public.incident_list (0123) is dropped. The role-and-privilege scan (00 §5.4.7) admits an app-readable view only
-- with security_barrier=true, and a barrier view stays a subquery scan, so the keyset page could no longer stream
-- from incident_project_state_list and sorted instead. evidgate_app already holds SELECT on incident and
-- incident_attempt (0122) under FORCE RLS, so the list statements read those tables directly with an explicit
-- org_id predicate and compute attempt_count inline. 0123 is not edited; its index stays.
--
-- LOCKS: DROP VIEW takes ACCESS EXCLUSIVE on the view only; no table is touched. 5 s lock_timeout.
set local lock_timeout = '5s';

drop view if exists public.incident_list;
