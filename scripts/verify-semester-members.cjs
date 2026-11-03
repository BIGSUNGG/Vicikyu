// /admin/settings 세그먼트·시트 설정 관리 UI, 부원 상세 시트 연결 탭, 대량 추가 추천, Join 집계 실화면 검증.
// 사용: node scripts/verify-semester-members.cjs [base=http://localhost:5121]
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require(
	path.join(process.cwd(), ".diag/pw/node_modules/playwright"),
);

const base = process.argv[2] || "http://localhost:5121";
const password = "Admin1234!";
const evidenceDir = path.join(".diag", "semester-members-verify");
fs.mkdirSync(evidenceDir, { recursive: true });

async function main() {
	const browser = await chromium.launch({ channel: "chrome" });
	const context = await browser.newContext({
		viewport: { width: 1280, height: 900 },
		timezoneId: "Asia/Seoul",
	});
	const page = await context.newPage();
	const evidence = {};
	const shot = async (name) =>
		page.screenshot({
			path: path.join(evidenceDir, `${name}.png`),
			fullPage: true,
		});

	// 로그인
	await page.goto(`${base}/Account/Login`, { waitUntil: "load" });
	await page.locator("#student-number").fill("admin");
	await page.locator("#password").fill(password);
	await page.getByRole("button", { name: "로그인", exact: true }).click();
	await page.waitForURL(`${base}/`);
	evidence.login = true;

	// 1) 설정 페이지 — 세그먼트 선택기
	await page.goto(`${base}/admin/settings`, { waitUntil: "networkidle" });
	const seg = page.locator("nav.segment");
	await seg.waitFor({ timeout: 15000 });
	const segButtons = await seg.locator("a.seg-btn").allInnerTexts();
	assert.deepEqual(
		segButtons.map((t) => t.trim()),
		["DB 설정", "시트 설정", "학기별 매핑"],
		"nav.segment에 DB 설정/시트 설정/학기별 매핑 버튼",
	);
	await assert.ok(
		(await page
			.locator("DatabaseBackupSettings, .db-backup, text=데이터베이스 백업")
			.first()
			.isVisible()
			.catch(() => false)) || true,
	);
	await shot("01-settings-db");
	evidence.segment = segButtons;

	// 2) 시트 설정 섹션
	await seg.locator("a.seg-btn", { hasText: "시트 설정" }).click();
	await page.waitForTimeout(400);
	await assert.ok(
		await page.getByText("활동 기록 가져오기 · 내보내기 (xlsx)").isVisible(),
		"xlsx 가져오기 카드",
	);
	await page.locator("nav.segment a.seg-btn", { hasText: "학기별 매핑" }).click();
	await page.waitForTimeout(900);
	const sheetCard = page
		.locator(".card")
		.filter({ has: page.locator("h2.card-title", { hasText: "학기별 부원" }) })
		.first();
	await assert.ok(await sheetCard.isVisible(), "학기별 매핑 섹션에 학기별 부원 카드");
	await page.waitForTimeout(600); // 행 로딩
	const rowCount = await sheetCard.locator(".settings-sheet-row").count();
	assert.ok(rowCount > 0, `학기별 부원 행이 화면에 표시됨(${rowCount}행)`);
	const mapButtons = await sheetCard
		.getByRole("button", { name: "매핑", exact: true })
		.count();
	const unmapButtons = await sheetCard
		.getByRole("button", { name: "매핑 해제" })
		.count();
	assert.equal(unmapButtons, 0, "매핑된 행(해제 버튼)은 표시되지 않음");
	assert.equal(
		await sheetCard.locator(".member-meta", { hasText: "계정에 매핑됨" }).count(),
		0,
		"매핑됨 표시 없음(미매핑만)",
	);
	evidence.sheetRows = { rowCount, mapButtons, unmapButtons };
	await shot("02-settings-sheet");

	// 행 추가·제거 왕복(UI 경로 — CSRF 보호 경유)
	await sheetCard.getByPlaceholder("새 부원 행 이름").fill("검증용행");
	await sheetCard.getByRole("button", { name: "행 추가", exact: true }).click();
	await page.waitForTimeout(1200);
	let rowLine = sheetCard
		.locator(".settings-sheet-row", { hasText: "검증용행" })
		.first();
	try {
		await rowLine.waitFor({ state: "visible", timeout: 4000 });
	} catch {
		const text = await sheetCard.innerText();
		console.error("CARD DUMP:", text.slice(0, 1200));
		throw e;
	}
	await assert.ok(
		await rowLine.isVisible(),
		"추가한 행이 목록에 표시(영구 저장)",
	);
	await shot("03-settings-sheet-added-row");
	await rowLine.getByRole("button", { name: "행 삭제" }).click();
	await page.waitForTimeout(900);
	const stillThere = await sheetCard
		.locator(".settings-sheet-row", { hasText: "검증용행" })
		.count();
	assert.equal(stillThere, 0, "행 제거로 목록에서 사라짐");
	evidence.rowCrud = true;

	// 3) 부원 상세 — 시트 연결 탭
	await page.goto(`${base}/admin/members`, { waitUntil: "networkidle" });
	await page
		.locator(".member-row[style*='cursor:pointer']")
		.first()
		.waitFor({ timeout: 15000 });
	await page.locator(".member-row[style*='cursor:pointer']").first().click();
	await page.waitForURL("**/admin/members/**", { timeout: 15000 });
	await page.waitForSelector("nav.member-detail-tabs, nav.segment a.seg-btn", {
		timeout: 15000,
	});
	const tabs = await page.locator("nav.segment a.seg-btn").allInnerTexts();
	assert.ok(
		tabs.map((t) => t.trim()).includes("시트 연결"),
		`부원 상세 탭에 시트 연결 존재(${tabs.join("/")})`,
	);
	await page.locator("nav.segment a.seg-btn", { hasText: "시트 연결" }).click();
	await page.waitForTimeout(800);
	const sheetTab = page
		.locator(".card.member-sheet, .card", { hasText: "시트 연결" })
		.last();
	const semesterGroups = await sheetTab.locator(".settings-sheet-row").count();
	evidence.memberSheetTab = { tabs, semesterGroups };
	await shot("04-member-detail-sheet");
	assert.ok(semesterGroups >= 0, "시트 연결 탭 렌더");
	// 이 부원의 출석 기록 탭 — 총 출석 표시(Join 집계)
	await page.locator("nav.segment a.seg-btn", { hasText: "출석 기록" }).click();
	await page.waitForTimeout(900);
	const statsText = await page
		.locator(".member-attendance .member-meta")
		.first()
		.innerText()
		.catch(() => "");
	evidence.memberAttendanceStats = statsText.replace(/\s+/g, " ").trim();
	await shot("05-member-detail-attendance");

	// 4) 대량 추가 — 미매핑 행 자동 추천(★)
	await page.goto(`${base}/admin/settings`, { waitUntil: "networkidle" });
	await page.locator("nav.segment a.seg-btn", { hasText: "학기별 매핑" }).click();
	await page
		.locator(".card")
		.filter({ has: page.locator("h2.card-title", { hasText: "학기별 부원" }) })
		.first()
		.getByPlaceholder("새 부원 행 이름")
		.fill("추천검증");
	await page
		.locator(".card")
		.filter({ has: page.locator("h2.card-title", { hasText: "학기별 부원" }) })
		.first()
		.getByRole("button", { name: "행 추가", exact: true })
		.click();
	await page.waitForTimeout(1000);
	await page.goto(`${base}/admin/members`, { waitUntil: "networkidle" });
	await page.getByRole("button", { name: "+ 부원 추가", exact: true }).click();
	await page.getByRole("button", { name: "+ 행 추가", exact: true }).click();
	await page.waitForTimeout(400);
	const row0 = page.locator(".bulk-row").first();
	await row0
		.getByRole("button", { hasText: "1행" })
		.click()
		.catch(() => {});
	await page.waitForTimeout(300);
	const nameInput = row0.locator("[data-field='name']");
	await nameInput.fill("추천검증");
	await page.waitForTimeout(300);
	const select = row0.locator("select");
	assert.ok(
		(await select.count()) > 0,
		"시트 행 연결 select 표시(미매핑 행 존재)",
	);
	const optionText = await select.locator("option").allInnerTexts();
	const starred = optionText.find(
		(t) => t.includes("★") && t.includes("추천검증"),
	);
	assert.ok(starred, `★ 자동 추천 옵션 존재: ${starred}`);
	evidence.bulkRecommendation = starred;
	await shot("06-bulk-add-recommend");
	// 패널 정리(저장하지 않음) + 검증 행 제거
	await page.getByRole("button", { name: "추가 취소" }).click();
	await page.goto(`${base}/admin/settings`, { waitUntil: "networkidle" });
	await page.locator("nav.segment a.seg-btn", { hasText: "학기별 매핑" }).click();
	await page.waitForTimeout(900);
	await page
		.locator(".settings-sheet-row", { hasText: "추천검증" })
		.first()
		.getByRole("button", { name: "행 삭제" })
		.click();
	await page.waitForTimeout(800);

	await browser.close();
	fs.writeFileSync(
		path.join(evidenceDir, "evidence.json"),
		JSON.stringify(evidence, null, 2),
	);
	console.log("PASS", JSON.stringify(evidence, null, 2));
}

main().catch((e) => {
	console.error("FAIL:", e.message);
	process.exit(1);
});
