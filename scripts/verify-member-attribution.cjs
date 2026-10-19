// 귀속 규칙(시트 탭 매핑) 실화면 검증 — 로컬 개발 서버(5121) + 실 WASM 화면.
// 봄부원(2026-1 매핑)·가을부원(2026-2 매핑) + 마감 앱 활동(3/10, 10/10) 시드 후
// 마이페이지·기록·관리자 부원 상세·활동 관리 화면에서 비매핑 학기 활동이 나타나지 않음을 확인한다.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");
const { execFileSync } = require("node:child_process");
const root = path.resolve(__dirname, "..");
const out = path.join(root, "artifacts/member-attribution/browser");
fs.mkdirSync(out, { recursive: true });
const { chromium } = require(
	path.join(root, ".diag/pw/node_modules/playwright"),
);
const base = process.env.BASE ?? "http://localhost:5121";
const dbPath =
	process.env.DB_PATH ??
	path.join(root, "VolleyballClub.Web/VolleyballClub.db");
const password = "Attr-Verify-2026!";
const report = {
	startedAt: new Date().toISOString(),
	success: false,
	base,
	engine: "Chromium / WASM (로컬 개발 서버)",
	os: { platform: os.platform(), release: os.release(), arch: os.arch() },
	cases: [],
	screenshots: [],
	apiEvidence: [],
	browserErrors: [],
};
function save() {
	fs.writeFileSync(
		path.join(out, "results.json"),
		JSON.stringify(report, null, 2),
	);
}
process.on("SIGINT", () => {
	save();
	process.exit(1);
});
process.on("SIGTERM", () => {
	save();
	process.exit(1);
});

async function goto(page, route) {
	const response = await page.goto(base + route, { waitUntil: "load" });
	assert.equal(response.status(), 200, `UI shell ${route}`);
	await page.locator(".app-shell").waitFor({ timeout: 90000 });
	await page.waitForLoadState("networkidle");
	assert.equal(
		await page.locator("#blazor-error-ui").isVisible(),
		false,
		route,
	);
}
async function api(page, method, resource, body, expected = 200) {
	const result = await page.evaluate(
		async ({ method, resource, body }) => {
			const headers = {};
			if (method !== "GET") {
				const csrf = await fetch("/api/auth/csrf");
				if (!csrf.ok) throw new Error("CSRF endpoint failed");
				headers["X-CSRF-TOKEN"] = (await csrf.json()).token;
			}
			if (body !== undefined) headers["Content-Type"] = "application/json";
			const response = await fetch(resource, {
				method,
				headers,
				body: body === undefined ? undefined : JSON.stringify(body),
			});
			const text = await response.text();
			let data = null;
			if (text) {
				try {
					data = JSON.parse(text);
				} catch {
					data = text;
				}
			}
			return { status: response.status, data };
		},
		{ method, resource, body },
	);
	assert.equal(
		result.status,
		expected,
		`${method} ${resource}: ${JSON.stringify(result.data)}`,
	);
	return result.data;
}
async function login(page, student, pwd) {
	await goto(page, "/Account/Login");
	await page.locator("#student-number").fill(student);
	await page.locator("#password").fill(pwd);
	await page.getByRole("button", { name: "로그인", exact: true }).click();
	await page.waitForURL(base + "/");
	await page.locator(".hero-band").waitFor();
	await page.waitForLoadState("networkidle");
}
async function logout(page) {
	await api(page, "POST", "/api/auth/logout", undefined, 204);
	await goto(page, "/Account/Login");
}
async function ensureMember(page, student, name) {
	const found = await api(page, "POST", "/api/admin/members/search", {
		Search: student,
		Page: 1,
		PageSize: 5,
	});
	const existing = (found.items ?? []).find((m) => m.studentNumber === student);
	if (existing) return existing.userId;
	const created = await api(page, "POST", "/api/admin/members", {
		StudentNumber: student,
		Name: name,
		Department: "검증학과",
		Password: password,
		Role: "MEMBER",
	});
	return created.userId;
}
async function shot(page, name) {
	const file = `${name}.png`;
	await page.screenshot({ path: path.join(out, file), fullPage: true });
	report.screenshots.push(file);
}

(async () => {
	let browser;
	try {
		browser = await chromium.launch({
			headless: true,
			timeout: 30000,
			...(process.env.BROWSER_EXECUTABLE
				? { executablePath: process.env.BROWSER_EXECUTABLE }
				: { channel: "msedge" }),
		});
		report.browser = browser.version();
		const page = await (
			await browser.newContext({ viewport: { width: 390, height: 844 } })
		).newPage();
		report.httpErrors = [];
		page.on("pageerror", (e) => report.browserErrors.push(String(e)));
		page.on("response", (r) => {
			if (r.status() >= 400)
				report.httpErrors.push(
					`${r.status()} ${r.request().method()} ${r.url().replace(base, "")}`,
				);
		});

		// 1) 관리자 로그인 → 검증 부원 2명 확보 → DB 시드(매핑+활동)
		await login(page, "admin", process.env.ADMIN_PASSWORD ?? "Admin1234!");
		const spring = await ensureMember(page, "20266001", "봄부원");
		const autumn = await ensureMember(page, "20266002", "가을부원");
		const sheets = await api(page, "GET", "/api/admin/sheets");
		const sheet =
			(sheets.items ?? sheets).find((x) => x.kind === 0) ??
			(sheets.items ?? sheets)[0];
		const spreadsheetId = sheet.spreadsheetId;
		const seed = JSON.parse(
			execFileSync(
				"python",
				[
					path.join(__dirname, "seed_member_attribution.py"),
					dbPath,
					spring,
					autumn,
					spreadsheetId,
				],
				{ encoding: "utf8" },
			),
		);
		report.apiEvidence.push({ step: "seed", spring, autumn, ...seed });

		// 2) 가을부원(2026-2 매핑) 마이페이지 — 이번 달 10/10 마감 활동이 결석으로 보인다
		await logout(page);
		await login(page, "20266002", password);
		await goto(page, "/my");
		await page.locator(".day-cell.missed").first().waitFor();
		assert.equal(
			await page.locator(".day-cell.missed").count(),
			1,
			"가을부원 10월 결석일 1개",
		);
		await shot(page, "mypage-autumn-october");
		const autumnStats = await api(page, "GET", "/api/my/stats");
		assert.equal(autumnStats.semesterActivities, 1, "가을부원 학기 통계 분모");
		assert.equal(autumnStats.myAttendances, 0);
		report.apiEvidence.push({ step: "autumn-stats", data: autumnStats });

		// 3) 가을부원 기록 — 10/10 '미출석' 행이 보인다(정상 귀속 대조군)
		await goto(page, "/records");
		await page.locator(".tl-item").first().waitFor();
		assert.equal(
			await page.locator(".tl-item").count(),
			1,
			"가을부원 10월 기록 1건",
		);
		assert.match(
			await page.locator(".tl-item .tl-title").first().innerText(),
			/미출석/,
		);
		await shot(page, "records-autumn-october");

		// 4) 봄부원(2026-1 매핑) 마이페이지·기록 — 이번 달(2026-2) 활동이 아예 나타나지 않는다
		await logout(page);
		await login(page, "20266001", password);
		await goto(page, "/my");
		await page.locator(".calendar").waitFor();
		await page.waitForLoadState("networkidle");
		assert.equal(
			await page.locator(".day-cell.missed").count(),
			0,
			"봄부원 10월 결석일 없음",
		);
		await shot(page, "mypage-spring-october");
		const springStats = await api(page, "GET", "/api/my/stats");
		assert.equal(springStats.semesterActivities, 0, "봄부원 학기 통계 분모 0");
		report.apiEvidence.push({ step: "spring-stats", data: springStats });
		await goto(page, "/records");
		await page.getByText("이 달에 활동 기록이 없습니다.").waitFor();
		await shot(page, "records-spring-october");
		const springRecords = await api(
			page,
			"GET",
			"/api/my/records?year=2026&month=10",
		);
		assert.equal(
			springRecords.monthActivities,
			0,
			"봄부원 10월 MonthActivities 0",
		);
		report.apiEvidence.push({ step: "spring-records", data: springRecords });

		// 5) 관리자 부원 상세 — 봄부원은 3/10 행만, 가을부원은 10/10 행만 나타난다
		await logout(page);
		await login(page, "admin", process.env.ADMIN_PASSWORD ?? "Admin1234!");
		await goto(page, `/admin/members/${spring}`);
		await page.locator(".member-detail-tabs").getByText("출석 기록").click();
		await page.locator(".member-row[data-activity-key]").first().waitFor();
		assert.equal(
			await page.locator('[aria-label*="2026.03.10"]').count(),
			1,
			"봄부원 3/10 행 존재",
		);
		assert.equal(
			await page.locator('[aria-label*="2026.10.10"]').count(),
			0,
			"봄부원 10/10 행 부재",
		);
		await shot(page, "memberdetail-spring");
		const springAddable = await api(
			page,
			"GET",
			`/api/admin/members/${spring}/attendance`,
		);
		assert.equal(
			springAddable.addableActivities.length,
			0,
			"봄부원 출석 추가 후보 없음",
		);
		report.apiEvidence.push({
			step: "spring-addable",
			data: springAddable.addableActivities,
		});

		await goto(page, `/admin/members/${autumn}`);
		await page.locator(".member-detail-tabs").getByText("출석 기록").click();
		await page.locator(".member-row[data-activity-key]").first().waitFor();
		assert.equal(
			await page.locator('[aria-label*="2026.10.10"]').count(),
			1,
			"가을부원 10/10 행 존재",
		);
		assert.equal(
			await page.locator('[aria-label*="2026.03.10"]').count(),
			0,
			"가을부원 3/10 행 부재",
		);
		await shot(page, "memberdetail-autumn");

		// 6) 활동 관리 일별 부원 목록 — 그 활동 학기 탭에 매핑된 부원만 나열된다
		await goto(page, "/admin/activities");
		await page.locator(".member-row[data-activity-key]").first().waitFor();
		await page
			.locator(
				`.member-row[data-activity-key="app:${seed.october}"] button[aria-label*="2026.10.10"]`,
			)
			.click();
		await page.locator('.activity-members[data-load-state="ready"]').waitFor();
		const dayMembers = await page
			.locator(".activity-members .member-row[data-user-id]")
			.count();
		assert.equal(dayMembers, 1, "10/10 일별 목록 1명(가을부원만)");
		assert.match(
			await page.locator(".activity-members").innerText(),
			/가을부원/,
		);
		assert.doesNotMatch(
			await page.locator(".activity-members").innerText(),
			/봄부원/,
		);
		await shot(page, "activities-day-1010");

		await page
			.locator(
				`.member-row[data-activity-key="app:${seed.march}"] button[aria-label*="2026.03.10"]`,
			)
			.click();
		await page.locator('.activity-members[data-load-state="ready"]').waitFor();
		// 3/10(2026-1) 일별 목록에는 2026-1 매핑 부원(봄부원)만 나타난다 — 가을부원은 제외
		assert.equal(
			await page.locator(".activity-members .member-row[data-user-id]").count(),
			1,
			"3/10 일별 목록 1명(봄부원만)",
		);
		const marchText = await page.locator(".activity-members").innerText();
		assert.match(marchText, /봄부원/);
		assert.doesNotMatch(marchText, /가을부원/);
		await shot(page, "activities-day-0310");

		assert.deepEqual(report.browserErrors, []); // JS 예외 없음
		// 로그인 전 /api/auth/user 프로브의 401은 정상 동작 — 그 외 HTTP 오류만 없어야 한다
		assert.deepEqual(
			report.httpErrors.filter((e) => !e.startsWith("401 GET /api/auth/user")),
			[],
		);
		report.success = true;
		report.cases.push(
			{ name: "마이페이지 캘린더·학기 통계", ok: true },
			{ name: "기록 페이지 월간 목록", ok: true },
			{ name: "관리자 부원 상세 활동 행·후보", ok: true },
			{ name: "활동 관리 일별 부원 목록", ok: true },
		);
	} catch (e) {
		report.failure = String(
			e && e.stack ? e.stack.split("\n").slice(0, 6).join("\n") : e,
		);
		process.exitCode = 1;
	} finally {
		if (browser) await browser.close().catch(() => {});
		report.finishedAt = new Date().toISOString();
		save();
	}
})();
