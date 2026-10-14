// Isolated local Docker + desktop Chromium regression. NOT a real iPhone/PWA test.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const { execFileSync } = require("node:child_process");
const root = path.resolve(__dirname, "..");
const evidenceDir = path.join(root, "artifacts", "wasm-browser");
fs.mkdirSync(evidenceDir, { recursive: true });
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
	environment: "isolated Docker / Chromium desktop engine",
	realIPhone: false,
	externalSheets: "test doubles",
	routes: [],
	flows: [],
	screenshots: [],
	measurements: [],
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
		console.error("Verification exceeded 12 minutes.");
		try {
			cleanup();
		} finally {
			process.exit(1);
		}
	},
	12 * 60 * 1000,
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
		`${method} ${resource}: ${JSON.stringify(result.data)}`,
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
		const admin = await createPage();
		const member = await createPage({ width: 393, height: 852 });
		const anonymous = await createPage();
		await goto(anonymous, "/Account/Login");
		await api(anonymous, "GET", "/api/my/profile", undefined, 401);
		await login(admin, "admin", adminPassword);
		await goto(admin, "/admin/members");
		await admin
			.getByRole("button", { name: "+ 부원 추가", exact: true })
			.click();
		const addForm = admin.locator("form");
		await addForm.locator('input[placeholder="202512345"]').fill("202699999");
		await addForm.locator('input[placeholder="김민수"]').fill("WASM 검증 부원");
		await addForm.locator('input[placeholder="컴퓨터공학과"]').fill("검증학과");
		await addForm.locator('input[type="password"]').fill(memberPassword);
		const added = await action(admin, "/api/admin/members", "POST", () =>
			addForm.getByRole("button", { name: "추가", exact: true }).click(),
		);
		evidence.flows.push("admin member create through WASM form");
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
					if (!page.isClosed()) {
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
