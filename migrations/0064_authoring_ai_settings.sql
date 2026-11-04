-- 0064_authoring_ai_settings.sql
--
-- WHY: a project's AI guidance decides what every generated manual case and every generated
-- script is written from, and what a generation costs. It must therefore be reviewable and
-- reproducible: each save appends one immutable version addressed by a content hash, so a
-- result produced yesterday can still name the document it was produced from after the
-- guidance is edited today. Nothing is ever overwritten in place.
--
-- SHAPE: `project_ai_settings` holds one row per saved version (`version_no` 1, 2, …), keyed
-- `(org_id, project_id, version_no)`; the four priority criteria are four constrained text
-- columns and the wire shape folds them into one closed object. `ai_settings_template` is the
-- platform catalogue of read-only, versioned starting documents in vi and en. Both tables keep
-- `created_at` only, because neither is ever updated.
--
-- The four plan-level limits (writing_style 250, coverage_strategy 1500, exclusion_rules 1500,
-- step_detail 200) are contractual; the description (1000), URL (2048) and per-criterion (250)
-- caps are shipped defaults an owner may tune by migration. `char_length` counts code points,
-- the same unit as the server-side counter. Scheme, userinfo and secret-shaped parts of
-- `environment_url` cannot be expressed faithfully as a CHECK and are enforced by the shared
-- URL rule before any statement runs.
--
-- CANONICAL DOCUMENT: `content_hash` is `sha256:` over the RFC 8785 canonical JSON of exactly
-- eight members — coverage_strategy, description, environment_url, exclusion_rules, languages,
-- priority_criteria {critical, high, medium, low}, step_detail, writing_style. `languages` is
-- stored sorted ascending with no duplicates, so the hash never depends on the order the boxes
-- were ticked. There is deliberately no unique constraint on `content_hash`: a revert must be
-- expressible as a new version carrying an older document.
--
-- RLS: ENABLE + FORCE and an explicit policy per role on each table. `project_ai_settings`
-- carries the tenant policy and a permissive `evidgate_owner` policy; `ai_settings_template` has
-- no `org_id`, so it carries a read-everything policy plus one denial per write command for the
-- app role, and the same owner policy. FORCE applies to the table owner too, which is why the
-- owner policy is load-bearing for the seed below and for later owner-only sweeps.
--
-- APPEND-ONLY: one guard trigger on `project_ai_settings` refuses UPDATE for every role and
-- DELETE for every role except `evidgate_owner`, with 23514. UPDATE has no exception because
-- history is never rewritten; the DELETE exception is what lets the owner's SECURITY DEFINER
-- retention and project-deletion sweeps remove a project's settings at all.
--
-- Idempotent in the shape the migration contract allows: `create table if not exists`,
-- `create … if not exists`, `drop … if exists`, `create or replace`, and seed inserts that
-- conflict on their natural key.

set local lock_timeout = '5s';

create table if not exists public.project_ai_settings (
  id uuid not null,
  org_id uuid not null,
  project_id uuid not null,
  version_no integer not null,
  description text not null default '',
  environment_url text,
  languages text[] not null,
  writing_style text not null default '',
  coverage_strategy text not null default '',
  priority_criterion_critical text not null default '',
  priority_criterion_high text not null default '',
  priority_criterion_medium text not null default '',
  priority_criterion_low text not null default '',
  exclusion_rules text not null default '',
  step_detail text not null default '',
  template_code text,
  template_version integer,
  content_hash text not null,
  author_membership_id uuid not null,
  reason text,
  created_at timestamptz not null default now(),
  constraint project_ai_settings_pkey primary key (id),
  constraint project_ai_settings_org_id_key unique (org_id, id),
  constraint project_ai_settings_version_key unique (org_id, project_id, version_no),
  constraint project_ai_settings_project_fk
    foreign key (org_id, project_id) references public.project (org_id, id),
  constraint project_ai_settings_author_fk
    foreign key (org_id, author_membership_id) references public.membership (org_id, id),
  constraint project_ai_settings_version_check check (version_no >= 1),
  constraint project_ai_settings_languages_check
    check (cardinality(languages) between 1 and 2 and languages <@ array['vi', 'en']::text[]),
  constraint project_ai_settings_description_check check (char_length(description) <= 1000),
  constraint project_ai_settings_url_check check (environment_url is null
    or char_length(environment_url) <= 2048),
  constraint project_ai_settings_writing_style_check check (char_length(writing_style) <= 250),
  constraint project_ai_settings_coverage_check check (char_length(coverage_strategy) <= 1500),
  constraint project_ai_settings_exclusions_check check (char_length(exclusion_rules) <= 1500),
  constraint project_ai_settings_step_detail_check check (char_length(step_detail) <= 200),
  constraint project_ai_settings_criteria_check check (
    char_length(priority_criterion_critical) <= 250 and char_length(priority_criterion_high) <= 250
    and char_length(priority_criterion_medium) <= 250 and char_length(priority_criterion_low) <= 250),
  -- Both members are written together or not at all. `is not null` is load-bearing: without it a
  -- half-null pair yields UNKNOWN (`null ~ pattern`, `null >= 1`) and PostgreSQL accepts UNKNOWN.
  constraint project_ai_settings_template_check check (
    (template_code is null and template_version is null)
    or (template_code is not null and template_version is not null
        and template_code ~ '^[a-z][a-z0-9_-]{1,31}$' and template_version >= 1)),
  constraint project_ai_settings_hash_check check (content_hash ~ '^sha256:[0-9a-f]{64}$'),
  constraint project_ai_settings_reason_check check (reason is null or char_length(reason) between 1 and 500)
);

-- Both reads of the table scan the same way: the head lookup takes the newest `version_no` of
-- one project, the timeline takes `(created_at, id)` newest first. The version key index above
-- serves the duplicate guard, not these scans.
create index if not exists project_ai_settings_head_idx
  on public.project_ai_settings (org_id, project_id, version_no desc);
create index if not exists project_ai_settings_list_idx
  on public.project_ai_settings (org_id, project_id, created_at desc, id desc);

create table if not exists public.ai_settings_template (
  id uuid not null,
  code text not null,
  locale text not null,
  version integer not null,
  name text not null,
  content jsonb not null,
  content_hash text not null,
  is_default boolean not null default false,
  created_at timestamptz not null default now(),
  constraint ai_settings_template_pkey primary key (id),
  constraint ai_settings_template_key unique (code, locale, version),
  constraint ai_settings_template_locale_check check (locale in ('vi', 'en')),
  constraint ai_settings_template_code_check check (code ~ '^[a-z][a-z0-9_-]{1,31}$'),
  constraint ai_settings_template_version_check check (version >= 1),
  constraint ai_settings_template_name_check check (char_length(name) between 1 and 120),
  constraint ai_settings_template_content_check
    check (jsonb_typeof(content) = 'object' and octet_length(content::text) <= 32768),
  constraint ai_settings_template_hash_check check (content_hash ~ '^sha256:[0-9a-f]{64}$')
);

-- Exactly one default per locale, enforced rather than merely asserted by the seed.
create unique index if not exists ai_settings_template_default_idx
  on public.ai_settings_template (locale) where is_default;

-- Append-only guard: history is never rewritten, and only the owner role may remove a version
-- (retention and project deletion run as owner SECURITY DEFINER functions).
create or replace function evidgate.authoring_settings_immutable() returns trigger
  language plpgsql
  as $$
  begin
    if tg_op = 'UPDATE' then
      raise exception 'settings_immutable: a project AI settings version is append-only (%)', tg_op
        using errcode = '23514';
    end if;
    if current_user <> 'evidgate_owner' then
      raise exception 'settings_immutable: only the owner role may delete a settings version (%)', tg_op
        using errcode = '23514';
    end if;
    return old;
  end
  $$;

revoke all on function evidgate.authoring_settings_immutable() from public;

drop trigger if exists project_ai_settings_immutable on public.project_ai_settings;
create trigger project_ai_settings_immutable
  before update or delete on public.project_ai_settings
  for each row execute function evidgate.authoring_settings_immutable();

alter table public.project_ai_settings enable row level security;
alter table public.project_ai_settings force row level security;

drop policy if exists project_ai_settings_app_tenant on public.project_ai_settings;
create policy project_ai_settings_app_tenant on public.project_ai_settings
  for all
  to evidgate_app
  using (org_id = evidgate.current_org_id())
  with check (org_id = evidgate.current_org_id());

drop policy if exists project_ai_settings_owner_backfill on public.project_ai_settings;
create policy project_ai_settings_owner_backfill on public.project_ai_settings
  for all
  to evidgate_owner
  using (true)
  with check (true);

grant select, insert on public.project_ai_settings to evidgate_app;
revoke update, delete on public.project_ai_settings from evidgate_app;
grant select on public.project_ai_settings to evidgate_operator;
revoke all on public.project_ai_settings from evidgate_worker_sched;

alter table public.ai_settings_template enable row level security;
alter table public.ai_settings_template force row level security;

drop policy if exists ai_settings_template_app_read on public.ai_settings_template;
create policy ai_settings_template_app_read on public.ai_settings_template
  for select
  to evidgate_app
  using (true);

-- One denial per write command: a `for all … using (true) with check (false)` policy would
-- leave DELETE permitted, because its USING clause also governs DELETE.
drop policy if exists ai_settings_template_app_insert_denied on public.ai_settings_template;
create policy ai_settings_template_app_insert_denied on public.ai_settings_template
  for insert
  to evidgate_app
  with check (false);

drop policy if exists ai_settings_template_app_update_denied on public.ai_settings_template;
create policy ai_settings_template_app_update_denied on public.ai_settings_template
  for update
  to evidgate_app
  using (false)
  with check (false);

drop policy if exists ai_settings_template_app_delete_denied on public.ai_settings_template;
create policy ai_settings_template_app_delete_denied on public.ai_settings_template
  for delete
  to evidgate_app
  using (false);

drop policy if exists ai_settings_template_owner_backfill on public.ai_settings_template;
create policy ai_settings_template_owner_backfill on public.ai_settings_template
  for all
  to evidgate_owner
  using (true)
  with check (true);

grant select on public.ai_settings_template to evidgate_app;
revoke insert, update, delete on public.ai_settings_template from evidgate_app;
grant select on public.ai_settings_template to evidgate_operator;
revoke all on public.ai_settings_template from evidgate_worker_sched;

-- Seeds: two codes in two locales, version 1, one default per locale. These rows are immutable —
-- a correction is a new version row in a later migration, never an update. Each `content_hash`
-- is the digest the application's canonical hasher produces for the same document, so the
-- default settings reference and the catalogue agree.
insert into public.ai_settings_template
  (id, code, locale, version, name, content, content_hash, is_default)
values
  ('01926b1c-4a00-7000-8000-000000000001', 'default', 'vi', 1,
   'Hướng dẫn mặc định (Tiếng Việt)',
   $json${
     "description": "",
     "environment_url": null,
     "languages": ["en", "vi"],
     "writing_style": "",
     "coverage_strategy": "",
     "priority_criteria": {"critical": "", "high": "", "medium": "", "low": ""},
     "exclusion_rules": "",
     "step_detail": ""
   }$json$::jsonb,
   'sha256:41ad97f6ed1a4ac002ad6227e47600023b2cb99b879c91ba91fe9f0cfad658b0',
   true),
  ('01926b1c-4a00-7000-8000-000000000002', 'default', 'en', 1,
   'Default guidance (English)',
   $json${
     "description": "",
     "environment_url": null,
     "languages": ["en", "vi"],
     "writing_style": "",
     "coverage_strategy": "",
     "priority_criteria": {"critical": "", "high": "", "medium": "", "low": ""},
     "exclusion_rules": "",
     "step_detail": ""
   }$json$::jsonb,
   'sha256:41ad97f6ed1a4ac002ad6227e47600023b2cb99b879c91ba91fe9f0cfad658b0',
   true),
  ('01926b1c-4a00-7000-8000-000000000003', 'web-form', 'vi', 1,
   'Biểu mẫu web (Tiếng Việt)',
   $json${
     "description": "Hướng dẫn viết test case thủ công cho luồng biểu mẫu trên web: cần bao phủ gì, diễn đạt một bước thế nào và điều gì không được tự động hoá.",
     "environment_url": null,
     "languages": ["en", "vi"],
     "writing_style": "Mỗi bước là một hành động ở thể mệnh lệnh, gọi tên trường theo nhãn hiển thị và nêu kết quả mong đợi mà người kiểm thử nhìn thấy được.",
     "coverage_strategy": "Với mỗi biểu mẫu, bao phủ luồng thành công với dữ liệu hợp lệ tối thiểu; từng trường bắt buộc khi để trống; giá trị biên ở độ dài nhỏ nhất và lớn nhất; phần kiểm tra phía máy chủ mà trình duyệt không tái hiện được; phân quyền, nghĩa là thành viên không có quyền bị từ chối và thành viên của tổ chức khác không truy cập được bản ghi; và mọi hành động phá huỷ, gồm hộp xác nhận, đường huỷ và trạng thái còn lại khi thao tác thất bại.",
     "priority_criteria": {
       "critical": "Lỗi chặn phát hành: mất dữ liệu, đọc hoặc ghi chéo tổ chức, đăng nhập hỏng, hoặc thanh toán bị tính hai lần.",
       "high": "Luồng chính sai hoặc thiếu kiểm tra phân quyền, nhưng vẫn có cách xử lý khác.",
       "medium": "Luồng phụ, thông báo kiểm tra dữ liệu hoặc trạng thái rỗng hiển thị sai.",
       "low": "Câu chữ, khoảng cách hoặc khác biệt giao diện không đổi hành vi."
     },
     "exclusion_rules": "Không tự động hoá hành động xoá dữ liệu mà đội không thể khôi phục: xoá cơ sở dữ liệu sản xuất, xoá dự án sản xuất, thu hồi toàn bộ quản trị viên của tổ chức, hoặc xoay token ingest đang dùng ngoài tổ chức kiểm thử. Hãy kiểm thử thủ công trong môi trường dùng một lần, hoặc không kiểm thử.",
     "step_detail": "Mỗi bước một hành động và một kết quả mong đợi; chỉ thêm dữ liệu kiểm thử khi giá trị đó quan trọng."
   }$json$::jsonb,
   'sha256:61f3c11e154d72fb7550391d23b4426b306800595b8c268a6aa9d3a40b126927',
   false),
  ('01926b1c-4a00-7000-8000-000000000004', 'web-form', 'en', 1,
   'Web form (English)',
   $json${
     "description": "Guidance for manual test cases and generated scripts on the web form flow: what to cover, how to word a step and what must never be automated.",
     "environment_url": null,
     "languages": ["en", "vi"],
     "writing_style": "Write each step as one action in the imperative, name the control by its visible label, and state the expected result as something a tester can see.",
     "coverage_strategy": "For every form, cover the happy path with the smallest valid input; each required field left empty; boundary values at the minimum and maximum length; the server-side validation a browser cannot reproduce; authorization, meaning a member without the permission is refused and a member of another tenant cannot reach the record; and every destructive action, including its confirmation, its cancel path and the state left behind when it fails.",
     "priority_criteria": {
       "critical": "A failure blocks the release: data loss, a cross-tenant read or write, a broken sign-in, or a payment charged twice.",
       "high": "The main path is wrong or a permission check is missing, but a workaround exists.",
       "medium": "A secondary path, a validation message or an empty state is wrong.",
       "low": "Wording, spacing or a cosmetic difference that changes no behaviour."
     },
     "exclusion_rules": "Do not automate an action that destroys data a team cannot restore: dropping a production database, deleting a production project, revoking every administrator of an org, or rotating a live ingest token outside a test tenant. Test those manually in a disposable environment, or not at all.",
     "step_detail": "One action and one expected result per step; add test data only when the value matters."
   }$json$::jsonb,
   'sha256:ebdf8d5e5d0a1598f1a1633162f5cca83aaa2ebd9f2a084427cde35657cfb1f7',
   false)
on conflict (code, locale, version) do nothing;

-- A seed that inserts zero rows must abort: the catalogue is read-only at runtime, so a missing
-- template row could only be repaired by another migration. FORCE row level security without the
-- owner policy would make exactly that happen silently.
do $$
declare
  template_count integer;
  default_locales integer;
  bad_locales integer;
begin
  select count(*) into template_count
    from public.ai_settings_template
   where code in ('default', 'web-form') and version = 1;

  if template_count <> 4 then
    raise exception 'ai_settings_template seed incomplete: expected 4 rows, found %', template_count;
  end if;

  select count(distinct locale) into default_locales
    from public.ai_settings_template
   where is_default;

  select count(*) into bad_locales
    from (select locale
            from public.ai_settings_template
           where is_default
           group by locale
          having count(*) <> 1) duplicates;

  if default_locales <> 2 or bad_locales <> 0 then
    raise exception 'ai_settings_template defaults wrong: % locales defaulted, % locales with a duplicate',
      default_locales, bad_locales;
  end if;
end
$$;
