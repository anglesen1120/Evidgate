-- 0125_incident_list_security_barrier.sql
--
-- public.incident_list (0123) is readable by evidgate_app, and the role-and-privilege scan (00 §5.4.7) admits an
-- app-readable view only when it is allow-listed and carries security_barrier=true, so a caller-supplied predicate
-- cannot be pushed below the view's own filters. 0123 set only security_invoker; this adds the barrier and keeps
-- security_invoker, so FORCE RLS on incident and incident_attempt still applies to the caller.
--
-- LOCKS: ALTER VIEW takes ACCESS EXCLUSIVE on the view only (no data rewrite); the view is read by one list route.
set local lock_timeout = '5s';

alter view public.incident_list set (security_barrier = true);
