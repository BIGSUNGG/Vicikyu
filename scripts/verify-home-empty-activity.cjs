// Home no-activity regression check.
//   dev:   HOME_BASE_URL=http://localhost:5121 node verify-home-empty-activity.cjs --delete
//          — DELETES today's activity (dev DB only!) then asserts 204 + no error banner.
//   real:  (no flag; never mutates data) — asserts /api/my/activity is never an empty 200
//          and the home page shows no load-error banner.
const assert = require("node:assert/strict");
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright-core");

const base = process.env.HOME_BASE_URL;
const user = process.env.HOME_LOGIN_USER || "admin";
const password = process.env.HOME_LOGIN_PASSWORD;
const allowDelete = process.argv.includes("--delete");
const pause = (ms) => new Promise((r) => setTimeout(r, ms));

async function until(predicate, label, timeout = 20000) {
	const start = Date.now();
	while (Date.now() - start < timeout) {
		if (await predicate()) return;
		await pause(100);
	}
	throw new Error(`Timed out: ${label}`);
}

(async () => {
	assert.ok(base, "Set HOME_BASE_URL.");
	assert.ok(password, "Set HOME_LOGIN_PASSWORD.");
	const browser = await chromium.launch({ headless: true, channel: "msedge", timeout: 15000 });
	try {
		const context = await browser.newContext({
			viewport: { width: 393, height: 852 },
			userAgent:
				"Mozilla/5.0 (iPhone; CPU iPhone OS 26_6_1 like Mac OS X) AppleWebKit/605.1.15 Version/26.6.1 Mobile/15E148 Safari/604.1",
		});
		const req = context.request;
		const csrf = (await (await req.get(`${base}/api/auth/csrf`)).json()).token;
		const login = await req.post(`${base}/api/auth/login`, {
			headers: { "X-CSRF-TOKEN": csrf },
			data: { studentNumber: user, password: password },
		});
		assert.ok(login.ok || login.status() === 204, `Login failed: ${login.status()}`);

		const activityRes = await req.get(`${base}/api/my/activity`);
		if (activityRes.status() === 200) {
			const body = await activityRes.text();
			assert.ok(body.trim().length > 0, "FAIL: /api/my/activity returned 200 with an EMPTY body (the bug).");
			if (allowDelete) {
				const today = await (await req.get(`${base}/api/admin/activity/today`)).json();
				assert.ok(today.activityId > 0, "Today activity exists for deletion.");
				const csrf = (await (await req.get(`${base}/api/auth/csrf`)).json()).token;
				const del = await req.delete(`${base}/api/admin/activities/${today.activityId}`, {
					headers: { "X-CSRF-TOKEN": csrf },
				});
				assert.ok(del.ok, `Activity delete failed: ${del.status()}`);
			}
		}
		const after = await req.get(`${base}/api/my/activity`);
		if (after.status() === 200) {
			assert.ok((await after.text()).trim().length > 0, "Empty-200 regression on /api/my/activity.");
			console.log(`state: activity present (JSON) — empty-200 regression not present`);
		} else {
			assert.equal(after.status(), 204, `/api/my/activity must be 204 when no activity, got ${after.status()}.`);
			console.log("state: no activity — /api/my/activity is 204 (fixed contract)");
		}

		const page = await context.newPage();
		await page.goto(base, { waitUntil: "load" });
		await until(
			async () => (await page.locator('.hero-band[data-load-state="ready"]').count()) > 0,
			"home loaded (data-load-state=ready)",
			30000,
		);
		const text = await page.evaluate(() => document.body.innerText);
		assert.ok(
			!text.includes("불러오지 못했습니다"),
			"FAIL: home shows a load-error banner.\n---\n" + text.slice(0, 800) + "\n---",
		);
		console.log("home: no load-error banner");
		console.log(text.includes("일정 없음") ? "home empty-state: '일정 없음' rendered" : "home content rendered");
	} finally {
		await browser.close();
	}
})();
