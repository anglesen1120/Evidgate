-- Restates the ACL of the expired ci_token_issue sweep. 0017 granted it to the schedule role alone, but 0019
-- replaced the body with `create or replace` and restated no ACL, so the live grant still comes from 0017 and
-- nothing in the replacing file says who may call a function that deletes across every tenant. This file
-- states it again and changes no body: PUBLIC and every runtime role lose EXECUTE, then the schedule role gets it.
--
-- `revoke` and `grant` are idempotent, so a re-run is a no-op. The brief function lock is bounded by lock_timeout.

set local lock_timeout = '5s';

revoke all on function evidgate.purge_expired_ci_token_issue(integer) from public;
revoke all on function evidgate.purge_expired_ci_token_issue(integer) from evidgate_app;
revoke all on function evidgate.purge_expired_ci_token_issue(integer) from evidgate_operator;
revoke all on function evidgate.purge_expired_ci_token_issue(integer) from evidgate_worker_sched;
grant execute on function evidgate.purge_expired_ci_token_issue(integer) to evidgate_worker_sched;
