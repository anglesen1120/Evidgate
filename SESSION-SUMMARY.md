# Tóm tắt phiên làm việc — Evidgate (cập nhật 2026-09-25)

Bản tóm tắt cho chủ dự án, để không phụ thuộc tin nhắn chat. Nguồn chính thức vẫn là `DECISIONS.md` và `plans/00-overview.md`; nếu file này lệch với chúng thì chúng đúng.

## 1. Dự án là gì

- **Tên:** Evidgate (*evidence* + *gate*). Tên làm việc cũ "ReleaseHub" bị loại vì trùng một công ty có sẵn. Lý do và các kiểm tra trùng tên nằm ở `DECISIONS.md` §2.
- **Mục tiêu:** thu kết quả test tự động (web, API, mobile, unit) từ CI hoặc agent của khách, lưu thành bằng chứng bất biến gắn với build, rồi đưa ra quyết định release PASS / FAIL / INCONCLUSIVE theo policy có phiên bản.
- **Trạng thái:** mới là kế hoạch, chưa có code. Tài liệu gốc (tiếng Việt, .docx) nằm trong `docs/source/`.

## 2. Cấu trúc repo

| Đường dẫn | Nội dung |
|---|---|
| `DECISIONS.md` | Tên, kiến trúc đã chốt A1–A10, stack và phiên bản, quy tắc healing an toàn, sự kiện đã kiểm chứng, quyết định còn mở O1–O10, chênh lệch so với bản gốc R1–R10 |
| `plans/00-overview.md` | Hợp đồng chung: thuật ngữ, milestone, kiến trúc, trạng thái, API, NFR, quy trình viết feature plan, tổng ước lượng (§14), nhật ký thay đổi (§15) |
| `plans/01`–`12` | Kế hoạch theo mảng: epic → story → task, có size |
| `AGENTS.md` | Quy tắc sửa kế hoạch (size, id, chú thích ✅/⚠️) |

Kế hoạch 01–12 là mức tổng quan. Trước khi làm, mỗi story có thêm feature plan chi tiết trong `plans/features/`, theo quy trình ở 00 §12.

## 3. Số liệu hiện tại (00 §14)

| | Thô | Có buffer 20 % |
|---|---|---|
| V1 (M0–M3) | 416.5 | 499.8 |
| V2 (M4) | 113.75 | 136.5 |
| V3 (M5) | 13.25 | 15.9 |
| **Tổng (487 task)** | **543.5** | **652.2** |

Đơn vị là ngày công. Chia theo milestone (thô): M0 10.5 · M1 134 · M2 152.5 · M3 119.5 · M4 113.75 · M5 13.25.

Kiểm tra bằng script trên toàn bộ plan: tổng từng epic khớp bảng §12 (Estimate summary) của chính plan đó, và tổng các plan khớp bảng 00 §14, không có id treo, không có ô phụ thuộc viết bằng chữ, không task nào phụ thuộc task ở milestone sau, không có vòng phụ thuộc.

## 4. Các vòng review đã xong

- **Vòng 1:** sửa hợp đồng quyền (`environment:write`, `security_event:read`), thêm endpoint `environment revision` và so sánh report, thêm O9, tính lại O8.
- **Vòng 2 (19 phát hiện, đã sửa hết và reviewer kiểm lại):**
  - khóa môi trường theo từng môi trường;
  - `data_isolation` chỉ còn một nguồn (khai báo suite trong 03);
  - quyền `billing:read` cho `billing_operator`;
  - phá 4 vòng phụ thuộc và sửa các chỗ phụ thuộc ngược milestone;
  - đánh số lại story 09.5.
- **Quyết định O8 (2026-09-25):** giữ nguyên phạm vi, milestone là thứ tự + cổng nghiệm thu, không phải ngày.
- **Lưu ý bổ sung sau vòng 2:**
  - 11-CR-1 (ASVS) ghi đúng là mới áp dụng một phần;
  - endpoint so sánh report không còn trả raw report qua `run:read`;
  - thêm O10 về nơi chạy replay healing ở V1.

## 5. Chủ dự án cần quyết định

| # | Câu hỏi | Cần trước |
|---|---|---|
| O1 | Khách pilot, CI và engine test của họ | Bắt đầu M1 |
| O2 | Quy mô đội và ai là Tech Lead / QA lead | Bắt đầu M1 |
| ~~O8~~ | **Đã chốt 2026-09-25 (chủ dự án xác nhận (b)):** giữ nguyên phạm vi; cột tuần ở 00 §4 chỉ là thứ tự, milestone kết thúc khi qua cổng nghiệm thu. Đã thử cắt V1: bỏ phần phụ còn 466.8 ngày công, bỏ thêm healing còn 403.8 — cả hai vẫn xa mốc 12 tuần. Ngày cụ thể đặt sau khi có O2 | — |
| O9 | Case nào là critical (QA lead và product owner của khách). Chưa chọn thì project chỉ ở chế độ advisory, không chặn release | Trước khi policy chuyển sang chặn release |
| **O10** | Khi healing V1 đề xuất bản sửa test, phải chạy lại test cũ và bản sửa trên máy nào để so sánh? Xem giải thích ở §5.1 | Trước feature plan 08.4.3 (M3) |

### 5.1 O10 giải thích ngắn

- Ở V1, healing chỉ **đề xuất** (shadow): khi một test hỏng vì locator đổi, hệ thống sinh bản sửa. Để biết bản sửa có đúng không, phải **chạy thật** test cũ và test đã sửa trên cùng build, cùng dữ liệu (story 08.4.3).
- Vấn đề: ở V1 Evidgate **không có máy chạy test nào của mình**. Quyết định A5 nói test chạy trong CI của khách; runner do Evidgate vận hành chỉ có từ V2.
- Vậy cần chọn chỗ chạy:
  - **(a) CI của khách:** CLI `evidgate` tải bản sửa về và chạy thêm một job trong CI của khách. Không đổi kiến trúc; tốn CI của khách; thêm khoảng 1 task trong 04.
  - **(b) Máy của Evidgate:** kéo phần runner cần thiết (05.7.1, 05.7.2, 05.7.4) và sandbox (11.2.1-T4) từ V2 về V1. Phải sửa A5, V1 tăng khoảng 12 ngày công thô (khoảng 20 nếu kéo cả 05.7), rủi ro bảo mật cao hơn vì chạy code của khách trên máy mình.
  - **(c) Chưa chạy ở V1:** V1 chỉ sinh và kiểm tra tĩnh bản sửa (AST, hash assertion); phần chạy thật lùi sang V2/M4. Rẻ nhất, nhưng số đo chất lượng healing ở pilot yếu hơn.
- Đề xuất: **(a)**, vì giữ A5, chi phí thấp và vẫn đo được chất lượng thật trong pilot.

Còn O3–O7 (cloud/region, nhà cung cấp LLM, giá, ngưỡng false-heal (chốt trước cổng V3), nhãn hiệu). Chi tiết ở `DECISIONS.md` §8.

## 6. Trạng thái khi dừng phiên (2026-09-25)

- Phiên này chỉ làm kế hoạch high-level; đã dừng theo yêu cầu. Chưa viết feature plan nào.
- O8 = (b) đã được chủ dự án xác nhận.
- Các sửa sau vòng review 2 chưa được reviewer kiểm tra riêng: endpoint so sánh report (04.4.1-T6, 00 §9.3), tiêu chí 08.4.3 → O10, dòng O8/O10. Nên kiểm tra hẹp các chỗ này ở đầu phiên sau.

## 7. Gợi ý cho các điểm chưa chốt

`RECOMMENDATIONS.md` ghi gợi ý cho O1–O10 và mọi câu hỏi mở trong §11 của các plan. Chủ dự án đồng ý dùng làm mặc định làm việc (2026-09-25). Đây chưa phải quyết định: mỗi dòng chỉ thành quyết định khi được chép vào `DECISIONS.md` §8 hoặc plan liên quan lúc review Definition of Ready của story cần nó. 06-O5 (emulator S3) đã đóng vì DECISIONS §4 đã trả lời.

Sau hai vòng review file gợi ý: O6 chuyển hạn thành "trước cổng V3" (08.8.3-T2, M5), theo quyết định của chủ dự án; các gợi ý đã khớp với FR đã chốt (Google Workspace vẫn có ở V1, backup nằm trong hạn xoá 30 ngày, raw report theo retention 14 ngày của artifact); đóng thêm 09-Q2 và câu hỏi tài khoản staging vì plan đã quyết.

## 8. Bước tiếp theo đề xuất

1. Chốt O1, O2. Có O2 (số người) mới đặt được ngày cho các milestone.
2. Chốt O10 trước khi viết feature plan 08.4.3.
3. Bắt đầu viết feature plan cho các story M0/M1 trong `plans/features/`.
4. Repo chưa có commit git nào. Khi anh đồng ý, tạo commit đầu tiên.
