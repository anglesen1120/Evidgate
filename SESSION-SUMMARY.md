# Tóm tắt phiên làm việc — Evidgate (cập nhật 2026-09-25, phiên 2)

Bản tóm tắt cho chủ dự án. Nguồn chính thức vẫn là `DECISIONS.md` và `plans/00-overview.md`; nếu file này lệch với chúng thì chúng đúng.

## 1. Dự án là gì

- **Tên:** Evidgate (*evidence* + *gate*). Thu kết quả test tự động (web, API, mobile, unit) từ CI/agent của khách, lưu thành bằng chứng bất biến gắn với build, ra quyết định release PASS / FAIL / INCONCLUSIVE theo policy có phiên bản; healing an toàn (chỉ sửa cơ chế test, không đổi ý định test).
- **Trạng thái:** đang làm M0 trên nhánh `feature/m0-foundation`: xong 01.1.1 T1–T3, 01.2.1, 01.3.1, 01.9.1 (toolchain, khung workspace, quality gate, OpenAPI + schema, ADR). Chưa có module, migration hay logic nghiệp vụ. Repo GitHub private `anglesen1120/Evidgate`, nhánh `main` (baseline) và `develop` (triển khai, đã có plan từ PR #1).

## 2. Đổi stack (CR-D-1, theo yêu cầu chủ dự án)

| Phần | Stack mới | Ghi chú |
|---|---|---|
| Backend + worker | **.NET 10 LTS**, ASP.NET Core minimal APIs, **CQRS** với `Mediator` 3 (MIT), EF Core 10 (ghi), Dapper (đọc) | .NET 11 mới là RC và STS nên không dùng |
| Database | **PostgreSQL 18**, RLS FORCE, migration SQL thuần bằng DbUp | |
| Hàng đợi nội bộ | Outbox + bảng `task` tự viết (`SKIP LOCKED`), lịch Cronos có khoá chống trùng | Thay pg-boss (thư viện Node) |
| Web | **Angular 22.2**: standalone, zoneless, signals, OnPush, `@ngrx/signals`, CDK, i18n vi + en | |
| Phía khách hàng | CLI, Playwright reporter, adapter, agent vẫn là **Node/TypeScript** | Reporter của Playwright bắt buộc là JS |

Thư viện bị loại vì licence không cho SaaS đóng mã (đã kiểm từ file licence trong nupkg): MediatR 14 (RPL-1.5), JsonSchema.Net 9 (EULA thu phí), FluentAssertions 8 (non-commercial). Quy tắc code chung nằm ở 00 §5.5 (CQRS C1–C9), §5.6 (Angular W1–W9), §5.7 (performance P1–P8, security S1–S8).

## 3. Review high-level (vai trò Tech Lead)

- **Vòng 1:** 4 reviewer chạy song song trên 12 area plan, ra **82 finding** (3 blocker, 21 high), xem `plans/features/REVIEW-2026-09-25.md`. Lỗi đáng chú ý:
  - Token CI bị lộ có thể giả mạo các shard còn thiếu.
  - `test_maintainer` có thể quarantine một case critical để biến FAIL thành PASS.
  - Nút duyệt healing có thể ra trước kill switch.
  - Policy RLS gây lỗi thay vì trả 0 dòng khi thiếu tenant context.
- **Vòng cuối**, sau khi viết feature plan, 3 reviewer:
  - **Security:** 14 finding. Lỗi critical là org khác có thể bind repo GitHub của mình để chiếm evidence CI.
  - **Performance/dữ liệu:** 15 finding. Ví dụ: event đến muộn bị từ chối nên cơ chế late window không chạy; batch gửi lại bị tính tiền hai lần.
  - **Nhất quán:** 7 finding.
- Đã xử lý bằng **101 phán quyết TL (T1–T101)**, trừ các mục ở §7a, xem `plans/features/README.md` §3. Phán quyết chạm hợp đồng chung được ghi vào 00 và log ở 00 §15.

## 4. Feature plan chi tiết

- **164/164 story** có feature plan tại `plans/features/<NN>-<area>/`. Mỗi file có 11 mục cố định; mỗi task được chia thành sub-task ≤ 0.5 ngày, và mỗi sub-task có một bước kiểm tra chạy được (tên test, lệnh hoặc screenshot).
- Kiểm tra bằng script:
  - `scripts/check_features.py` → **0 lỗi**. Script này kiểm cấu trúc, tổng sub-task, id, và header khớp đồ thị.
  - `scripts/plan_graph.py` → **0 lỗi**. Script này kiểm id treo, vòng phụ thuộc và phụ thuộc ngược milestone.
- Sinh tự động bằng `scripts/regen_docs.py`: bảng 00 §14, thứ tự thực thi và index ở README §5/§7.

## 5. Số liệu (00 §14)

| | Raw | Có buffer 20 % |
|---|---|---|
| V1 (M0–M3) | 433.25 | 519.9 |
| V2 (M4) | 113.75 | 136.5 |
| V3 (M5) | 13.25 | 15.9 |
| **Tổng (502 task)** | **560.25** | **672.3** |

Trước khi đổi stack: 487 task / 543.5 / 652.2. Tăng thêm 15 task (+16.75 ngày raw), gồm CQRS pipeline, idempotency store, task queue tự viết, fuzz parser, tách task xoá tenant (PDPL) và `run.purpose` cho replay.

## 6. Thứ tự thực thi (README §5)

- Làm theo cổng milestone, trong mỗi milestone thì theo **wave** phụ thuộc. Có 8 **bundle** (nhóm story đan xen nhau), phải làm chung và xếp theo task.
- **Đường găng:** chuỗi phụ thuộc dài nhất tới hết M3 chỉ khoảng **40 ngày**, trong khi khối lượng tới M3 khoảng 433 ngày. Tiến độ phụ thuộc số người, không phải phụ thuộc kỹ thuật: thêm người rút ngắn lịch gần như tỷ lệ thuận.
- **Gợi ý 3 lane:**
  - A: platform/core, do Tech Lead giữ đường găng.
  - B: evidence và decision.
  - C: web và các package phía khách hàng.

## 7. Chủ dự án cần quyết định

| # | Câu hỏi | Chặn |
|---|---|---|
| O1 | Khách pilot, CI và engine | Bắt đầu M1 |
| O2 | Quy mô đội, Tech Lead / QA lead | Bắt đầu M1, đặt ngày |
| O3 | Cloud/region (có cần lưu dữ liệu ở VN?) | Dữ liệu pilot ở M2 |
| O9 | Case nào là critical | Chuyển policy sang chặn release |
| O10 | Chỗ chạy replay healing ở V1 (khuyến nghị: CI của khách) | 08.4.3 |
| O4, O5, O6, O7 | LLM provider, giá, ngưỡng false-heal, nhãn hiệu | M4, M3, M5, khi ra mắt |

## 7a. Mục còn mở đã biết (không phải "đã sửa hết")

- **Ước lượng là cận dưới:** các control security và dữ liệu thêm ở vòng review cuối (T70–T101) được nhét vào size task cũ, chưa ước lượng lại. Mỗi story phải ước lượng lại ở DoR; nên hiểu 672.3 ngày là mức tối thiểu.
- **T91/T101:** khoá `FOR SHARE` trên đường ingest được chấp nhận có điều kiện, phải đo trong load test 10.6.1-T1; nếu không đạt thì dùng phương án dự phòng đã ghi.
- Các `[OPEN]` còn lại trong feature plan là quyết định của chủ dự án (O1–O10) hoặc dữ kiện ⚠️ sẽ được kiểm khi build.

## 8. Bước tiếp theo

1. Chốt O1, O2, rồi đặt ngày cho các milestone.
2. Bắt đầu M0 theo README §5.1: 01.1.1 (toolchain), 01.9.1 (ADR), 12.1.1 (phỏng vấn khách).
3. Mỗi story chạy DoR (README §6) trước khi code; mọi thay đổi task thì chạy `scripts/regen_docs.py` và `scripts/check_features.py`.
