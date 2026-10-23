-- 0019 (02.4.1/02.4.2 review): two fixes found by the performance review.
--
-- 1. The purge predicate `expires_at + interval < now()` is not sargable; rewrite it as a comparison of
--    the indexed column against a constant so the ci_token_issue_expires_idx range scan is used.
-- 2. A `last_used_at` touch on an ingest token must not bump `version`: a concurrent rotate/revoke with
--    If-Match would otherwise get a spurious 412 once a minute for every active token.

create or replace function evidgate.purge_expired_ci_token_issue(p_limit integer) returns integer
  language sql
  security definer
  set search_path = ''
  as $$
    with batch as (
      select t.id
        from public.ci_token_issue t
       where t.expires_at < now() - interval '10 minutes'
       order by t.expires_at
       limit least(greatest(p_limit, 0), 10000)
         for update skip locked
    ), removed as (
      delete from public.ci_token_issue t
       using batch b
       where t.id = b.id
      returning t.id
    )
    select count(*)::integer from removed
  $$;

-- The shared set_updated_at_and_version trigger stays (the schema convention requires it); a second trigger,
-- which fires after it (triggers run in name order), undoes the bump when only last_used_at changed.
create or replace function public.ingest_token_keep_version_on_touch() returns trigger
  language plpgsql
  as $$
  declare
    probe public.ingest_token := new;
  begin
    probe.last_used_at := old.last_used_at;
    probe.updated_at := old.updated_at;
    probe.version := old.version;
    if probe is not distinct from old then
      new.updated_at := old.updated_at;
      new.version := old.version;
    end if;

    return new;
  end
  $$;

revoke all on function public.ingest_token_keep_version_on_touch() from public;
grant execute on function public.ingest_token_keep_version_on_touch() to evidgate_app;

drop trigger if exists ingest_token_touch_keeps_version on public.ingest_token;
create trigger ingest_token_touch_keeps_version
  before update on public.ingest_token
  for each row
  execute function public.ingest_token_keep_version_on_touch();
