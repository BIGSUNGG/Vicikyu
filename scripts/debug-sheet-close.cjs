// Close-event chain debug: popup → 닫기 → close event → .NET → download
const path = require("node:path");
const fs = require("node:fs");
const crypto = require("node:crypto");
const { execFileSync } = require("node:child_process");
const { chromium } = require(
	path.resolve(__dirname, "../.diag/pw/node_modules/playwright"),
);
const root = path.resolve(__dirname, "..");
const out = path.join(root, "artifacts/sheet-write-safety/debug");
fs.mkdirSync(out, { recursive: true });
const name = `vicikyu-dbg-${crypto.randomUUID()}`;
const image = `${name}:local`;
const password = `Verify-${crypto.randomUUID()}!`;
let browser, page, base;
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
	} finally {
		if (fd !== undefined) fs.closeSync(fd);
	}
}
(async () => {
	try {
		docker(["build", "-q", "-t", image, "."], 600000, "build.log");
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
			`-e`,
			`Seed__AdminPassword=${password}`,
			"-e",
			"Google__ServiceAccountJson=",
			"-e",
			"Google__SpreadsheetId=",
			"-e",
			"Sheets__SyncOnStartup=false",
			image,
		]);
		docker(["start", name]);
		base = `http://${docker(["port", name, "8080/tcp"])}`;
		let healthy = false;
		for (let i = 0; i < 60 && !healthy; i++) {
			try {
				healthy = (
					await fetch(base + "/Account/Login", {
						signal: AbortSignal.timeout(1000),
					})
				).ok;
			} catch {}
			if (!healthy) await new Promise((r) => setTimeout(r, 500));
		}
		browser = await chromium.launch({
			headless: true,
			channel: process.env.BROWSER_CHANNEL || "msedge",
		});
		page = await (
			await browser.newContext({ viewport: { width: 1280, height: 900 } })
		).newPage();
		page.setDefaultTimeout(15000);
		page.on("console", (m) =>
			console.log("CONSOLE", m.type(), m.text().slice(0, 300)),
		);
		page.on("pageerror", (e) =>
			console.log("PAGEERROR", e.message.slice(0, 300)),
		);
		page.on("response", (r) => {
			if (r.url().includes("/api/"))
				console.log(
					"API",
					r.request().method(),
					r.url().split("/api/")[1],
					r.status(),
				);
		});
		await page.goto(base + "/Account/Login");
		await page.locator("#student-number").fill("admin");
		await page.locator("#password").fill(password);
		await page.getByRole("button", { name: "로그인", exact: true }).click();
		await page.waitForURL(base + "/");
		const csrf = await page.evaluate(
			async () => (await (await fetch("/api/auth/csrf")).json()).token,
		);
		const call = async (method, url, body) => {
			const r = await page.evaluate(
				async ({ method, url, body, csrf }) => {
					const headers = { "X-CSRF-TOKEN": csrf };
					if (body !== undefined) headers["Content-Type"] = "application/json";
					const resp = await fetch(url, {
						method,
						headers,
						body: body === undefined ? undefined : JSON.stringify(body),
					});
					return {
						status: resp.status,
						headers: Object.fromEntries(resp.headers.entries()),
						text: await resp.text(),
					};
				},
				{ method, url, body, csrf },
			);
			console.log(
				"CALL",
				method,
				url,
				r.status,
				r.headers["x-sheet-sync-failed"] ?? "",
			);
			return r;
		};
		const fixture = "/api/verification/activity-management";
		const s = JSON.parse((await call("GET", fixture + "/state")).text);
		const first = s.sheets.find(
			(x) => x.spreadsheetId === "verification-activity-first",
		);
		const member = s.members.find((x) => x.studentNumber === "202512001");
		await call("POST", fixture + "/fault", { mode: "beforeApply" });
		await page.goto(base + "/admin/activities");
		await page.locator('.activity-list[data-load-state="ready"]').waitFor();
		const list = JSON.parse((await call("GET", "/api/admin/activities")).text);
		const completed = list.find(
			(x) => x.sheetId === first.id && x.date === "2026-10-01",
		);
		await page
			.locator(`.activity-list [data-activity-key="${completed.key}"]`)
			.getByRole("button", { name: /수정$/ })
			.click();
		await page.locator('.activity-members[data-load-state="ready"]').waitFor();
		const toggleUrl = `/api/admin/activities/${encodeURIComponent(completed.key)}/members/${member.id}`;
		await page
			.locator(
				`.activity-members [data-user-id="${member.id}"] [role="switch"]`,
			)
			.click();
		await page.waitForResponse(
			(r) => r.url() === base + toggleUrl && r.request().method() === "PUT",
		);
		const dialog = page.locator('[data-testid="sheet-sync-dialog"]');
		await dialog.waitFor({ state: "visible" });
		console.log(
			"DIALOG OPEN, pending status:",
			JSON.parse((await call("GET", "/api/admin/sheets/sync-status")).text)
				.pendingCount,
		);
		console.log("dialog open attr:", await dialog.evaluate((d) => d.open));
		// 닫기 클릭 후 이벤트 체인 관찰
		const downloadPromise = page
			.waitForEvent("download", { timeout: 10000 })
			.catch((e) => "NO_DOWNLOAD: " + e.message.split("\n")[0]);
		await dialog.getByRole("button", { name: "닫기", exact: true }).click();
		await page.waitForTimeout(2500);
		console.log(
			"dialog open after close click:",
			await dialog.evaluate((d) => d.open),
		);
		const result = await downloadPromise;
		console.log(
			"DOWNLOAD RESULT:",
			result === "string-start" ? "captured" : result,
		);
		await page.screenshot({
			path: path.join(out, "debug.png"),
			fullPage: false,
		});
	} catch (e) {
		console.error("FAIL", e.message);
		process.exitCode = 1;
	} finally {
		if (browser) await browser.close();
		try {
			docker(["logs", name], 15000, "server.log");
		} catch {}
		try {
			docker(["rm", "-f", name]);
			docker(["image", "rm", image]);
		} catch {}
	}
})();
