// Focused, synthetic-data SQLite backup UI regression. Desktop Chromium, not a real iPhone/PWA.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const { execFileSync } = require("node:child_process");
const root = path.resolve(__dirname, "..");
const dir = path.join(root, "artifacts", "database-backup");
fs.mkdirSync(dir, { recursive: true });
const { chromium } = require(
	process.env.PLAYWRIGHT_MODULE ||
		path.join(root, ".diag", "pw", "node_modules", "playwright"),
);
const image = "vicikyu-db-backup-check";
const name = `vicikyu-db-backup-check-${crypto.randomUUID().slice(0, 8)}`;
const network = `${name}-net`;
const password = `Verify-${crypto.randomUUID()}!`;
const errors = [];
const result = {
	startedAt: new Date().toISOString(),
	success: false,
	environment: "isolated Docker Verification / headless desktop Chromium",
	realIPhone: false,
	standalonePwa: false,
	externalSheets: "server-side test doubles; empty Google configuration",
	flows: [],
	screenshots: [],
	measurements: [],
	errors,
	wasmResponses: 0,
};
let containerCreated = false,
	networkCreated = false,
	browser,
	base;
function docker(...args) {
	try {
		return execFileSync("docker", args, {
			cwd: root,
			encoding: "utf8",
			timeout: 120000,
			maxBuffer: 12 * 1024 * 1024,
			stdio: ["ignore", "pipe", "pipe"],
		}).trim();
	} catch (e) {
		throw new Error(
			`Docker ${args[0]} failed: ${String(e.stderr || "").slice(-1500)}`,
		);
	}
}
const existingContainers = () =>
	docker("ps", "--format", "{{.ID}}\t{{.Names}}\t{{.Image}}")
		.split("\n")
		.filter((x) => x && !x.includes(name))
		.sort();
function cleanup() {
	if (containerCreated) {
		try {
			fs.writeFileSync(path.join(dir, "container.log"), docker("logs", name));
		} catch (e) {
			errors.push(e.message);
		}
		docker("rm", "-f", name);
		containerCreated = false;
	}
	if (networkCreated) {
		docker("network", "rm", network);
		networkCreated = false;
	}
}
const deadline = setTimeout(() => {
	errors.push("Verification exceeded 10 minutes");
	try {
		cleanup();
	} finally {
		result.success = false;
		fs.writeFileSync(
			path.join(dir, "results.json"),
			JSON.stringify(result, null, 2),
		);
		process.exit(1);
	}
}, 600000);
for (const signal of ["SIGINT", "SIGTERM"])
	process.on(signal, () => {
		try {
			cleanup();
		} finally {
			process.exit(1);
		}
	});
async function api(page, method, resource, body, expected = 200) {
	const response = await page.evaluate(
		async ({ method, resource, body }) => {
			const headers = {};
			if (method !== "GET")
				headers["X-CSRF-TOKEN"] = (
					await (await fetch("/api/auth/csrf")).json()
				).token;
			if (body !== undefined) headers["Content-Type"] = "application/json";
			const r = await fetch(resource, {
				method,
				headers,
				credentials: "same-origin",
				body: body === undefined ? undefined : JSON.stringify(body),
			});
			const text = await r.text();
			try {
				return { status: r.status, data: text ? JSON.parse(text) : null };
			} catch {
				throw new Error(
					`Non-JSON API response: ${method} ${resource} (${r.status})`,
				);
			}
		},
		{ method, resource, body },
	);
	assert.equal(
		response.status,
		expected,
		`${method} ${resource}: unexpected status`,
	);
	return response.data;
}
async function go(page, route) {
	assert.equal(
		(await page.goto(base + route, { waitUntil: "load" })).status(),
		200,
	);
	await page.locator(".app-shell").waitFor();
	await page.waitForLoadState("networkidle");
	assert.equal(await page.locator("#blazor-error-ui").isVisible(), false);
}
async function login(page) {
	await go(page, "/Account/Login");
	await page.locator("#student-number").fill("admin");
	await page.locator("#password").fill(password);
	await page.getByRole("button", { name: "로그인", exact: true }).click();
	await page.waitForURL(base + "/");
	await api(page, "GET", "/api/auth/user");
}
async function createPage(viewport) {
	const context = await browser.newContext({
		viewport,
		reducedMotion: "reduce",
		timezoneId: "Asia/Seoul",
	});
	const page = await context.newPage();
	page.setDefaultTimeout(20000);
	page.setDefaultNavigationTimeout(45000);
	page.on("pageerror", (e) => errors.push(e.message));
	page.on("console", (m) => {
		if (
			m.type() === "error" &&
			!m.text().startsWith("Failed to load resource:")
		)
			errors.push(m.text());
	});
	page.on("websocket", () =>
		errors.push("Unexpected server circuit websocket"),
	);
	page.on("response", (r) => {
		if (r.url().includes(".wasm") && r.ok()) result.wasmResponses++;
	});
	return page;
}
async function capture(page, label) {
	await page.getByTestId("database-backup").scrollIntoViewIfNeeded();
	const measurement = await page.evaluate(() => {
		const d = document.scrollingElement;
		const section = document.querySelector('[data-testid="database-backup"]');
		const rect = section.getBoundingClientRect();
		return {
			width: innerWidth,
			height: innerHeight,
			scrollWidth: d.scrollWidth,
			clientWidth: d.clientWidth,
			section: { left: rect.left, right: rect.right, width: rect.width },
		};
	});
	assert.ok(
		measurement.scrollWidth <= measurement.clientWidth + 1,
		`Horizontal overflow: ${label}`,
	);
	assert.ok(
		measurement.section.left >= -1 &&
			measurement.section.right <= measurement.width + 1,
		`Section outside viewport: ${label}`,
	);
	const relative = `artifacts/database-backup/${label}.png`;
	await page.screenshot({ path: path.join(root, relative), fullPage: true });
	result.measurements.push({ label, ...measurement });
	result.screenshots.push({
		path: relative,
		sha256: crypto
			.createHash("sha256")
			.update(fs.readFileSync(path.join(root, relative)))
			.digest("hex"),
	});
}
async function selectAndConfirm(page, input) {
	await page.getByTestId("database-import-file").setInputFiles(input);
	await page.getByTestId("database-import-review").click();
	assert.equal(
		await page.getByTestId("database-import-confirm").isDisabled(),
		true,
	);
	await page.getByTestId("database-import-acknowledge").check();
}
async function verify(viewport, label) {
	const page = await createPage(viewport),
		observer = await createPage(viewport);
	try {
		await login(page);
		await login(observer);
		await go(page, "/admin/settings");
		assert.match(
			await page.getByTestId("database-backup").innerText(),
			/민감정보/,
		);
		assert.match(
			await page.getByTestId("database-backup").innerText(),
			/자동 동기화는 유지/,
		);
		await capture(page, `${label}-settings`);
		const [download] = await Promise.all([
			page.waitForEvent("download"),
			page.getByTestId("database-export").click(),
		]);
		assert.match(download.suggestedFilename(), /^vicikyu-.*\.db$/);
		const exported = path.join(dir, `${label}-export.db`);
		await download.saveAs(exported);
		const bytes = fs.readFileSync(exported);
		assert.equal(bytes.subarray(0, 16).toString(), "SQLite format 3\u0000");
		result.flows.push({
			label,
			action: "download",
			file: path.relative(root, exported).replaceAll("\\", "/"),
			bytes: bytes.length,
			sha256: crypto.createHash("sha256").update(bytes).digest("hex"),
		});
		const studentNumber = `backup-${label}`;
		await api(page, "POST", "/api/admin/members", {
			studentNumber,
			name: "합성 검증 부원",
			department: "검증학과",
			password: `Synthetic-${crypto.randomUUID()}!`,
		});
		const count = async () =>
			(
				await api(page, "POST", "/api/admin/members/search", {
					search: studentNumber,
				})
			).totalCount;
		assert.equal(await count(), 1);
		let imports = 0;
		page.on("request", (r) => {
			if (
				r.method() === "POST" &&
				r.url().includes("/api/admin/database/import")
			)
				imports++;
		});
		await selectAndConfirm(page, exported);
		await capture(page, `${label}-confirmation`);
		const beforeCancel = imports;
		await page.getByTestId("database-import-cancel").click();
		assert.equal(await page.getByTestId("database-confirmation").count(), 0);
		assert.equal(imports, beforeCancel);
		assert.equal(await count(), 1);
		result.flows.push({
			label,
			action: "cancel",
			noImportRequest: true,
			originalDataPreserved: true,
			acknowledgementRequired: true,
		});
		await selectAndConfirm(page, {
			name: "corrupt.db",
			mimeType: "application/octet-stream",
			buffer: Buffer.from("not a SQLite database"),
		});
		const [invalid] = await Promise.all([
			page.waitForResponse(
				(r) =>
					r.url().endsWith("/api/admin/database/import") &&
					r.request().method() === "POST",
			),
			page.getByTestId("database-import-confirm").click(),
		]);
		assert.equal(invalid.status(), 400);
		await page.getByTestId("database-error").waitFor();
		assert.match(
			await page.getByTestId("database-error").innerText(),
			/損|손상|무결성|읽을 수/,
		);
		assert.equal(await count(), 1);
		await capture(page, `${label}-invalid-file`);
		result.flows.push({
			label,
			action: "corrupt-file",
			status: invalid.status(),
			originalDataPreserved: true,
		});
		if (label === "desktop") {
			const token = await page.evaluate(
				async () => (await (await fetch("/api/auth/csrf")).json()).token,
			);
			const oversized = await page.request.post(
				base + "/api/admin/database/import",
				{
					headers: { "X-CSRF-TOKEN": token },
					multipart: {
						file: {
							name: "oversized.db",
							mimeType: "application/octet-stream",
							buffer: Buffer.alloc(64 * 1024 * 1024 + 1),
						},
						confirm: "RESTORE",
					},
					timeout: 30000,
				},
			);
			assert.ok(
				[400, 413].includes(oversized.status()),
				"Kestrel must reject >64MiB upload",
			);
			assert.equal(await count(), 1);
			result.flows.push({
				label,
				action: "real-kestrel-upload-limit",
				status: oversized.status(),
				originalDataPreserved: true,
			});
		}
		await selectAndConfirm(page, exported);
		const [restored] = await Promise.all([
			page.waitForResponse(
				(r) =>
					r.url().endsWith("/api/admin/database/import") &&
					r.request().method() === "POST",
			),
			page.getByTestId("database-import-confirm").click(),
		]);
		assert.equal(restored.status(), 204);
		await page.waitForURL(base + "/Account/Login");
		await page.locator("#student-number").waitFor();
		await api(observer, "GET", "/api/auth/user", undefined, 401);
		await login(page);
		assert.equal(await count(), 0);
		await go(page, "/admin/settings");
		await capture(page, `${label}-restored`);
		result.flows.push({
			label,
			action: "restore-relogin",
			status: restored.status(),
			wholeSnapshotRestored: true,
			otherOldCookieRevoked: true,
		});
	} finally {
		await page.context().close();
		await observer.context().close();
	}
}
(async () => {
	let existingBefore;
	try {
		existingBefore = existingContainers();
		result.existingContainersBefore = existingBefore;
		result.image = docker("image", "inspect", image, "--format", "{{.Id}}");
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
			`Seed__AdminPassword=${password}`,
			"-e",
			"Google__ServiceAccountJson=",
			"-e",
			"Google__SpreadsheetId=",
			"-e",
			"Sheets__SyncOnStartup=false",
			image,
		);
		containerCreated = true;
		const isolation = JSON.parse(docker("inspect", name))[0];
		assert.ok(
			isolation.Mounts.every((m) => m.Type === "tmpfs"),
			"No bind or persistent volumes allowed",
		);
		result.isolation = {
			container: name,
			network,
			mounts: isolation.Mounts.map((m) => ({
				type: m.Type,
				destination: m.Destination,
			})),
			tmpfs: isolation.HostConfig.Tmpfs,
			loopbackOnly: true,
		};
		base = `http://127.0.0.1:${docker("port", name, "8080/tcp").split(":").at(-1)}`;
		result.base = base;
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
				/* Bounded readiness probe, asserted below. */
			}
			await new Promise((resolve) => setTimeout(resolve, 500));
		}
		assert.ok(ready, "Isolated server did not become ready");
		browser = await chromium.launch({
			headless: true,
			timeout: 30000,
			...(process.env.BROWSER_EXECUTABLE
				? { executablePath: process.env.BROWSER_EXECUTABLE }
				: { channel: "chrome" }),
		});
		result.browser = browser.version();
		result.os = process.platform;
		const anonymous = await browser.newContext();
		assert.equal(
			(
				await anonymous.request.get(base + "/api/admin/database/export")
			).status(),
			401,
		);
		await anonymous.close();
		await verify({ width: 1280, height: 900 }, "desktop");
		await verify({ width: 393, height: 852 }, "mobile-viewport");
		result.backups = docker(
			"exec",
			name,
			"find",
			"/tmp/database-backups",
			"-type",
			"f",
			"-name",
			"before-restore-*.db",
		).split("\n");
		assert.equal(result.backups.length, 2);
		assert.ok(result.wasmResponses > 0, "Actual WASM assets must be loaded");
		assert.deepEqual(errors, []);
		result.success = true;
	} catch (e) {
		result.failure = e.stack;
		errors.push(e.message);
		process.exitCode = 1;
	} finally {
		if (browser) await browser.close();
		try {
			cleanup();
			result.cleanup = {
				containerRemoved: !containerCreated,
				networkRemoved: !networkCreated,
			};
		} catch (e) {
			errors.push(e.message);
			result.success = false;
			process.exitCode = 1;
		}
		try {
			result.existingContainersAfter = existingContainers();
			assert.deepEqual(result.existingContainersAfter, existingBefore);
			result.operatingContainersUnchanged = true;
		} catch (e) {
			errors.push(e.message);
			result.success = false;
			process.exitCode = 1;
		}
		result.finishedAt = new Date().toISOString();
		clearTimeout(deadline);
		fs.writeFileSync(
			path.join(dir, "results.json"),
			JSON.stringify(result, null, 2),
		);
		console.log(
			JSON.stringify({
				success: result.success,
				flows: result.flows.length,
				screenshots: result.screenshots.length,
				errors,
				result: "artifacts/database-backup/results.json",
			}),
		);
	}
})();
