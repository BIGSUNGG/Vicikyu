// Isolated local Docker + desktop Chromium regression. NOT a real iPhone/PWA test.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const { execFileSync } = require("node:child_process");
const verifyLoadingUx = require("./verify-loading-ux.cjs");
const verifyMemberBulk = require("./verify-member-bulk-add.cjs");
const workbookPath = process.env.MEMBER_WORKBOOK_PATH;
if (!workbookPath || !fs.existsSync(workbookPath)) throw new Error("MEMBER_WORKBOOK_PATH must point to the supplied 46-record XLSX (read-only input).");
let sensitiveRosterActive = false;
const root = path.resolve(__dirname, "..");
const evidenceDir = path.join(root, "artifacts", "wasm-browser");
const highPingDir = path.join(root, "artifacts", "high-ping-navigation");
fs.mkdirSync(evidenceDir, { recursive: true });
fs.mkdirSync(highPingDir, { recursive: true });
const { chromium } = require(
	process.env.PLAYWRIGHT_MODULE ||
		path.join(root, ".diag", "pw", "node_modules", "playwright"),
);
const suffix = crypto.randomUUID().slice(0, 8);
const name = `vicikyu-wasm-check-${suffix}`;
const network = `${name}-network`;
const adminPassword = `Verify-${crypto.randomUUID()}!`;
const memberPassword = `Member-${crypto.randomUUID()}!`;
const errors = [];
const evidence = {
	runStartedAt: new Date().toISOString(),
	environment: "isolated Docker / Chromium desktop engine",
	realIPhone: false,
	externalSheets: "test doubles",
	routes: [],
	flows: [],
	screenshots: [],
	measurements: [],
	navigation: {
		baseline: {
			file: "artifacts/high-ping-navigation/baseline.json",
			calendarTopShiftPx: 159,
			blankExposureMs: 1892,
		},
		conditions: [],
	},
	wasmResponses: 0,
};
let browser;
let base;
let containerCreated = false;
let networkCreated = false;

function docker(...args) {
	try {
		return execFileSync("docker", args, {
			cwd: root,
			encoding: "utf8",
			timeout: 120000,
			stdio: ["ignore", "pipe", "pipe"],
		}).trim();
	} catch (error) {
		// Do not include command arguments: docker run carries disposable login passwords.
		throw new Error(
			`Docker ${args[0]} failed (exit ${error.status}): ${String(error.stderr || "").slice(-2000)}`,
		);
	}
}
function cleanup() {
	if (containerCreated) {
		try {
			fs.writeFileSync(
				path.join(evidenceDir, "container.log"),
				docker("logs", name),
			);
		} catch {
			/* Diagnostics are best effort; cleanup is not. */
		}
		docker("rm", "-f", name);
		containerCreated = false;
	}
	if (networkCreated) {
		docker("network", "rm", network);
		networkCreated = false;
	}
}
for (const signal of ["SIGINT", "SIGTERM"])
	process.on(signal, () => {
		try {
			cleanup();
		} finally {
			process.exit(1);
		}
	});
const deadline = setTimeout(
	() => {
		console.error("Verification exceeded 30 minutes.");
		try {
			cleanup();
		} finally {
			process.exit(1);
		}
	},
	30 * 60 * 1000,
);

async function api(page, method, resource, body, expected = 200) {
	const result = await page.evaluate(
		async ({ method, resource, body }) => {
			const headers = {};
			if (method !== "GET") {
				const csrf = await fetch("/api/auth/csrf", {
					credentials: "same-origin",
				});
				if (!csrf.ok) throw new Error("CSRF token endpoint failed");
				headers["X-CSRF-TOKEN"] = (await csrf.json()).token;
			}
			if (body !== undefined) headers["Content-Type"] = "application/json";
			const response = await fetch(resource, {
				method,
				headers,
				credentials: "same-origin",
				body: body === undefined ? undefined : JSON.stringify(body),
			});
			const text = await response.text();
			let data = null;
			try {
				data = text ? JSON.parse(text) : null;
			} catch {
				throw new Error(
					`Non-JSON API response: ${method} ${resource} (${response.status})`,
				);
			}
			return { status: response.status, data };
		},
		{ method, resource, body },
	);
	assert.equal(
		result.status,
		expected,
		`${method} ${resource}: unexpected HTTP status`,
	);
	return result.data;
}
async function goto(page, route) {
	const response = await page.goto(base + route, { waitUntil: "load" });
	assert.equal(response.status(), 200, `UI shell ${route}`);
	await page.locator(".app-shell").waitFor();
	await page.waitForLoadState("networkidle");
	assert.equal(
		await page.locator("#blazor-error-ui").isVisible(),
		false,
		route,
	);
}
async function action(page, resource, method, perform) {
	const [response] = await Promise.all([
		page.waitForResponse(
			(r) =>
				r.url().split("?")[0] === base + resource &&
				r.request().method() === method,
		),
		perform(),
	]).catch((error) => {
		throw new Error(`${method} ${resource}: ${error.message}`);
	});
	assert.ok(
		response.ok(),
		`${method} ${resource} failed with ${response.status()}`,
	);
	await page.waitForLoadState("networkidle");
	return response.status() === 204 ? null : response.json();
}
async function login(page, student, password) {
	await goto(page, "/Account/Login");
	await page.locator("#student-number").fill(student);
	await page.locator("#password").fill(password);
	await page.getByRole("button", { name: "로그인", exact: true }).click();
	await page.waitForURL(base + "/");
	await page.locator(".hero-band").waitFor();
	await page.waitForLoadState("networkidle");
	assert.equal(
		(await api(page, "GET", "/api/auth/user")).name.length > 0,
		true,
	);
}
async function createPage(viewport = { width: 1280, height: 900 }) {
	const context = await browser.newContext({
		viewport,
		timezoneId: "America/Los_Angeles",
		reducedMotion: "reduce",
	});
	const page = await context.newPage();
	page.setDefaultTimeout(20000);
	page.setDefaultNavigationTimeout(45000);
	page.on("pageerror", (error) => errors.push(error.message));
	page.on("console", (message) => {
		if (
			message.type() === "error" &&
			!message.text().startsWith("Failed to load resource:")
		)
			errors.push(message.text());
	});
	page.on("websocket", (socket) =>
		errors.push(`Unexpected server circuit websocket: ${socket.url()}`),
	);
	page.on("request", (request) => {
		if (request.url().startsWith(base + "/_blazor"))
			errors.push("Unexpected /_blazor request");
	});
	page.on("response", (response) => {
		const resource = response.url().startsWith(base)
			? response.url().slice(base.length).split("?")[0]
			: response.url();
		if (resource.includes(".wasm") && response.ok()) evidence.wasmResponses++;
		if (response.status() >= 400 && !resource.startsWith("/api/"))
			errors.push(`Asset/UI HTTP ${response.status()}: ${resource}`);
	});
	return page;
}
async function measure(page, label) {
	const data = await page.evaluate(() => {
		const doc = document.scrollingElement;
		const shell = document.querySelector(".app-shell");
		const nav = document.querySelector(".bottom-nav");
		const navRect = nav?.getBoundingClientRect();
		return {
			width: innerWidth,
			height: innerHeight,
			scrollWidth: doc.scrollWidth,
			clientWidth: doc.clientWidth,
			scrollHeight: doc.scrollHeight,
			clientHeight: doc.clientHeight,
			auth: shell.classList.contains("auth-shell"),
			shellWidth: shell.getBoundingClientRect().width,
			navVisible: !!nav && getComputedStyle(nav).display !== "none",
			navBottom: navRect?.bottom,
			navPosition: nav ? getComputedStyle(nav).position : null,
			navInset: nav ? parseFloat(getComputedStyle(nav).bottom) : null,
			background: getComputedStyle(shell).backgroundColor,
		};
	});
	assert.ok(
		data.scrollWidth <= data.clientWidth + 1,
		`Horizontal overflow ${label}: ${JSON.stringify(data)}`,
	);
	if (data.auth) assert.equal(data.navVisible, false, `Auth nav ${label}`);
	if (label.includes("Login"))
		assert.ok(
			data.scrollHeight <= data.clientHeight + 1,
			`Fitting login scroll ${label}`,
		);
	if (data.navVisible) {
		assert.equal(data.navPosition, "fixed", `Nav positioning ${label}`);
		assert.ok(data.navInset >= 0 && data.navInset <= 32, `Nav inset ${label}`);
		assert.ok(
			Math.abs(data.navBottom + data.navInset - data.height) <= 2,
			`Nav anchor ${label}: ${JSON.stringify(data)}`,
		);
	}
	evidence.measurements.push({ label, ...data });
	const filename = `${label.replace(/[^a-zA-Z0-9_-]/g, "_")}.png`;
	await page.screenshot({
		path: path.join(evidenceDir, filename),
		fullPage: true,
	});
	evidence.screenshots.push(filename);
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const round = (value) =>
	value == null || Number.isNaN(value) ? null : Math.round(value * 10) / 10;

async function waitForHomeReady(page) {
	await page.locator('.home-tiles[data-load-state="ready"]').waitFor();
	await page.locator(".tile.lilac").waitFor();
	await page.getByText("회비", { exact: true }).waitFor();
}

function isMyDataPath(pathname) {
	return ["/api/my/profile", "/api/my/stats", "/api/my/calendar"].includes(
		pathname,
	);
}

async function installMyDataRoute(
	page,
	{ delayMs = 0, failProfileOnce = false },
) {
	const records = [];
	let profileFailed = false;
	const handler = async (route) => {
		const request = route.request();
		let url;
		try {
			url = new URL(request.url());
		} catch {
			throw new Error("Invalid browser request URL in navigation evidence.");
		}
		if (!isMyDataPath(url.pathname)) {
			await route.continue();
			return;
		}
		const started = performance.now();
		const record = {
			path: `${url.pathname}${url.search}`,
			method: request.method(),
			startNodeMs: round(started),
		};
		records.push(record);
		if (delayMs > 0) await sleep(delayMs);
		if (
			failProfileOnce &&
			url.pathname === "/api/my/profile" &&
			!profileFailed
		) {
			profileFailed = true;
			record.injectedStatus = 500;
			record.responseNodeMs = round(performance.now());
			await route.fulfill({
				status: 500,
				contentType: "application/json",
				body: JSON.stringify({ message: "프로필을 불러오지 못했습니다." }),
			});
			return;
		}
		const response = await route.fetch();
		record.status = response.status();
		record.responseNodeMs = round(performance.now());
		await route.fulfill({ response });
	};
	const patterns = [
		"**/api/my/profile",
		"**/api/my/stats",
		"**/api/my/calendar**",
	];
	for (const pattern of patterns) await page.route(pattern, handler);
	return {
		records,
		dispose: async () => {
			for (const pattern of patterns) await page.unroute(pattern, handler);
		},
	};
}

async function installMyStateCollector(page) {
	await page.evaluate(() => {
		window.__captureMyState = () => {
			const text = document.body.innerText;
			const doc = document.scrollingElement;
			const rect = (selector) => {
				const element = document.querySelector(selector);
				if (!element) return null;
				const r = element.getBoundingClientRect();
				return { top: r.top, left: r.left, width: r.width, height: r.height };
			};
			const visible = (element) =>
				!!element && getComputedStyle(element).display !== "none";
			const navItems = [...document.querySelectorAll("nav a.nav-item")].map(
				(a) => ({
					text: a.textContent.trim(),
					href: a.getAttribute("href"),
					visible: visible(a),
					pointerEvents: getComputedStyle(a).pointerEvents,
				}),
			);
			const settings = [...document.querySelectorAll("button")].find((button) =>
				button.textContent.includes("설정"),
			);
			const calButtons = [...document.querySelectorAll(".cal-nav button")];
			return {
				ms: Math.round((performance.now() - window.__myNavStartedAt) * 10) / 10,
				path: location.pathname,
				title:
					document.querySelector(".hf-title, .hb-title")?.textContent.trim() ??
					null,
				chips: [...document.querySelectorAll(".hf-chip")].map((x) =>
					x.textContent.trim(),
				),
				busy:
					document.querySelector(".my-grid")?.getAttribute("aria-busy") ?? null,
				status:
					document.querySelector('[role="status"]')?.textContent.trim() ?? null,
				alert:
					document.querySelector('[role="alert"]')?.textContent.trim() ?? null,
				calendar:
					document.querySelector(".cal-title")?.textContent.trim() ?? null,
				calendarTop: rect(".calendar")?.top ?? null,
				myGrid: rect(".my-grid"),
				ringWrap: rect(".ring-wrap"),
				viewport: { width: innerWidth, height: innerHeight },
				contentHeight: doc?.scrollHeight ?? null,
				placeholderCount: document.querySelectorAll(
					".my-placeholder, .my-ring-placeholder",
				).length,
				settingsDisabled: settings?.disabled ?? null,
				calendarButtonsDisabled: calButtons.map((button) => button.disabled),
				navUsable:
					navItems.filter((item) => item.visible).length > 0 &&
					navItems
						.filter((item) => item.visible)
						.every((item) => item.pointerEvents !== "none"),
				navItems,
				hasBlazorError:
					visible(document.querySelector("#blazor-error-ui")) &&
					!document.querySelector("#blazor-error-ui")?.hidden,
				containsZeroDate: text.includes("0년 0월"),
				containsDefaultDate: text.includes("가입일 0001"),
				containsZeroCount: /(?:^|[^\d])0회(?:$|[^\d])/.test(text),
				containsBlankProfileChip: [
					...document.querySelectorAll(".hf-chip"),
				].some((x) => x.textContent.trim() === "·"),
			};
		};
		window.__myNavStartedAt = performance.now();
		window.__myNavFrames = [];
		window.__myNavObserverDone = false;
		const snap = () => {
			window.__myNavFrames.push(window.__captureMyState());
			const state = window.__myNavFrames.at(-1);
			if (
				performance.now() - window.__myNavStartedAt < 5000 &&
				!(state.path === "/my" && state.busy === "false")
			) {
				requestAnimationFrame(snap);
			} else {
				window.__myNavObserverDone = true;
			}
		};
		requestAnimationFrame(snap);
	});
}

async function captureMyState(page) {
	return page.evaluate(() => window.__captureMyState());
}

async function myResourceTimings(page) {
	return page.evaluate(() => {
		const records = [];
		const start = window.__myNavStartedAt;
		for (const entry of performance.getEntriesByType("resource")) {
			let url;
			try {
				url = new URL(entry.name);
			} catch {
				throw new Error("Invalid resource URL in navigation evidence.");
			}
			if (
				!["/api/my/profile", "/api/my/stats", "/api/my/calendar"].includes(
					url.pathname,
				)
			)
				continue;
			records.push({
				path: `${url.pathname}${url.search}`,
				startMs: Math.round((entry.startTime - start) * 10) / 10,
				responseStartMs: Math.round((entry.responseStart - start) * 10) / 10,
				endMs: Math.round((entry.responseEnd - start) * 10) / 10,
				durationMs: Math.round(entry.duration * 10) / 10,
			});
		}
		return records;
	});
}

function firstRecord(records, pathname) {
	return records.find((record) => record.path.split("?")[0] === pathname);
}

async function fixtureCalendarMonth(page) {
	const today = await api(page, "GET", "/api/my/activity");
	const [year, month] = today.activityDate.split("-").map(Number);
	return { year, month };
}

async function assertKnownFixtureDtos(page, target) {
	const { year, month } = await fixtureCalendarMonth(page);
	const profile = await api(page, "GET", "/api/my/profile");
	const stats = await api(page, "GET", "/api/my/stats");
	const calendar = await api(
		page,
		"GET",
		`/api/my/calendar?year=${year}&month=${month}`,
	);
	assert.deepEqual(
		{
			name: profile.name,
			studentNumber: profile.studentNumber,
			department: profile.department,
			role: profile.role,
			totalAttendance: profile.totalAttendance,
		},
		{
			name: "김민수",
			studentNumber: "202512001",
			department: "컴퓨터공학과",
			role: "MEMBER",
			totalAttendance: 1,
		},
		"seeded MY profile DTO",
	);
	assert.deepEqual(
		{
			semesterActivities: stats.semesterActivities,
			myAttendances: stats.myAttendances,
			attendanceRatePercent: stats.attendanceRatePercent,
			rank: stats.rank,
			totalActiveMembers: stats.totalActiveMembers,
		},
		{
			semesterActivities: 1,
			myAttendances: 1,
			attendanceRatePercent: 100,
			rank: 1,
			totalActiveMembers: 16,
		},
		"seeded MY stats DTO",
	);
	assert.deepEqual(
		{
			year: calendar.year,
			month: calendar.month,
			monthCount: calendar.monthCount,
			semesterCount: calendar.semesterCount,
			totalCount: calendar.totalCount,
		},
		{ year, month, monthCount: 1, semesterCount: 1, totalCount: 1 },
		"seeded MY calendar DTO",
	);
	target.finalDtos = { profile, stats, calendar };
}

async function verifyWarmHomeToMy(page, viewport, delayMs) {
	await page.setViewportSize(viewport);
	await goto(page, "/");
	await waitForHomeReady(page);
	const { year, month } = await fixtureCalendarMonth(page);
	const route = await installMyDataRoute(page, { delayMs });
	const label = `${viewport.width}x${viewport.height}-${delayMs}ms`;
	const condition = {
		mode: "warm Home -> MY",
		viewport,
		injectedRequestDelayMs: delayMs,
	};
	evidence.navigation.conditions.push(condition);
	try {
		await page.evaluate(() => performance.clearResourceTimings());
		await installMyStateCollector(page);
		await page.locator('nav a[href="/my"]:visible').first().click();
		await page.waitForURL(base + "/my");
		await page.locator(".my-grid").waitFor();
		const pendingScreenshot = `pending-${label}.png`;
		await page.screenshot({
			path: path.join(highPingDir, pendingScreenshot),
			fullPage: true,
		});
		await page.locator('.my-grid[aria-busy="false"]').waitFor();
		const ready = await captureMyState(page);
		const loadedScreenshot = `loaded-${label}.png`;
		await page.screenshot({
			path: path.join(highPingDir, loadedScreenshot),
			fullPage: true,
		});
		const frames = await page.evaluate(() => window.__myNavFrames ?? []);
		condition.frames = frames.filter(
			(_, index) => index % 5 === 0 || index === frames.length - 1,
		);
		condition.resources = await myResourceTimings(page);
		condition.routeRecords = route.records;
		condition.screenshots = [pendingScreenshot, loadedScreenshot];
		const pending = frames.find(
			(frame) => frame.path === "/my" && frame.busy === "true",
		);
		assert.ok(pending, `MY pending skeleton observed (${label})`);
		assert.equal(
			pending.status,
			"정보를 불러오는 중…",
			`pending status (${label})`,
		);
		assert.equal(
			pending.settingsDisabled,
			true,
			`settings disabled while pending (${label})`,
		);
		assert.ok(
			pending.calendarButtonsDisabled.length === 2 &&
				pending.calendarButtonsDisabled.every(Boolean),
			`calendar controls disabled while pending (${label})`,
		);
		assert.equal(
			pending.navUsable,
			true,
			`nav remains usable while pending (${label})`,
		);
		for (const frame of frames.filter((x) => x.path === "/my")) {
			assert.equal(frame.containsZeroDate, false, `no 0년 0월 (${label})`);
			assert.equal(frame.containsDefaultDate, false, `no 0001 date (${label})`);
			assert.equal(frame.containsZeroCount, false, `no fake 0회 (${label})`);
			assert.equal(
				frame.containsBlankProfileChip,
				false,
				`no blank profile chip (${label})`,
			);
		}
		assert.equal(ready.busy, "false", `MY ready marker (${label})`);
		assert.equal(ready.hasBlazorError, false, `no Blazor overlay (${label})`);
		assert.equal(ready.alert, null, `no load alert (${label})`);
		assert.equal(ready.title, "김민수", `ready profile name (${label})`);
		assert.ok(
			ready.chips.includes("202512001 · 컴퓨터공학과"),
			`ready profile chip (${label})`,
		);
		assert.ok(ready.chips.includes("부원"), `ready role chip (${label})`);
		assert.ok(
			ready.chips.includes("가입일 2026.03.02"),
			`ready joined chip (${label})`,
		);
		assert.equal(
			ready.calendar,
			`${year}년 ${month}월`,
			`ready calendar title (${label})`,
		);
		const movement = Math.abs(ready.calendarTop - pending.calendarTop);
		condition.calendarTopMovementPx = round(movement);
		assert.ok(
			movement <= 4,
			`calendarTop shifted ${round(movement)}px (baseline 159px, ${label})`,
		);
		const required = ["/api/my/profile", "/api/my/stats", "/api/my/calendar"];
		for (const pathname of required)
			assert.ok(
				firstRecord(route.records, pathname),
				`request observed ${pathname} (${label})`,
			);
		const starts = required.map(
			(pathname) => firstRecord(route.records, pathname).startNodeMs,
		);
		const startSpread = Math.max(...starts) - Math.min(...starts);
		condition.requestStartSpreadMs = round(startSpread);
		assert.ok(
			startSpread <= 250,
			`MY requests did not overlap (${round(startSpread)}ms, ${label})`,
		);
		if (delayMs >= 600) {
			const profile = firstRecord(route.records, "/api/my/profile");
			assert.ok(
				firstRecord(route.records, "/api/my/stats").startNodeMs <
					profile.responseNodeMs &&
					firstRecord(route.records, "/api/my/calendar").startNodeMs <
						profile.responseNodeMs,
				`stats/calendar did not start before delayed profile response (${label})`,
			);
		}
	} finally {
		await route.dispose();
	}
	await assertKnownFixtureDtos(page, condition);
}

async function verifyProfileFaultRetry(page) {
	await goto(page, "/");
	await waitForHomeReady(page);
	const route = await installMyDataRoute(page, { failProfileOnce: true });
	const condition = {
		mode: "MY profile 500 then retry",
		viewport: page.viewportSize(),
	};
	evidence.navigation.conditions.push(condition);
	try {
		await page.evaluate(() => performance.clearResourceTimings());
		await installMyStateCollector(page);
		await page.locator('nav a[href="/my"]:visible').first().click();
		await page.waitForURL(base + "/my");
		const alert = page.locator('[role="alert"]');
		await alert.waitFor();
		const failed = await captureMyState(page);
		condition.failedState = failed;
		condition.routeRecordsBeforeRetry = [...route.records];
		const failedScreenshot = "profile500-alert.png";
		await page.screenshot({
			path: path.join(highPingDir, failedScreenshot),
			fullPage: true,
		});
		assert.ok(
			failed.alert.includes("프로필을 불러오지 못했습니다."),
			"explicit profile load alert",
		);
		assert.equal(
			failed.hasBlazorError,
			false,
			"no error overlay for profile 500",
		);
		assert.equal(
			failed.containsZeroDate,
			false,
			"no 0년 0월 after profile 500",
		);
		assert.equal(
			failed.containsDefaultDate,
			false,
			"no 0001 after profile 500",
		);
		assert.equal(
			failed.containsZeroCount,
			false,
			"no fake 0회 after profile 500",
		);
		await alert
			.getByRole("button", { name: "다시 불러오기", exact: true })
			.click();
		await page.locator('.my-grid[aria-busy="false"]').waitFor();
		const ready = await captureMyState(page);
		condition.readyState = ready;
		condition.resources = await myResourceTimings(page);
		condition.routeRecords = route.records;
		const retryScreenshot = "profile500-retry-loaded.png";
		await page.screenshot({
			path: path.join(highPingDir, retryScreenshot),
			fullPage: true,
		});
		condition.screenshots = [failedScreenshot, retryScreenshot];
		assert.equal(ready.alert, null, "alert cleared after retry");
		assert.equal(ready.title, "김민수", "retry loaded profile");
		assert.equal(ready.calendar, "2026년 10월", "retry loaded calendar");
	} finally {
		await route.dispose();
	}
	await assertKnownFixtureDtos(page, condition);
}

async function verifyHighPingNavigation() {
	const mobile = await createPage({ width: 393, height: 852 });
	const desktop = await createPage({ width: 1280, height: 900 });
	try {
		for (const page of [mobile, desktop]) {
			await login(page, "202512001", "Member1234!");
			await waitForHomeReady(page);
		}
		for (const page of [mobile, desktop]) {
			for (const delayMs of [0, 600])
				await verifyWarmHomeToMy(page, page.viewportSize(), delayMs);
		}
		await verifyProfileFaultRetry(mobile);
	} finally {
		await mobile.context().close();
		await desktop.context().close();
	}
}

(async () => {
	try {
		docker("image", "inspect", "vicikyu-wasm-audit", "--format", "{{.Id}}");
		docker("network", "create", network);
		networkCreated = true;
		docker(
			"run",
			"-d",
			"--name",
			name,
			"--network",
			network,
			"--tmpfs",
			"/tmp",
			"-p",
			"127.0.0.1::8080",
			"-e",
			"ASPNETCORE_ENVIRONMENT=Verification",
			"-e",
			"ConnectionStrings__DefaultConnection=Data Source=/tmp/vicikyu-wasm-audit.db",
			"-e",
			`Seed__AdminPassword=${adminPassword}`,
			"-e",
			"Google__ServiceAccountJson=",
			"-e",
			"Google__SpreadsheetId=",
			"-e",
			"Sheets__SyncOnStartup=false",
			"vicikyu-wasm-audit",
		);
		containerCreated = true;
		const port = docker("port", name, "8080/tcp").split(":").at(-1);
		base = `http://127.0.0.1:${port}`;
		let ready = false;
		for (let i = 0; i < 60; i++) {
			try {
				if (
					(
						await fetch(base + "/Account/Login", {
							signal: AbortSignal.timeout(2000),
						})
					).ok
				) {
					ready = true;
					break;
				}
			} catch {
				/* Startup retry is bounded and has a final assertion. */
			}
			await new Promise((resolve) => setTimeout(resolve, 500));
		}
		assert.ok(ready, "Verification container did not become ready");
		evidence.image = docker(
			"image",
			"inspect",
			"vicikyu-wasm-audit",
			"--format",
			"{{.Id}}",
		);
		browser = await chromium.launch({
			headless: true,
			timeout: 30000,
			...(process.env.BROWSER_EXECUTABLE
				? { executablePath: process.env.BROWSER_EXECUTABLE }
				: { channel: "msedge" }),
		});
		evidence.browser = browser.version();
		await verifyHighPingNavigation();
		const admin = await createPage();
		const member = await createPage({ width: 393, height: 852 });
		const anonymous = await createPage();
		await goto(anonymous, "/Account/Login");
		await api(anonymous, "GET", "/api/my/profile", undefined, 401);
		await login(admin, "admin", adminPassword);
		await goto(admin, "/admin/members");
        const added = await verifyMemberBulk.synthetic({ page: admin, api, action, evidence, evidenceDir, memberPassword });
        evidence.flows.push("unified editable bulk preview / append / defaults / validation / single manual commit");
		await login(member, "202699999", memberPassword);
		await api(member, "GET", "/api/admin/teams", undefined, 403);
		await goto(member, "/admin");
		await member.waitForURL(base + "/Account/AccessDenied");
		await goto(anonymous, "/records");
		await anonymous.waitForURL(/\/Account\/Login/);
		await goto(admin, "/admin");
		const otp = await action(admin, "/api/admin/activity/otp", "POST", () =>
			admin.getByRole("button", { name: /^OTP (생성|변경)$/ }).click(),
		);
		await goto(member, "/");
		await member.getByRole("button", { name: "출석하기", exact: true }).click();
		await member.locator(".otp-input").fill(otp);
		await action(member, "/api/my/attendance", "POST", () =>
			member.getByRole("button", { name: "출석 확인", exact: true }).click(),
		);
		await member.getByText("오늘 출석 완료", { exact: true }).waitFor();
		const now = await api(member, "GET", "/api/my/activity");
		assert.equal(now.isAttended, true);
		const date = now.activityDate;
		const [year, month] = date.split("-").map(Number);
		const records = await api(
			member,
			"GET",
			`/api/my/records?year=${year}&month=${month}`,
		);
		assert.ok(records.records.length > 0);
		await api(member, "GET", `/api/my/calendar?year=${year}&month=${month}`);
		await goto(admin, "/admin/attendance");
		await api(
			admin,
			"DELETE",
			`/api/admin/attendance/${added.userId}`,
			{ confirmTeamImpact: true },
			204,
		);
		await api(
			admin,
			"POST",
			`/api/admin/attendance/${added.userId}`,
			undefined,
			204,
		);
		evidence.flows.push(
			"OTP UI / own records+calendar / admin attendance correction / 401+403 navigation",
		);
		await goto(admin, "/admin/teams");
		await admin
			.locator('input[placeholder="게스트 이름"]')
			.fill("WASM 검증 게스트");
		await action(admin, "/api/admin/teams/guests", "POST", () =>
			admin.getByRole("button", { name: "+ 게스트 추가", exact: true }).click(),
		);
		await action(admin, "/api/admin/teams/generate", "POST", () =>
			admin.getByRole("button", { name: "랜덤 팀 뽑기", exact: true }).click(),
		);
		assert.equal((await api(member, "GET", "/api/my/teams")).length, 0);
		await admin
			.getByRole("button", { name: "팀 편성 확정", exact: true })
			.click();
		await action(admin, "/api/admin/teams/confirm", "POST", () =>
			admin
				.locator(".confirm-box")
				.getByRole("button", { name: "확정", exact: true })
				.click(),
		);
		const teams = await api(member, "GET", "/api/my/teams");
		assert.ok(teams.length >= 2);
		const moving = teams[0].members[0];
		await api(admin, "PUT", `/api/admin/teams/members/${moving.teamMemberId}`, {
			targetTeamId: teams[1].teamId,
		});
		evidence.flows.push(
			"guest / random team UI / confirmation visibility / team move",
		);
		await goto(admin, "/admin/fees");
		await admin
			.getByRole("button", { name: "+ 기간 추가", exact: true })
			.click();
		const feeForm = admin.locator("form").first();
		await feeForm
			.locator('input:not([type]), input[type="text"]')
			.fill("WASM 검증 회비");
		await feeForm.locator('input[type="date"]').nth(0).fill("2098-01-01");
		await feeForm.locator('input[type="date"]').nth(1).fill("2098-06-30");
		await feeForm.locator('input[type="number"]').fill("10000");
		const fee = await action(admin, "/api/admin/fees", "POST", () =>
			feeForm.getByRole("button", { name: "추가", exact: true }).click(),
		);
		await api(
			admin,
			"PUT",
			`/api/admin/fees/${fee.id}/members/${added.userId}`,
			{ isPaid: true },
			204,
		);
		assert.ok((await api(member, "GET", "/api/my/fees")).some((f) => f.isPaid));
		await goto(admin, "/admin/semesters");
		await admin
			.getByRole("button", { name: "+ 학기 추가", exact: true })
			.click();
		const semesterForm = admin.locator("form").first();
		await semesterForm
			.locator('input:not([type]), input[type="text"]')
			.fill("WASM 검증 학기");
		await semesterForm.locator('input[type="date"]').nth(0).fill("2098-01-01");
		await semesterForm.locator('input[type="date"]').nth(1).fill("2098-06-30");
		const semester = await action(admin, "/api/admin/semesters", "POST", () =>
			semesterForm.getByRole("button", { name: "추가", exact: true }).click(),
		);
		await api(admin, "PUT", `/api/admin/semesters/${semester.id}`, {
			name: "WASM 수정 학기",
			startDate: "2098-01-01",
			endDate: "2098-06-30",
		});
		await api(
			admin,
			"DELETE",
			`/api/admin/semesters/${semester.id}`,
			undefined,
			204,
		);
		evidence.flows.push(
			"fee UI create+payment / semester UI create+update+delete",
		);
		await goto(admin, "/admin/settings");
		await admin.locator('input[placeholder="출석부"]').fill("WASM 검증 시트");
		await admin
			.locator('input[placeholder^="https://docs.google.com"]')
			.fill(
				"https://docs.google.com/spreadsheets/d/wasm-verification-sheet-0000000001/edit",
			);
		await action(admin, "/api/admin/sheets", "POST", () =>
			admin.getByRole("button", { name: "추가", exact: true }).click(),
		);
		await admin.locator("textarea").fill("verification-only-not-credentials");
		await action(admin, "/api/admin/settings/google-key", "PUT", () =>
			admin.getByRole("button", { name: "키 저장", exact: true }).click(),
		);
		assert.equal(await admin.locator("textarea").inputValue(), "");
		await goto(admin, "/admin");
		await action(admin, "/api/admin/sheets/resync", "POST", () =>
			admin.getByRole("button", { name: "시트 갱신", exact: true }).click(),
		);
		const audit = await api(admin, "GET", "/api/admin/audit");
		assert.ok(audit.length > 0);
		assert.ok(
			!JSON.stringify(audit).includes("verification-only-not-credentials"),
		);
		await goto(admin, `/admin/members/${added.userId}`);
		await api(admin, "GET", `/api/admin/members/${added.userId}/mappings`);
		evidence.flows.push(
			"settings sheet+write-only key UI / fake resync / audit / member detail+mapping",
		);
		await goto(member, "/my");
		await member.getByRole("button", { name: "설정", exact: true }).click();
		const swatch = member.locator(".color-swatch").first();
		const colorLabel = await swatch.getAttribute("aria-label");
		await swatch.click();
		await action(member, "/api/my/profile/color", "PUT", () =>
			member.getByRole("button", { name: "색 저장", exact: true }).click(),
		);
		assert.equal(
			(await api(member, "GET", "/api/my/profile")).color,
			colorLabel.replace("색상 ", ""),
		);
		await member.getByRole("button", { name: "닫기", exact: true }).click();
		evidence.flows.push("personal settings color UI persistence");
		await verifyLoadingUx({
			createPage,
			login,
			api,
			goto,
			base,
			adminPassword,
			memberId: added.userId,
			memberStudentNumber: "202699999",
			evidence,
			evidenceDir,
		});
		const routes = [
			"/",
			"/my",
			"/records",
			"/admin",
			"/admin/attendance",
			"/admin/teams",
			"/admin/members",
			`/admin/members/${added.userId}`,
			"/admin/fees",
			"/admin/semesters",
			"/admin/settings",
			"/admin/audit",
			"/Account/Login",
			"/Account/Logout",
			"/Account/AccessDenied",
			"/Error",
			"/not-found",
		];
		for (const viewport of [
			{ width: 1280, height: 900 },
			{ width: 393, height: 852 },
		]) {
			await admin.setViewportSize(viewport);
			for (const route of routes) {
				await goto(admin, route);
				await admin.reload({ waitUntil: "load" });
				await admin.locator(".app-shell").waitFor();
				await admin.waitForLoadState("networkidle");
				const label = `${viewport.width}x${viewport.height}-${route}`;
				await measure(admin, label);
				evidence.routes.push({
					route,
					viewport,
					directLoad: true,
					reload: true,
				});
			}
		}
		await goto(admin, "/admin");
		await admin.getByRole("button", { name: "활동 마감", exact: true }).click();
		await action(admin, "/api/admin/activity/close", "POST", () =>
			admin
				.locator(".confirm-box")
				.getByRole("button", { name: "마감", exact: true })
				.click(),
		);
		await admin
			.getByRole("button", { name: "활동 다시 열기", exact: true })
			.click();
		await action(admin, "/api/admin/activity/reopen", "POST", () =>
			admin
				.locator(".confirm-box")
				.getByRole("button", { name: "다시 열기", exact: true })
				.click(),
		);
		evidence.flows.push("activity close/reopen UI and server persistence");
		await goto(member, "/my");
		await member.getByRole("button", { name: "설정", exact: true }).click();
		const nextPassword = `Changed-${suffix}!`;
		await member
			.getByPlaceholder("현재 비밀번호", { exact: true })
			.fill(memberPassword);
		await member
			.getByPlaceholder("새 비밀번호(4자 이상)", { exact: true })
			.fill(nextPassword);
		await member
			.getByPlaceholder("새 비밀번호 확인", { exact: true })
			.fill(nextPassword);
		await action(member, "/api/my/profile/password", "PUT", () =>
			member
				.getByRole("button", { name: "비밀번호 변경", exact: true })
				.click(),
		);
		await api(member, "GET", "/api/auth/user", undefined, 401);
		await login(member, "202699999", nextPassword);
		await goto(member, "/Account/Logout");
		await member.getByRole("button", { name: "로그아웃", exact: true }).click();
		await member.waitForURL(base + "/Account/Login");
		await api(member, "GET", "/api/my/profile", undefined, 401);
		evidence.flows.push(
			"password change / previous session revoked / re-login / logout UI",
		);
        sensitiveRosterActive = true;
        await verifyMemberBulk.actual({ page: admin, api, action, evidence, workbookPath, goto });
        evidence.flows.push("actual 46-record XLSX / isolated seed collisions / atomic commit / duplicate rejection");
		assert.ok(
			evidence.wasmResponses > 0,
			"No real WASM binary responses observed",
		);
		assert.deepEqual(errors, [], "Browser errors");
		evidence.success = true;
		console.log(
			`PASS: ${evidence.routes.length} route/viewport checks, ${evidence.flows.length} workflows, ${evidence.wasmResponses} WASM responses, 0 browser errors.`,
		);
	} catch (error) {
		evidence.success = false;
		evidence.failure = error.message;
		evidence.failurePages = [];
		if (browser) {
			for (const [index, context] of browser.contexts().entries()) {
				for (const page of context.pages()) {
                    if (!page.isClosed() && sensitiveRosterActive) {
                        evidence.failurePages.push({ url: page.url().replace(base, ""), text: "Redacted: actual-workbook verification; no personal data captured." });
                    } else if (!page.isClosed()) {
                        await page.screenshot({
							path: path.join(evidenceDir, `failure-${index}.png`),
							fullPage: true,
						});
						evidence.failurePages.push({
							url: page.url().replace(base, ""),
							text: (await page.locator("body").innerText()).slice(-1800),
						});
					}
				}
			}
		}
		console.error(error.message);
		process.exitCode = 1;
	} finally {
		evidence.errors = errors;
		fs.writeFileSync(
			path.join(highPingDir, "current.json"),
			JSON.stringify(evidence.navigation, null, 2),
		);
		fs.writeFileSync(
			path.join(evidenceDir, "results.json"),
			JSON.stringify(evidence, null, 2),
		);
		try {
			if (browser) await browser.close();
		} finally {
			cleanup();
			clearTimeout(deadline);
		}
	}
})();
