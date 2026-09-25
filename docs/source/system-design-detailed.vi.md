# 1 Kết luận và quyết định thiết kế
ReleaseHub là tên làm việc cho nền tảng tạo, quản lý, điều phối và phân tích kiểm thử tự động. Tài liệu này dành cho đội kỹ thuật, làm cơ sở chia module, xây hợp đồng API và triển khai theo phiên bản. Thiết kế mở rộng bản review trước; các lựa chọn công nghệ, ngưỡng tải và lịch trình dưới đây là đề xuất, chưa phải kết quả thử nghiệm hay cam kết SLA.
Đề xuất xây control plane bằng TypeScript, dùng PostgreSQL làm nguồn dữ liệu chính và object storage cho bằng chứng. Test chạy trong CI hoặc agent của khách trước; Playwright, Appium và framework unit hiện có chịu trách nhiệm thực thi. Kiến trúc có thể tự xây hoàn toàn, không phụ thuộc n8n.
### Quyết định cốt lõi
| Quyết định | Hệ quả triển khai |
| Một backend chia module và worker riêng | Tránh phân tán sớm; module sở hữu dữ liệu và API nội bộ rõ ràng. |
| Git giữ mã test đã duyệt | Studio tạo draft; mọi lần chạy ghim commit và revision bất biến. |
| Bằng chứng và policy quyết định release | Không lấy số test xanh trên dashboard làm quyết định cuối. |
| Healing bắt đầu ở chế độ đề xuất | Học từ lỗi đã xác minh; kiểm chứng bản sửa trước khi áp dụng. |
| Agent do khách vận hành trước | Giảm phạm vi hạ tầng chạy; vẫn phải bảo vệ secret và môi trường khách. |
### Safe healing có nghĩa gì
Hệ thống được phép thích nghi với thay đổi kỹ thuật đã được kiểm chứng, ví dụ locator bị đổi nhưng cùng đối tượng nghiệp vụ. Nó phải giữ nguyên ý định test, assertion và bằng chứng gốc. Không có thuật toán nào bảo đảm tuyệt đối rằng mọi bản sửa đều an toàn; thiết kế giảm rủi ro bằng giới hạn quyền, kiểm tra độc lập, phê duyệt và rollback.
### Bản đồ đọc tài liệu
Trang 3–5: phạm vi và yêu cầu. Trang 6–12: kiến trúc, công nghệ, runner, tạo test và healing. Trang 13–14: vòng đời và release gate. Trang 15–19: dữ liệu, API và tính nhất quán. Trang 20–22: bảo mật, vận hành và nghiệm thu. Trang 23–25: đánh đổi, kế hoạch và nguồn đối chiếu.
---PAGE---
# 2 Phạm vi sản phẩm và phân kỳ
Sản phẩm đầy đủ phục vụ cả đội đã có automation lẫn đội cần tạo test mới. Để triển khai được, mỗi phiên bản phải ghi rõ khả năng nhận kết quả, tạo test, chạy test và phân tích; việc nhận báo cáo Appium không đồng nghĩa đã có device farm.
| Năng lực | V1 Pilot | V2 Điều phối | V3 Mở rộng |
| Quản lý test | Catalog, suite, owner, Git revision | Draft, review, template tái dùng | Editor nghiệp vụ nếu có nhu cầu |
| Nhận kết quả | Playwright và JUnit đã kiểm chứng | Appium adapter sâu hơn | Framework khác theo khách |
| Chạy test | CI hiện có của khách | Agent web/API; Android có điều kiện | iOS và hosted runner theo chi phí |
| Phân tích | Run, release, flaky, bằng chứng | So sánh revision và root cause | Phân tích xu hướng nâng cao |
| Healing | Thu thập lỗi; shadow, không tự áp dụng | Đề xuất locator và PR có duyệt | Tái dùng bản sửa đã duyệt theo policy |
| Thương mại | Pilot trả phí, quota, retention | Thuê bao và tính mức dùng | Gói enterprise khi có người vận hành |
### Giới hạn của bản đầu
V1 chọn một CI phổ biến trong nhóm khách pilot, Playwright TypeScript và một biến thể JUnit rõ ràng. Hỗ trợ thêm framework phải đi qua bộ fixture và hợp đồng adapter. Dashboard vẫn phân loại web, API, mobile, unit ngay từ đầu; độ sâu tạo/chạy của mỗi nhóm khác nhau.
### Điều kiện chuyển phiên bản
V2 chỉ bắt đầu khi V1 nhận kết quả đáng tin, có khách dùng lặp lại và đo được nhu cầu điều phối. Healing chỉ mở quyền tạo PR khi bộ kiểm chứng bắt được các bản sửa nguy hiểm và có người chịu trách nhiệm duyệt. V3 cần dữ liệu chất lượng, ngân sách chạy và cơ chế tắt tự động khi chất lượng giảm.
Khách hàng, số kỹ sư và ngân sách chưa được xác nhận. Mọi mốc trong tài liệu là thứ tự ưu tiên; không coi hỗ trợ tất cả nền tảng là điều kiện để bán pilot đầu tiên.
---PAGE---
# 2 Phạm vi từng nhóm kiểm thử
Lưu riêng ba trục: platform gồm web, Android, iOS, backend; level gồm unit, integration, E2E; purpose gồm functional, contract, regression. API là interface. “Function test” cần làm rõ là kiểm thử hàm hay kiểm thử chức năng nghiệp vụ.
| Nhóm | Engine đề xuất | Thiết kế riêng cần có |
| Web | Playwright TypeScript | Browser/version, locale, viewport, auth state, DOM và trace. Mobile emulation chỉ là web. |
| API | Playwright API ban đầu | OpenAPI import, schema, auth, dữ liệu setup, assertion và cleanup; mã phản hồi chưa đủ kiểm tra nghiệp vụ. |
| Mobile | Appium qua driver | Android dùng UiAutomator2; iOS dùng XCUITest trên macOS phù hợp. Ghim app digest, OS, device và driver. |
| Unit và function | Framework của repo | Ví dụ pytest, JUnit hoặc framework JS; thu report qua adapter. Không tạo một engine thay mọi ngôn ngữ. |
### Ví dụ xuyên suốt về đặt hàng
Unit kiểm tra hàm tính tổng và làm tròn tiền. API kiểm tra tạo đơn, quyền truy cập và trạng thái sau giao dịch. Web kiểm tra người dùng hoàn tất checkout. Mobile kiểm tra cùng nghiệp vụ trên app. Các test cùng gắn requirement “tạo đơn”, nhưng vẫn là các case và bằng chứng riêng.
Suite API có namespace dữ liệu theo run, tài khoản thử và idempotency key của nghiệp vụ. Suite UI xác minh đúng đơn vừa tạo thay vì chỉ thấy thông báo thành công. Với payment hoặc gửi email, dùng sandbox; không tự retry trên tài khoản production.
### Tác động lên healing
Web locator là phạm vi thử nghiệm đầu tiên. Mobile locator chỉ mở sau khi có hierarchy, context native/webview và bộ kiểm chứng riêng. API schema, status mong đợi, công thức unit và snapshot nghiệp vụ không được tự sửa để khớp output mới. Hệ thống có thể đề xuất regression test cho bug đã xác nhận; người duyệt quyết định đặc tả đúng.
Playwright cung cấp reporter, API testing và cơ chế locator; Appium chia khả năng theo driver [1–4]. JUnit cần mapping theo producer cụ thể vì cấu trúc và extension có thể khác nhau [5].
---PAGE---
# 3 Yêu cầu và trải nghiệm kỹ thuật
Ba người dùng chính là QA engineer quản lý suite và triage; developer sửa ứng dụng hoặc test; QA lead quản lý policy, quyền và báo cáo phát hành. Tenant admin quản lý tổ chức, quota và tích hợp. Vai trò có thể kiêm nhiệm ở đội nhỏ nhưng quyền vẫn phải tách.
| Màn hình | Tác vụ chính | Bằng chứng cần hiển thị |
| Project và Catalog | Kết nối repo, lọc test, gán owner | Test ID, revision, loại test, lịch sử rename |
| Test Studio | Tạo draft và review patch | Diff, assertion, dữ liệu, dry run, người duyệt |
| Run và Release | Khởi chạy, hủy, xem gate | Build digest, manifest, shard thiếu, từng attempt |
| Failure Inbox | Phân loại và giao người xử lý | Log, trace, lỗi gốc, lỗi hạ tầng, issue liên quan |
| Healing Review | Duyệt hoặc bác bỏ đề xuất | Patch, kiểm chứng, rủi ro, lineage, rollback |
| Settings | Agent, secret reference, policy | Quyền, audit, quota và thời hạn lưu |
### Luồng người dùng cần hoàn chỉnh
Onboarding: tạo project → kết nối repo/CI → chạy một suite mẫu → đối chiếu report gốc → bật thu thập thường xuyên. Tạo test: chọn template → nhập yêu cầu và assertion → dry run → review → merge → chọn suite. Điều tra: mở failure → phân loại → sửa sản phẩm hoặc đề xuất healing → chạy lại revision mới → so sánh.
### Yêu cầu phi chức năng đề xuất
Không có đường truy cập chéo tenant trong các test phân quyền. Event chỉ được ACK sau commit bền vững. Run thiếu bằng chứng bắt buộc không được PASS. Ở tải pilot, p95 ingest batch dưới 1 giây và quyết định dưới 60 giây sau khi đủ dữ liệu là mục tiêu đo, không phải bảo đảm sẵn có.
### Tiêu chí UX bắt buộc
“Complete” chỉ mô tả thu thập xong; PASS/FAIL/INCONCLUSIVE mô tả đánh giá. Flaky và healed phải có nhãn riêng. Màn hình luôn giữ lỗi trước healing, lý do thiếu dữ liệu và revision đã dùng. Phê duyệt healing không đồng nghĩa phê duyệt phát hành.
---PAGE---
# 4 Kiến trúc tổng thể
@diagram architecture
Hình 1  Control plane lưu metadata và quyết định; data plane thực thi test. Mũi tên thể hiện trao đổi HTTPS và dữ liệu, không trao quyền shell cho control plane.
### Ranh giới module
Identity kiểm tra người dùng và service identity. Catalog sở hữu test/suite/revision. Orchestrator lập kế hoạch và lease job. Ingestion nhận event. Evidence quản lý artifact. Policy tạo quyết định bất biến. Healing tạo và kiểm chứng candidate. Usage ghi mức dùng để tính phí.
Backend triển khai dạng modular monolith; API và worker là process riêng cùng codebase. Mỗi module có interface, migration và chủ sở hữu logic. Worker đọc job bền vững trong PostgreSQL; object storage giữ trace, video và raw report. Search và analytics ban đầu dùng bảng đọc/index phù hợp.
### Hai chế độ thực thi
CI mode: CLI đăng ký run rồi CI tự chạy engine và upload kết quả. Agent mode: agent chủ động kéo job được phép, chạy trong sandbox của khách và gửi event. Không mở cổng inbound vào mạng khách. Hosted runner là một data plane khác ở giai đoạn sau, phải có cô lập mạnh hơn cho mã không tin cậy.
### Tách đường healing
Lỗi đi vào phân loại và kho bằng chứng trước. Healing worker nhận dữ liệu đã lọc, không có production secret hay quyền sửa policy. Candidate chỉ thành mã test chính thức qua luồng duyệt và Git; kết quả shadow không ghi đè run gốc.
---PAGE---
# 4 Lựa chọn công nghệ
Đề xuất này ưu tiên một đội nhỏ có kinh nghiệm TypeScript. Nếu đội hiện tại mạnh Java hoặc .NET, có thể đổi backend nhưng giữ nguyên hợp đồng, state machine và ranh giới quyền. Chốt phiên bản được hỗ trợ, license và security patch khi bắt đầu triển khai; không ghim “latest” trong runner.
| Lớp | Lựa chọn đề xuất | Lý do và đánh đổi |
| Web app | React và TypeScript | Phù hợp giao diện catalog, diff và report; UI không tự tính release gate. [10] |
| Backend | NestJS trên Node.js LTS | Module và API rõ; thuận tiện dùng chung type. Công việc nặng chạy worker riêng. [11] |
| Dữ liệu | PostgreSQL, SQL migration | Transaction, constraint, JSONB có kiểm soát, RLS; cần quản lý index và vacuum. [6] |
| Artifact | S3 hoặc dịch vụ tương thích | Upload trực tiếp qua URL ngắn hạn; kiểm chứng API tương thích trước khi đổi provider. [8] |
| Job ban đầu | PostgreSQL và outbox | Ít thành phần vận hành; không giữ transaction trong suốt thời gian chạy test. [7] |
| Runner | CLI và agent TypeScript | Tái dùng reporter; pin engine/image. Chỉ cân nhắc Go khi có nhu cầu phân phối đã đo. |
| Observability | OpenTelemetry và collector | Gắn trace/run/job ID; xuất vào backend quan sát được chọn. [12] |
### Công nghệ cho học và healing
V1 dùng rule, fingerprint lỗi và truy hồi lịch sử trong PostgreSQL. V2 thêm LLM gateway với JSON schema, ngân sách, timeout và provider adapter. Có thể thêm vector search khi tìm kiếm theo nghĩa chứng minh lợi ích; chưa cần vector database riêng. Không fine-tune ngay khi chưa có nhãn chất lượng và quyền sử dụng dữ liệu.
### Những thành phần chưa cần
Không bắt buộc n8n, Kafka, Kubernetes hoặc Temporal ở V1. n8n có thể nhận webhook cho thông báo. Temporal đáng thử khi workflow nhiều bước, chờ duyệt dài và phục hồi trạng thái làm queue tự xây khó duy trì; vẫn phải xử lý idempotency của tác động bên ngoài [13].
---PAGE---
# 4 Runner và hợp đồng adapter
Agent có identity riêng theo tenant và pool, chỉ kéo job có capability phù hợp. JobSpec bất biến chứa repo/commit, suite revision, app digest, environment revision, engine image digest, timeout, resource limit, secret reference và artifact policy. Không nhận chuỗi shell tùy ý từ người dùng ít quyền.
| Interface | Đầu vào | Đầu ra hoặc nghĩa vụ |
| discover | Commit, suite và config | Danh sách case ID, variant và shard plan; không gọi side effect nghiệp vụ. |
| prepare | JobSpec và secret refs | Sandbox, dữ liệu thử, engine; báo lỗi setup riêng. |
| execute | Manifest đã khóa | Event cho từng attempt; deadline và cancel token. |
| collect | Thư mục cho phép | Artifact inventory, checksum; không lấy file ngoài workspace. |
| cleanup | Run namespace | Dọn tài nguyên idempotent; báo riêng nếu dọn thất bại. |
### Capability và tương thích
Adapter khai báo protocol major/minor, engine family/version, platform, hỗ trợ discovery, step events, retry và cancellation. Server từ chối major không tương thích. Adapter có thể “ingest only”; khi không chứng minh được manifest đầy đủ, report được xem nhưng chưa đủ điều kiện release gate.
### Cô lập và thiết bị
Web/API dùng workspace dùng một lần, user không đặc quyền và giới hạn tài nguyên. Mobile cần lease độc quyền cho device, reset app/data theo policy, giới hạn cổng Appium và lưu app checksum. iOS cần pool macOS phù hợp cùng quy trình signing; không hứa hỗ trợ iOS bằng Linux container [4].
### Khi agent mất kết nối
Heartbeat đề xuất 15 giây, lease 90 giây, có jitter và điều chỉnh sau đo. Hết lease tăng fencing epoch; event worker cũ được lưu làm bằng chứng stale, không hoàn tất job mới. Trước khi chạy lại job có side effect, phải xác nhận môi trường đã cô lập hoặc dọn xong. Fencing trong database không dừng được tác động đang diễn ra trong ứng dụng.
---PAGE---
# 4 Tạo và quản lý test
Git là nguồn chuẩn cho mã test đã duyệt; PostgreSQL giữ metadata và ánh xạ revision. Mỗi case có UUID ổn định trong annotation hoặc sidecar manifest. Đổi tên hoặc di chuyển file không tạo case mới. Import không có ID tạo identity tạm từ producer, suite và tên; UI yêu cầu mapping khi thấy collision hoặc rename.
### Quy trình authoring
Người dùng chọn template theo loại test, khai báo precondition, action, assertion và cleanup. Studio tạo draft revision, kiểm tra cú pháp và discovery, chạy dry run ở sandbox, rồi tạo patch để review. Sau merge, webhook đăng ký commit và suite revision chính thức. V1 có thể chỉ cung cấp template và Git flow; editor đầy đủ ở V2.
API template nhập OpenAPI để dựng request skeleton, không suy ra assertion nghiệp vụ từ schema. Web template ưu tiên role/name hoặc test ID có contract [3]. Unit giữ framework trong repo. Mobile template lưu platform capability và fixture riêng, không tái sử dụng máy móc locator web.
### Mô hình suite và dữ liệu
Suite revision chốt danh sách case/variant, thứ tự phụ thuộc thật sự, shard strategy, retry policy và yêu cầu môi trường. Test độc lập là mặc định; dependency được khai báo thành DAG, từ chối cycle. Setup lỗi tạo blocked cho test phụ thuộc, không tạo passed.
Dữ liệu thử dùng namespace tenant/project/run và seed có phiên bản. Secret chỉ lưu reference. Fixture có timeout, cleanup và cơ chế dọn tài nguyên mồ côi theo TTL. Biến động dữ liệu không được xử lý bằng cách tự nới assertion.
### AI tạo test và bug thành regression
AI nhận yêu cầu đã được người dùng duyệt và context cho phép; xuất draft mã và giải thích assertion. Bug được xác nhận có thể tạo candidate regression: tái hiện fail trên build lỗi và pass trên build đã sửa khi các build còn khả dụng. Nếu không có build đối chứng, ghi rõ giới hạn kiểm chứng. Mã AI chưa duyệt không chạy trong môi trường có quyền nhạy cảm.
---PAGE---
# 4 Ranh giới safe healing
Healing sửa cơ chế kiểm thử để duy trì ý định đã được xác nhận. Hệ thống trước hết phân loại lỗi thành product defect, test drift, environment/infra, data issue hoặc unknown. Nhãn tự động là giả thuyết kèm bằng chứng; unknown không kích hoạt tự áp dụng.
| Loại thay đổi | Xử lý mặc định | Kiểm chứng cần có |
| Locator đổi nhưng cùng đối tượng | Được tạo candidate | Identity nghiệp vụ, unique match, action và hậu điều kiện giữ nguyên |
| Worker chết hoặc lỗi mạng tạm | Retry hạ tầng có giới hạn | Job cho phép retry; dữ liệu và side effect được cô lập |
| Timeout hoặc đồng bộ UI | Chỉ đề xuất có review | Chứng minh điều kiện chờ; không che performance regression |
| API schema, expected status, giá trị | Không tự áp dụng | Product owner xác nhận đặc tả; sửa test theo quy trình thông thường |
| Bỏ assertion, skip, force click | Cấm trong healing tự động | Chuyển về sửa test thủ công và review |
| Snapshot ảnh hoặc unit output đổi | Không tự cập nhật baseline | Xác nhận thay đổi sản phẩm và review độc lập |
### Các bất biến bắt buộc
Không xóa lỗi ban đầu; không đổi test ID, build, policy hoặc dữ liệu để lấy kết quả xanh. Không thêm catch nuốt lỗi, mock kết quả nghiệp vụ, vô hiệu network check hoặc tăng retry vô hạn. AST validator giới hạn node được sửa; hash assertion trước/sau phải giữ nguyên đối với locator healing.
Giữ nguyên assertion là điều kiện cần, chưa đủ: locator mới có thể trỏ sai nút nhưng assertion yếu vẫn pass. Vì vậy phải kiểm tra semantic identity, unique match và hậu điều kiện độc lập. Với thanh toán, phân quyền, xóa dữ liệu hoặc case bảo mật, mặc định chỉ đề xuất và luôn cần người duyệt.
### Ví dụ cần từ chối
Nút “Thanh toán” biến mất do bug, nhưng candidate tìm thấy “Lưu đơn”. Dù click thành công, hệ thống không được chấp nhận chỉ dựa vào độ giống DOM. Phải chặn vì thay đổi hành động nghiệp vụ; chuyển failure cho developer.
---PAGE---
# 4 Luồng kiểm chứng và áp dụng healing
@diagram healing
Hình 2  Candidate đi qua kiểm tra quyền và ngữ nghĩa trước khi chạy shadow. Bất kỳ gate nào thất bại đều giữ lỗi gốc và chuyển triage.
### Pipeline có giới hạn
Thu error, DOM/accessibility snapshot đã lọc, locator cũ, trace và revision → phân loại → truy hồi bản sửa cùng project → sinh tối đa 3 candidate → kiểm tra allowlist AST → kiểm chứng trong sandbox. Mặc định tối đa 2 lượt xác minh cho mỗi candidate; quota và deadline theo project chặn vòng lặp vô hạn.
Baseline và candidate chạy trên cùng app digest, fixture và môi trường có thể so sánh. Kiểm tra unique match, role/name/test ID, vùng nghiệp vụ và hậu điều kiện độc lập. Thêm negative control: tình huống sản phẩm sai phải tiếp tục fail. Nếu không thể tái hiện ổn định, candidate ở trạng thái insufficient_evidence.
### Duyệt và rollout
PR chứa diff, lỗi gốc, context hash, kết quả đối chứng, assertion hash và thời hạn. Người duyệt có quyền test maintainer; case nhạy cảm cần reviewer khác tác giả. Phê duyệt gắn với candidate hash và base commit; đổi context hoặc rebase phải kiểm chứng lại.
Sau merge, chạy run mới và canary trên tập suite đã chọn. Nếu regression hoặc nghi false heal, tắt rule ngay, thu hồi candidate và revert bằng PR. Không viết lại quyết định release cũ. Tự áp dụng runtime bị tắt ở V1/V2; V3 chỉ xem xét tái dùng mapping đã duyệt trong context giới hạn.
---PAGE---
# 4 Học từ lỗi và đánh giá chất lượng
“Tự học” ban đầu là tích lũy tri thức đã xác minh và cải thiện xếp hạng candidate, không phải tự cập nhật trọng số mô hình sau mọi lỗi. Mỗi incident lưu fingerprint, version, context, root cause do người xác nhận, patch, kết quả kiểm chứng và phản hồi sau rollout.
| Thành phần | Dữ liệu và quy tắc |
| Knowledge record | Tenant/project, engine, test revision, fingerprint, locator cũ/mới, context hash, nhãn và nguồn nhãn |
| Candidate ranker | Ưu tiên bản sửa từng được duyệt trong cùng context; similarity chỉ dùng xếp hạng, không cấp quyền |
| Nhãn phản hồi | accepted, rejected, reverted, unknown; run pass đơn lẻ không tự tạo nhãn đúng |
| Version và lineage | Lưu rule/model/prompt version, record được truy hồi, patch hash và validation run IDs |
| Thu hồi | Record sai bị revoked; candidate phụ thuộc được đánh dấu và dừng áp dụng |
### Bộ đánh giá trước khi mở quyền
Tạo tập lỗi locator thật, bug sản phẩm thật, UI đổi nghiệp vụ, phần tử trùng tên, nhiều locale, network lỗi và dữ liệu lỗi. Chia train/retrieval và holdout theo thời gian hoặc project để tránh rò rỉ cùng sự cố. Người đánh giá xem bằng chứng độc lập; dữ liệu khách không chia sẻ xuyên tenant nếu chưa có đồng ý rõ ràng.
Đo candidate precision, false-heal rate, tỷ lệ bỏ sót bug, tỷ lệ cần người duyệt, rollback rate và chi phí mỗi bản sửa được chấp nhận. False heal là bản sửa được áp dụng nhưng làm mất khả năng phát hiện lỗi hoặc đổi ý định test; mẫu số là số bản sửa áp dụng đã được adjudicate. Trường hợp unknown báo riêng.
### Điều kiện an toàn
Trước V2, toàn bộ ca cấm trong bộ nghiệm thu phải bị chặn; chưa đủ để kết luận an toàn ngoài thực tế. Với 0 lỗi trên 300 mẫu độc lập, cận trên gần đúng 95% vẫn khoảng 1%; lỗi thường tương quan nên độ tin cậy thực tế có thể thấp hơn. Không dùng “confidence 0.99” của LLM làm xác suất an toàn.
LLM hoặc kho tri thức hỏng thì test và release gate vẫn chạy bình thường; healing chuyển unavailable. Kill switch toàn hệ thống, tenant và project phải hoạt động trước khi bật tính năng áp dụng.
---PAGE---
# 5 Vòng đời chạy test và trạng thái
Một run là lần kiểm thử logic với manifest bất biến; job là đơn vị giao cho runner; attempt là từng lần thực thi case. Retry upload, retry job và retry test là ba thao tác khác nhau, có ID và audit riêng.
| Đối tượng | Trạng thái | Quy tắc chuyển |
| Run | created → queued → running → collecting → complete | Chỉ complete khi reconcile đủ manifest; có nhánh incomplete và canceled |
| Job | queued → leased → running → finished | lost hoặc canceled giữ lịch sử; attempt mới dùng epoch mới |
| Case attempt | passed, failed, skipped, timed_out, error, blocked | Giữ raw status; normalized status không ghi đè kết quả gốc |
| Healing candidate | proposed → validated → approved → applied | Nhánh rejected, expired, revoked; mọi chuyển có actor và hash |
### Trình tự chuẩn
a) CI đăng ký release component digest và test commit. Discovery lập danh sách case/variant và shard. API khóa manifest cùng suite, policy, fixture và environment revision.
b) Scheduler chọn pool có capability, cấp lease và quota. Runner chuẩn bị sandbox, resolve secret reference và chạy đúng JobSpec. Event ghi từng attempt; upload artifact tách riêng.
c) Producer finalize với expected count, sequence cuối và shard ID. Reconciler chờ event đã ACK, đối chiếu count/ID và artifact bắt buộc, rồi tạo evidence revision hoàn chỉnh hoặc incomplete.
d) Policy evaluator dùng snapshot để tạo decision; webhook báo CI và UI. Decision API đọc revision chính xác. Sau khi hoàn tất, cleanup và retention chạy độc lập, có cảnh báo nếu lỗi.
### Hủy và chạy lại
Cancel tạo yêu cầu bền vững, agent dừng mềm rồi kill theo timeout cấu hình. Trong lúc race, vẫn nhận bằng chứng đã chạy nhưng run canceled không đổi thành complete. Rerun tạo run mới có parent_run_id; chỉ rerun case lỗi phải khai báo subset, không được giả làm đủ regression suite.
---PAGE---
# 5 Chính sách đánh giá release
Một release có thể gồm web commit, API image digest và mobile app digest khác nhau. Manifest ghi map component → digest và liên kết run tương ứng. Không ghép run “gần nhất” theo thời gian nếu build không khớp. Environment revision và dependency version ảnh hưởng kết quả cũng được lưu.
| Bằng chứng | Quyết định mặc định | Diễn giải |
| Tất cả case bắt buộc đạt, đủ shard và artifact bắt buộc | PASS | Chỉ đúng với phạm vi, build và policy đã ghim |
| Có lỗi assertion bắt buộc đã xác nhận | FAIL | Giữ FAIL dù đồng thời còn thiếu bằng chứng khác |
| Thiếu shard, mismatch build, unknown status hoặc setup lỗi | INCONCLUSIVE | Không có bằng chứng đầy đủ để xác nhận đạt |
| Retry test fail rồi pass | Theo policy flaky | Pilot đề xuất chặn case critical; còn lại cảnh báo rõ |
| Candidate healing chỉ pass trong shadow | Giữ decision gốc | Không dùng shadow thay bằng chứng chính thức |
### Quy tắc policy v1
Policy revision định nghĩa case/suite bắt buộc, allowed skip, flaky budget, artifact bắt buộc, timeout thu thập và freshness. Expected count bằng 0 là INCONCLUSIVE trừ suite khai báo không có case và được policy cho phép. Case critical bị skip/blocked không được PASS. Quarantine có owner, lý do và expiry; vẫn hiển thị failure.
Evidence completeness, test outcome và gate decision là ba trường riêng. Có lỗi thật cùng missing shard thì FAIL kèm completeness=false; không làm mất thông tin thiếu dữ liệu. Mỗi evaluation lưu manifest hash, evidence revision và policy hash để tái tạo.
### Waiver và bằng chứng đến muộn
Waiver là quyền chấp nhận rủi ro của con người, tách khỏi test result; cần scope, lý do, approver và expiry. Báo cáo hiển thị “được duyệt bằng waiver”, không đổi test failed thành passed. Event đến muộn có thể tạo evaluation revision mới theo policy, nhưng decision đã dùng release vẫn bất biến và truy vấn được.
---PAGE---
# 6 Mô hình dữ liệu tổng thể
Quan hệ chính: organization → project → test_case → test_revision; suite_revision liên kết các test_revision qua suite_member. Release có nhiều release_component và run. Run có nhiều job; job có nhiều case_attempt; event và artifact tham chiếu đúng run/job/attempt. Evaluation trỏ vào evidence_revision và policy_revision.
| Nhóm bảng | Khóa và trường chính | Bất biến |
| Organization và access | org_id, project_id, membership, role | Mọi dữ liệu nghiệp vụ có tenant scope |
| Test catalog | case_id, revision_id, commit, file, labels | ID ổn định; revision đã chạy không sửa |
| Suite và fixture | suite_revision, member, variant, fixture_revision | Membership và tham số snapshot theo run |
| Release và run | release_id, component_digest, manifest_hash | Không gộp khác build thành cùng bằng chứng |
| Execution | job_id, attempt_id, epoch, producer_id | Một lần thực thi có identity riêng |
| Evidence | event_id, payload_hash, artifact_id, checksum | Event gốc append only trong thời hạn lưu |
| Decision | evaluation_id, evidence_rev, policy_rev, decision | Đánh giá lại tạo revision mới |
| Healing | incident, candidate, validation, approval, knowledge | Patch/hash và lineage truy được tới lỗi gốc |
### Tham số hóa và coverage
Variant dùng hash của tập tham số đã chuẩn hóa nhưng không chứa secret. Mỗi case × variant × target environment là một planned execution. Browser/device matrix được lưu rõ để Firefox fail không bị lần chạy Chromium pass ghi đè. Coverage trong UI là tỷ lệ thực thi bộ đã khai báo; không gọi là coverage mã nguồn nếu chưa nhập dữ liệu instrumentation.
### Dữ liệu có cấu trúc và JSONB
Các trường dùng lọc, phân quyền, join và constraint là cột typed. JSONB giữ producer metadata và extension có schema/version, không thay thế toàn bộ mô hình. Raw report nằm ở object storage; database giữ checksum, parser version và mapping warning. Lưu thời gian UTC; duration ưu tiên đồng hồ monotonic phía runner.
---PAGE---
# 6 Ràng buộc lưu trữ và index
| Bảng hoặc khóa | Constraint đề xuất | Index và truy vấn chính |
| project | UNIQUE org_id, project_id | Liệt kê project theo membership |
| test_revision | UNIQUE org_id, case_id, revision_hash | Case history theo created_at |
| run | UNIQUE org_id, project_id, idempotency_key | project_id, created_at, id cho cursor |
| ingest_event | UNIQUE org_id, run_id, event_id | run_id, producer_id, sequence |
| case_attempt | UNIQUE org_id, run_id, execution_key, attempt_no | run_id, status; case_id, started_at |
| artifact | UNIQUE org_id, object_key | run_id; expires_at cho retention |
| evaluation | UNIQUE org_id, run_id, evaluation_revision | run_id, revision giảm dần |
| usage_event | UNIQUE org_id, billing_key | tenant, period, metric |
### Toàn vẹn tenant và đồng thời
Foreign key nghiệp vụ dùng cặp org_id và object_id với unique key tương ứng, tránh liên kết sang tenant khác. RLS là lớp bảo vệ bổ sung: transaction đặt tenant context bằng SET LOCAL, app role không là owner hoặc BYPASSRLS; cấu hình FORCE phù hợp và kiểm tra đường admin [6].
Case/suite draft dùng optimistic concurrency với version hoặc ETag. Mọi approve candidate yêu cầu If-Match và patch_hash. Hai người cập nhật cùng base revision nhận 409/412 thay vì silently overwrite. Chỉ một evaluation revision được tạo cho cùng evidence/policy qua unique constraint.
### Tăng trưởng và xóa
Bắt đầu với bảng thường và index theo truy vấn đo được. Chỉ partition event theo thời gian khi kích thước và kế hoạch retention cho thấy lợi ích; unique constraint của PostgreSQL trên bảng partition có giới hạn cần thiết kế lại khóa trước migration. Không giả định dedupe toàn cục vẫn giữ nguyên sau partition.
Retention xóa object trước hoặc đánh dấu pending_delete, retry idempotent rồi cập nhật metadata. Xóa tenant phải bao phủ tri thức healing và bản sao lưu theo thời hạn công bố. Audit còn giữ phải tối thiểu dữ liệu và tuân theo chính sách lưu đã thống nhất.
---PAGE---
# 6 API điều khiển và chạy test
API REST dùng JSON, prefix /v1, cursor pagination và request_id. Project trong URL phải khớp token scope. Ví dụ bên dưới là hợp đồng đề xuất, cần chuyển thành OpenAPI và fixture trong M1; ID dạng viết tắt chỉ để minh họa.
| Endpoint | Quyền | Hành vi chính |
| POST /projects/{p}/runs | run:create | Khóa manifest; Idempotency-Key bắt buộc |
| GET /runs/{r} | run:read | Trạng thái và evidence revision |
| POST /runs/{r}/cancel | run:cancel | 202 yêu cầu hủy; không báo đã dừng ngay |
| GET /runs/{r}/decision | decision:read | Revision chính xác và lý do; chưa có trả pending |
| POST /agents/jobs/claim | job:claim | Chọn theo pool; trả lease, epoch, deadline |
| POST /jobs/{j}/heartbeat | job:execute | Gia hạn lease nếu epoch hiện hành |
### Tạo run
```json
{
  "mode": "ci",
  "release_id": "rel_demo",
  "suite_revision_id": "suite_rev_7",
  "test_commit": "commit_digest",
  "components": {"api": "sha256:app_digest"},
  "environment_revision_id": "env_rev_2",
  "manifest_ref": "manifest_verified_42",
  "policy_revision_id": "policy_rev_3"
}
```
Manifest được upload và xác minh trước; chứa case/variant, matrix và shard plan. Cùng Idempotency-Key và cùng canonical request hash trả run cũ; khác payload trả 409. Ghi run, planned execution và outbox trong transaction trước 201/200.
Lỗi chuẩn gồm code, message, request_id, retryable và details đã lọc secret. 401/403 cho xác thực/quyền; 409 cho xung đột; 422 cho schema hoặc manifest không hợp lệ; 429 kèm Retry-After. CLI dùng timeout và backoff có jitter; không retry mù thao tác có side effect.
---PAGE---
# 6 API event artifact và healing
| Endpoint | Hợp đồng |
| POST /runs/{r}/events | Batch ≤ 500 event và ≤ 1 MiB trước giải nén; giới hạn đề xuất phải load test |
| POST /runs/{r}/finalize | Producer, shard, last_sequence, count và manifest_hash; 202 chờ reconcile |
| POST /artifacts/upload-url | Scope run/attempt, media type, size, checksum; URL và key do server cấp |
| POST /artifacts/{a}/complete | Xác minh object/size/checksum trước trạng thái ready |
| POST /healing/{c}/approve | If-Match, patch_hash, validation_set, expiry; kiểm tra lại quyền và context |
| POST /healing/{c}/revoke | Lý do bắt buộc; vô hiệu áp dụng tương lai và ghi audit |
### Envelope event tối thiểu
```json
{
  "schema_version": 1,
  "event_id": "evt_unique", "producer_id": "shard_1",
  "sequence": 12, "job_id": "job_demo", "epoch": 3,
  "execution_key": "case_variant_browser",
  "attempt_no": 1, "type": "test.finished",
  "status": "failed", "duration_ms": 1280,
  "artifact_ids": ["artifact_demo"]
}
```
Server xác thực toàn batch, canonicalize và commit atomic. Schema sai hoặc ID trùng nhưng khác payload làm cả batch bị từ chối; response chỉ rõ index lỗi để client sửa. Event trùng cùng hash được ACK duplicate; event mới được ACK accepted. Producer sequence có uniqueness theo run/producer và không được tái dùng khi restart; dùng producer mới hoặc resume checkpoint.
Artifact dùng key duy nhất theo tenant/run/attempt/artifact; URL ký sẵn là bearer credential, hết hạn ngắn, không thay thế kiểm tra quyền [8]. Không cấp key cho client tự chọn. Webhook delivery có event ID, chữ ký, timestamp và retry; consumer phải dedupe. Healing approval không mang quyền merge Git trừ khi integration được cấp riêng.
---PAGE---
# 7 Tính nhất quán và phục hồi
| Tình huống | Cách xử lý | Điều phải giữ |
| ACK bị mất | Client gửi lại ID và payload cũ | Không đếm trùng và không tính phí trùng |
| Event sai thứ tự | Buffer hoặc upsert theo identity và sequence | Không dùng thời điểm nhận làm thứ tự chạy |
| Worker crash | Claim ngắn bằng SKIP LOCKED; lease và checkpoint | Không giữ DB lock suốt job [7] |
| CI mất mạng | Spool giới hạn dung lượng, retry tới deadline | Không tự rerun nghiệp vụ vì upload lỗi |
| Finalize đến trước event | Reconcile chờ sequence/count và manifest | Thiếu dữ liệu không complete |
| Upload object xong nhưng DB lỗi | Complete retry; sweeper dọn orphan | Không coi object tồn tại là đã được duyệt |
| Worker cũ sống lại | So epoch; lưu stale evidence riêng | Không ghi kết quả vào job thay thế |
### Outbox và lịch công việc
Transaction nghiệp vụ ghi outbox; worker gửi webhook hoặc tạo task tiếp theo rồi đánh dấu. Delivery là at least once; endpoint bên nhận cần idempotency. Không tuyên bố exactly once cho side effect ngoài database. Job quá số retry chuyển failed queue để con người xử lý, kèm runbook và dữ liệu đã lọc.
Scheduler chia lượt theo tenant/pool, áp concurrency cap và giới hạn hàng đợi. Job chờ device không giữ slot web. Deadline gồm queue timeout và execution timeout riêng. Khi pool thiếu capability, báo unschedulable thay vì treo vô hạn. Lock môi trường chỉ dùng khi suite không thể cô lập dữ liệu.
### Muộn và phát lại
Run có collection deadline và late window theo policy. Event sau deadline giữ timestamp nhận và không sửa snapshot cũ. Reprocess raw report với parser mới tạo evidence revision mới; rerun tạo execution mới. Hai thao tác này có ý nghĩa khác nhau và phải hiển thị riêng.
Các job kiểm tra consistency định kỳ phát hiện run treo, lease hết hạn, artifact orphan, usage thiếu và candidate hết hạn. Mỗi sửa chữa tự động phải idempotent, có audit và tránh chạy lại nghiệp vụ.
---PAGE---
# 8 Bảo mật và ranh giới quyền
| Vai trò | Phạm vi mặc định | Giới hạn |
| Viewer | Xem report được cấp | Không xem secret; artifact nhạy cảm cần quyền riêng |
| QA engineer | Tạo draft, run, triage | Không tự đổi policy release |
| Test maintainer | Duyệt test và healing | Không tự duyệt case nhạy cảm do mình tạo |
| Release approver | Policy và waiver | Waiver không sửa kết quả test |
| Tenant admin | Membership, integration, quota | Truy cập secret value không mặc định đi kèm |
| Agent service | Job được giao và upload | Không duyệt healing, policy hoặc đọc project khác |
### Mã test và CI không tin cậy
PR từ fork không nhận secret nhạy cảm hay chạy trên pool nội bộ có quyền cao. Sandbox dùng một lần, network allowlist và identity ít quyền; hosted multi-tenant cần VM/microVM hoặc ranh giới tương đương đã kiểm chứng. Container đơn lẻ không được mặc định là đủ. GitHub cũng nhấn mạnh rủi ro của mã không tin cậy trong CI [9].
### Artifact và secret
Secret giữ ở vault/CI của khách, resolve theo job và mask trước upload. DOM, ảnh/video và body có thể chứa dữ liệu cá nhân; cho phép tắt hoặc redact tại nguồn. Parser XML tắt external entities; giới hạn archive, decompression, path traversal và MIME. HTML/trace xem trên origin cô lập với CSP, không chung cookie ứng dụng.
### Quyền của AI
DOM, log và tài liệu truy hồi là dữ liệu không tin cậy, có thể chứa prompt injection. LLM chỉ xuất structured candidate; không có tool shell, production secret, quyền merge hay sửa policy. Rule engine độc lập kiểm tra output. Truy hồi bắt buộc tenant filter; context gửi ra provider phải được cho phép và lọc dữ liệu trước.
Audit lưu actor, action, object, hash trước/sau và request ID. Truy cập hỗ trợ cần scope và expiry. URL artifact ngắn hạn, token xoay vòng, webhook có chống replay. On-premise và data residency là lựa chọn triển khai cần chốt, chưa là cam kết của V1.
---PAGE---
# 9 Triển khai vận hành và sức chứa
Dev dùng dịch vụ local tương đương; staging tách account/secret và dữ liệu; production gồm load balancer, API replicas, worker replicas, PostgreSQL managed có backup và object storage. Đề xuất Docker image pin digest, migration mở rộng trước rồi thu hẹp sau; chưa cần Kubernetes khi vận hành đơn giản vẫn đáp ứng.
### Mô hình tải pilot để kiểm chứng
Giả định 5 project × 10 run/ngày × 2.000 kết quả = 100.000 kết quả/ngày. Nếu metadata trung bình 1 KiB/kết quả thì khoảng 98 MiB/ngày, gần 8,6 GiB/90 ngày trước index, retry và backup. 50 run × 20 artifact × 2 MB = 2 GB/ngày; retention 14 ngày khoảng 28 GB. Đây là phép tính giả định, không phải benchmark.
Burst nghiệm thu đề xuất: 50.000 event trong 60 giây, khoảng 833 event/giây, theo batch và quota. Video, trace và healing replay có thể chiếm chi phí lớn hơn database. Đo peak thực tế rồi quyết định pool và retention; không suy ra capacity production từ trung bình ngày.
| SLI hoặc mục tiêu nội bộ | Cách đo và hành động |
| Ingest p95 dưới 1 giây | Tại API, batch trong giới hạn; alert khi vượt kéo dài |
| Decision p95 dưới 60 giây | Từ đủ event/artifact bắt buộc đến evaluation bền vững |
| Availability pilot 99,5% tháng | Endpoint cốt lõi thành công; không dùng test pass rate |
| RPO 15 phút và RTO 4 giờ | Mục tiêu restore thảm họa; thử phục hồi trước pilot |
### Phục hồi và quan sát
Zero loss cho event đã ACK áp dụng crash thông thường trong phạm vi durability của database; thảm họa theo RPO, không cam kết tuyệt đối. Theo dõi queue age, lease loss, incomplete rate, parser errors, artifact failures và healing false positives. Log có trace_id/run_id; secret không là label metric.
Backup có PITR nếu provider hỗ trợ; kiểm tra restore theo lịch. Deploy canary, so decision shadow rồi rollout theo project. Rollback code không được phá schema; lỗi healing có kill switch riêng, không buộc tắt ingest.
---PAGE---
# 9 Nghiệm thu và chi phí sản phẩm
| Nhóm kiểm chứng | Ca bắt buộc trước mở quyền |
| Adapter và mapping | Retry, skip, parameterized, shard, XML lỗi, Unicode, artifact thiếu; đối chiếu report gốc |
| State và data | Duplicate, reorder, finalize sớm, cancel race, lease expiry, late event, mismatched digest |
| Tenant và secret | Cross-tenant API/FK/RLS, URL hết hạn, replay token, path traversal, prompt injection |
| Healing | Sai đối tượng, assertion yếu, đổi nghiệp vụ, context stale, approved patch bị sửa, kill switch |
| Release | Missing evidence không PASS; waiver/late revision không đổi lịch sử |
| Vận hành | Load burst, worker crash, object outage, DB restore, rollback và agent upgrade |
### Nghiệm thu healing theo giai đoạn
V1 chỉ shadow nên không tạo thay đổi vào repo; đo chất lượng và thời gian triage. V2 yêu cầu validation reproducible, review đúng hash và rollback thử thành công. Trước V3 phải có dữ liệu lỗi đủ đa dạng, đánh giá false heal theo mức nghiêm trọng và chốt rủi ro chấp nhận được. Chưa đủ mẫu thì giữ chế độ đề xuất.
### Mô hình chi phí
Chi phí tenant = phần API/DB phân bổ + object GB-tháng + egress + phút runner + phút device + token AI + giờ hỗ trợ. Usage ledger ghi source ID duy nhất và giá áp dụng theo kỳ; sửa sai bằng adjustment, không âm thầm sửa số đã chốt. Healing replay có quota và trần tiền riêng.
Gợi ý thương mại: phí onboarding, thuê bao theo project/concurrency kèm quota kết quả và artifact. Hosted runner, device và AI vượt quota tính riêng sau khi đo. V1 chạy tại CI của khách không phát sinh phút hosted runner trong hóa đơn của nền tảng; vẫn có cloud và support cost.
### Đo giá trị thực tế
So sánh thời gian triage và bảo trì test trước/sau, số incident lặp lại được xử lý đúng, tỷ lệ khách tiếp tục dùng và biên đóng góp sau chi phí. Tỷ lệ “healed thành công” cao nhưng bỏ sót bug tăng là kết quả không đạt. Chưa có dữ liệu để chốt giá bán hoặc doanh thu dự báo.
---PAGE---
# 10 Đánh đổi và rủi ro còn lại
| Quyết định | Phương án thay thế | Khi cần xét lại |
| Tự xây control plane | n8n cho workflow chính | Chỉ dùng n8n khi tác vụ ít trạng thái, không quyết định test/gate |
| Tái dùng engine | Tự viết browser/API engine | Không đề xuất; chi phí tương thích khó tạo lợi thế |
| PostgreSQL queue | Redis queue hoặc Temporal | Queue contention, workflow dài và phục hồi khó hơn chi phí vận hành thêm |
| Agent của khách | Hosted runner ngay | Có nhu cầu trả phí, cô lập đủ mạnh và unit economics đạt |
| Healing có review | Tự sửa lúc chạy | Chỉ cân nhắc phạm vi hẹp sau đánh giá độc lập và rollout có kiểm soát |
| Rule và retrieval | Fine-tune mô hình | Có nhãn đủ tốt, dữ liệu được phép dùng và benchmark chứng minh lợi ích |
Rủi ro lớn nhất là giá trị sản phẩm chưa được kiểm chứng, phạm vi đa nền tảng quá rộng và healing che bug. Giảm bằng pilot có dữ liệu thật, capability matrix minh bạch, giữ bằng chứng gốc và ưu tiên không áp dụng khi thiếu chắc chắn. Framework upgrade cần compatibility suite, agent canary và thời hạn hỗ trợ phiên bản.
# 11 Quyết định cần chốt
| Quyết định | Người chịu trách nhiệm dự kiến | Thời điểm |
| Khách pilot, CI và framework ưu tiên | Chủ dự án và QA lead | Trước M1 |
| Stack theo năng lực đội, ngân sách cloud | Tech lead | Trước chọn provider |
| Case critical và ý định nghiệp vụ | QA lead và product owner của khách | Trước bật gate |
| Dữ liệu được gửi AI và retention | Khách hàng và người phụ trách dữ liệu | Trước shadow healing |
| Quyền duyệt, quota, tiêu chí false heal | QA lead và tech lead | Trước V2 |
Các vai trò trên chưa phải phân công nhân sự thực tế. Thiết kế không yêu cầu chốt tất cả ngay; mỗi quyết định phải có chủ sở hữu trước mốc phụ thuộc.
---PAGE---
# 12 Lộ trình và backlog triển khai
Ước lượng tham khảo cho 2–3 kỹ sư có kinh nghiệm backend và QA automation, chưa gồm tuyển dụng hoặc device farm. Tổng thời gian phụ thuộc độ hoàn chỉnh test của khách. Một người triển khai nên giữ V1 ingest/reporting và healing shadow trước khi làm editor hoặc agent đa nền tảng.
| Mốc | Đầu ra có thể review | Điều kiện đi tiếp |
| M0  Tuần 1–2 | Phỏng vấn, mẫu report và lỗi thật; chốt CI/engine | Có đội sẵn sàng cung cấp dữ liệu và thử trả phí |
| M1  Tuần 3–5 | Schema, identity, manifest, CLI và adapter đầu | Fixture mapping, dedupe và quyền tenant đạt |
| M2  Tuần 6–8 | Report, artifact, policy advisory, audit | Run thiếu bằng chứng không PASS; restore đạt |
| M3  Tuần 9–12 | V1 pilot; incident labels và healing shadow | Khách dùng lặp lại; đo chi phí và chất lượng |
| M4  Tuần 13–18 | V2 agent web/API, draft workflow, healing PR | Validation và approval đúng hash; rollback đạt |
| M5  Sau đánh giá V2 | Android sâu hơn; iOS/hosted hoặc V3 theo nhu cầu | Có nhu cầu trả phí và đủ bằng chứng an toàn |
### Backlog hai sprint đầu
Sprint 1: repository/module skeleton; migration tenant/project/membership; stable test ID; manifest schema; identity và quyền project; adapter contract và fixture golden. Đầu ra demo là import một suite và xem đúng case/variant/build, kể cả dữ liệu trùng.
Sprint 2: event API atomic; artifact upload/complete; finalize/reconcile; run detail; evidence completeness; test cancel/retry/missing shard; OpenAPI và CLI quickstart. Đầu ra demo là tái tạo chính xác một run có lỗi và thiếu bằng chứng, không sinh PASS sai.
### Definition of done
Mỗi capability có API/schema version, migration, authorization, telemetry, failure path, compatibility fixture và hướng dẫn vận hành. Healing cần thêm validator, validation evidence, approval binding, audit và kill switch. Chỉ mở quyền theo project sau khi có báo cáo nghiệm thu; lịch trình không thay thế điều kiện chất lượng.
Điểm bắt đầu đề xuất: duyệt hợp đồng manifest và event trước, sau đó dựng một lát cắt hoàn chỉnh từ CI tới decision. Tích lũy dữ liệu lỗi cho healing ngay trong V1, phát triển quyền sửa sau khi có kiểm chứng.
---PAGE---
### Nguồn đối chiếu và phạm vi áp dụng
Đối chiếu ngày 23 tháng 9 năm 2026. Các nguồn dưới đây xác nhận khả năng hoặc giới hạn công cụ. Kiến trúc ReleaseHub, API, state machine, safe healing, ngưỡng nghiệm thu và lộ trình là thiết kế đề xuất trong tài liệu này; không phải chứng nhận của nhà cung cấp.
[1] Playwright reporters — kết quả có cấu trúc và reporter tùy chỉnh.
https://playwright.dev/docs/test-reporters
[2] Playwright API testing — request context và kiểm tra API.
https://playwright.dev/docs/api-testing
[3] Playwright best practices — locator và test độc lập.
https://playwright.dev/docs/best-practices
[4] Appium drivers và XCUITest installation — phạm vi platform.
https://appium.io/docs/en/latest/ecosystem/drivers/
[4a] XCUITest installation — yêu cầu môi trường macOS phù hợp.
https://appium.github.io/appium-xcuitest-driver/latest/getting-started/installation/
[5] pytest output — JUnit XML và lưu ý tương thích.
https://docs.pytest.org/en/stable/how-to/output.html
[6] PostgreSQL row security — RLS và quyền bypass.
https://www.postgresql.org/docs/current/ddl-rowsecurity.html
[7] PostgreSQL SELECT — SKIP LOCKED cho xử lý queue.
https://www.postgresql.org/docs/current/sql-select.html
[8] Amazon S3 presigned URLs — upload và giới hạn quyền URL.
https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html
[9] GitHub Actions secure use — bảo vệ workflow và runner.
https://docs.github.com/en/actions/reference/security/secure-use
[10] React — mô hình component cho giao diện.
https://react.dev/learn
[11] NestJS — framework backend TypeScript và module.
https://docs.nestjs.com/
[12] OpenTelemetry — thu thập traces, metrics và logs.
https://opentelemetry.io/docs/what-is-opentelemetry/
[13] Temporal — durable execution cho workflow.
https://docs.temporal.io/
