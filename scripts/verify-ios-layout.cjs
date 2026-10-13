// Browser geometry check. Chromium/WebKit on a desktop is NOT an installed iPhone PWA.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const { chromium } = require(
	process.env.PLAYWRIGHT_MODULE || "playwright-core",
);
const measureOnly = process.argv.includes("--measure-only");
const base = process.env.LAYOUT_BASE_URL;
const evidence = [];

async function measure(page, label) {
	const data = await page.evaluate(() => {
		const doc = document.scrollingElement;
		const main = document.querySelector(".app-content");
		const shell = document.querySelector(".app-shell");
		const nav = document.querySelector(".bottom-nav");
		const viewport = window.visualViewport;
		const bottom =
			(viewport?.offsetTop || 0) + (viewport?.height || innerHeight);
		const rect = nav?.getBoundingClientRect();
		return {
			viewportHeight: viewport?.height || innerHeight,
			viewportWidth: viewport?.width || innerWidth,
			innerHeight,
			clientHeight: doc.clientHeight,
			scrollHeight: doc.scrollHeight,
			scrollY,
			overflow: doc.scrollHeight - bottom,
			mainPaddingBottom: getComputedStyle(main).paddingBottom,
			shellBottom: shell.getBoundingClientRect().bottom,
			shellWidth: shell.getBoundingClientRect().width,
			shellColor: getComputedStyle(shell).backgroundColor,
			contentLeft: main.getBoundingClientRect().left,
			contentWidth: main.getBoundingClientRect().width,
			lastContentBottom: main.lastElementChild?.getBoundingClientRect().bottom,
			topPadding: getComputedStyle(
				document.querySelector(".login-top, .hero-band"),
			).paddingTop,
			nav:
				nav && getComputedStyle(nav).display !== "none"
					? {
							position: getComputedStyle(nav).position,
							bottom: rect.bottom,
							gap: bottom - rect.bottom,
							inlineTop: nav.style.top,
						}
					: null,
		};
	});
	evidence.push({ label, ...data });
	console.log(JSON.stringify({ label, ...data }));
	return data;
}

(async () => {
	assert.ok(
		base,
		"Set LAYOUT_BASE_URL to the actual user-facing deployment; do not silently test a different server.",
	);
	assert.ok(
		process.env.LAYOUT_LOGIN_PASSWORD,
		"Set LAYOUT_LOGIN_PASSWORD for a development account.",
	);
	const browser = await chromium.launch({
		headless: true,
		channel: "msedge",
		timeout: 15000,
		...(process.env.BROWSER_EXECUTABLE
			? { channel: undefined, executablePath: process.env.BROWSER_EXECUTABLE }
			: {}),
	});
	try {
		const context = await browser.newContext({
			viewport: { width: 393, height: 852 },
			userAgent:
				"Mozilla/5.0 (iPhone; CPU iPhone OS 26_6_1 like Mac OS X) AppleWebKit/605.1.15 Version/26.6.1 Mobile/15E148 Safari/604.1",
		});
		// Exercise the old platform-detection branch, not emulate iOS WebKit itself.
		await context.addInitScript(() =>
			Object.defineProperty(navigator, "standalone", { get: () => true }),
		);
		const page = await context.newPage();
		page.setDefaultTimeout(10000);
		page.setDefaultNavigationTimeout(15000);
		const errors = [];
		page.on("pageerror", (error) => errors.push(error.message));

		for (const viewport of [
			{ width: 393, height: 852 },
			{ width: 393, height: 640 },
			{ width: 768, height: 852 },
			{ width: 1024, height: 900 },
		]) {
			await page.setViewportSize(viewport);
			await page.goto(`${base}/Account/Login`, { waitUntil: "load" });
			const data = await measure(
				page,
				`login-${viewport.width}x${viewport.height}`,
			);
			if (!measureOnly) {
				assert.equal(data.nav, null, "Auth screens have no bottom menu.");
				assert.ok(
					data.overflow <= 1,
					"A fitting login form must not create document overflow.",
				);
				assert.ok(
					data.shellBottom >= data.viewportHeight - 1,
					"Ivory background covers the viewport.",
				);
				assert.equal(data.shellColor, "rgb(254, 249, 242)");
				assert.ok(
					Math.abs(data.shellWidth - viewport.width) <= 1,
					"Ivory background covers the full width.",
				);
				assert.ok(
					data.contentWidth <= 560,
					"Auth content keeps its existing width limit.",
				);
				assert.equal(
					data.mainPaddingBottom,
					"16px",
					"No bottom-menu reservation on auth screens.",
				);
				if (viewport.width >= 1024)
					assert.ok(
						Math.abs(
							data.contentLeft + data.contentWidth / 2 - viewport.width / 2,
						) <= 1,
						"Auth has no empty side-nav column.",
					);
			}
		}
		// A short viewport stands in for reduced available space, not a real iOS keyboard.
		await page.setViewportSize({ width: 393, height: 480 });
		await page.goto(`${base}/Account/Login`, { waitUntil: "load" });
		await page.locator("#password").focus();
		await page
			.locator('.login-form button[type="submit"]')
			.scrollIntoViewIfNeeded();
		const short = await measure(page, "login-short-viewport-393x480");
		const submit = await page
			.locator('.login-form button[type="submit"]')
			.boundingBox();
		if (!measureOnly) {
			assert.ok(
				short.scrollY > 0 && short.scrollHeight > short.clientHeight,
				"Long content still scrolls.",
			);
			assert.ok(
				submit.y >= 0 && submit.y + submit.height <= short.viewportHeight + 1,
				"Submit stays reachable.",
			);
		}
		const html = await (await page.request.get(`${base}/Account/Login`)).text();
		const token = html.match(
			/name="__RequestVerificationToken" value="([^"]+)"/,
		);
		assert.ok(token, "Antiforgery token exists.");
		// Funnel terminates TLS; the existing server can emit an HTTP redirect.
		// Do not follow it: establish the cookie, then navigate to the configured base.
		const response = await page.request.post(`${base}/Account/Login`, {
			maxRedirects: 0,
			form: {
				"Input.StudentNumber": process.env.LAYOUT_LOGIN_USER || "admin",
				"Input.Password": process.env.LAYOUT_LOGIN_PASSWORD,
				__RequestVerificationToken: token[1],
				_handler: "login",
			},
		});
		assert.equal(
			response.status(),
			302,
			"Login must authenticate, not just return a 200 error form.",
		);
		console.log(
			"Login response: 302; redirect scheme=" +
				new URL(response.headers().location, base).protocol,
		);
		for (const viewport of [
			{ width: 393, height: 852 },
			{ width: 1024, height: 900 },
		]) {
			await page.setViewportSize(viewport);
			await page.goto(`${base}/Account/Logout`, { waitUntil: "load" });
			const logout = await measure(
				page,
				`logout-${viewport.width}x${viewport.height}`,
			);
			if (!measureOnly) {
				assert.equal(logout.nav, null);
				assert.ok(logout.overflow <= 1);
				assert.ok(logout.shellBottom >= logout.viewportHeight - 1);
				assert.equal(logout.shellWidth, viewport.width);
				assert.ok(logout.contentWidth <= 560);
				assert.equal(logout.mainPaddingBottom, "16px");
				assert.ok(
					Math.abs(
						logout.contentLeft + logout.contentWidth / 2 - viewport.width / 2,
					) <= 1,
					"Logout content is centered without a sidebar.",
				);
			}
		}
		await page.setViewportSize({ width: 393, height: 852 });
		await page.goto(base, { waitUntil: "domcontentloaded" });
		await page.waitForSelector(".bottom-nav");
		const launch = await measure(page, "home-initial-render");
		assert.ok(launch.nav, "Authenticated home renders the nav.");
		if (!measureOnly) {
			assert.equal(launch.nav.position, "fixed");
			assert.ok(Math.abs(launch.nav.gap - 10) <= 1);
		}
		await page.click('.bottom-nav a[href="/records"]');
		await page.waitForURL("**/records");
		await page.waitForSelector(".cal-nav");
		const tab = await measure(page, "records-after-tab");
		if (!measureOnly) assert.ok(Math.abs(tab.nav.gap - 10) <= 1);
		// Prove that interactive routing/hydration finished before testing a drag.
		const month = await page.locator(".cal-title").textContent();
		await page.locator(".more-btn").click();
		await page.waitForFunction(
			(old) => document.querySelector(".cal-title").textContent !== old,
			month,
		);
		// Keep the fixture outside Blazor-owned content, which can be rerendered.
		// This changes only the test document, never application records.
		await page.evaluate(() => {
			const filler = document.createElement("div");
			filler.style.height = "1800px";
			document.body.append(filler);
		});
		for (const y of [200, 650, 0]) {
			await page.evaluate((y) => window.scrollTo(0, y), y);
			const data = await measure(page, `records-scroll-${y}`);
			if (!measureOnly) {
				assert.equal(
					data.scrollY,
					y,
					"The document must actually scroll to the requested position.",
				);
				assert.equal(data.nav.position, "fixed");
				assert.ok(
					Math.abs(data.nav.gap - 10) <= 1,
					"No JS-following delay on scroll.",
				);
			}
			await page.evaluate(
				() =>
					new Promise((resolve) =>
						requestAnimationFrame(() => requestAnimationFrame(resolve)),
					),
			);
			const settled = await measure(page, `records-scroll-${y}-settled`);
			if (!measureOnly) {
				assert.equal(settled.scrollY, y);
				assert.equal(settled.nav.position, "fixed");
				assert.ok(
					Math.abs(settled.nav.gap - 10) <= 1,
					"Scroll handlers cannot switch positioning later.",
				);
			}
		}
		await page.setViewportSize({ width: 1024, height: 900 });
		await page.goto(base, { waitUntil: "load" });
		const desktop = await measure(page, "authenticated-home-1024x900");
		if (!measureOnly) {
			assert.equal(desktop.nav, null);
			assert.equal(await page.locator(".side-nav").isVisible(), true);
			assert.equal(
				desktop.contentLeft,
				310,
				"MainLayout keeps the original sidebar column.",
			);
			assert.equal(
				desktop.contentWidth,
				666,
				"MainLayout keeps its original content width.",
			);
		}
		assert.deepEqual(errors, [], "No browser script errors.");
		console.log(
			measureOnly
				? "MEASUREMENTS ONLY — not a passing regression check"
				: "PASS — desktop Chromium geometry; iPhone verification is still required",
		);
	} finally {
		await browser.close();
		if (process.env.LAYOUT_REPORT)
			fs.writeFileSync(
				process.env.LAYOUT_REPORT,
				JSON.stringify(
					{ engine: "desktop Chromium", measureOnly, evidence },
					null,
					2,
				) + "\n",
			);
	}
})().catch((error) => {
	// Playwright request traces can include cookies. Never print the full trace.
	console.error(error.name + ": " + String(error.message).split("\n")[0]);
	process.exitCode = 1;
});
