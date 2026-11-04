# Vicikyu 기능표

전수 조사일: 2026-11-03 · 검증: `dotnet build` 0 오류 · `dotnet test` 204/204 통과(기존 201 + 갭 보강 3) ·
개발 서버(<http://localhost:5121>) admin 세션으로 전 화면 셸 200 + 전 페이지 데이터 API 200/204 확인.

열 설명 — **구분**: 화면(사용자 흐름) / API(HTTP 엔드포인트) / 서비스(엔드포인트 미노출).
**검증**: ✅ = 자동화 테스트 + 서버 검증 모두 통과. 테스트는 `VolleyballClub.Tests` 서비스 통합
(ServiceHost) 또는 HTTP(TestHost) 레벨.

---

## 1. 인증 · 계정

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 화면 | 로그인 (`/Account/Login`) | 학번+비밀번호 로그인, CSRF 토큰 발급 | HttpApiTests, DatabaseBackupTests | ✅ |
| 화면 | 로그아웃 (`/Account/Logout`) | 세션 종료 | HttpApiTests | ✅ |
| 화면 | 접근 거부 (`/Account/AccessDenied`) | 권한 없음 안내 | MemberAdminAccessTests(권한 정책) | ✅ |
| API | `GET /api/auth/csrf` | 안티포저기 토큰 발급 | HttpApiTests | ✅ |
| API | `GET /api/auth/user` | 현재 세션 사용자(ID/역할) | HttpApiTests, DatabaseBackupTests | ✅ |
| API | `POST /api/auth/login` | 로그인(비활성 계정 거부 포함) | HttpApiTests | ✅ |
| API | `POST /api/auth/logout` | 로그아웃 | HttpApiTests | ✅ |

## 2. 홈 · 출석 체크 (부원)

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 화면 | 홈 (`/`) | 오늘 활동 상태, OTP 출석, 내 팀, 회비 배너 | OtpAttendanceTests, EndToEndScenarioTests | ✅ |
| 컴포넌트 | AttendanceStatusCard / AttendanceMemberList | 출석 상태 카드·오늘 출석자 목록 | AttendancePolicyTests | ✅ |
| API | `GET /api/my/activity` | 오늘 내 활동 상태 | OtpAttendanceTests, EndToEndScenarioTests | ✅ |
| API | `POST /api/my/attendance` | OTP 문자 출석 체크인 | OtpAttendanceTests, AttendancePolicyTests | ✅ |

## 3. 기록 · 캘린더 · 통계 (부원)

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 화면 | 기록 (`/records`) | 월간 활동 이력·출석, 학기 통계, 출석왕 랭킹 | RecordsTests, RankingTests | ✅ |
| API | `GET /api/my/calendar` | 월간 캘린더(예정/없음 표시 포함) | RecordsTests | ✅ |
| API | `GET /api/my/records` | 월간 기록 목록 | RecordsTests | ✅ |
| API | `GET /api/my/stats` | 내 학기별 통계 | RecordsTests(학기 귀속) | ✅ |
| 서비스 | `GetRankingAsync` | 출석왕 랭킹(공동 순위, 월/학기 필터) — **API/화면 미노출, 서비스+테스트만 존재** | RankingTests | ✅(서비스) |

## 4. 마이페이지 (부원)

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 화면 | 마이페이지 (`/my`) | 프로필, 색상 변경, 비밀번호 변경, 내 회비 | MemberColorTests, MemberPasswordResetTests, HttpApiTests | ✅ |
| API | `GET /api/my/profile` | 내 프로필 | HttpApiTests | ✅ |
| API | `PUT /api/my/profile/color` | 내 색상 변경(팔레트 검증) | MemberColorTests | ✅ |
| API | `PUT /api/my/profile/password` | 비밀번호 변경(현재 비밀번호 확인) | MemberPasswordResetTests | ✅ |

## 5. 회원 관리 (관리자)

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 화면 | 회원 목록 (`/admin/members`) | 검색·정렬·상태 필터·페이징 | MemberPageTests | ✅ |
| 화면 | 회원 상세 (`/admin/members/{id}`) | 정보 수정, 활동 이력, 회비, 수동 출석 | MemberAttributionTests, MemberAdminAccessTests | ✅ |
| 화면 | 단체 추가 (MemberBulkAdd) | xlsx 업로드 일괄 등록(미리보기 포함) | MemberBulkTests, MemberWorkbookTests | ✅ |
| API | `GET/POST /api/admin/members`, `GET/PUT /api/admin/members/{id}` | 회원 CRUD | MemberAdminAccessTests, HttpApiTests | ✅ |
| API | `POST /api/admin/members/search` | 목록 조회(페이징·필터) | MemberPageTests | ✅ |
| API | `POST /api/admin/members/bulk` | 일괄 등록 확정 | MemberBulkTests | ✅ |
| API | `POST /api/admin/members/import` | xlsx 미리보기 | MemberWorkbookTests | ✅ |
| API | `GET /api/admin/members/{id}/activities`, `/attendance` | 회원별 활동·출석 이력 | AdminActivityManagementTests | ✅ |

## 6. 활동 관리 · 출석 운영 (관리자)

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 화면 | 대시보드 (`/admin`) | 오늘 운동 상태·OTP·개시/마감 제어 | AdminActivityManagementTests, MidnightCloseTests | ✅ |
| 화면 | 활동 관리 (`/admin/activities`) | 활동 CRUD, 과거 활동 수정·일괄 저장 | AdminActivityManagementTests, AdminActivityCorrectionTests, AdminActivityBatchSaveTests | ✅ |
| 화면 | 출석 관리 (`/admin/attendance`) | 오늘 출석자 목록, 수동 추가/취소 | AttendancePolicyTests | ✅ |
| API | `GET/POST /api/admin/activities`, `PUT/DELETE …/{key}` | 활동 CRUD | AdminActivityManagementTests | ✅ |
| API | `GET/PUT /api/admin/activities/{key}/members…` | 활동 멤버 출석 조정 | AdminActivityCorrectionTests | ✅ |
| API | `GET /api/admin/activity/today` | 오늘 활동 조회(시작 전은 가상 '준비 중', 행 생성 없음) | ActivityStartTests | ✅ |
| API | `POST /api/admin/activity/new·otp` | 활동 시작(OTP 자동 생성), 활동 중 OTP 재발급 | OtpAttendanceTests | ✅ |
| API | `POST /api/admin/activity/new` | 마감 후 새 세션 시작 | StartNewActivityTests | ✅ |
| API | `POST /api/admin/activity/cancel·close·reopen` | 활동 취소·마감·재개 | ActivityCancelTests, MidnightCloseTests | ✅ |
| API | `GET/POST/DELETE /api/admin/attendance…` | 오늘 출석 조회·수동 추가/취소(팀 영향 확인) | AttendancePolicyTests | ✅ |

## 7. 팀 편성 (관리자)

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 화면 | 팀 편성 (`/admin/teams`) | 오늘 팀, 참가자 목록, 자동 편성, 이동/추가/제거, 게스트, 확정/취소 | TeamGenerationTests, ParticipantOrderTests | ✅ |
| API | `GET /api/my/teams`, `GET /api/admin/teams` | 오늘 팀(부원/관리자 뷰) | TeamGenerationTests | ✅ |
| API | `GET /api/admin/teams/participants` | 참가자·게스트 목록(순서) | ParticipantOrderTests | ✅ |
| API | `POST /api/admin/teams/generate` | 조별 자동 편성 | TeamGenerationTests | ✅ |
| API | `PUT/POST/DELETE /api/admin/teams/members…` | 팀원 이동/추가/제거 | TeamGenerationTests | ✅ |
| API | `POST /api/admin/teams/cancel·confirm` | 팀 취소·확정 | TeamGenerationTests | ✅ |
| API | `POST /api/admin/teams/guests` | 게스트 추가 | TeamGenerationTests | ✅ |
| API | `DELETE /api/admin/teams/guests/{guestId}` | 게스트 제거(팀 배치 제거 포함) | ~~갭~~ → **GuestRemovalTests (신규 보강)** | ✅ |

## 8. 회비 (관리자)

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 화면 | 회비 (`/admin/fees`) | 회비 기간 관리, 납부 상태 목록·수납 처리 | FeePeriodUpdateTests, FeePeriodDeleteTests | ✅ |
| API | `GET /api/my/fees`, `/current` | 내 납부 상태, 현재 기간 | FeeAuthorizationTests, HttpApiTests | ✅ |
| API | `GET/POST/PUT/DELETE /api/admin/fees…` | 회비 기간 CRUD | FeePeriodUpdateTests, FeePeriodDeleteTests | ✅ |
| API | `GET /api/admin/fees/{id}/statuses` | 납부 상태 목록(필터·검색) | FeePeriodUpdateTests | ✅ |
| API | `PUT /api/admin/fees/{id}/members/{userId}` | 수납 처리(메모) | FeeAuthorizationTests | ✅ |

## 9. 학기 (관리자)

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 화면 | 학기 (`/admin/semesters`) | 학기 등록·수정·삭제 | SemesterTests | ✅ |
| API | `GET/POST/PUT/DELETE /api/admin/semesters…` | 학기 CRUD | SemesterTests | ✅ |

## 10. 활동 기록 아카이브 · 시트 연동 (관리자)

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 화면 | 설정→활동 기록 가져오기/내보내기 (`/admin/settings`) | 학기별 탭 xlsx 병합 가져오기, 전체 내보내기 | ActivityArchiveTests | ✅ |
| 화면 | 미매핑 부원 관리 | 계정 미매칭 행 보관·연동 | ActivityArchiveTests | ✅ |
| API | `POST /api/admin/activities/import`, `GET …/export` | xlsx 가져오기·내보내기 | ActivityArchiveTests | ✅ |
| API | `GET /api/admin/unmapped-members`, `POST …/{id}/link` | 미매핑 부원 조회·계정 연동 | ActivityArchiveTests | ✅ |
| 서비스 | 시트 동기화(SheetSync) | 외부 시트 변경 감지·재동기화 | ActivityArchiveTests(SheetSync 시나리오) | ✅ |

## 11. 감사 로그 (관리자)

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 화면 | 감사 로그 (`/admin/audit`) | 최근 관리자 행위 이력 | 각 도메인 테스트(AuditLog 16곳 검증) | ✅ |
| API | `GET /api/admin/audit` | 최근 로그 조회(take) | HttpApiTests | ✅ |

## 12. 데이터베이스 백업 · 유지보수 (관리자)

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 화면 | 백업 설정(DatabaseBackupSettings, `/admin/settings` 내) | DB 내보내기/복원, 백업 보존 기간 | DatabaseBackupTests | ✅ |
| API | `GET /api/admin/database/export` | 전체 DB 다운로드 | DatabaseBackupTests | ✅ |
| API | `POST /api/admin/database/import` | 전체 복원(RESTORE 확인, 64MB 제한, 로그아웃) | DatabaseBackupTests | ✅ |
| API | `GET/PUT /api/admin/database/backup-retention` | 백업 보존 기간 조회·설정 | DatabaseBackupTests | ✅ |
| 서비스 | BackupScheduler · DatabaseMaintenance | 야간 자동 백업·유지보수 미들웨어 | DatabaseBackupTests | ✅ |

## 13. 공통 인프라

| 구분 | 기능 | 설명 | 관련 테스트 | 검증 |
| --- | --- | --- | --- | --- |
| 미들웨어 | ApiMiddleware · ApiValidation | 통일 에러 응답, 요청 값 검증(신뢰 경계) | HttpApiTests | ✅ |
| 도메인 | KstClock · MemberColors · DayMark | KST 기준 자정 처리, 색상 팔레트, 캘린더 날 표시 | MidnightCloseTests, MemberColorTests, RecordsTests | ✅ |
| 서비스 | SettingService(ISettingService) | **미사용** — 백업 보존 기간은 DatabaseBackupService가 Settings 테이블 직접 사용. 기능 영향 없음(리팩토링 대상, 본 목표 범위 밖) | — | — |

---

## 갭 분석 요약

전수 조사 결과(애플리케이션 인터페이스 62개 메서드 × 테스트 소스 대조 + HTTP 엔드포인트 전수 대조):

- **테스트 갭 1건**: 팀 게스트 제거(`DELETE /api/admin/teams/guests/{guestId}` / `RemoveGuestAsync`) —
  추가는 커버되나 제거(팀 배치까지 정리)만 무커버 → **GuestRemovalTests 신규 작성으로 보강 완료**.
- 그 외 전 기능 서비스 통합/HTTP 테스트 양쪽 중 최소 한 레벨 이상에서 커버(총 204개 테스트 통과).
- 노출되지 않은 구현 2건(서비스 레벨): 출석왕 랭킹(테스트 있음, API/화면 미연결), SettingService(미사용).
  기능 변경이 아니므로 본 목표에서는 표에 기록만 함.
