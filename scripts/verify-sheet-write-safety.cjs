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
		const [download, response] = await Promise.all([
			adminPage.waitForEvent("download"),
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
		await inspectBackup(download, "admin-failure-auto");
		const dialog = adminPage.locator('[data-testid="sheet-sync-dialog"]');
		await dialog.waitFor({ state: "visible" });
		for (let i = 0; i < 4; i++) await adminPage.keyboard.press("Tab");
		await measure(adminPage, "desktop-admin-failure");
		await adminPage.setViewportSize({ width: 390, height: 844 });
		await measure(adminPage, "mobile-admin-failure");
		await adminPage.keyboard.press("Escape");
		await dialog.waitFor({ state: "hidden" });
		report.cases.push(
			"admin failure: committed DB + response flag + automatic standalone .db + accessible native popup",
		);
		await api(adminPage, "POST", "/api/admin/sheets/resync");
		assert.equal(
			(await api(adminPage, "GET", "/api/admin/sheets/sync-status"))
				.pendingCount,
			0,
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
		report.cases.push(
			"member failure: successful check-in, no member DB access/download, dashboard warning popup, admin-only backup",
		);
		await dialog.getByRole("button", { name: "닫기", exact: true }).click();
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
