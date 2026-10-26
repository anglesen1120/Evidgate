# Role prompts in public repositories — GitHub review for 13.2.2-T1

## 1. Header, scope and method

- **Story / task:** `13.2.2` role profiles and the role prompt library, task `13.2.2-T1` (`plans/features/13-product-phases/13.2.2-role-profiles-and-prompt-library.md:313`); acceptance criterion `13.2.2-AC18` at `plans/features/13-product-phases/13.2.2-role-profiles-and-prompt-library.md:49`.
- **Scope (roles):** business analyst (requirement → user story → acceptance criteria); QA / test plan; test-case writer; integration-scenario writer; PM / release summary; developer failure triage. Locales: vi and en.
- **Retrieval date:** 2026-10-26 (every query, commit and file below was read on that date).
- **Who / what:** one research memo assembled from six read-only research slices — BA, QA, integration, PM, dev triage, locale/en–vi. Each slice searched GitHub, opened actual prompt and licence files at a pinned commit, and recorded its own queries and verdicts. No slice executed a prompt, evaluated prompt quality empirically, or modified a repository.
- **Method:** `gh api -X GET search/repositories`, `gh api -X GET search/code`, and a small number of web searches. Repository and code searches were spaced at least 3 seconds apart (code searches 6–7 seconds); page size 10–30; repository ordering by stars or default relevance, as recorded per row. No minimum stars and no pushed-date filter were imposed by the role slices, so specialist low-star projects would not disappear; two supplementary PM queries used `stars:>=20`. Each candidate's metadata, `commits/HEAD`, recursive tree, `/license` endpoint and selected `contents/<path>?ref=<sha>` were opened through the API; cited web permalinks were additionally opened with `curl` (HTTP 200 except explicitly recorded 404 licence endpoints). Licence verdicts come from reading the licence text, not from a GitHub badge or SPDX label.
- **Shortlist signals only:** stars and recency bound the candidate set. They are never licence evidence and never evidence of prompt quality; no source here has a demonstrated production outcome.
- **Source definition used below:** a *source* is a repository whose actual prompt text, or its licence and scope evidence, was opened at a pinned commit, whether retained or rejected. Two rejected rows are evidence-only and are marked as such: `PM-R2` has a README and LICENSE but no prompt body, and `DEV-R1` is an empty repository with nothing readable. Repositories that appeared only as search metadata or snippets are listed separately as screened-but-not-inspected and are not counted as sources; they carry no licence record because none was read.
- **Counts:** 72 query rows recorded; 49 slice ids over 46 distinct inspected repositories (38 shortlisted rows, 8 inspected-and-rejected; 44 with prompt or instruction text opened, 2 evidence-only: `PM-R2`, `DEV-R1`); text-copy allowed = 17, text-copy = no 29, of which 21 rows carry ⚠️ (no licence link available, unclear scope, or an unverified README claim). Ids are role-prefixed: `BA-n`, `QA-n`, `INT-n`, `PM-n`, `DEV-n`, `LOC-n`, rejected rows use `-R`. Overlapping slice ids are merged into one row: `QA-1` = `INT-1`, `BA-6` = `LOC-5`, `INT-6` = `LOC-8`.

### Queries

| Slice | Query | Endpoint | Filters | Retrieval date | total_count | Inspected |
|---|---|---|---|---|---|---|
| BA | `topic:prompt-engineering "business analyst"` | search/repositories | topic + quoted phrase; stars descending; first 30 | 2026-10-26 | 5 | 3 |
| BA | `"business analyst prompt"` | search/repositories | quoted phrase; stars descending; first 30 | 2026-10-26 | 2 | 1 |
| BA | `"acceptance criteria prompt"` | search/repositories | quoted phrase; stars descending; first 30 | 2026-10-26 | 2 | 2 |
| BA | `"user story acceptance criteria generator prompt"` | search/repositories | quoted phrase; stars descending; first 30 | 2026-10-26 | 0 | 0 — no usable result |
| BA | `awesome-chatgpt-prompts` | search/repositories | term; stars descending; first 30 | 2026-10-26 | 231 | 2 |
| BA | `"prompt phân tích nghiệp vụ"` | search/repositories | quoted Vietnamese phrase; stars descending; first 30 | 2026-10-26 | 0 | 0 — no usable result |
| BA | `"tiêu chí chấp nhận prompt"` | search/repositories | quoted Vietnamese phrase; stars descending; first 30 | 2026-10-26 | 0 | 0 — no usable result |
| QA | `QA test case prompt` | search/repositories | sort=stars; per_page=30; no stars/date minimum | 2026-10-26 | 60 | 2 |
| QA | `test case generator prompt` | search/repositories | sort=stars; per_page=30; no minimum | 2026-10-26 | 35 | 1 |
| QA | `test plan generator prompt` | search/repositories | sort=stars; per_page=30; no minimum | 2026-10-26 | 3 | 1 |
| QA | `topic:prompt-engineering "test case"` | search/repositories | topic + quoted phrase; sort=stars; per_page=30 | 2026-10-26 | 55 | 2 |
| QA | `"software tester prompt"` | search/repositories | quoted phrase; sort=stars; per_page=30 | 2026-10-26 | 0 | 0 |
| QA | `"prompt viết test case"` | search/repositories | quoted Vietnamese phrase; sort=stars; per_page=30 | 2026-10-26 | 0 | 0 |
| QA | `"kiểm thử phần mềm prompt"` | search/repositories | quoted Vietnamese phrase; sort=stars; per_page=30 | 2026-10-26 | 0 | 0 |
| QA | `software tester prompt` | search/repositories | unquoted; sort=stars; per_page=30 | 2026-10-26 | 12 | 0 |
| QA | `"prompt viết test case" GitHub` | web | limit=5; no date filter | 2026-10-26 | not supplied (5 entries) | 1: naodeng |
| QA | `"kiểm thử phần mềm prompt" GitHub` | web | limit=4; no date filter | 2026-10-26 | not supplied (4 entries) | 0 additional; corroborated naodeng |
| QA | `"viết test case" in:file language:Markdown` | search/code | Markdown; per_page=10; default relevance | 2026-10-26 | 1756 | 1: anhtester/antigravity-testing-kit |
| QA | `"test case" "kiểm thử" in:file language:Markdown` | search/code | Markdown; per_page=10; default relevance | 2026-10-26 | 13280 | 0 additional |
| INT | `integration scenario prompt` | search/repositories | none; stars descending; first 30 | 2026-10-26 | 19 | 1: chauhan-Nirdosh |
| INT | `integration test scenario generator prompt` | search/repositories | none; stars descending; first 30 | 2026-10-26 | 0 | 0 — no usable result |
| INT | `API test scenario LLM prompt` | search/repositories | none; stars descending; first 30 | 2026-10-26 | 5 | 1: chauhan-Nirdosh (overlap) |
| INT | `end-to-end test scenario prompt` | search/repositories | none; stars descending; first 30 | 2026-10-26 | 3 | 1: lee-to, rejected |
| INT | `topic:prompt-engineering "integration test"` | search/repositories | topic filter; stars descending; first 30 | 2026-10-26 | 2 | 0 — no scenario-writer prompt |
| INT | `OpenAPI test scenario prompt` | search/repositories | none; stars descending; first 30 | 2026-10-26 | 0 | 0 — no usable result |
| INT | `OpenAPI LLM test generation` | search/repositories | none; stars descending; first 30 | 2026-10-26 | 14 | 4: apicat, testronaut, ShawnLi-code, ByronLove10 |
| INT | `API testing prompts` | search/repositories | none; stars descending; first 30 | 2026-10-26 | 644 | 1: tayyabakmal1 |
| INT | `integration testing prompts` | search/repositories | none; stars descending; first 30 | 2026-10-26 | 194 | 0 — mixed evaluators/unrelated repos |
| INT | `openapi test generation AI` | search/repositories | none; stars descending; first 30 | 2026-10-26 | 20 | 2 overlapping: testronaut, ShawnLi-code |
| INT | `"integration test" "prompt" in:file extension:md` | search/code | in:file, extension:md; first 30 | 2026-10-26 | 847872 | 0 files — too broad |
| INT | `"kiểm thử tích hợp" "prompt" in:file` | search/code | in:file; first 30 | 2026-10-26 | 1066 | 0 files — documentation noise |
| PM | `release summary prompt` | search/repositories | relevance terms only; sort=stars; per_page=30 | 2026-10-26 | 6 | 0 — README keyword matches |
| PM | `release notes generator prompt` | search/repositories | sort=stars; per_page=30 | 2026-10-26 | 1 | 1: S6 |
| PM | `release notes LLM prompt changelog` | search/repositories | sort=stars; per_page=30 | 2026-10-26 | 0 | 0 — no usable result |
| PM | `go no-go release readiness prompt` | search/repositories | sort=stars; per_page=30 | 2026-10-26 | 0 | 0 — no usable result |
| PM | `project manager prompt` | search/repositories | sort=stars; per_page=30 | 2026-10-26 | 144 | 1 README/licence + companion opened |
| PM | `topic:prompt-engineering "release notes"` | search/repositories | topic filter; sort=stars; per_page=30 | 2026-10-26 | 5 | 1: S5 |
| PM | `"release notes" "prompt" in:file language:Markdown` | search/code | in:file; Markdown; per_page=12 | 2026-10-26 | 351232 | 0 — broad, noisy |
| PM | `"release readiness" in:file language:Markdown` | search/code | in:file; Markdown; per_page=12 | 2026-10-26 | 112512 | 0 — broad |
| PM | `"go/no-go" "prompt" in:file language:Markdown` | search/code | in:file; Markdown; per_page=12 | 2026-10-26 | 62208 | 0 — broad |
| PM | `"release notes" path:prompts in:file` | search/code | path; in:file; per_page=12 | 2026-10-26 | 1386 | 1: S1 |
| PM | `"phát hành" "prompt" in:file language:Markdown` | search/code | in:file; Markdown; per_page=12 | 2026-10-26 | 7536 | 0 usable vi prompt |
| PM | `release notes ai generator stars:>=20` | search/repositories | stars:>=20; per_page=12 | 2026-10-26 | 0 | 0 — no usable result |
| PM | `changelog llm stars:>=20` | search/repositories | stars:>=20; per_page=12 | 2026-10-26 | 5 | 0 — not advanced |
| PM | `"release notes" "system_prompt" in:file` | search/code | in:file; per_page=12 | 2026-10-26 | 117 | 1: S2 |
| PM | `"release readiness" "prompt" path:skills in:file` | search/code | path:skills; in:file; per_page=12 | 2026-10-26 | 1560 | 2: S3, S4 |
| PM | `"release notes" "ghi chú" in:file` | search/code | in:file; per_page=12 | 2026-10-26 | 2268 | 0 usable prompt |
| PM | `"release summary prompt"` | search/repositories | exact phrase; per_page=30 | 2026-10-26 | 0 | 0 — no usable result |
| DEV | `failure triage prompt` | search/repositories | no thresholds; stars descending; 30 | 2026-10-26 | 8 | 1: D1 |
| DEV | `test failure analysis LLM prompt` | search/repositories | no thresholds; stars descending; 30 | 2026-10-26 | 7 | 0 — no usable prompt |
| DEV | `CI failure root cause prompt` | search/repositories | no thresholds; stars descending; 30 | 2026-10-26 | 0 | 0 — no usable result |
| DEV | `flaky test classification prompt` | search/repositories | no thresholds; stars descending; 30 | 2026-10-26 | 0 | 0 — no usable result |
| DEV | `stack trace analysis prompt` | search/repositories | no thresholds; stars descending; 30 | 2026-10-26 | 0 | 0 — no usable result |
| DEV | `topic:prompt-engineering debugging` | search/repositories | topic filter; stars descending; 30 | 2026-10-26 | 206 | 0 — top page not inspected |
| DEV | `prompt phân tích lỗi kiểm thử` | search/repositories | no thresholds; stars descending; 30 | 2026-10-26 | 1 | 0 sources; 1 empty candidate |
| DEV | `"flaky" "triage" "prompt" in:file extension:md` | search/code | Markdown; default relevance; 15 | 2026-10-26 | 31744 | 1: D2 |
| DEV | `"CI failure" "prompt" in:file` | search/code | in:file; default relevance; 15 | 2026-10-26 | 68352 | 0 — broad |
| DEV | `"stack trace" "root cause" in:file path:prompts` | search/code | path contains prompts; relevance; 15 | 2026-10-26 | 576 | 3: D3–D5 |
| DEV | `"test failure" "hypotheses" in:file` | search/code | in:file; default relevance; 15 | 2026-10-26 | 18240 | 1: D6 |
| LOC | `prompt tiếng Việt` | search/repositories | sort=stars; per_page=30 | 2026-10-26 | 17 | 4: S1, S2, S6, S9 |
| LOC | `awesome prompts vietnamese` | search/repositories | sort=stars; per_page=30 | 2026-10-26 | 1 | 1: S11 |
| LOC | `ChatGPT prompt tiếng Việt kiểm thử` | search/repositories | sort=stars; per_page=30 | 2026-10-26 | 0 | 0 — no usable result |
| LOC | `prompt BA tiếng Việt` | search/repositories | sort=stars; per_page=30 | 2026-10-26 | 1 | 1: S2 (book, not a BA pack) |
| LOC | `prompt viết user story tiếng Việt` | search/repositories | sort=stars; per_page=30 | 2026-10-26 | 0 | 0 — no usable result |
| LOC | `tạo test case bằng ChatGPT` | search/repositories | sort=stars; per_page=30 | 2026-10-26 | 0 | 0 — no usable result |
| LOC | `"test case" "tiếng Việt" in:file extension:md` | search/code | per_page=10 | 2026-10-26 | 8528 | 3: S3, plus off-topic |
| LOC | `"user story" "tiếng Việt" in:file extension:md` | search/code | per_page=10 | 2026-10-26 | 1698 (retry) | 0 new; first attempt HTTP 403 |
| LOC | `"integration" "tiếng Việt" "prompt" in:file extension:md` | search/code | per_page=10 | 2026-10-26 | 12560 | 0 relevant |
| LOC | `"release" "tiếng Việt" "prompt" in:file extension:md` | search/code | per_page=10 | 2026-10-26 | 12016 | 0 relevant |
| LOC | `"debug" "tiếng Việt" "prompt" in:file extension:md` | search/code | per_page=10 | 2026-10-26 | 7680 | 0 relevant |
| LOC | `"business analyst" "tiếng Việt" in:file extension:md` | search/code | per_page=10 | 2026-10-26 | 538 | 3: S4, S7, S2 |
| LOC | `prompts.chat in:name` | search/repositories | per_page=5 | 2026-10-26 | 5 shown | 1: S5 |

Query counts are GitHub `total_count` snapshots, not relevance-filtered counts, and must not be summed across rows: several rows overlap and the first query of the BA, INT and LOC slices was re-run for the recorded batch. Zero rows record search coverage under those exact query/filter combinations, not absence from GitHub. Multilingual check: `f/awesome-chatgpt-prompts` at `cd7ac608ac794b9249ea62ce630475e5d344470d` has 1651 blobs, no Vietnamese folder or translation directory, and exactly 1 of 2992 `prompts.csv` rows contains Vietnamese diacritics — the repository is effectively en-only.

## 2. Shortlist

`SPDX` is what GitHub reports; the licence text actually read is in §4. Every shortlist row's commit is the default-branch HEAD opened on 2026-10-26, recorded with the full 40-character sha. `copy` is the text-copy disposition under the narrower prompt-text allow-list of §4, not a claim about commercial use.

| ID | Source | Role | Licence SPDX | Licence link at commit | Inspected commit | Last commit | What idea to take | copy |
|---|---|---|---|---|---|---|---|---|
| BA-1 | [tmquang-dev/awesome-ba-prompts](https://github.com/tmquang-dev/awesome-ba-prompts) | BA, PO | MIT | [LICENSE](https://github.com/tmquang-dev/awesome-ba-prompts/blob/53cb57cfc3f2b44f5506bc9238377ec05854cd89/LICENSE) | `53cb57cfc3f2b44f5506bc9238377ec05854cd89` | 2026-08-17 | Two-stage draft-then-critique criteria with source references and explicit unknowns | n ⚠️ |
| BA-2 | [nimmalavineela91-hub/claude-business-analyst-requirements-copilot](https://github.com/nimmalavineela91-hub/claude-business-analyst-requirements-copilot) | BA | none | ⚠️ none — `/license` 404, no licence file in tree | `8e343a3193c1897485f8d957dcaa6902e64cf3b3` | 2026-08-10 | Draft status, requirement ids, assumptions and clarification questions kept explicit | n |
| BA-3 | [sudeepshetty-629/Business-Analyst-Prompt](https://github.com/sudeepshetty-629/Business-Analyst-Prompt) | BA | none (reserved rights) | ⚠️ none — `/license` 404, README reserves all rights | `9b08ea4faa0c6dc8f8ab07e9e3af4c38e98754ab` | 2026-02-25 | Decision table as a separate output for multi-condition rules; observability per criterion | n |
| BA-4 | [SalimKashou/AC-PromptSmith](https://github.com/SalimKashou/AC-PromptSmith) | BA-adjacent, PM, QA lead | NOASSERTION (text MIT) | [LICENSE](https://github.com/SalimKashou/AC-PromptSmith/blob/6854e86d262ac47ecdfb20470db5be5b52289487/LICENSE) | `6854e86d262ac47ecdfb20470db5be5b52289487` | 2026-02-19 | User-outcome mode and technical-layer mode as separate versioned operation parameters | y |
| BA-5 | [dungnv05/pm-qa-automation-toolkit](https://github.com/dungnv05/pm-qa-automation-toolkit) | PM, QA-adjacent | none | ⚠️ none — `/license` 404; README claims MIT without terms | `606a4c1f543fc1073084d5937e68bacb9f778ead` | 2026-07-11 | Criteria expansion and testability mapping as two separate small operations | n |
| BA-6, LOC-5 | [f/prompts.chat](https://github.com/f/prompts.chat) (formerly `f/awesome-chatgpt-prompts`) | PM, BA-adjacent, QA, release | NOASSERTION (composite) | [LICENSE](https://github.com/f/prompts.chat/blob/cd7ac608ac794b9249ea62ce630475e5d344470d/LICENSE), [LICENSE-CC0](https://github.com/f/prompts.chat/blob/cd7ac608ac794b9249ea62ce630475e5d344470d/LICENSE-CC0) | `cd7ac608ac794b9249ea62ce630475e5d344470d` | 2026-10-26 | Fixed PRD section outline gated on a defined subject; measurable quality gates; release checklist with ship-ready flag, blockers and rollback | y |
| BA-7 | [kuzniarr/personal-ba-framework](https://github.com/kuzniarr/personal-ba-framework) | BA | MIT | [LICENSE](https://github.com/kuzniarr/personal-ba-framework/blob/86b67572b175a2426574f3af71a74eddb8072aef/LICENSE) | `86b67572b175a2426574f3af71a74eddb8072aef` | 2026-10-25 | Decided facts, proposals and unknowns kept apart; reachability filter; approve decomposition before criteria | y |
| QA-1, INT-1 | [tayyabakmal1/qa-prompt-library](https://github.com/tayyabakmal1/qa-prompt-library) | QA lead, test plan, API/contract integration | none | ⚠️ none — `/license` 404; README claims MIT | `69bd2ddde2a0b55f666516780daf82f07da59fd7` | 2026-01-19 | Coverage taxonomy chosen from requirement context; dependency graph, provider states and fault conditions before contract cases | n |
| QA-2 | [sobhanrajabi31/test-scenario-prompts](https://github.com/sobhanrajabi31/test-scenario-prompts) | Manual QA, case writer, integration | MIT | [LICENSE](https://github.com/sobhanrajabi31/test-scenario-prompts/blob/3a7c32f68b96e94bff09f665edc6d5715e1a9fa9/LICENSE) | `3a7c32f68b96e94bff09f665edc6d5715e1a9fa9` | 2026-07-31 | Every expected result judgeable; side-effect absence checked; undefined behaviour recorded separately | y |
| QA-3 | [AlexKuchkov/chatgpt-for-qa](https://github.com/AlexKuchkov/chatgpt-for-qa) | QA lead, strategy, report | MIT | [LICENSE](https://github.com/AlexKuchkov/chatgpt-for-qa/blob/f6bbc6223dab0211f977822561fc3ef58fff2034/LICENSE) | `f6bbc6223dab0211f977822561fc3ef58fff2034` | 2025-06-24 | One distinct user action per QA activity instead of one universal prompt | y |
| QA-4 | [vishalchincholi1/ai-test-plan-generator](https://github.com/vishalchincholi1/ai-test-plan-generator) | QA lead, test plan | none | ⚠️ none — `/license` 404; README embeds MIT-form text without a named holder | `3c199e79cd6b229accf63ef0773fe4df868ecb5f` | 2026-02-10 | Explicit unknowns, capacity-consistent schedule, measurable entry/exit criteria | n |
| QA-5 | [KarthikKuppu/AI-Powered-Test-Case-Generator-Uploader](https://github.com/KarthikKuppu/AI-Powered-Test-Case-Generator-Uploader) | Manual case writer | none | ⚠️ none — `/license` 404, no grant in README/instructions | `fda00fb5a34eaecdeeb158c460eb34973acc0bde` | 2026-09-28 | Case-to-criterion traceability and missing-constraint questions surfaced before writing | n |
| QA-6 | [naodeng/awesome-qa-prompt](https://github.com/naodeng/awesome-qa-prompt) | QA reviewer, case writer | NOASSERTION | [LICENSE](https://github.com/naodeng/awesome-qa-prompt/blob/efc18f6b1b052c7538659639a5b0c83d5debec8d/LICENSE) | `efc18f6b1b052c7538659639a5b0c83d5debec8d` | 2026-09-16 | Partial or blocked output as a first-class state; proposed scenarios separated from executed results | n |
| QA-7 | [pmutua/CodeCraftGPT](https://github.com/pmutua/CodeCraftGPT) | Automated test author (adjacent) | MIT | [LICENSE](https://github.com/pmutua/CodeCraftGPT/blob/e0562953b2911193bc2bef738cbc66cc3acfbd32/LICENSE) | `e0562953b2911193bc2bef738cbc66cc3acfbd32` | 2023-12-29 | Stable role/output contract separate from task material; framework bound from trusted configuration | y |
| QA-8 | [anhtester/antigravity-testing-kit](https://github.com/anhtester/antigravity-testing-kit) | Manual QA, risk-based designer (vi) | MIT | [LICENSE](https://github.com/anhtester/antigravity-testing-kit/blob/8e33c0eb9acbd98cb13b4aa09fe8e3e3327c1d9c/LICENSE) | `8e33c0eb9acbd98cb13b4aa09fe8e3e3327c1d9c` | 2026-07-22 | Human clarification gate; technique selection per requirement; per-field validation checklist | y |
| INT-2 | [apicat/apicat](https://github.com/apicat/apicat) | API scenario writer | none | ⚠️ none — `/license` 404; README claims MIT | `60ad4e9b10c16d5fbceb28638373bf639899558d` | 2026-07-15 | Inventory of atomic scenarios first, then expand one purpose into a typed case | n |
| INT-3 | [tusharranjan-ai/testronaut](https://github.com/tusharranjan-ai/testronaut) | API/integration case writer | MIT | [LICENSE](https://github.com/tusharranjan-ai/testronaut/blob/f3067ae7dd410070d35f8e3dc556639ed7e46940/LICENSE) | `f3067ae7dd410070d35f8e3dc556639ed7e46940` | 2026-10-15 | Requirement provenance plus separate precondition, fixture-need and assertion fields | y |
| INT-4 | [ShawnLi-code/llm-api-tester](https://github.com/ShawnLi-code/llm-api-tester) | API edge cases, failure analyst | none | ⚠️ none — `/license` 404, no licence file | `8949083e4f8f78ad9f428df98f113041a8349b5d` | 2026-08-13 | Deterministic contract cases separated from advisory model suggestions; authoring apart from triage | n |
| INT-5 | [ByronLove10/openapi-test-generator](https://github.com/ByronLove10/openapi-test-generator) | API edge-case writer | none | ⚠️ none — `/license` 404; README claims MIT | `f4d545b836f0a87e442292abfaaacf2a3d008e8e` | 2026-06-02 | Operation identity pinned outside model output; diversity allocated across input-shape violations | n |
| INT-6, LOC-8 | [chauhan-Nirdosh/Test-Case-Generation-Prompt](https://github.com/chauhan-Nirdosh/Test-Case-Generation-Prompt) | Integration-scenario writer | none | ⚠️ none — `/license` 404, no licence file | `e81e31c196c9344f7d465aa05a9d625281d11364` | 2026-06-26 | Source-to-target boundaries first-class; queue/database/cache observation points and correlation evidence apart from steps; failure and recovery matrix | n |
| PM-1 | [ServiceNowDevProgram/ServiceNow-GenAi-Prompt-Library](https://github.com/ServiceNowDevProgram/ServiceNow-GenAi-Prompt-Library) | PM release notes | none | ⚠️ none — `/license` 404, no licence file | `234b91db384085a7e69f361302f086f8b0ad05c1` | 2025-12-08 | Pair each change with the affected area and explain it for mixed technical audiences | n |
| PM-2 | [kienlucy08/release-notes-gpt](https://github.com/kienlucy08/release-notes-gpt) | PM release communications | none | ⚠️ none — `/license` 404; vendored dependency licences do not cover the prompt | `cd44caf74f8914563ed8a83fc48cccfe288ca7bd` | 2026-03-19 | Change description kept separate from user impact, with an internal-visibility flag | n |
| PM-3 | [FredericValette/TAIKI](https://github.com/FredericValette/TAIKI) | Release manager, QA controller | none | ⚠️ none — `/license` 404, no licence file | `56a367d4af76804f0caff8f07aecb8df48bc8454` | 2026-10-14 | Evidence id, timestamp, limit and owner per gate; failed, missing and stale evidence kept distinct | n |
| PM-4 | [Yeachan-Heo/oh-my-claudecode](https://github.com/Yeachan-Heo/oh-my-claudecode) | Release manager | MIT | [LICENSE](https://github.com/Yeachan-Heo/oh-my-claudecode/blob/a382cc730e575c85887c8fb8b8d60ca3b19cd0da/LICENSE) | `a382cc730e575c85887c8fb8b8d60ca3b19cd0da` | 2026-10-26 | Discover project rules before planning; user impact and migration guidance ahead of internals | y |
| PM-5 | [davidtaylor6130/My-AI-Skills](https://github.com/davidtaylor6130/My-AI-Skills) | Release PM, operator | MIT | [LICENSE](https://github.com/davidtaylor6130/My-AI-Skills/blob/cf3145559fc4db3ef8288b5d155f532ed3fb3d56/LICENSE) | `cf3145559fc4db3ef8288b5d155f532ed3fb3d56` | 2026-10-23 | Bind claims to the exact revision; reports distinguished from confirmed defects; unavailable inventories disclosed | y |
| PM-6 | [parameshwararajasekaran-droid/XDAS-Release-Notes-Generator-Experimental](https://github.com/parameshwararajasekaran-droid/XDAS-Release-Notes-Generator-Experimental) | Product marketing, PM | none | ⚠️ none — `/license` 404, tree has no licence file | `301f2b40542071957e9f4195d98074c9f6e9deef` | 2026-03-30 | Coverage check that every selected module appears, with a compact per-module overview | n |
| DEV-1 | [vit5411/ci-triage-agent](https://github.com/vit5411/ci-triage-agent) | Developer CI triage | none | ⚠️ none — `/license` 404, no licence file | `ffb413e43d06b382325c22df219e99a05ef0049f` | 2026-09-16 | Failing test id, frame and relevant source kept together; no invented APIs or line locations | n |
| DEV-2 | [fugazi/test-automation-skills-agents](https://github.com/fugazi/test-automation-skills-agents) | Developer + QA investigation | MIT | [LICENSE](https://github.com/fugazi/test-automation-skills-agents/blob/f5a0e6474f8e578f06aadade82f2eb6ce69189fd/LICENSE) | `f5a0e6474f8e578f06aadade82f2eb6ce69189fd` | 2026-10-21 | Classify only after a reproduction attempt; allow a non-reproduced outcome; mask evidence before persisting it | y |
| DEV-3 | [Yeachan-Heo/oh-my-codex](https://github.com/Yeachan-Heo/oh-my-codex) | Developer debugger | MIT | [LICENSE](https://github.com/Yeachan-Heo/oh-my-codex/blob/6b1c110ec06b45a5644ceb6ee60788f7ccae6c46/LICENSE) | `6b1c110ec06b45a5644ceb6ee60788f7ccae6c46` | 2026-10-25 | One falsifiable hypothesis at a time, an escalation limit, and manifestation separated from origin | y |
| DEV-4 | [alirezarezvani/claude-code-tresor](https://github.com/alirezarezvani/claude-code-tresor) | Developer analysis | MIT | [LICENSE](https://github.com/alirezarezvani/claude-code-tresor/blob/4b680504d29a205380436e9970eccf8faa28d21d/LICENSE) | `4b680504d29a205380436e9970eccf8faa28d21d` | 2026-02-25 | Normalised intake fields — expected/actual, environment and version, recent change — before analysis | y |
| DEV-5 | [ai-boost/awesome-prompts](https://github.com/ai-boost/awesome-prompts) | Developer debugging | GPL-3.0 | [LICENSE](https://github.com/ai-boost/awesome-prompts/blob/f7988ca8cf7785f1efd2b5617c0f3ddc43b2ba73/LICENSE) | `f7988ca8cf7785f1efd2b5617c0f3ddc43b2ba73` | 2026-10-26 | Ranked hypotheses with predictions and discriminating checks; environment/dependency explanations considered | n ⚠️ |
| DEV-6 | [microsoft/lisa](https://github.com/microsoft/lisa) | Developer, infrastructure investigator | MIT | [LICENSE](https://github.com/microsoft/lisa/blob/5419485194aba08d1e89518bf7240a2f94aa3d78/LICENSE) | `5419485194aba08d1e89518bf7240a2f94aa3d78` | 2026-10-24 | Evidence gathering separated from final synthesis; exact evidence pointers; recommendation left empty without defect evidence | y |
| LOC-1 | [macthuystore/ai-prompts-vietnamese](https://github.com/macthuystore/ai-prompts-vietnamese) | Generic vi scaffold (dev verification, PM report) | MIT | [LICENSE](https://github.com/macthuystore/ai-prompts-vietnamese/blob/d637855170c2a04c7aa8bcf408145c6ee15c8646/LICENSE) | `d637855170c2a04c7aa8bcf408145c6ee15c8646` | 2026-09-11 | Model tags its own unverified statements; facts, inference and missing information written separately | y |
| LOC-2 | [fx-studio/vibe-coding-2026-essentials](https://github.com/fx-studio/vibe-coding-2026-essentials) | vi PRD, criteria, case table, release checklist | CC-BY-NC-ND-4.0 AND MIT (split) | [LICENSE.md](https://github.com/fx-studio/vibe-coding-2026-essentials/blob/74390b2b875f5ca89fd3d1fb6be19f712cba2d49/LICENSE.md) | `74390b2b875f5ca89fd3d1fb6be19f712cba2d49` | 2026-07-31 | Per-prompt "when to use / expected result" pair with a fixed output skeleton; four-bucket case taxonomy | n |
| LOC-3 | [anhtester/claude-testing-kit](https://github.com/anhtester/claude-testing-kit) | vi BA, QA strategy, case authoring, in-app integration, triage | MIT | [LICENSE](https://github.com/anhtester/claude-testing-kit/blob/2c73a57f3713090dca98589e217a7a22817cb2e5/LICENSE) | `2c73a57f3713090dca98589e217a7a22817cb2e5` | 2026-08-08 | Module map plus dimension catalogue with an explicitly chosen combination strategy; evidence hierarchy with a refusal/tag rule; stable id registry | y |
| LOC-4 | [phucnt-bazone-vietnam/use-case-writer](https://github.com/phucnt-bazone-vietnam/use-case-writer) | BA use-case specification | NOASSERTION (text MIT + attribution clause) | [LICENSE](https://github.com/phucnt-bazone-vietnam/use-case-writer/blob/695cd6c14ec5e05e67380e2d05dde79909fcd207/LICENSE) | `695cd6c14ec5e05e67380e2d05dde79909fcd207` | 2026-05-16 | Scope-first discipline: actor specificity, no branching in the main course, fill-confirm-validate loop | y (with credit) |
| LOC-6 | [nguyenchikien1970-creator/prompt-viet](https://github.com/nguyenchikien1970-creator/prompt-viet) | vi translation corpus of S5 | none | ⚠️ none — no licence blob in tree; README only states the upstream licence | `118f4d2ff4924a061984d5e3e0637c963ffc2c56` | 2026-10-07 | Record schema pairing original and vi text with category, quality status and provenance | n |
| LOC-7 | [olbboy/BA-Kit](https://github.com/olbboy/BA-Kit) | vi BA lifecycle, QA, release slicing | CC-BY-NC-4.0 | [LICENSE](https://github.com/olbboy/BA-Kit/blob/8c17ff334042977e8594ee7205a1bcbf817f7f10/LICENSE) | `8c17ff334042977e8594ee7205a1bcbf817f7f10` | 2026-04-13 | Artifact-chain framing where each prompt names its downstream artifact; cross-artifact consistency and gate scoring; UAT with rollback | n |

### Rejected after inspection

| ID | Source | Why rejected | Licence SPDX | Licence link at commit | Inspected commit | Last commit | copy |
|---|---|---|---|---|---|---|---|
| BA-R1 | [PlexPt/awesome-chatgpt-prompts-zh](https://github.com/PlexPt/awesome-chatgpt-prompts-zh) | Chinese derivative repeating the generic PRD outline; translated-prompt provenance not cleared | MIT | [LICENSE](https://github.com/PlexPt/awesome-chatgpt-prompts-zh/blob/a7085311b98a4954aa7ba8c1dbf45b087796dbdb/LICENSE) | `a7085311b98a4954aa7ba8c1dbf45b087796dbdb` | 2026-04-28 | n ⚠️ |
| INT-R1 | [lee-to/ai-tester](https://github.com/lee-to/ai-tester) | Role mismatch: an end-to-end runner for testing agent skills, not a prompt that writes integration scenarios | MIT | [LICENSE](https://github.com/lee-to/ai-tester/blob/1390148019bf4d080aa9101c52a859ae86cee3d0/LICENSE) | `1390148019bf4d080aa9101c52a859ae86cee3d0` | 2026-10-07 | n (role) |
| PM-R1 | [deanpeters/product-manager-prompts](https://github.com/deanpeters/product-manager-prompts) | Off-phase: produces a pre-build launch announcement, not a release or readiness summary | NOASSERTION (text CC-BY-NC-SA-4.0) | [LICENSE](https://github.com/deanpeters/product-manager-prompts/blob/c3b91dba8f7782fac72c7aa340b98d6554b1f3ec/LICENSE) | `c3b91dba8f7782fac72c7aa340b98d6554b1f3ec` | 2026-08-10 | n |
| PM-R2 | [deanpeters/product-management-agents](https://github.com/deanpeters/product-management-agents) | Inspected companion with no prompt body: tree holds only README and LICENSE; discovery only | MIT | [LICENSE](https://github.com/deanpeters/product-management-agents/blob/7535fb15073f90710ec4c9d7049cb8d6fe791970/LICENSE) | `7535fb15073f90710ec4c9d7049cb8d6fe791970` | 2025-05-27 | n (no prompt) |
| DEV-R1 | [hoimanchi113-dev/diva](https://github.com/hoimanchi113-dev/diva) | No usable content: empty repository, `commits/HEAD` returned HTTP 409, no prompt and no licence | none | ⚠️ none — `/license` 404 and no HEAD to pin | ⚠️ none — empty repository | 2026-07-20 (pushed) | n ⚠️ |
| LOC-R1 | [LockMan04/Vietnamese-prompts](https://github.com/LockMan04/Vietnamese-prompts) | Off-role (image/video/marketing/study prompts) and unlicensed | none | ⚠️ none — 114 blobs, zero licence-like files | `3aedcdf3ed45462f66da5442013719d1a1237842` | 2025-11-08 | n |
| LOC-R2 | [hgthinhng/prompt-chuan](https://github.com/hgthinhng/prompt-chuan) | Off-role: a meta skill for writing prompts, not a role prompt; retained only as an injection-posture reference | MIT | [LICENSE](https://github.com/hgthinhng/prompt-chuan/blob/a1a920da7e08b49367b270cc82a64e62084de426/LICENSE) | `a1a920da7e08b49367b270cc82a64e62084de426` | 2026-10-13 | n (no role text) |
| LOC-R3 | [gonegirl07/awesome-vn-agent-skills](https://github.com/gonegirl07/awesome-vn-agent-skills) | Off-role: CV, README, PR-review, commit-message and translation skills; none of the six roles | MIT | [LICENSE](https://github.com/gonegirl07/awesome-vn-agent-skills/blob/abaf484c40b891a5c414eea2b5b05c5bed13addf/LICENSE) | `abaf484c40b891a5c414eea2b5b05c5bed13addf` | 2026-09-02 | n (no role text) |

Every copy=no row carries one of four reasons, kept explicit so that "no" is never mistaken for "forbidden to use commercially": **no licence** (no grant exists at the inspected commit); **outside the allow-list** (a verified licence — PolyForm Noncommercial, CC-BY-NC, CC-BY-NC-ND, GPL-3.0 — that this research does not copy from); **unverified claim** (a README or badge asserts a licence whose text is absent or whose scope is unclear); **off-role** (the licence permits copying, but the repository contains no prompt for any of the six roles). LOC-R2 and LOC-R3 are MIT by verified text and are off-role; BA-R1 is MIT by text but unverified in provenance; DEV-R1 has no licence and no content at all. No text from any of them is used.

### Screened but not inspected

These appeared only as search metadata, snippets or an unopened link. They are not sources: no prompt was read, no licence was read, and no row here is evidence of content or licence.

| Slice | Repository | Status |
|---|---|---|
| BA | `deleonloza/ai-business-analyst-agent-langgraph`, `syedkhubaib-analyst/google-ai-essentials` | Returned by topic query; not inspected |
| BA | `gcowin/PLW` | Returned by quoted query; not inspected |
| QA | `leonardyeoxl/O-RAN-Security-Test-Case-Generator` | Metadata and tree only; narrow domain, no detected licence; prompt not opened |
| QA | `SoftwareTestingTrends/ai-prompts-for-software-testers` | Returned by unquoted query; not opened |
| INT | `R3dShad0w7/PromptMe`, `OWASP/www-project-promptme` | Descriptions concern LLM security challenges; not opened |
| INT | `drissiOmar98/Spring-AI-Integration`, `rafa761/spec-agent-hackaton-bairesdev-202603` | Topic hits insufficient to establish a scenario-writing prompt; not opened |
| PM | `Sfedfcv/redesigned-pancake` and 5 further `release summary prompt` hits | README/website keyword matches, not prompt files; not opened |
| LOC | `anhtester/antigravity-testing-kit` | Listed unopened in the locale slice; later opened and retained as QA-8 — see §7 |
| LOC | `phamtuanchip/book_ba`, `loversky02/product-team-skill-suite`, `hoangpm96/ai-switcher`, `Mk141121/salary` | Query hits; not opened beyond snippets |
| LOC | `2025Emma/vibe-coding-cn`, and README-only hits `ChatGPTNextWeb/NextChat`, `huggingface/transformers`, `MHSanaei/3x-ui`, `jumpserver/jumpserver`, `starship/starship` | Out of locale or no role prompts; not opened as sources |

## 3. Gap matrix

Phases are Evidgate's requirement / planning / authoring / execution / post-run. G1 output kinds are `checklist` and `test_case`; `test_plan`, `integration_scenario`, `release_summary` and `failure_triage` are refused in G1 and only `release_summary` and `failure_triage` are enabled later, in G2 (`13.2.2-AC24`, `plans/features/13-product-phases/13.2.2-role-profiles-and-prompt-library.md:55`).

| Role | Phase | Evidgate output kind | en | vi |
|---|---|---|---|---|
| BA | requirement → authoring | `checklist`, `test_case` | **copyable source** — BA-4, BA-7, BA-6, LOC-4 (English output only); ideas-only BA-1, BA-2, BA-3, BA-5 | **copyable source** — LOC-3 requirements analyzer; ideas-only LOC-7, LOC-2, LOC-6 |
| QA test plan | planning | `test_plan` (G2, unavailable in G1) | **copyable source** — QA-2, QA-3, BA-6 quality-engineering rows; ideas-only QA-4, QA-1 | **copyable source** — LOC-3 risk-based modes with risk levels and RTM; ideas-only LOC-7 gate scoring, LOC-6 |
| Test-case writer | authoring | `test_case` | **copyable source** — QA-2, QA-3, QA-7 (automated, adjacent), BA-6; ideas-only QA-5, QA-6 | **copyable source** — QA-8, LOC-3 case templates; ideas-only LOC-2 Prompt 30, LOC-6 |
| Integration-scenario writer | planning → authoring | `integration_scenario` (G2, unavailable in G1; owner `OI-13-305`) | **ideas-only source** — INT-6, INT-1, INT-2, INT-4, INT-5; adjacent copyable INT-3 covers endpoint-level cases only | **copyable source (partial)** — LOC-3 chained in-app module command; **no** service-to-service, queue or DLQ coverage ⇒ T2 authors the extension |
| PM release summary | post-run | `release_summary` (G2, unavailable in G1) | **copyable source** — PM-4, PM-5, BA-6 release checklist and ghostwriter rows; ideas-only PM-3, PM-1, PM-2, PM-6 | **ideas-only source** — LOC-2 production checklist; nearest copyable input LOC-1 is a generic report scaffold with no PR/tag input |
| Developer failure triage | execution → post-run | `failure_triage` (G2, unavailable in G1) | **copyable source** — DEV-2, DEV-3, DEV-4, DEV-6, BA-6 debugging rows; ideas-only DEV-1, DEV-5 | **copyable source** — LOC-3 flaky-analysis template, LOC-1 verification prompt; ideas-only LOC-2, LOC-6 |

Per-gap authoring notes (all `[INFERENCE]`, no source has demonstrated production quality):

- **BA, en:** no inspected source schema-validates a trace from each criterion to an input span and an approved decision, and none proves INVEST or Gherkin quality. T2 must write the operation contract — stable story and criterion ids, source references, decided/proposed/unknown separation, explicit gaps — and treat Gherkin as notation, not an executable test claim.
- **BA, vi:** copyable vi text exists in LOC-3, but its REQ-ID and needs-verification vocabulary would collide with Evidgate's own AC and evidence terms. LOC-4 is MIT with a mandatory credit line yet forces English output even for Vietnamese input, so it is an en-cell source and contributes structure only. T2 must author vi wording with a Vietnamese terminology glossary, diacritic and number/date/currency rules, and the same machine schema as en.
- **QA test plan, en:** phase planning is well covered; execution evidence is not. T2 must not infer pass/fail or coverage counts from a plan, and priorities must trace to project settings rather than a profile's claim.
- **QA test plan, vi:** the only copyable vi strategy text is LOC-3's risk modes; there are no copyable vi measurable gates with numbers. T2 must author the gate vocabulary, and any numeric threshold must come from configuration, never from the prompt.
- **Test-case writer, en/vi:** case structure is copyable, but no source supplies a deterministic schema for checklist-item → case with evidence provenance and refusal states. T2 authors that schema-bound output; expected values must be sourced or explicitly synthetic.
- **Integration scenario, en:** the only genuine multi-system prompt (INT-6) is unlicensed and invites fabrication when documentation is thin. T2/T8 must author boundary, async-delivery, compensation, tenant-safe fixture, cleanup-ownership and correlation-id requirements themselves; per `OI-13-305` this is a separate G2 story, so the memo records the gap only.
- **Integration scenario, vi:** the copyable vi source covers chained UI modules inside one application, not queues, retries or cross-service state. Authoring the Vietnamese service-to-service extension is T2/T8 work, not a translation of LOC-3.
- **PM release summary, en/vi:** no inspected prompt produces the fixed-schema, non-technical decision summary Evidgate needs (release identity and scope, observed counts, mandatory-gate gaps, material defects, residual-risk owners, evidence links, confidence, accountable human authority). The vi gap is total. Numeric aggregation must be computed upstream; T8 gets the seed only after its schema and pinned evidence-revision inputs exist.
- **Developer failure triage, en/vi:** no source prohibits a "fixed/verified" verdict in machine-checkable form, and none captures job/run/attempt ids, exit codes, causal ordering, retry denominators or environment drift in a fixed schema. T8 must author those fields plus an insufficient-evidence state and a no-tools, no-fix boundary.
- **Locale mechanics, both:** no inspected prompt handles Vietnamese output rules beyond one UTF-8 check; vi/en semantic parity is unverified everywhere.

## 4. Licence table

`SPDX as GitHub reports` is the API value, which is not evidence on its own: BA-4 reports NOASSERTION while its text is a complete MIT grant, and BA-6 reports NOASSERTION while its text splits prompt data (CC0-1.0) from site code (MIT). A `license` endpoint 404 is recorded as "no licence detectable", never as an inaccessible repository; every such repository was still reached at its pinned commit, and its licence absence is proven by the recursive tree listing.

Copy disposition applies the binding rule in `../../DECISIONS.md:100` (dependencies must allow closed-source commercial SaaS; no AGPL/SSPL/BUSL/RPL, paid-maintenance EULA or non-commercial licence in shipped code or hosted services; LGPL needs an ADR) together with `../adr/0002-compatibility-and-licence-matrix.md:13` (ADR-0002 restates that rule and makes the matrix the running register), and the narrower prompt-text allow-list fixed for this research at `plans/features/13-product-phases/13.2.2-role-profiles-and-prompt-library.md:321`: text may be copied **only** from MIT, Apache-2.0, BSD, CC0 or CC-BY, and only after exact licence, version and scope verification with attribution and notices recorded. CC-BY-SA, GPL/AGPL, CC-BY-NC(-SA), PolyForm Noncommercial, no licence and unclear scope mean ideas and structure only, with **no text**. This is a deliberately narrower prompt-text copy allow-list for this memo; it is not a claim that the other licences forbid every commercial use, and it is not legal advice.

| ID | SPDX as GitHub reports | SPDX as read from licence text | Licence permalink at commit | Copyright holder / version | Scope caveat | Attribution / notice duty | Copy disposition |
|---|---|---|---|---|---|---|---|
| BA-1 | MIT | MIT, 2026 | [LICENSE](https://github.com/tmquang-dev/awesome-ba-prompts/blob/53cb57cfc3f2b44f5506bc9238377ec05854cd89/LICENSE) | (c) 2026 Antigravity BA Community | README says the collection was standardised from BAvolta's 43 prompts; no upstream grant verified | Would need MIT notice plus upstream clearance | **No text, ⚠️** — ideas only |
| BA-2 | none | no licence text present | ⚠️ none — no licence file in inspected tree | none declared | No grant at all; all rights reserved by default | n/a | **No text** |
| BA-3 | none | no licence text; README reserves all rights | ⚠️ none — no licence file in inspected tree | (c) 2026 Sudeep Shetty (reserved) | Reserved rights cover published repository content | n/a | **No text** |
| BA-4 | NOASSERTION | MIT, complete grant wrapped in Markdown | [LICENSE](https://github.com/SalimKashou/AC-PromptSmith/blob/6854e86d262ac47ecdfb20470db5be5b52289487/LICENSE) | (c) 2025 Salim Kashou | Software and associated documentation; no third-party attribution or code-only exclusion found; does not cover user attachments | Retain copyright and permission notice if substantial text were copied | **Text allowed** (independent wording still preferred) |
| BA-5 | none | README claims MIT but omits conditions and holder | ⚠️ none — `/license` 404 | none stated | Informal reuse claim cannot be verified to cover the prompt file | None possible | **No text, ⚠️** |
| BA-6, LOC-5 | NOASSERTION | Prompt data and submitted prompt text CC0-1.0; site/source content MIT | [LICENSE](https://github.com/f/prompts.chat/blob/cd7ac608ac794b9249ea62ce630475e5d344470d/LICENSE), [LICENSE-CC0](https://github.com/f/prompts.chat/blob/cd7ac608ac794b9249ea62ce630475e5d344470d/LICENSE-CC0) | MIT (c) 2022-present Fatih Kadir Akin and contributors; CC0 instrument names no holder; inspected entry credited @orinachum | Explicit prompt-data dedication covers in-repository entries, not external pages a prompt links to, trademarks or rights a contributor did not own | CC0 requires no notice; record contributor and commit as provenance | **Text allowed** for inspected prompt data only |
| BA-7 | MIT | MIT, 2026 | [LICENSE](https://github.com/kuzniarr/personal-ba-framework/blob/86b67572b175a2426574f3af71a74eddb8072aef/LICENSE) | (c) 2026 Volodymyr Kuzniar | Software and documentation; not client materials or later supplied designs; a personal starter set, not an aggregate pack | Retain copyright and licence notice | **Text allowed** |
| QA-1, INT-1 | none | README claims MIT; no licence file | ⚠️ none — `/license` 404, no licence file in 137-blob tree | none stated | Badge is a claim, not a grant | None possible | **No text, ⚠️** |
| QA-2 | MIT | MIT, 2026 | [LICENSE](https://github.com/sobhanrajabi31/test-scenario-prompts/blob/3a7c32f68b96e94bff09f665edc6d5715e1a9fa9/LICENSE) | (c) 2026 Sobhan Rajabi | Software and associated documentation; first-party prompt files; not arbitrary linked sources | Retain MIT notice | **Text allowed** |
| QA-3 | MIT | MIT, 2025 | [LICENSE](https://github.com/AlexKuchkov/chatgpt-for-qa/blob/f6bbc6223dab0211f977822561fc3ef58fff2034/LICENSE) | (c) 2025 Alex Kuchkov | Root grant covers associated documentation; examples are in-repository | Retain MIT notice | **Text allowed** |
| QA-4 | none | README embeds MIT-form text with a bare 2026 copyright and no holder; a separate guard file attributes third-party material | ⚠️ none — `/license` 404, no licence file in 5-blob tree | bare (c) 2026; third-party attribution unresolved | Third-party attribution leaves scope unverified; author identity in README does not prove ownership of that material | Unresolved | **No text, ⚠️** |
| QA-5 | none | no licence text; no grant in README, instructions or guardrails | ⚠️ none — `/license` 404, no licence file in 20-blob tree | not stated | No grant at all | n/a | **No text, ⚠️** |
| QA-6 | NOASSERTION (Other) | PolyForm Noncommercial 1.0.0; README applies it to this prompt project | [LICENSE](https://github.com/naodeng/awesome-qa-prompt/blob/efc18f6b1b052c7538659639a5b0c83d5debec8d/LICENSE) | no licensor name in LICENSE | Non-commercial restriction is outside the allow-list | Not applicable | **No text** |
| QA-7 | MIT | MIT, 2023 | [LICENSE](https://github.com/pmutua/CodeCraftGPT/blob/e0562953b2911193bc2bef738cbc66cc3acfbd32/LICENSE) | (c) 2023 Philip Mutua | Root grant; first-party prompt source; not dependencies | Retain MIT notice | **Text allowed** |
| QA-8 | MIT | MIT, 2026 | [LICENSE](https://github.com/anhtester/antigravity-testing-kit/blob/8e33c0eb9acbd98cb13b4aa09fe8e3e3327c1d9c/LICENSE) | (c) 2026 Anh Tester | In-repository manual prompts only; not external resources or unread kit material | Retain MIT notice | **Text allowed** |
| INT-2 | none | README claims MIT; no licence file | ⚠️ none — `/license` 404, no licence file in tree | none stated | Badge is a claim, not a grant | None possible | **No text, ⚠️** |
| INT-3 | MIT | MIT, 2026 | [LICENSE](https://github.com/tusharranjan-ai/testronaut/blob/f3067ae7dd410070d35f8e3dc556639ed7e46940/LICENSE) | (c) 2026 Tushar Ranjan | Software and associated documentation; repository-owned skill and embedded prompt; not linked external collections | Retain MIT notice | **Text allowed** |
| INT-4 | none | no licence text | ⚠️ none — `/license` 404, no licence file | none stated | README references other projects architecturally; that is not evidence their text was licensed here | n/a | **No text, ⚠️** |
| INT-5 | none | README claims MIT naming 2026 Byron Love; no licence text | ⚠️ none — `/license` 404, no licence file | claim only | No grant text to verify | None possible | **No text, ⚠️** |
| INT-6, LOC-8 | none | no licence text; README request also 404 | ⚠️ none — `/license` 404, no licence file | none stated | No grant at all | n/a | **No text, ⚠️** |
| PM-1 | none | no licence text | ⚠️ none — `/license` 404, no licence file | none stated | Community submission hub; contributor rights cannot be inferred from the organisation name | n/a | **No text, ⚠️** |
| PM-2 | none | no project licence | ⚠️ none — `/license` 404 | none stated | Vendored `node_modules` licences do not license the application prompt | n/a | **No text, ⚠️** |
| PM-3 | none | no licence text | ⚠️ none — `/license` 404, no licence file | none stated | German README is description only; prompts are English | n/a | **No text, ⚠️** |
| PM-4 | MIT | MIT, 2025 | [LICENSE](https://github.com/Yeachan-Heo/oh-my-claudecode/blob/a382cc730e575c85887c8fb8b8d60ca3b19cd0da/LICENSE) | (c) 2025 Yeachan Heo | Does not extend to arbitrary linked or aggregated third-party material elsewhere in the repository | Retain MIT notice | **Text allowed** |
| PM-5 | MIT | MIT, 2026 | [LICENSE](https://github.com/davidtaylor6130/My-AI-Skills/blob/cf3145559fc4db3ef8288b5d155f532ed3fb3d56/LICENSE) | (c) 2026 David Taylor | Two inspected first-party files; live-linked reference documents not inspected and not covered by inference | Retain MIT notice | **Text allowed** |
| PM-6 | none | no licence text | ⚠️ none — `/license` 404; tree has app, logo and requirements only | none stated | No grant at all | n/a | **No text, ⚠️** |
| PM-R1 | NOASSERTION (Other) | CC-BY-NC-SA-4.0 | [LICENSE](https://github.com/deanpeters/product-manager-prompts/blob/c3b91dba8f7782fac72c7aa340b98d6554b1f3ec/LICENSE) | expression attributed to Dean Peters by the plain-English guide; the legal body names no holder | Guide says pre-v2.3.0 copies were MIT; that is not permission for the inspected commit; the companion repo's MIT does not transfer | Not applicable | **No text** |
| PM-R2 | MIT | MIT, 2025 | [LICENSE](https://github.com/deanpeters/product-management-agents/blob/7535fb15073f90710ec4c9d7049cb8d6fe791970/LICENSE) | (c) 2025 Dean Peters | Licenses that repository only; no prompt body exists there and it does not relicense PM-R1 | None needed | **No text** (nothing to copy) |
| DEV-1 | none | no licence text | ⚠️ none — `/license` 404, no licence file | none stated | No affirmative text-copy grant | n/a | **No text** |
| DEV-2 | MIT | MIT (no numeric version) | [LICENSE](https://github.com/fugazi/test-automation-skills-agents/blob/f5a0e6474f8e578f06aadade82f2eb6ce69189fd/LICENSE) | root (c) 2026 Douglas Urrea Ocampo; skill-local (c) 2026 Douglas Fugazi | Primary skill points to its local licence; only opened in-repository material is covered | Preserve the applicable root or local notice | **Text allowed** |
| DEV-3 | MIT | MIT (no numeric version) | [LICENSE](https://github.com/Yeachan-Heo/oh-my-codex/blob/6b1c110ec06b45a5644ceb6ee60788f7ccae6c46/LICENSE) | (c) 2026 Yeachan-Heo | Not a grant for unofficial forks or links | Retain MIT notice | **Text allowed** |
| DEV-4 | MIT | MIT (no numeric version) | [LICENSE](https://github.com/alirezarezvani/claude-code-tresor/blob/4b680504d29a205380436e9970eccf8faa28d21d/LICENSE) | (c) 2025 Alireza Rezvani | A collection; the opened local template has no third-party origin found, but linked projects are not covered | Retain MIT notice | **Text allowed** |
| DEV-5 | GPL-3.0 | GNU GPL version 3, 29 June 2007 | [LICENSE](https://github.com/ai-boost/awesome-prompts/blob/f7988ca8cf7785f1efd2b5617c0f3ddc43b2ba73/LICENSE) | licence-document (c) 2007 Free Software Foundation; prompt author copyright not identified; no only/or-later grant found | README mixes local prompts with adapted external material, so root GPL does not establish that provenance; API value must not be normalised to `-or-later` | Not applicable | **No text, ⚠️** (GPL outside allow-list) |
| DEV-6 | MIT | MIT (no numeric version) | [LICENSE](https://github.com/microsoft/lisa/blob/5419485194aba08d1e89518bf7240a2f94aa3d78/LICENSE) | (c) Microsoft Corporation, no year in notice | Local analyzer prompts; no rights inferred for linked dependencies or services | Retain MIT notice | **Text allowed** |
| LOC-1 | MIT | MIT, 2026 | [LICENSE](https://github.com/macthuystore/ai-prompts-vietnamese/blob/d637855170c2a04c7aa8bcf408145c6ee15c8646/LICENSE) | (c) 2026 Mặc Thủy | Presented as the author's own library; no third-party attribution list | Retain MIT notice | **Text allowed** (content is generic) |
| LOC-2 | NOASSERTION | `CC-BY-NC-ND-4.0 AND MIT`, split by artifact | [LICENSE.md](https://github.com/fx-studio/vibe-coding-2026-essentials/blob/74390b2b875f5ca89fd3d1fb6be19f712cba2d49/LICENSE.md) | (c) 2026 Tien Le | Book text and manuscript are NC+ND; only sample code and `templates/` are MIT. An appendix is manuscript text | Not applicable | **No text** (NC/ND) |
| LOC-3 | MIT | MIT, 2026 | [LICENSE](https://github.com/anhtester/claude-testing-kit/blob/2c73a57f3713090dca98589e217a7a22817cb2e5/LICENSE) | (c) 2026 Anh Tester | Single repository licence read as covering `.claude/skills`, `.claude/commands` and `.claude/rules`; no separate third-party notice in opened files | Retain MIT notice | **Text allowed** |
| LOC-4 | NOASSERTION | MIT plus an added attribution clause | [LICENSE](https://github.com/phucnt-bazone-vietnam/use-case-writer/blob/695cd6c14ec5e05e67380e2d05dde79909fcd207/LICENSE) | (c) 2026 Phúc NT | Redistribution must credit the original author and may not obscure authorship; output rules force English even for Vietnamese input | Mandatory credit line: Phúc NT / BA Zone / Digital School | **Text allowed** with the credit line; en output only, so it is not a vi source and no text is planned |
| LOC-6 | none | no licence text | ⚠️ none — no licence blob in tree | none stated | README states the upstream corpus licence; a translation is a derivative whose rights are unaddressed | n/a | **No text, ⚠️** |
| LOC-7 | NOASSERTION | CC BY-NC 4.0 | [LICENSE](https://github.com/olbboy/BA-Kit/blob/8c17ff334042977e8594ee7205a1bcbf817f7f10/LICENSE) | (c) 2026 BA-Kit Contributors | Non-commercial; promotional claims about the kit are not licence evidence | Not applicable | **No text** |
| BA-R1 | MIT | MIT, 2025 | [LICENSE](https://github.com/PlexPt/awesome-chatgpt-prompts-zh/blob/a7085311b98a4954aa7ba8c1dbf45b087796dbdb/LICENSE) | (c) 2025 plex | Translated/derivative prompt provenance not independently cleared, so the repository MIT does not by itself clear the entry | Would need MIT notice plus upstream clearance | **No text, ⚠️** |
| INT-R1 | MIT | MIT, 2026 | [LICENSE](https://github.com/lee-to/ai-tester/blob/1390148019bf4d080aa9101c52a859ae86cee3d0/LICENSE) | (c) 2026 lee-to | Software grant; the file inspected is a runner, not a scenario prompt | Would need MIT notice | **No text** (role mismatch) |
| DEV-R1 | none | none available | ⚠️ none — `/license` 404, no HEAD to pin | none stated | Empty repository; nothing readable | n/a | **No text, ⚠️** |
| LOC-R1 | none | no licence text | ⚠️ none — 114 blobs, zero licence-like files | none stated | No grant at all | n/a | **No text** |
| LOC-R2 | MIT | MIT, 2026 | [LICENSE](https://github.com/hgthinhng/prompt-chuan/blob/a1a920da7e08b49367b270cc82a64e62084de426/LICENSE) | (c) 2026 hgthinhng | Whole repository; the artifact is a meta skill with no role coverage | Would need MIT notice | **No text** (no role text) |
| LOC-R3 | MIT | MIT, 2026 | [LICENSE](https://github.com/gonegirl07/awesome-vn-agent-skills/blob/abaf484c40b891a5c414eea2b5b05c5bed13addf/LICENSE) | (c) 2026 gonegirl07 and contributors | Per-skill front matter repeats MIT; roles do not include the six in scope | Would need MIT notice | **No text** (no role text) |
| Screened, not inspected | not recorded | not read | ⚠️ none | not read | No licence was read for the repositories in §2's third table; a collection's licence is never presumed to cover linked or aggregated prompts | n/a | **No text** |

Two scope rules applied throughout: a collection's licence is not presumed to cover prompts it links to or aggregates, and a permissive grant covers only the artifacts actually read at that commit. Nothing in the row above licenses a user's own input, a tenant's data, or a model's output.

## 5. Ideas not copied

Idea text below is independently reworded and contains no quoted prompt fragment longer than five words. "Why not copied" records the disposition even where the licence allows copying, because the G1 seeds must exist in vi and en and stay bound to the platform schema, so original wording is preferred everywhere.

| ID | Idea in own words | Role | Why not copied | Lands in |
|---|---|---|---|---|
| BA-1 | Draft criteria, then run a second pass that attacks and rewrites weak criteria, citing the input each one rests on | BA | ⚠️ upstream provenance unclear despite local MIT | T2 BA seed |
| BA-2 | Mark output as draft, keep requirement ids, refuse unsupported business rules, list open questions | BA | no licence | T2 BA seed |
| BA-3 | Emit a decision table for multi-condition rules instead of hiding logic in prose | BA | no licence, reserved rights | T2 BA seed (`checklist` kind) |
| BA-4 | Separate a user-outcome mode from a technical-layer mode as two versioned operations | BA/PM | licence allows; original wording chosen | T2 BA/QA seeds |
| BA-5 | Treat criteria expansion and testability mapping as two small operations | PM/QA | ⚠️ README-only licence claim | T2 QA seed |
| BA-6, LOC-5 | Fixed document outline that waits for a defined subject; measurable quality gates with an explicit red-flag list; release checklist carrying a ship-ready flag, blockers and rollback | PM/QA/release | licence allows (CC0 prompt data); original wording chosen, and only inspected entries | T2 QA seed; T8 release summary |
| BA-7 | Keep decided facts, proposals and unknowns apart, filter unreachable states, approve decomposition before criteria | BA | licence allows; original wording chosen | T2 BA seed |
| QA-1, INT-1 | Choose a coverage taxonomy from requirement context; for contracts, map dependency graph, provider states and fault conditions first | QA/API | ⚠️ no licence file | T2 QA seed; T8 integration note |
| QA-2 | Make every expected result judgeable, check that no side effect occurs, record undefined behaviour separately | QA | licence allows; original wording chosen | T2 QA seed (`test_case`) |
| QA-3 | Offer separate actions for writing cases, planning strategy and summarising evidence | QA | licence allows; original wording chosen | T2 QA seed |
| QA-4 | Require explicit unknowns and a schedule that fits stated capacity; use measurable entry and exit criteria | QA | ⚠️ embedded grant, unresolved third-party attribution | T2 QA seed (test-plan family, G2) |
| QA-5 | Trace every case to a named criterion and surface missing constraints before writing | QA | no licence | T2 QA seed |
| QA-6 | Make partial or blocked output a normal state; keep proposed scenarios apart from executed results | QA | PolyForm Noncommercial | T2 QA seed (state model) |
| QA-7 | Keep role instruction and output contract stable; take the test framework only from trusted configuration | QA (automation) | licence allows; adjacent, not a manual-case source | T2 QA schema note |
| QA-8 | Put a human clarification gate before derivation and select design techniques from the requirement, not from a fixed list | QA (vi) | licence allows; original wording chosen | T2 QA vi seed |
| INT-2 | Build an atomic scenario inventory, then expand one chosen purpose into a typed case | Integration | ⚠️ no licence file | T8 integration research note |
| INT-3 | Keep requirement provenance, prerequisites, fixture needs and machine-checkable assertions as separate fields | Integration | licence allows; original wording chosen | T8 integration; T2 assertion vocabulary |
| INT-4 | Separate deterministic contract cases from advisory suggestions, and authoring from post-run analysis | Integration | ⚠️ no licence | T8 integration note |
| INT-5 | Pin operation identity outside model output and spread case diversity across input-shape violations | Integration | ⚠️ README-only claim | T8 integration note |
| INT-6, LOC-8 | Make source-to-target boundaries first-class; observe queues, storage and caches with correlation evidence apart from steps; add a failure and recovery matrix | Integration | ⚠️ no licence; the source also invites invention when documents are thin | T8 integration research (gap only, `OI-13-305`) |
| PM-1 | Pair each change with its affected area and explain it for mixed technical audiences | PM | ⚠️ no licence | T8 release summary |
| PM-2 | Keep the change description apart from user impact and flag internal-only changes | PM | ⚠️ no licence | T8 release summary |
| PM-3 | Attach evidence id, timestamp, limit and owner to each gate and keep failed, missing and stale evidence distinct | PM | ⚠️ no licence; closest conceptual fit does not grant text | T8 release summary (decision shape) |
| PM-4 | Discover project rules before planning; explain compatibility and migration consequences before low-level detail | PM | licence allows; original wording chosen | T8 release summary |
| PM-5 | Bind claims to the exact revision; separate reports from confirmed defects; disclose missing inventories | PM | licence allows; original wording chosen | T8 release summary |
| PM-6 | Verify that every selected module appears, with a compact overview before detail | PM | ⚠️ no licence | T8 release summary (coverage check) |
| DEV-1 | Keep failing test id, frame and relevant source together and forbid invented APIs or locations | Dev triage | no licence | T8 failure triage |
| DEV-2 | Classify only after a reproduction attempt and permit a non-reproduced outcome; mask sensitive evidence before persisting it | Dev triage | licence allows; the source's fix-and-validate phase is out of scope | T8 failure triage |
| DEV-3 | Test one falsifiable hypothesis at a time, with an escalation limit and manifestation separated from origin | Dev triage | licence allows; original wording chosen | T8 failure triage |
| DEV-4 | Normalise expected versus actual, environment and version, and recent change before analysis | Dev triage | licence allows; original wording chosen | T8 failure triage |
| DEV-5 | Rank a few hypotheses with predictions and discriminating checks; consider environment and dependency causes | Dev triage | GPL-3.0 and collection provenance | T8 failure triage (idea only) |
| DEV-6 | Gather evidence and synthesise separately; cite exact evidence pointers; leave a recommendation empty when no defect was found | Dev triage | licence allows; original wording chosen | T8 failure triage |
| LOC-1 | Have the model tag every statement it could not verify and split facts, inference and missing information | Dev/PM (vi) | licence allows; content is generic | T2 seed quality rule; T4 safety note |
| LOC-2 | Pair each prompt with a "when to use" and an "expected result" note, and fix the output skeleton; four-bucket case coverage | BA/QA/release (vi) | CC BY-NC-ND on the manuscript | T2 seed layout (idea only) |
| LOC-3 | Module map plus dimension catalogue with an explicitly chosen combination strategy; evidence hierarchy with a refusal tag; stable id registry across reruns | BA/QA (vi) | licence allows; its id vocabulary would collide with Evidgate's | T2 QA vi seed; T4 evidence rule |
| LOC-4 | Scope first: specific actors, no branching in the main course, fill one section, confirm, then validate | BA (vi) | licence allows with credit; forces English output | T2 BA seed structure (vi wording authored) |
| LOC-6 | Store original and vi text as a pair with category, quality status and provenance | Locale plumbing | ⚠️ derivative with no licence | T2 library shape (provenance pairing) |
| LOC-7 | Frame the library as an artifact chain where each prompt names its downstream artifact; add cross-artifact consistency and gate scoring; UAT with pass/fail and rollback | BA/QA/release (vi) | CC BY-NC 4.0 | T2 seed layout; T8 release summary shape |
| BA-R1 | Generic PRD section list | BA | Chinese derivative, provenance unclear | none |
| INT-R1 | Sandboxed runner for testing agent skills | Integration | role mismatch, no scenario prompt | none |
| PM-R1 | Working-backwards launch narrative with labelled assumptions | PM | CC-BY-NC-SA-4.0; off-phase | none |
| PM-R2 | Repository index of PM resources | PM | no prompt body | none |
| DEV-R1 | none readable | Dev triage | empty repository | none |
| LOC-R1 | Single-shot creative prompts with bracket slots | none of the six | unlicensed and off-role | none |
| LOC-R2 | Fence untrusted input and label it as data; separate data from instructions with tags | prompt engineering | meta skill, off-role | T4 safety note |
| LOC-R3 | Partition review feedback into must-change, nice-to-have and ask | none of the six | off-role | none |

### Prompt-injection posture

One line per source: whether supplied text is treated as data or as instructions. None of the inspected prompts is a safe model for Evidgate; each is at best a partial hint for T4.

- BA-1: placeholders organise input but do not demote it to nonexecutable data; critique is quality control, not a defence.
- BA-2: separate role/input sections and no-invention rules help grounding; embedded instructions in stakeholder notes are not rejected.
- BA-3: one long role prompt mixes artifacts and input; no untrusted-data boundary.
- BA-4: story, context and reference strings are interpolated under labels with no escaping; the instruction to encode assumed facts adds a requirements-integrity risk.
- BA-5: bare micro-prompts have no role hierarchy, delimiters or override rejection.
- BA-6, LOC-5: role-play rows expect follow-on user requests as instructions; only one release row states an explicit no-action boundary.
- BA-7: source hierarchy, anti-hallucination rules and write approval reduce accidental change; supplied documents are still authoritative with no embedded-instruction rejection.
- QA-1, INT-1: placeholders separate inputs visually; no trust boundary or refusal rule.
- QA-2: source paths are constrained and generated HTML must be escaped; escaping is not prompt-injection protection.
- QA-3: task direction and supplied material are mixed with no delimiters.
- QA-4: factual grounding is explicit, and user input remains an allowed knowledge source without a boundary.
- QA-5: guardrails describe embedded instructions as story text and forbid role changes and command execution — useful intent, not proof of enforcement.
- QA-6: artifact identity and evidence gates are strong; no explicit instruction-versus-data boundary.
- QA-7: system and human roles exist, but code is interpolated as plain human text and a library label is interpolated into the system message.
- QA-8: start markers and human checkpoints organise the workflow; pasted requirements are not declared untrusted.
- INT-2: context tags delimit API descriptions; interpolated user and system material has no data-only rule.
- INT-3: system methodology and user payloads are separated; the skill does not explicitly reject malicious spec instructions, though a capability-restricted tool was observed statically.
- INT-4: path names, failure records and spec excerpts are concatenated with no delimitation rule.
- INT-5: structured JSON improves formatting separation but does not forbid instructions inside schema descriptions.
- INT-6, LOC-8: pasted documentation is authoritative to analyse and extrapolate from, with no hostile-content handling.
- PM-1: story title and description are appended with no untrusted-data boundary.
- PM-2: a system role sets format, but the completed-items slot is not declared untrusted.
- PM-3: demands evidence and an external policy, but does not delimit evidence text or reject instructions inside it.
- PM-4: reads repository and cached release rules as operative instructions; generation-only scope must not inherit its publication side effects.
- PM-5: treats live-fetched references as workflow instructions with no hostile-source isolation.
- PM-6: interpolates project and story content into one user prompt; stripping HTML does not remove instruction attacks.
- DEV-1: failure text and source excerpts follow static instructions with no boundary.
- DEV-2: evidence-redaction rules help confidentiality, but file-backed journaling can persist injected instructions.
- DEV-3: newer task updates may override older instructions, and logs are primary evidence; no guard against directives inside logs.
- DEV-4: placeholder fields mark input regions visually; nothing denies authority to their content.
- DEV-5: XML-like sections delimit instructions, not untrusted evidence.
- DEV-6: evidence requirements and JSON-only synthesis do not neutralise hostile log text, and demanding verbatim tokens can echo attacker content.
- LOC-1: bracket slots and a no-invention rule, with no delimiter or data-only statement.
- LOC-2: bracket slots and an environment-file rule show data hygiene; pasted text is not declared nonexecutable.
- LOC-3: strongest vi stance — treats documents as claims and the live system as truth, refuses to invent when a source cannot be read; adversarial pasted text is not fenced.
- LOC-4: scopes pasted material and confirms section by section; no injection guard.
- LOC-6: inherits the upstream role-play style with no added guard.
- LOC-7: bracket slots and a verbatim paste; the paste is not labelled as data.
- LOC-R2: the only inspected source that states outright that untrusted input must be fenced and labelled as data; it is a meta skill with no role coverage.
- Other rejected rows (BA-R1, INT-R1, PM-R1, PM-R2, DEV-R1, LOC-R1, LOC-R3): no in-scope injection behaviour observed; PM-R1 shares context and instructions in one prompt with no untrusted declaration.

**T4 consequence:** Evidgate must version trusted instructions, policy and output schema in platform code, serialise requirement text, notes and evidence as delimited and labelled data, ignore embedded directives, require citations for factual claims, and grant no tools or egress. No inspected prompt supplies that model.

### Fix-claim posture

The dev slice found that public debugging prompts diverge sharply on whether a triage result may claim a fix. Evidgate must prohibit "fixed", "resolved" or "verified" verdicts from failure-triage output: no changed files, no rerun claims.

| Source | Observed fix-related instruction, as structure | Verdict for Evidgate |
|---|---|---|
| DEV-1 | Asks for a short proposed remedy with no verification step | Contrary — keep the evidence-only cause hypothesis and label any remedy as untested |
| DEV-2 | A later phase applies a fix and validates stability | Split — take "classification is a finding", reject apply and validate |
| DEV-3 | Recommends one minimal change plus a proving test | Partly contrary — a recommendation may be a hypothesis with a proving check, never a fix claim |
| DEV-4 | Extends into remediation, prevention and testing the fix | Contrary — out of scope |
| DEV-5 | Proposes a root-cause fix or a labelled workaround | Contrary but instructive — a workaround must carry residual risk, not a resolved status |
| DEV-6 | Leaves the code recommendation empty when no defect was found | Most aligned — adopt empty-by-default and require defect evidence before filling it |

Only DEV-6 gates emptiness on evidence, and none of the six provides a machine-checkable prohibition. T8 must add it explicitly.

## 6. Recommendation

- **[DECISION, inherited]** G1 ships only BA and QA seeds for `checklist` and `test_case` in vi and en; `test_plan`, `integration_scenario`, `release_summary` and `failure_triage` stay refused in G1 (`13.2.2-AC4`, `13.2.2-AC24`). The PM and dev research here therefore informs T8, not T2's G1 matrix. Output schemas, redaction, untrusted-input handling and no-tools boundaries are owned by platform code, never by a profile or a seed (`plans/features/13-product-phases/13.2.2-role-profiles-and-prompt-library.md:334`).
- **[DECISION, inherited]** Text may be copied only from the allow-listed licences after exact verification, with attribution and notices recorded, and seeds carry the provenance line defined at `plans/features/13-product-phases/13.2.2-role-profiles-and-prompt-library.md:338`.
- **[INFERENCE] Copy nothing for the first seed set.** Seventeen rows are licence-eligible, but every seed must exist in vi and en with an identical machine schema, Evidgate's criterion and evidence vocabulary, and reversible provenance, so an imported English prompt would be rewritten anyway. Copying would also create notice and scope obligations that no current G1 need justifies. If a copy is ever made, the minimum record is a NOTICE row naming the source repository, the 40-character commit, the licence and the copyright holder, plus a per-seed provenance line; for MIT that means reproducing the copyright and permission notice, and for CC0 that means recording the contributor and commit even though CC0 requires no notice. For LOC-4 the credit line "Phúc NT / BA Zone / Digital School" is mandatory.
- **[INFERENCE] What T2 writes itself:** the BA seed operation contract (requirement text and approved facts in; stories with stable ids, criteria with ids and Given/When/Then fields, source references, decided/proposed/unknown separation, explicit gaps, human approval out); the QA seed contract (checklist item or criterion in; cases with ids, preconditions, one action and one observable expected result per step, evidence-based priority, traceability and deferred gaps out; no execution outcome); the vi and en wording of both; the terminology glossary and vi typographic rules; the enum and field names that the platform schema already fixes; and the seed provenance and review revision.
- **[INFERENCE] What T2 adapts as ideas only:** BA-7's decided/proposed/unknown split and reachability filter; BA-4's split between user-outcome and technical-layer modes; QA-2's judgeable expected results and side-effect checks; QA-6's partial or blocked output state; QA-8's clarification gate; LOC-3's module and dimension structure with an explicitly chosen combination strategy, renamed to Evidgate's vocabulary.
- **[INFERENCE] Output structure recommendation for the BA seed:** trusted instruction block; delimited data block carrying requirement text, project facts and settings; required fields for story id, criterion id, source reference, observability, negative or boundary counterpart and open question; refusal states for missing requirement text, unsupported decision and unreadable source; and a fixed machine schema outside the editable prose. A decision table is an optional second artifact, not a substitute for criteria.
- **[INFERENCE] Output structure recommendation for the QA seed:** the same trusted/untrusted split; case ids, source ids, preconditions, numbered single-action steps, one observable expected result per step, evidence-based priority and explicit gaps; selected rather than exhaustive coverage, with negative and boundary cases tied to a stated risk or decision; and an explicit "not executed" state so a generated case is never reported as a result.
- **[INFERENCE] vi authoring plan:** author vi independently from the en intent, not by machine translation; have a human BA and a human QA review the vi and en pair for meaning equivalence, terminology, diacritics, number, date and currency formats, and the "không dấu" search variant; record the review revision in the seed provenance line; keep machine keys and enums identical across locales. No inspected vi source provides measurable quality gates, so numeric thresholds must come from project settings.
- **[INFERENCE] What T8 needs:** for `release_summary`, a fixed schema over release identity and scope, observed counts computed upstream, mandatory-gate gaps, material defects, residual-risk owners, evidence links, confidence and the accountable human decision — with missing, stale and not-run kept distinct, and no authority to approve or publish. For `failure_triage`, reproducibility state, suspected cause family (product, test, environment, infrastructure, unknown), supporting and conflicting evidence, confidence, missing evidence, one discriminating next check, an empty-by-default recommendation, and an explicit prohibition on fixed or verified verdicts.
- **[INFERENCE] What `test_plan` and `integration_scenario` research implies (`OI-13-305`):** both remain unallocated G2 work. `test_plan` has copyable en planning structure and thin vi strategy text; `integration_scenario` has no copyable source for service-to-service behaviour in either locale, and the one genuine multi-system prompt is unlicensed and invites fabrication. The receiving story must define schemas, pinned evidence-revision inputs and refusal states before any seed is authored; this memo records the gap and creates no task.

## 7. Unresolved evidence

- **Licence absence, verified but unresolved:** BA-2, BA-3, BA-5, QA-1/INT-1, QA-4, QA-5, INT-2, INT-4, INT-5, INT-6/LOC-8, PM-1, PM-2, PM-3, PM-6, DEV-1, DEV-R1 and LOC-R1 have no licence file at the inspected commit. Every one was still reached at its pinned commit (except DEV-R1) and its absence is proven by the tree listing and the 404 licence endpoint. No licence URL was invented; these rows stay ⚠️ with copy = no.
- **Upstream provenance unresolved:** BA-1 (derived from an unverified community corpus) and BA-R1 (a translation) carry local MIT text whose scope over the prompt wording is not established. Both stay no-text.
- **Third-party attribution unresolved:** QA-4 embeds an MIT-form grant with no named holder and a separately attributed guard file; DEV-5 is a curated mix with adapted external material under a root GPL. Both stay no-text.
- **Empty repository:** DEV-R1 has no HEAD commit (`commits/HEAD` returned HTTP 409), so no 40-character sha exists to pin; only its absence is evidence.
- **Not inspected:** the repositories in §2's third table have no licence record, no commit and no phrase. QA's `leonardyeoxl/O-RAN-Security-Test-Case-Generator` was reached only at metadata and tree level.
- **Failed query, not a zero result:** LOC query 8 (`"user story" "tiếng Việt" in:file extension:md`) first returned HTTP 403 rate limiting; it was retried the same day and yielded 1698, and is not counted as evidence of absence. QA's two web queries reported no `total_count`, so no count is claimed.
- **Zero-result queries:** BA's long generator phrase and both vi phrases; QA's quoted software-tester phrase and both quoted vi phrases; INT's scenario-generator, OpenAPI-scenario and topic rows; PM's four summary/LLM/go-no-go/exact-phrase rows and both `stars:>=20` rows; DEV's four triage wording rows plus the topic page; LOC's three vi repository queries. These record search coverage under the exact query/filter combinations, not absence from GitHub.
- **Search scope limits:** repository search matches names, descriptions and topics, so it cannot find prompt text; several code searches returned six-figure totals that were barely sampled (`integration test` 847872, `test case kiểm thử` 13280, PM Markdown rows in the hundreds of thousands). The vi ecosystem is covered by one copyable source (LOC-3), which is not representative.
- **Licence changes after the inspected commit:** none was checked. A later copy would need the licence re-read at the moment of copying, because PM-R1's own guide records an earlier MIT period and LOC-2 splits one repository across two licences.
- **Locale parity unverified:** no source validates vi/en semantic equivalence, and LOC-4 forces English output even for Vietnamese input, so it cannot serve as a vi output model.
- **Two intra-slice discrepancies, resolved conservatively here:** the locale slice listed `anhtester/antigravity-testing-kit` as surfaced-but-not-opened, while the QA slice opened it and verified MIT at `8e33c0eb9acbd98cb13b4aa09fe8e3e3327c1d9c`; this memo records QA-8 as inspected and keeps the locale slice's non-inspection note only as history. The QA slice's fingerprint `Never execute commands` is three words, below the four-word guidance for the appendix, and is recorded as given rather than extended from an unread range.
- **Re-open requirement:** a later review of this memo must re-open at least five sources at their pinned commits and re-read both licence texts and prompt bodies before any copy decision; §5 and §6 assume that re-check has not yet happened.
- **What this memo does not do:** it is not legal advice, it does not evaluate prompt quality or runtime safety, and it creates no contract. `[DECISION]` markers above quote decisions already taken in `DECISIONS.md` and the 13.2.2 plan; everything else is `[INFERENCE]`.

### Verbatim-phrase appendix

Every phrase below is a short verbatim fingerprint recorded from an opened file at the pinned commit, quoted only so a later check can grep this repository for accidental verbatim reuse. They are not seed text and must not be inserted into any prompt. Non-English rows are counted in tokens or characters as the slice recorded; three items fall below the four-word guidance and are noted.

| ID | grep-phrases |
|---|---|
| BA-1 | `critique your own output`, `Recommend one option and justify` |
| BA-2 | `Do not invent business rules`, `All outputs must be reviewed` |
| BA-3 | `Every requirement you produce meets`, `Decision Tables for Complex Logic` |
| BA-4 | `make reasonable assumptions and encode`, `Target 30–60+ bullets total` |
| BA-5 | `Expand a one-line story into`, `Map acceptance criteria to manual` |
| BA-6, LOC-5 | `Please respond to me as`, `Do not write any PRD`, `set ship_ready to false and list blockers`, `Preserve scope exactly as written`, `Never invent CVE IDs` |
| BA-7 | `A completing action never precedes`, `Never present a guess as decided` |
| QA-1, INT-1 | `Combination of boundary values across fields`, `Suspension Criteria and Resumption Requirements`, `Design microservices integration test suite`, `Generate consumer-driven contract tests` |
| QA-2 | `Find the deliberate logic`, `Two summary tables at the end`, `no request is sent` |
| QA-3 | `positive and negative test cases`, `one-page test strategy template`, `expected vs. actual behavior` |
| QA-4 | `Prioritize realistic timelines`, `Every assertion must be traceable`, `List unknown or missing information` |
| QA-5 | `Each step must have exactly`, `No test case should exist`, `Never execute commands` (3 words) |
| QA-6 | `Produce a minimum QA case body`, `An assumption cannot override a`, `Prefer the minimum set that verifies` |
| QA-7 | `You are a software tester`, `for the given code snippet`, `Please generate test functions` |
| QA-8 | `Liệt kê tất cả input fields`, `Đặt ra danh sách câu hỏi`, `Hãy chọn kỹ thuật phù hợp` |
| INT-2 | `a test case only tests`, `purpose of the test` (4 tokens) |
| INT-3 | `A negative case breaks exactly`, `what real data the operator` |
| INT-4 | `high-value edge case tests`, `An API contract test failed` |
| INT-5 | `Only generate the request fields`, `Prioritize those most likely to` |
| INT-6, LOC-8 | `Do NOT generate only functional test cases`, `Focus on interactions between systems`, `Follow ISTQB best practices` |
| PM-1 | `create weekly release notes`, `all technical skill levels`, `which part of the platform` |
| PM-2 | `Labels for Strategic Theme`, `A concise description in plain language`, `list of completed items` |
| PM-3 | `missing mandatory evidence is not`, `decision rationale and actionable residual risks`, `Record failed, missing, stale`, `Overrides must be explicit and auditable` |
| PM-4 | `one sentence, link to the PR`, `Omit changes users never see`, `derived from repo inspection` |
| PM-5 | `Distinguish reports from confirmed defects`, `Keep the notes proportionate`, `actual validation and results` |
| PM-6 | `Each project must be mentioned explicitly`, `Let wording adapt to actual updates`, `End output immediately after last feature` |
| DEV-1 | `Only use the excerpt provided`, `do not invent line numbers` |
| DEV-2 | `Classify after reproducing, not before`, `a wrong early label poisons` |
| DEV-3 | `No speculation without evidence`, `One hypothesis at a time` |
| DEV-4 | `What information to gather`, `How to test each hypothesis` |
| DEV-5 | `You work by hypothesis`, `Do not scatter debug prints` |
| DEV-6 | `Give top 3 possible reasons`, `Clearly distinguish between confirmed issues` |
| LOC-1 | `Đánh dấu [CẦN KIỂM CHỨNG]`, `suy luận và thông tin còn thiếu`, `Không tự tạo số liệu, nguồn, URL` |
| LOC-2 | `Tạo test cases toàn diện`, `Thoát vòng xoáy debug`, `tốt trên localhost nhưng lỗi khi deploy` |
| LOC-3 | `Chưa mở evidence thì chưa được ghi`, `Con người xác định chiến lược`, `KHÔNG tự bịa business rules` |
| LOC-4 | `following the industry-standard 13-field template`, `apply 4 scoping rules + coffee-break test`, `Normal Course, Alternative Course, Exceptions` |
| LOC-6 | `truy tìm nguyên nhân gốc rễ (root cause)`, `chỉ số hiệu quả (KPIs)`, `kim tự tháp kiểm thử` |
| LOC-7 | `Kiểm tra tính nhất quán cross-artifact giữa`, `Sinh test suite đầy đủ từ Acceptance Criteria`, `pass/fail criteria, và rollback procedure` |
| BA-R1 | `请您作为产品经理回复我` (11 chars), `在我要求具体主题、功能或开发的PRD之前` (20 chars), anchor `担任产品经理` |
| INT-R1 | none recorded — the opened file is a runner, not a prompt |
| PM-R1 | `ask at most 3 targeted questions`, `Avoid hype; favor credible claims`, `Commercial use requires expressed written permission` |
| PM-R2 | no prompt text; README probes only: `A toolkit of Agentic AI solutions`, `prompts, projects, and learning materials`, `empower product managers with practical` |
| DEV-R1 | none — empty repository, nothing readable |
| LOC-R1 | `Hãy phân tích 5 điểm mạnh độc đáo`, `đề xuất 3 cách định vị thương hiệu`, `lời hứa giá trị trong 1 câu` |
| LOC-R2 | `Đích quyết định hình dạng, xem bảng`, `Chỉ hỏi người dùng khi thiếu`, `rào lại và ghi rõ` |
| LOC-R3 | `Review để chặn bug và nợ kỹ thuật`, `vị trí + vấn đề + vì sao`, `Không đòi refactor ngoài scope` |
