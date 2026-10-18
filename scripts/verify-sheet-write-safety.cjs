// Real WASM + real services/SQLite; existing Verification-only synthetic Google boundary.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const os = require("node:os");
const { execFileSync } = require("node:child_process");
const { chromium } = require(
	path.resolve(__dirname, "../.diag/pw/node_modules/playwright"),
);
const root = path.resolve(__dirname, "..");
const out = path.join(root, "artifacts/sheet-write-safety/browser");
fs.mkdirSync(out, { recursive: true });
const name = `vicikyu-sheet-safety-${crypto.randomUUID()}`;
const image = `${name}:local`;
const password = `Verify-${crypto.randomUUID()}!`;
const report = {
	success: false,
	fakeGoogle: true,
	liveGoogle: false,
	realIPhone: false,
	os: { platform: os.platform(), release: os.release() },
	cases: [],
	measurements: [],
	downloads: [],
	browserErrors: [],
	wasmResponses: 0,
};
let containerOwned = false,
	imageOwned = false,
	browser,
	adminPage,
	base;
function docker(args, timeout = 30000, log) {
	const fd = log ? fs.openSync(path.join(out, log), "w") : undefined;
	try {
		return execFileSync("docker", args, {
			cwd: root,
			timeout,
			encoding: "utf8",
			maxBuffer: 8 * 1024 * 1024,
			stdio: ["ignore", fd ?? "pipe", fd ?? "pipe"],
		})?.trim();
	} catch (e) {
		throw new Error(
			`Docker ${args[0]} failed (${e.status ?? "timeout"}); see ${log || "server.log"}`,
		);
	} finally {
		if (fd !== undefined) fs.closeSync(fd);
	}
}
async function api(page, method, url, body, expected = 200) {
	const response = await page.evaluate(
		async ({ method, url, body }) => {
			const headers = {};
			if (method !== "GET")
				headers["X-CSRF-TOKEN"] = (
					await (await fetch("/api/auth/csrf")).json()
				).token;
			if (body !== undefined) headers["Content-Type"] = "application/json";
			const r = await fetch(url, {
				method,
				headers,
				body: body === undefined ? undefined : JSON.stringify(body),
			});
			const text = await r.text();
			let data = null;
			try {
				if (text) data = JSON.parse(text);
			} catch {
				throw new Error(
					`Invalid JSON response: ${method} ${url} (${r.status})`,
				);
			}
			return { status: r.status, data };
		},
		{ method, url, body },
	);
	assert.equal(response.status, expected, `${method} ${url}`);
	return response.data;
}
async function login(page, number, pass) {
	await page.goto(base + "/Account/Login");
	await page.locator("#student-number").fill(number);
	await page.locator("#password").fill(pass);
	await page.getByRole("button", { name: "로그인", exact: true }).click();
	await page.waitForURL(base + "/");
}
async function inspectBackup(download, label) {
	const file = path.join(out, `${label}.db`);
	await download.saveAs(file);
	assert.equal(
		fs.readFileSync(file).subarray(0, 16).toString("ascii"),
		"SQLite format 3\0",
	);
	let evidence;
	try {
		evidence = JSON.parse(
			execFileSync(
				"python",
				[
					"-c",
					"import sqlite3,json,sys; c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True); print(json.dumps({'integrity':c.execute('PRAGMA integrity_check').fetchone()[0],'pending':c.execute('SELECT COUNT(*) FROM Activities WHERE SheetSyncPendingAt IS NOT NULL').fetchone()[0]}))",
					file,
				],
				{ encoding: "utf8" },
			),
		);
	} catch (e) {
		throw new Error(`Cannot verify ${label} SQLite backup: ${e.message}`);
	}
	assert.equal(evidence.integrity, "ok");
	assert.ok(evidence.pending > 0);
	report.downloads.push({ label, filename: path.basename(file), ...evidence });
}
async function measure(page, label) {
	const data = await page
		.locator('[data-testid="sheet-sync-dialog"]')
		.evaluate((dialog) => {
			const r = dialog.getBoundingClientRect();
			return {
				width: innerWidth,
				height: innerHeight,
				x: r.x,
				y: r.y,
				right: r.right,
				bottom: r.bottom,
				overflow:
					document.scrollingElement.scrollWidth -
					document.scrollingElement.clientWidth,
				focusInside: dialog.contains(document.activeElement),
			};
		});
	assert.ok(
		data.x >= 0 &&
			data.y >= 0 &&
			data.right <= data.width + 1 &&
			data.bottom <= data.height + 1,
	);
	assert.ok(data.overflow <= 1);
	assert.equal(data.focusInside, true);
	assert.ok(
		Math.abs((data.x + data.right) / 2 - data.width / 2) <= 2,
		"dialog horizontal centering",
	);
	assert.ok(
		Math.abs((data.y + data.bottom) / 2 - data.height / 2) <= 2,
		"dialog vertical centering",
	);
	report.measurements.push({ label, ...data });
	await page.screenshot({
		path: path.join(out, `${label}.png`),
		fullPage: false,
	});
}
(async () => {
	try {
		imageOwned = true;
		docker(
			["build", "--progress=plain", "-t", image, "."],
			600000,
			"build.log",
		);
		docker([
			"create",
			"--name",
			name,
			"--tmpfs",
			"/tmp",
			"-p",
			"127.0.0.1::8080",
			"-e",
			"ASPNETCORE_ENVIRONMENT=Verification",
			"-e",
			"Verification__ActivityManagement=true",
			"-e",
			"ConnectionStrings__DefaultConnection=Data Source=/tmp/vicikyu-wasm-audit.db",
			"-e",
			`Seed__AdminPassword=${password}`,
			"-e",
			"Google__ServiceAccountJson=",
			"-e",
			"Google__SpreadsheetId=",
			"-e",
			"Sheets__SyncOnStartup=false",
			image,
		]);
		containerOwned = true;
		docker(["start", name]);
		const binding = docker(["port", name, "8080/tcp"]);
		assert.match(binding, /^127\.0\.0\.1:\d+$/);
		base = `http://${binding}`;
		report.url = base;
		let healthy = false;
		for (let i = 0; i < 60; i++) {
			try {
				healthy = (
					await fetch(base + "/Account/Login", {
						signal: AbortSignal.timeout(1000),
					})
				).ok;
			} catch {}
			if (healthy) break;
			await new Promise((resolve) => setTimeout(resolve, 500));
		}
		assert.ok(healthy, "isolated Verification app failed to start");
		browser = await chromium.launch({
			headless: true,
			channel: process.env.BROWSER_CHANNEL || "msedge",
		});
		report.browser = browser.version();
		const admin = await browser.newContext({
			viewport: { width: 1280, height: 900 },
			timezoneId: "Asia/Seoul",
		});
		adminPage = await admin.newPage();
		adminPage.setDefaultTimeout(20000);
		adminPage.on("pageerror", (e) => report.browserErrors.push(e.message));
		adminPage.on("response", (r) => {
			if (r.url().includes(".wasm") && r.ok()) report.wasmResponses++;
		});
		const autoDownloads = [];
		adminPage.on("download", (d) => autoDownloads.push(d));
		await login(adminPage, "admin", password);
		const fixture = "/api/verification/activity-management";
		const s = await api(adminPage, "GET", fixture + "/state");
		assert.equal(s.fakeGoogle, true);
		assert.equal(s.liveGoogle, false);
		assert.equal(s.database, "Data Source=/tmp/vicikyu-wasm-audit.db");
		const first = s.sheets.find(
			(x) => x.spreadsheetId === "verification-activity-first",
		);
		const member = s.members.find((x) => x.studentNumber === "202512001");
		const list = await api(adminPage, "GET", "/api/admin/activities");
		const completed = list.find(
			(x) => x.sheetId === first.id && x.date === "2026-10-01",
		);
		await adminPage.goto(base + "/admin/activities");
		await adminPage
			.locator('.activity-list[data-load-state="ready"]')
			.waitFor();
		await adminPage
			.locator(`.activity-list [data-activity-key="${completed.key}"]`)
			.getByRole("button", { name: /수정$/ })
			.click();
		await adminPage
			.locator('.activity-members[data-load-state="ready"]')
			.waitFor();
		await api(
			adminPage,
			"POST",
			fixture + "/fault",
			{ mode: "beforeApply" },
			204,
		);
		const toggle = `/api/admin/activities/${encodeURIComponent(completed.key)}/members/${member.id}`;
		const [response] = await Promise.all([
			adminPage.waitForResponse(
				(r) => r.url() === base + toggle && r.request().method() === "PUT",
			),
			adminPage
				.locator(
					`.activity-members [data-user-id="${member.id}"] [role="switch"]`,
				)
				.click(),
		]);
		assert.equal(response.status(), 204);
		assert.equal(response.headers()["x-sheet-sync-failed"], "true");
		assert.equal(
			autoDownloads.length,
			0,
			"no immediate backup download — popup first",
		);
		const dialog = adminPage.locator('[data-testid="sheet-sync-dialog"]');
		await dialog.waitFor({ state: "visible" });
		for (let i = 0; i < 4; i++) await adminPage.keyboard.press("Tab");
		await measure(adminPage, "desktop-admin-failure");
		await adminPage.setViewportSize({ width: 390, height: 844 });
		await measure(adminPage, "mobile-admin-failure");
		await adminPage.setViewportSize({ width: 1280, height: 900 });
		await dialog.getByRole("button", { name: "닫기", exact: true }).click();
		await dialog.waitFor({ state: "hidden" });
		// 일회성 결함: 목록 새로고침의 동기화 재시도로 이미 해소됐다 — 백업 없음(팝업 닫힘 후에도 갱신 가능)
		await adminPage.waitForTimeout(1500);
		assert.equal(
			autoDownloads.length,
			0,
			"transient failure self-healed — no backup download",
		);
		assert.equal(
			(await api(adminPage, "GET", "/api/admin/sheets/sync-status"))
				.pendingCount,
			0,
			"retry drained pending after popup close",
		);
		report.cases.push(
			"transient admin failure: popup guidance, no immediate download, self-healed retry, zero backups",
		);
		await api(adminPage, "POST", "/api/admin/sheets/resync");
		assert.equal(
			(await api(adminPage, "GET", "/api/admin/sheets/sync-status"))
				.pendingCount,
			0,
		);

		// 시나리오 2: 매핑 누락 → 팝업 인라인 해결(행 연결) → 자동 갱신 → 팝업 닫힘 + 백업 없음
		const pre = await api(adminPage, "GET", fixture + "/state");
		const today = pre.activities.find((x) => x.activityDate === "2026-10-16");
		const attended = new Set(
			pre.attendances
				.filter((x) => x.activityId === today.id)
				.map((x) => x.userId),
		);
		const unmappedTarget = pre.members.find(
			(u) => u.studentNumber !== "admin" && !attended.has(u.id),
		);
		assert.ok(unmappedTarget, "오늘 미출석 부원이 매핑 해제 대상");
		await api(
			adminPage,
			"PUT",
			`/api/admin/members/${unmappedTarget.id}/mappings`,
			{
				SheetId: first.id,
				TabName: "2026-2",
				RowLabel: null,
				AddNewRow: false,
			},
			204,
		);
		const quickAdd = await api(
			adminPage,
			"POST",
			`/api/admin/attendance/${unmappedTarget.id}`,
			undefined,
			204,
		);
		assert.equal(quickAdd, null);
		// 원시 fetch는 ApiHttp 이벤트를 건드리지 않는다 — 대시보드 폴링이 펜딩을 감지해 팝업을 띄운다
		await adminPage.goto(base + "/admin");
		await adminPage
			.locator('.dashboard-data[data-load-state="ready"]')
			.waitFor();
		await dialog.waitFor({ state: "visible" });
		const fixBox = adminPage.locator(
			`[data-testid="fix-${unmappedTarget.id}"]`,
		);
		await fixBox.waitFor();
		await measure(adminPage, "desktop-mapping-fix");
		const downloadsBeforeFix = autoDownloads.length;
		await fixBox.locator("select").selectOption(unmappedTarget.name);
		await fixBox
			.getByRole("button", { name: "연결 저장", exact: true })
			.click();
		await dialog.waitFor({ state: "hidden" });
		assert.equal(
			(await api(adminPage, "GET", "/api/admin/sheets/sync-status"))
				.pendingCount,
			0,
		);
		assert.equal(
			autoDownloads.length,
			downloadsBeforeFix,
			"resolved in popup — no backup download",
		);
		const afterFix = await api(adminPage, "GET", fixture + "/state");
		const fixedToday = afterFix.activities.find(
			(x) => x.activityDate === "2026-10-16",
		);
		assert.ok(
			afterFix.attendances.some(
				(a) => a.activityId === fixedToday.id && a.userId === unmappedTarget.id,
			),
			"DB 출석 보존",
		);
		const firstDoc = afterFix.source.documents.find(
			(d) => d.document === first.spreadsheetId && d.tab.name === "2026-2",
		);
		const fixedRow = firstDoc.tab.rows.findIndex(
			(r) => r[0] === unmappedTarget.name,
		);
		const todayColumn = firstDoc.tab.rows[1].findIndex((h) =>
			String(h).includes("10. 16"),
		);
		assert.ok(fixedRow >= 0 && todayColumn >= 0, "행·열 위치 확인");
		assert.equal(
			firstDoc.tab.rows[fixedRow][todayColumn],
			"출석",
			"실시트(합성) 라이브 열 출석 마크",
		);
		report.cases.push(
			"mapping-missing: popup inline row link + auto resync success + popup closed + zero downloads",
		);

		// 시나리오 2b: 팝업에서 새 행 추가(행 추가·연결) → 자동 갱신 → 팝업 닫힘
		// 실제 사용 시나리오: 시트에 없는 새 부원을 관리자가 추가
		// 새 부원 생성 (시트에는 없는 이름)
		const newName = "신규팝업테스트";
		await api(
			adminPage,
			"POST",
			"/api/admin/members",
			{
				StudentNumber: "20269999",
				Name: newName,
				Department: "",
				Password: "Test1234!",
			},
			200,
		);
		// 생성된 부원의 userId를 상태에서 직접 찾는다
		const state2b = await api(adminPage, "GET", fixture + "/state");
		const newMember = state2b.members.find(
			(m) => m.name === newName || m.studentNumber === "20269999",
		);
		assert.ok(
			newMember,
			`새 부원 조회 실패: ${JSON.stringify(state2b.members.map((m) => m.name)).slice(0, 100)}`,
		);
		// 새 부원 출석 추가 → 시트에 행이 없어 미반영 발생
		await api(
			adminPage,
			"POST",
			`/api/admin/attendance/${newMember.id}`,
			undefined,
			204,
		);
		await adminPage.goto(base + "/admin");
		await adminPage
			.locator('.dashboard-data[data-load-state="ready"]')
			.waitFor();
		await dialog.waitFor({ state: "visible" });
		const fixBox2b = adminPage.locator(`[data-testid="fix-${newMember.id}"]`);
		await fixBox2b.waitFor();
		const downloadsBefore2b = autoDownloads.length;
		// ＋ 새 행 추가 → 입력창 확인 → 행 추가·연결
		await fixBox2b.getByRole("button", { name: "＋ 새 행 추가" }).click();
		const labelInput = fixBox2b.locator("input.form-control");
		await labelInput.waitFor();
		assert.equal(
			await labelInput.inputValue(),
			newName,
			"라벨 기본값 = 부원 이름",
		);
		await fixBox2b
			.getByRole("button", { name: "행 추가·연결", exact: true })
			.click();
		await dialog.waitFor({ state: "hidden" });
		assert.equal(
			(await api(adminPage, "GET", "/api/admin/sheets/sync-status"))
				.pendingCount,
			0,
			"팝업 행 추가 후 펜딩 해소",
		);
		assert.equal(
			autoDownloads.length,
			downloadsBefore2b,
			"팝업 행 추가 해결 — 백업 없음",
		);
		const after2b = await api(adminPage, "GET", fixture + "/state");
		const doc2b = after2b.source.documents.find(
			(d) => d.document === first.spreadsheetId && d.tab.name === "2026-2",
		);
		const newRowIdx = doc2b.tab.rows.findIndex((r) => r[0] === newName);
		assert.ok(newRowIdx >= 0, "시트에 새 행 추가됨");
		assert.equal(
			doc2b.tab.rows[newRowIdx][
				doc2b.tab.rows[1].findIndex((h) => String(h).includes("10. 16"))
			],
			"출석",
			"새 행의 라이브 열 출석 마크",
		);
		report.cases.push(
			"popup row-add: ＋ 새 행 추가 → 행 추가·연결 → auto resync → popup closed + zero downloads",
		);
		await adminPage.setViewportSize({ width: 1280, height: 900 });
		await adminPage.goto(base + "/admin");
		await adminPage
			.locator('.dashboard-data[data-load-state="ready"]')
			.waitFor();
		const current = await api(adminPage, "GET", "/api/admin/activity/today");
		const snapshot = await api(adminPage, "GET", fixture + "/state");
		const candidate = snapshot.members.find(
			(u) =>
				u.studentNumber !== "admin" &&
				!snapshot.attendances.some(
					(a) => a.activityId === current.activityId && a.userId === u.id,
				),
		);
		assert.ok(candidate);
		const memberContext = await browser.newContext({
			viewport: { width: 390, height: 844 },
		});
		const memberPage = await memberContext.newPage();
		let memberDownloads = 0;
		memberPage.on("download", () => memberDownloads++);
		await login(memberPage, candidate.studentNumber, "Member1234!");
		await api(memberPage, "GET", "/api/admin/database/export", undefined, 403);
		await api(
			memberPage,
			"GET",
			"/api/admin/sheets/sync-status",
			undefined,
			403,
		);
		await memberPage
			.getByRole("button", { name: "출석하기", exact: true })
			.click();
		await memberPage.locator(".otp-input").fill(current.otpCode);
		await api(
			adminPage,
			"POST",
			fixture + "/fault",
			{ mode: "beforeApply" },
			204,
		);
		const downloadsBefore = autoDownloads.length;
		const [checkedIn] = await Promise.all([
			memberPage.waitForResponse(
				(r) =>
					r.url() === base + "/api/my/attendance" &&
					r.request().method() === "POST",
			),
			memberPage
				.getByRole("button", { name: "출석 확인", exact: true })
				.click(),
		]);
		assert.equal(checkedIn.status(), 200);
		assert.equal(checkedIn.headers()["x-sheet-sync-failed"], undefined);
		await memberPage.getByText("오늘 출석 완료", { exact: true }).waitFor();
		await dialog.waitFor({ state: "visible", timeout: 15000 });
		assert.equal(memberDownloads, 0);
		assert.equal(autoDownloads.length, downloadsBefore);
		await measure(adminPage, "dashboard-member-failure");
		const [manual] = await Promise.all([
			adminPage.waitForEvent("download"),
			dialog
				.getByRole("button", { name: ".db 백업 다운로드", exact: true })
				.click(),
		]);
		await inspectBackup(manual, "member-failure-admin-download");
		const [closeBackup] = await Promise.all([
			adminPage.waitForEvent("download"),
			dialog.getByRole("button", { name: "닫기", exact: true }).click(),
		]);
		await dialog.waitFor({ state: "hidden" });
		await inspectBackup(closeBackup, "member-failure-close");
		report.cases.push(
			"member failure: successful check-in, no member DB access/download, dashboard popup, admin-only backups (manual + close-triggered)",
		);
		await api(adminPage, "POST", "/api/admin/sheets/resync");
		assert.equal(
			(await api(adminPage, "GET", "/api/admin/sheets/sync-status"))
				.pendingCount,
			0,
		);
		report.cases.push("retry drains pending and returns to sheet authority");
		assert.ok(report.wasmResponses > 0);
		assert.deepEqual(report.browserErrors, []);
		await memberContext.close();
		report.success = true;
		console.log(
			`PASS ${report.cases.length} browser scenarios, ${report.downloads.length} SQLite backups; synthetic Google only`,
		);
	} catch (e) {
		report.failure = e.message;
		console.error(e.message);
		process.exitCode = 1;
		if (adminPage && !adminPage.isClosed())
			try {
				await adminPage.screenshot({
					path: path.join(out, "failure.png"),
					fullPage: true,
				});
			} catch {}
	} finally {
		if (browser) await browser.close();
		if (containerOwned) {
			try {
				docker(["logs", name], 15000, "server.log");
			} finally {
				docker(["rm", "-f", name]);
			}
		}
		if (imageOwned) docker(["image", "rm", image]);
		fs.writeFileSync(
			path.join(out, "results.json"),
			JSON.stringify(report, null, 2),
		);
	}
})();
