// Browser-only loading regression, called by verify-wasm-migration.cjs against its disposable DB.
const assert = require("node:assert/strict");
const path = require("node:path");
const fs = require("node:fs");
const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function parseUrl(value) {
	try {
		return new URL(value);
	} catch {
		throw new Error("Invalid URL in isolated loading verification.");
	}
}

async function until(predicate, label, timeout = 15000) {
	const start = Date.now();
	while (Date.now() - start < timeout) {
		if (await predicate()) return;
		await pause(25);
	}
	throw new Error(`Timed out: ${label}`);
}

function collector(config) {
	const startedAt = performance.now();
	const generation = (window.__loadingUxGeneration || 0) + 1;
	window.__loadingUxGeneration = generation;
	window.__loadingUxStop = false;
	window.__loadingUxFrames = [];
	const visible = (element) => {
		if (
			!element ||
			["hidden", "collapse"].includes(getComputedStyle(element).visibility)
		)
			return false;
		for (let p = element; p; p = p.parentElement) {
			const style = getComputedStyle(p);
			if (
				style.display === "none" ||
				style.contentVisibility === "hidden" ||
				Number(style.opacity) === 0
			)
				return false;
		}
		const box = element.getBoundingClientRect();
		return box.width > 0 && box.height > 0;
	};
	const rect = (selector) => {
		const element = document.querySelector(selector);
		if (!element) return null;
		const r = element.getBoundingClientRect();
		return {
			top: r.top,
			documentTop: r.top + scrollY,
			left: r.left,
			width: r.width,
			height: r.height,
		};
	};
	window.__loadingUxCapture = () => {
		const marker = document.querySelector(config.marker);
		return {
			ms: performance.now(),
			path: location.pathname,
			state: marker?.getAttribute("data-load-state") ?? null,
			busy: marker?.getAttribute("aria-busy") ?? null,
			title: rect(".hero-band .hb-title, .hero-band .hf-title, .page-title"),
			anchor: rect(config.anchor),
			scrollY,
			contentHeight: document.scrollingElement.scrollHeight,
			placeholders: [
				...document.querySelectorAll(".loading-placeholder"),
			].filter(visible).length,
			placeholderColors: [...document.querySelectorAll(".loading-placeholder")]
				.filter(visible)
				.map((e) => getComputedStyle(e).backgroundColor),
			statuses: document.querySelectorAll('[role="status"]').length,
			alerts: [...document.querySelectorAll('[role="alert"]')]
				.filter(visible)
				.map((e) => e.innerText),
			visiblePayload: config.payload
				.flatMap((selector) =>
					[...document.querySelectorAll(selector)]
						.flatMap((e) => [e, ...e.querySelectorAll("*")])
						.filter(
							(e) =>
								visible(e) &&
								(e.innerText?.trim() || e.matches("input,select,textarea")),
						)
						.map((e) => ({
							selector,
							text: (e.innerText || e.value || "").slice(0, 1200),
						})),
				)
				.slice(0, 12),
			zeroDate:
				document.body.innerText.includes("0년 0월") ||
				document.body.innerText.includes("가입일 0001"),
			blazorError: visible(document.querySelector("#blazor-error-ui")),
		};
	};
	const snap = () => {
		if (window.__loadingUxStop || generation !== window.__loadingUxGeneration)
			return;
		if (location.pathname === config.route)
			window.__loadingUxFrames.push(window.__loadingUxCapture());
		if (
			window.__loadingUxFrames.length < 2400 &&
			performance.now() - startedAt < 40000
		)
			requestAnimationFrame(snap);
	};
	requestAnimationFrame(snap);
}

module.exports = async function verifyLoadingUx({
	createPage,
	login,
	api,
	goto,
	base,
	adminPassword,
	memberId,
	memberStudentNumber,
	evidence,
	evidenceDir,
}) {
	const server = parseUrl(base);
	assert.equal(
		server.hostname,
		"127.0.0.1",
		"loading checks must use disposable loopback server",
	);
	assert.notEqual(server.port, "5122", "never target production");
	const dir = path.join(evidenceDir, "loading-ux");
	fs.mkdirSync(dir, { recursive: true });
	const get = (pattern) => ({ method: "GET", pattern });
	const post = (pattern) => ({ method: "POST", pattern });
	const specs = [
		{
			id: "home",
			route: "/",
			marker: ".home-tiles",
			anchor: ".home-tiles",
			payload: [
				".home-tiles .t-value:not(:has(.loading-placeholder)), .home-title-value:not(:has(.loading-placeholder))",
				".home-attendance > div:first-child",
				'.home-teams[data-retained="true"]',
			],
			queries: [get(/^\/api\/my\/(activity|teams|stats|fees(?:\/current)?)$/)],
			hold: /\/api\/my\/teams$/,
		},
		{
			id: "records",
			route: "/records",
			marker: ".records-card",
			anchor: ".records-card",
			payload: [
				".records-values",
				".hero-band .hf-title:not(:has(.loading-placeholder))",
			],
			queries: [get(/^\/api\/my\/(stats|records)$/)],
			hold: /\/api\/my\/records$/,
		},
		{
			id: "dashboard",
			route: "/admin",
			marker: ".dashboard-data",
			anchor: ".dashboard-data",
			payload: [".dashboard-data > .card:not([aria-hidden]):has(.card-row)"],
			queries: [
				post(/^\/api\/admin\/activity\/today$/),
				get(/^\/api\/admin\/teams$/),
			],
			hold: /\/api\/admin\/teams$/,
		},
		{
			id: "attendance",
			route: "/admin/attendance",
			marker: ".attendance-card",
			anchor: ".attendance-card",
			payload: [".attendance-card .admin-load-values"],
			queries: [get(/^\/api\/admin\/attendance$/)],
		},
		{
			id: "audit",
			route: "/admin/audit",
			marker: ".audit-card",
			anchor: ".audit-card",
			payload: [".audit-card .admin-load-values"],
			queries: [get(/^\/api\/admin\/audit$/)],
		},
		{
			id: "fees",
			route: "/admin/fees",
			marker: ".fees-results",
			anchor: ".fees-periods",
			payload: [".fees-results .admin-load-values"],
			queries: [
				get(/^\/api\/admin\/fees(?:\/\d+\/statuses)?$/),
				get(/^\/api\/my\/fees\/current$/),
			],
			hold: /\/api\/admin\/fees\/\d+\/statuses$/,
		},
		{
			id: "members",
			route: "/admin/members",
			marker: ".members-results",
			anchor: ".members-card",
			payload: [".members-results .admin-load-values"],
			queries: [post(/^\/api\/admin\/members\/search$/)],
		},
		{
			id: "member-detail",
			route: `/admin/members/${memberId}`,
			template: "/admin/members/{UserId}",
			marker: ".member-detail-tabs",
			anchor: ".member-detail-tabs",
			payload: [".member-edit", ".member-mappings", ".member-attendance"],
			queries: [get(new RegExp(`^/api/admin/members/${memberId}$`))],
		},
		{
			id: "semesters",
			route: "/admin/semesters",
			marker: ".semesters-list",
			anchor: ".semesters-card",
			payload: [".semesters-list .admin-load-values"],
			queries: [get(/^\/api\/admin\/semesters$/)],
		},
		{
			id: "settings",
			route: "/admin/settings",
			marker: ".settings-card",
			anchor: ".settings-card",
			payload: [".settings-sheet-value"],
			queries: [get(/^\/api\/admin\/sheets$/)],
		},
		{
			id: "teams",
			route: "/admin/teams",
			marker: ".teams-data",
			anchor: ".teams-data",
			payload: [
				".teams-data > .teams-setup",
				".teams-data > .teams-grid",
				'.teams-data > .card:not([aria-hidden="true"]):not([role="alert"])',
			],
			queries: [
				get(/^\/api\/admin\/(teams(?:\/participants)?|activity\/today)$/),
			],
			hold: /\/api\/admin\/activity\/today$/,
		},
	];
	const report = (evidence.loadingUx = {
		environment: "disposable Verification Docker / desktop Chromium engine",
		realIPhone: false,
		viewports: [
			{ width: 393, height: 852 },
			{ width: 1280, height: 900 },
		],
		delaysMs: [0, 600, 2000],
		inventory: [
			...specs.map((s) => ({ route: s.template || s.route, applicable: true })),
			...[
				"/Account/Login",
				"/Account/Logout",
				"/Account/AccessDenied",
				"/Error",
				"/not-found",
			].map((route) => ({
				route,
				applicable: false,
				reason:
					"No initial page-content query; submit/mutation behavior excluded.",
			})),
		],
		actions: Object.fromEntries(specs.map((s) => [s.id, ["error-retry"]])),
		conditions: [],
		partialGroups: [],
		races: [],
		localControls: [],
		complete: false,
	});
	const cfg = (s) => ({
		route: s.route,
		marker: s.marker,
		anchor: s.anchor,
		payload: s.payload,
	});
	const ready = (page, s, state = "ready") =>
		page.waitForFunction(
			({ marker, state }) =>
				document.querySelector(marker)?.getAttribute("data-load-state") ===
				state,
			{ marker: s.marker, state },
			{ timeout: 25000 },
		);
	const capture = (page) => page.evaluate(() => window.__loadingUxCapture());
	const frames = async (page) =>
		page.evaluate(() => {
			window.__loadingUxStop = true;
			return window.__loadingUxFrames || [];
		});
	let serial = 0;

	async function intercept(page, s, options = {}) {
		const records = [];
		let failed = false;
		let held = false;
		let closing = false;
		const gates = new Map();
		const release = (key) => {
			for (const [name, resolve] of gates)
				if (key === undefined || key === name) {
					resolve();
					gates.delete(name);
				}
		};
		const hold = (key, record) =>
			new Promise((resolve, reject) => {
				if (closing) return resolve();
				record.held = true;
				record.gate = key;
				const timer = setTimeout(() => {
					gates.delete(key);
					reject(new Error(`Response gate deadline: ${key}`));
				}, 15000);
				gates.set(key, () => {
					clearTimeout(timer);
					resolve();
				});
			});
		const handler = async (route) => {
			const request = route.request();
			const url = parseUrl(request.url());
			if (
				!s.queries.some(
					(q) => q.method === request.method() && q.pattern.test(url.pathname),
				)
			)
				return route.continue();
			const record = {
				sequence: records.length + 1,
				path: url.pathname,
				search: url.search,
				method: request.method(),
				startMs: performance.now(),
				injectedDelayMs: options.delayMs || 0,
			};
			if (request.postData()) {
				try {
					record.body = JSON.parse(request.postData());
				} catch {
					throw new Error(`Invalid JSON query request: ${url.pathname}`);
				}
			}
			records.push(record);
			if (options.delayMs) await pause(options.delayMs);
			if (
				options.fault &&
				!failed &&
				(!options.faultMatch || options.faultMatch.test(url.pathname))
			) {
				failed = true;
				record.injected = options.fault;
				record.data =
					options.fault === "500"
						? { message: "검증용 일시적 조회 실패" }
						: null;
				record.status =
					options.fault === "204" ? 204 : options.fault === "null" ? 200 : 500;
				await route.fulfill({
					status: record.status,
					...(record.status === 204
						? {}
						: {
								contentType: "application/json",
								body:
									options.fault === "null"
										? "null"
										: JSON.stringify({ message: "검증용 일시적 조회 실패" }),
							}),
				});
			} else {
				const response = await route.fetch();
				record.status = response.status();
				const text = await response.text();
				try {
					record.data = text ? JSON.parse(text) : null;
				} catch {
					record.nonJson = true;
				}
				const raceGate = options.gateFor?.(record);
				if (raceGate) await hold(raceGate, record);
				else if (options.hold && !held && options.hold.test(url.pathname)) {
					held = true;
					await hold("partial", record);
				}
				if (options.staleFailure?.(record)) {
					record.status = 500;
					record.injected = "stale-500";
					await route.fulfill({
						status: 500,
						contentType: "application/json",
						body: JSON.stringify({ message: "검증용 오래된 조회 실패" }),
					});
				} else if (options.mapResponse) {
					record.data = options.mapResponse(record, record.data);
					await route.fulfill({ response, json: record.data });
				} else await route.fulfill({ response });
			}
			record.fulfilledMs = performance.now();
		};
		await page.route("**/api/**", handler);
		return {
			records,
			release,
			dispose: async () => {
				closing = true;
				release();
				await page.unroute("**/api/**", handler);
			},
		};
	}

	function validateTrace(trace, label, delayMs) {
		const pending = trace.filter((f) => f.state === "loading");
		if (delayMs > 0)
			assert.ok(pending.length > 0, `${label}: actual pending frames`);
		for (const f of trace.filter((f) => f.state)) {
			assert.equal(f.zeroDate, false, `${label}: no fake date`);
			assert.equal(f.blazorError, false, `${label}: no Blazor error overlay`);
			if (f.state === "loading") {
				assert.equal(
					f.visiblePayload.length,
					0,
					`${label}: no prior/default/partial actual payload: ${JSON.stringify(f.visiblePayload)}`,
				);
				assert.ok(f.placeholders > 0, `${label}: visible skeleton`);
				assert.ok(
					f.placeholderColors.some(
						(c) => c !== "transparent" && c !== "rgba(0, 0, 0, 0)",
					),
					`${label}: skeleton CSS delivered`,
				);
				assert.ok(f.statuses > 0, `${label}: accessible status`);
			}
		}
		return pending;
	}

	function geometry(before, after, label) {
		const result = {
			before,
			after,
			resultHeightChangePx: after.contentHeight - before.contentHeight,
			scrollChangePx: after.scrollY - before.scrollY,
		};
		for (const key of ["title", "anchor"]) {
			assert.ok(before[key] && after[key], `${label}: ${key} measured`);
			const movement = Math.abs(
				after[key].documentTop - before[key].documentTop,
			);
			result[`${key}DocumentMovementPx`] = movement;
			result[`${key}ViewportMovementPx`] = Math.abs(
				after[key].top - before[key].top,
			);
			assert.ok(movement <= 4, `${label}: ${key} layout shifted ${movement}px`);
			if (result.scrollChangePx === 0)
				assert.ok(
					result[`${key}ViewportMovementPx`] <= 4,
					`${label}: ${key} viewport shifted`,
				);
		}
		return result;
	}

	async function screenshot(page, label, phase) {
		const name = `${String(++serial).padStart(4, "0")}-${label.replace(/[^a-z0-9-]/gi, "-")}-${phase}.png`;
		await page.screenshot({ path: path.join(dir, name), fullPage: false });
		return `artifacts/wasm-browser/loading-ux/${name}`;
	}

	function assertRequiredQueries(s, records) {
		const required = {
			home: [
				"/api/my/activity",
				"/api/my/teams",
				"/api/my/stats",
				"/api/my/fees/current",
			],
			records: ["/api/my/stats", "/api/my/records"],
			dashboard: ["/api/admin/activity/today", "/api/admin/teams"],
			attendance: ["/api/admin/attendance"],
			audit: ["/api/admin/audit"],
			fees: ["/api/admin/fees", "/api/my/fees/current"],
			members: ["/api/admin/members/search"],
			"member-detail": [`/api/admin/members/${memberId}`],
			semesters: ["/api/admin/semesters"],
			settings: ["/api/admin/sheets"],
			teams: [
				"/api/admin/teams/participants",
				"/api/admin/teams",
				"/api/admin/activity/today",
			],
		}[s.id];
		for (const resource of required)
			assert.ok(
				records.some((r) => r.path === resource),
				`${s.id}: required query ${resource}`,
			);
		if (
			s.id === "home" &&
			records.filter((r) => r.path === "/api/my/fees/current").at(-1)?.data
		)
			assert.ok(
				records.some((r) => r.path === "/api/my/fees"),
				"home: conditional current-fee statuses",
			);
		if (s.id === "fees")
			assert.ok(
				records.some((r) => /\/statuses$/.test(r.path)),
				"fees: selected metadata and statuses both fetched",
			);
	}

	async function newPage(viewport) {
		const page = await createPage(viewport);
		await login(page, "admin", adminPassword);
		await page.locator('.home-tiles[data-load-state="ready"]').waitFor();
		return page;
	}

	async function prepareMenu(page, s) {
		const source =
			s.id === "member-detail"
				? "/admin/members"
				: s.route === "/"
					? "/records"
					: s.route.startsWith("/admin/")
						? "/admin"
						: "/";
		await goto(page, source);
		if (s.id === "member-detail") {
			await page
				.getByPlaceholder("이름 / 학번 검색", { exact: true })
				.fill(memberStudentNumber);
			await ready(
				page,
				specs.find((x) => x.id === "members"),
			);
			await page
				.locator(".member-row[tabindex]")
				.filter({ hasText: memberStudentNumber })
				.waitFor();
		}
	}

	async function clickMenu(page, s) {
		if (s.id === "member-detail")
			await page
				.locator(".member-row[tabindex]")
				.filter({ hasText: memberStudentNumber })
				.click();
		else await page.locator(`a[href="${s.route}"]:visible`).first().click();
		await page.waitForURL(base + s.route);
	}

	async function entry(s, viewport, delayMs, options = {}) {
		const page = await newPage(viewport);
		const label = `${s.id}-${viewport.width}-${delayMs}-${options.caseName || options.fault || (options.hold ? "partial" : options.mode || options.motion || "initial")}`;
		const row = {
			id: label,
			route: s.template || s.route,
			viewport,
			delayMs,
			mode: options.mode || "direct",
			fault: options.fault || null,
			faultTarget: options.faultMatch ? String(options.faultMatch) : null,
			motion: options.motion || "reduce",
			assertionsPassed: false,
		};
		report.conditions.push(row);
		console.log(`LOADING ${label}`);
		let route;
		try {
			if (options.motion)
				await page.emulateMedia({ reducedMotion: options.motion });
			if (options.mode === "menu") await prepareMenu(page, s);
			if (options.mode === "reload") await goto(page, s.route);
			await page.addInitScript(collector, cfg(s));
			if (options.mode === "menu") await page.evaluate(collector, cfg(s));
			route = await intercept(page, s, { delayMs, ...options });
			if (options.mode === "menu") await clickMenu(page, s);
			else if (options.mode === "reload")
				await page.reload({ waitUntil: "domcontentloaded" });
			else await page.goto(base + s.route, { waitUntil: "domcontentloaded" });
			await page.locator(s.marker).waitFor({ state: "attached" });
			if (options.hold) {
				await until(
					() =>
						route.records.some((r) => r.held) &&
						route.records.some((r) => !r.held && r.fulfilledMs),
					`${label}: partial responses`,
				);
				const partial = await capture(page);
				assert.equal(
					partial.state,
					"loading",
					`${label}: group remains loading`,
				);
				assert.equal(
					partial.visiblePayload.length,
					0,
					`${label}: no partial reveal`,
				);
				row.partial = partial;
				row.duringScreenshot = await screenshot(page, label, "partial");
				report.partialGroups.push({
					route: s.route,
					heldPattern: String(options.hold),
					condition: label,
				});
				route.release();
			}
			if (options.fault && options.expectError !== false) {
				await ready(page, s, "error");
				const error = await capture(page);
				assert.ok(error.alerts.length, `${label}: explicit error`);
				row.error = error;
				row.errorScreenshot = await screenshot(page, label, "error");
				await page
					.getByRole("button", { name: "다시 불러오기", exact: true })
					.click();
			}
			if (delayMs > 0 && !options.hold) {
				await until(
					async () =>
						(await capture(page)).state === "loading" ||
						(await capture(page)).state === "ready",
					`${label}: rendered state`,
				);
				row.duringScreenshot = await screenshot(page, label, "during");
			}
			await ready(page, s);
			await page.evaluate(
				() =>
					new Promise((resolve) =>
						requestAnimationFrame(() => requestAnimationFrame(resolve)),
					),
			);
			const after = await capture(page);
			assert.equal(after.alerts.length, 0, `${label}: ready without alert`);
			if (s.id === "member-detail")
				assert.equal(
					await page.locator(".member-edit input").first().inputValue(),
					memberStudentNumber,
					`${label}: correct member form`,
				);
			row.readyScreenshot = await screenshot(page, label, "ready");
			row.frames = await frames(page);
			const pending = validateTrace(row.frames, label, delayMs);
			row.geometry = pending.length
				? geometry(pending[0], after, label)
				: {
						pendingPaintObserved: false,
						ready: after,
						reason:
							"0ms can finish without a pending paint; no application delay inserted.",
					};
			row.requests = route.records;
			assert.ok(route.records.length, `${label}: real query observed`);
			assertRequiredQueries(s, route.records);
			row.assertionsPassed = true;
		} catch (error) {
			row.failure = error.message;
			row.requests = route?.records || [];
			row.frames ||= await frames(page).catch(() => []);
			row.failureScreenshot = await screenshot(page, label, "failure").catch(
				() => null,
			);
			throw error;
		} finally {
			if (route) await route.dispose();
			await page.context().close();
		}
	}

	async function query(page, s, label, delayMs, perform, options = {}) {
		const row = {
			id: label,
			route: s.template || s.route,
			viewport: page.viewportSize(),
			delayMs,
			mode: "read-requery",
			assertionsPassed: false,
		};
		report.conditions.push(row);
		console.log(`REQUERY ${label}`);
		await page.evaluate(collector, cfg(s));
		const route = await intercept(page, s, { delayMs, ...options });
		try {
			await perform();
			await until(
				() => route.records.length > 0,
				`${label}: request dispatched`,
			);
			if (options.fault) {
				await ready(page, s, "error");
				row.error = await capture(page);
				assert.ok(row.error.alerts.length, `${label}: explicit query error`);
				row.errorScreenshot = await screenshot(page, label, "error");
				await page
					.getByRole("button", { name: "다시 불러오기", exact: true })
					.click();
			}
			if (delayMs > 0)
				row.duringScreenshot = await screenshot(page, label, "during");
			await until(
				() =>
					route.records.every((r) => r.fulfilledMs) &&
					(s.id !== "fees" ||
						route.records.some((r) => /\/statuses$/.test(r.path))),
				`${label}: query responses delivered`,
			);
			await ready(page, s);
			await page.evaluate(
				() =>
					new Promise((resolve) =>
						requestAnimationFrame(() => requestAnimationFrame(resolve)),
					),
			);
			const after = await capture(page);
			row.frames = await frames(page);
			const pending = validateTrace(row.frames, label, delayMs);
			assert.equal(after.alerts.length, 0, `${label}: no query error`);
			row.geometry = pending.length
				? geometry(pending[0], after, label)
				: { pendingPaintObserved: false, ready: after };
			row.requests = route.records;
			row.readyScreenshot = await screenshot(page, label, "ready");
			if (s.id === "records") {
				const latest = route.records
					.filter((r) => r.path === "/api/my/records")
					.at(-1)?.data;
				assert.ok(latest, `${label}: month DTO`);
				assert.equal(
					await page.locator(".cal-title").innerText(),
					`${latest.year}년 ${latest.month}월`,
				);
				assert.equal(
					(await page.locator(".hero-band .hf-title").innerText()).trim(),
					`${latest.month}월 ${latest.monthCount}회 출석`,
				);
			}
			if (s.id === "members") {
				const latest = route.records.at(-1).data;
				const text = await page
					.locator(".members-results .admin-load-values")
					.innerText();
				for (const member of latest.items)
					assert.ok(
						text.includes(member.studentNumber),
						`${label}: latest member rows`,
					);
			}
			if (s.id === "fees") {
				const status = route.records.find((r) => /\/statuses$/.test(r.path));
				assert.ok(status, `${label}: selected period statuses fetched`);
				const id = Number(status.path.split("/")[4]);
				const period = route.records
					.find((r) => r.path === "/api/admin/fees")
					?.data.find((p) => p.id === id);
				assert.ok(period, `${label}: fresh selected metadata`);
				assert.ok(
					(
						await page.locator(".fees-results .card-title").innerText()
					).includes(period.name),
				);
			}
			row.assertionsPassed = true;
		} catch (error) {
			row.failure = error.message;
			row.requests = route.records;
			row.frames ||= await frames(page).catch(() => []);
			row.failureScreenshot = await screenshot(page, label, "failure").catch(
				() => null,
			);
			throw error;
		} finally {
			await route.dispose();
		}
	}

	const paint = (page) =>
		page.evaluate(
			() =>
				new Promise((resolve) =>
					requestAnimationFrame(() => requestAnimationFrame(resolve)),
				),
		);

	async function readRaces(viewport) {
		for (const id of ["members", "records", "fees"]) {
			for (const variant of [
				"old-success-before",
				"old-failure-before",
				"old-success-after",
				"old-failure-after",
				"latest-failure",
			]) {
				const s = specs.find((x) => x.id === id);
				const page = await newPage(viewport);
				const row = {
					id: `race-${id}-${variant}`,
					route: s.route,
					variant,
					viewport,
					assertionsPassed: false,
				};
				report.races.push(row);
				console.log(`RACE ${row.id}`);
				let route;
				try {
					await goto(page, s.route);
					let older, newer, otherPeriod;
					if (id === "members") {
						const input = page.getByPlaceholder("이름 / 학번 검색", {
							exact: true,
						});
						older = () => input.fill(memberStudentNumber);
						newer = () => input.fill("");
					} else if (id === "records") {
						older = () =>
							page
								.getByRole("button", { name: "이전 달", exact: true })
								.click();
						newer = () =>
							page
								.getByRole("button", { name: "다음 달", exact: true })
								.click();
					} else {
						const active = (
							await page.locator(".fees-periods button.active").innerText()
						).trim();
						const labels = await page
							.locator(".fees-periods > .filter-group button")
							.allTextContents();
						otherPeriod = labels.map((x) => x.trim()).find((x) => x !== active);
						assert.ok(otherPeriod, "distinct fee periods for ordering");
						older = () =>
							page
								.locator(".fees-periods")
								.getByRole("button", { name: otherPeriod, exact: true })
								.click();
						newer = () =>
							page
								.locator(".fees-periods")
								.getByRole("button", { name: active, exact: true })
								.click();
						row.expectedPeriod = active;
					}
					let count = 0;
					route = await intercept(page, s, {
						gateFor: (r) => {
							const primary =
								id === "members"
									? r.path.endsWith("/search")
									: id === "records"
										? r.path === "/api/my/records"
										: r.path === "/api/admin/fees";
							if (!primary) return null;
							const n = ++count;
							return n === 1 ? "older" : n === 2 ? "newer" : null;
						},
						staleFailure: (r) =>
							(r.gate === "older" && variant.includes("old-failure")) ||
							(r.gate === "newer" && variant === "latest-failure"),
						...(id === "members"
							? {
									mapResponse: (r, data) => ({
										...data,
										items: data.items.map((m, n) =>
											n === 0
												? {
														...m,
														name: r.gate === "older" ? "이전응답" : "최신응답",
													}
												: m,
										),
									}),
								}
							: {}),
					});
					await page.evaluate(collector, cfg(s));
					await older();
					await until(
						() => route.records.some((r) => r.gate === "older"),
						`${row.id}: older held`,
					);
					await newer();
					await until(
						() => route.records.some((r) => r.gate === "newer"),
						`${row.id}: newer held`,
					);
					const settleOlder = async () => {
						await until(
							() => route.records.find((r) => r.gate === "older")?.fulfilledMs,
							`${row.id}: old delivered`,
						);
						if (id === "fees" && !variant.includes("old-failure")) {
							const oldId = route.records
								.find((r) => r.gate === "older")
								.data.find((p) => p.name === otherPeriod).id;
							await until(
								() =>
									route.records.some(
										(r) =>
											r.path === `/api/admin/fees/${oldId}/statuses` &&
											r.fulfilledMs,
									),
								`${row.id}: stale dependent request finished`,
							);
						}
						await paint(page);
					};
					if (variant.endsWith("before")) {
						route.release("older");
						await settleOlder();
						row.whileLatestPending = await capture(page);
						assert.equal(
							row.whileLatestPending.state,
							"loading",
							`${row.id}: old finally cannot clear newer loading`,
						);
						assert.equal(
							row.whileLatestPending.alerts.length,
							0,
							`${row.id}: old error is ignored`,
						);
						route.release("newer");
					} else {
						route.release("newer");
						await ready(
							page,
							s,
							variant === "latest-failure" ? "error" : "ready",
						);
						route.release("older");
						await settleOlder();
					}
					if (variant === "latest-failure") {
						const error = await capture(page);
						assert.equal(
							error.state,
							"error",
							`${row.id}: stale success cannot clear latest error`,
						);
						assert.ok(error.alerts.length);
						row.latestError = error;
						await page
							.getByRole("button", { name: "다시 불러오기", exact: true })
							.click();
					}
					await ready(page, s);
					await paint(page);
					row.ready = await capture(page);
					assert.equal(row.ready.alerts.length, 0, row.id);
					if (id === "members") {
						assert.ok(
							(await page.locator(".members-results").innerText()).includes(
								"최신응답",
							),
							row.id,
						);
						assert.ok(
							!(await page.locator(".members-results").innerText()).includes(
								"이전응답",
							),
							row.id,
						);
					} else if (id === "records") {
						const dto = route.records
							.filter((r) => r.path === "/api/my/records")
							.at(-1).data;
						assert.equal(
							(await page.locator(".hf-title").innerText()).trim(),
							`${dto.month}월 ${dto.monthCount}회 출석`,
							row.id,
						);
					} else
						assert.ok(
							(
								await page.locator(".fees-results .card-title").innerText()
							).includes(row.expectedPeriod),
							row.id,
						);
					row.frames = await frames(page);
					validateTrace(row.frames, row.id, 1);
					row.requests = route.records;
					row.screenshot = await screenshot(page, row.id, "ready");
					row.assertionsPassed = true;
				} catch (error) {
					row.failure = error.message;
					row.requests = route?.records || [];
					throw error;
				} finally {
					if (route) await route.dispose();
					await page.context().close();
				}
			}
		}
	}

	async function routeRaces(viewport, otherMemberId, oldFailure) {
		const s = specs.find((x) => x.id === "member-detail");
		const page = await newPage(viewport);
		const row = {
			id: `member-route-A-B-A-${oldFailure ? "failure" : "success"}`,
			viewport,
			assertionsPassed: false,
		};
		report.races.push(row);
		let route;
		try {
			await goto(page, "/admin/members");
			let count = 0;
			route = await intercept(
				page,
				{ ...s, queries: [get(/^\/api\/admin\/members\/[^/]+$/)] },
				{
					gateFor: (r) =>
						r.path === `/api/admin/members/${memberId}`
							? ++count === 1
								? "older-A"
								: count === 2
									? "newer-A"
									: null
							: null,
					mapResponse: (r, data) =>
						r.path === `/api/admin/members/${memberId}`
							? {
									...data,
									name: r.gate === "older-A" ? "이전응답" : "최신응답",
								}
							: data,
					staleFailure: (r) => oldFailure && r.gate === "older-A",
				},
			);
			await page.evaluate(collector, cfg(s));
			await page.evaluate((url) => window.Blazor.navigateTo(url), s.route);
			await until(
				() => route.records.some((r) => r.gate === "older-A"),
				"old route A held",
			);
			await page.evaluate(
				(url) => window.Blazor.navigateTo(url),
				`/admin/members/${otherMemberId}`,
			);
			await until(
				() =>
					route.records.some(
						(r) => r.path.endsWith(otherMemberId) && r.fulfilledMs,
					),
				"route B response",
			);
			await ready(page, s);
			await page.evaluate((url) => window.Blazor.navigateTo(url), s.route);
			await until(
				() => route.records.some((r) => r.gate === "newer-A"),
				"new route A held",
			);
			route.release("newer-A");
			await ready(page, s);
			route.release("older-A");
			await until(
				() => route.records.find((r) => r.gate === "older-A").fulfilledMs,
				"old A delivered",
			);
			await paint(page);
			assert.equal(
				await page.locator(".member-edit input").nth(1).inputValue(),
				"최신응답",
				"A-B-A draft uses new generation",
			);
			assert.ok(
				(await page.locator(".hb-title").innerText()).includes("최신응답"),
			);
			row.frames = await frames(page);
			validateTrace(row.frames, row.id, 1);
			row.ready = await capture(page);
			row.requests = route.records;
			row.screenshot = await screenshot(page, row.id, "ready");
			row.assertionsPassed = true;
		} catch (error) {
			row.failure = error.message;
			row.requests = route?.records || [];
			throw error;
		} finally {
			if (route) await route.dispose();
			await page.context().close();
		}
	}

	async function tabRouteRaces(viewport, otherMemberId) {
		const detail = specs.find((s) => s.id === "member-detail");
		for (const kind of ["mappings", "attendance"]) {
			const page = await newPage(viewport);
			const tab = kind === "mappings" ? "시트 연결" : "출석 기록";
			const marker =
				kind === "mappings" ? ".member-mappings" : ".member-attendance";
			const s = {
				...detail,
				marker,
				anchor: marker,
				payload:
					kind === "mappings"
						? [".member-mappings .field:has(select)"]
						: [".member-attendance .admin-load-values"],
			};
			const row = {
				id: `tab-route-A-B-A-${kind}`,
				viewport,
				assertionsPassed: false,
			};
			report.races.push(row);
			let route;
			try {
				await goto(page, detail.route);
				let n = 0;
				route = await intercept(
					page,
					{
						...s,
						queries: [
							get(/^\/api\/admin\/members\/[^/]+$/),
							get(new RegExp(`^/api/admin/members/${memberId}/${kind}$`)),
						],
					},
					{
						gateFor: (r) =>
							r.path.endsWith(`/${kind}`)
								? ++n === 1
									? "older-tab"
									: n === 2
										? "newer-tab"
										: null
								: null,
						...(kind === "attendance"
							? {
									mapResponse: (r, data) =>
										r.gate === "older-tab"
											? {
													...data,
													monthCount: 0,
													semesterCount: 0,
													totalCount: 0,
													records: [],
													addableActivities: data.addableActivities.map(
														(a) => ({ ...a, alreadyAttended: false }),
													),
												}
											: data,
								}
							: {}),
					},
				);
				await page.evaluate(collector, cfg(s));
				await page.getByText(tab, { exact: true }).click();
				await until(
					() => route.records.some((r) => r.gate === "older-tab"),
					`${row.id}: old tab held`,
				);
				await page.evaluate(
					(url) => window.Blazor.navigateTo(url),
					`/admin/members/${otherMemberId}`,
				);
				await until(
					() =>
						route.records.some(
							(r) => r.path.endsWith(otherMemberId) && r.fulfilledMs,
						),
					`${row.id}: B loaded`,
				);
				await ready(page, detail);
				await page.evaluate(
					(url) => window.Blazor.navigateTo(url),
					detail.route,
				);
				await until(
					() =>
						route.records.some(
							(r) =>
								r.path === `/api/admin/members/${memberId}` && r.fulfilledMs,
						),
					`${row.id}: new A loaded`,
				);
				await ready(page, detail);
				if (kind === "mappings") {
					route.release("older-tab");
					await until(
						() => route.records.find((r) => r.gate === "older-tab").fulfilledMs,
						`${row.id}: stale cache delivered`,
					);
					await paint(page);
				}
				await page.getByText(tab, { exact: true }).click();
				await until(
					() => route.records.some((r) => r.gate === "newer-tab"),
					`${row.id}: fresh route cannot reuse stale tab cache`,
				);
				route.release("newer-tab");
				await ready(page, s);
				route.release("older-tab");
				await until(
					() => route.records.find((r) => r.gate === "older-tab").fulfilledMs,
					`${row.id}: old tab finished`,
				);
				await paint(page);
				if (kind === "attendance") {
					const dto = route.records.find((r) => r.gate === "newer-tab").data;
					assert.ok(
						dto.totalCount > 0,
						"nonempty attendance distinguishes stale empty response",
					);
					assert.ok(
						(
							await page
								.locator(".member-attendance .admin-load-values > .member-meta")
								.innerText()
						).includes(`총 ${dto.totalCount}회`),
					);
				}
				row.frames = await frames(page);
				validateTrace(row.frames, row.id, 1);
				row.requests = route.records;
				row.ready = await capture(page);
				row.screenshot = await screenshot(page, row.id, "ready");
				row.assertionsPassed = true;
			} catch (error) {
				row.failure = error.message;
				row.requests = route?.records || [];
				throw error;
			} finally {
				if (route) await route.dispose();
				await page.context().close();
			}
		}
	}

	async function postWriteRouteRace(viewport, otherMemberId) {
		const s = specs.find((x) => x.id === "member-detail");
		const page = await newPage(viewport);
		const row = {
			id: "post-write-member-read-route",
			viewport,
			assertionsPassed: false,
		};
		report.races.push(row);
		let route;
		let original;
		try {
			await goto(page, s.route);
			original = await api(page, "GET", `/api/admin/members/${memberId}`);
			route = await intercept(
				page,
				{ ...s, queries: [get(/^\/api\/admin\/members\/[^/]+$/)] },
				{
					gateFor: (r) =>
						r.path === `/api/admin/members/${memberId}` ? "post-write-A" : null,
				},
			);
			await page.locator(".member-edit input").nth(2).fill("경합 검증학과");
			await page
				.locator(".member-edit")
				.getByRole("button", { name: "저장", exact: true })
				.click();
			await until(
				() => route.records.some((r) => r.gate === "post-write-A"),
				"post-write read held",
			);
			await page.evaluate(
				(url) => window.Blazor.navigateTo(url),
				`/admin/members/${otherMemberId}`,
			);
			await until(
				() =>
					route.records.some(
						(r) => r.path.endsWith(otherMemberId) && r.fulfilledMs,
					),
				"new member response delivered",
			);
			await ready(page, s);
			route.release("post-write-A");
			await until(
				() => route.records.find((r) => r.gate === "post-write-A").fulfilledMs,
				"old post-write read delivered",
			);
			await paint(page);
			const expected = route.records.find((r) =>
				r.path.endsWith(otherMemberId),
			).data;
			assert.equal(
				await page.locator(".member-edit input").first().inputValue(),
				expected.studentNumber,
			);
			assert.equal(
				await page.locator(".member-edit input").nth(1).inputValue(),
				expected.name,
			);
			assert.ok(
				(await page.locator(".hb-title").innerText()).includes(expected.name),
				"late write-followup read cannot replace B with A",
			);
			row.requests = route.records;
			row.screenshot = await screenshot(page, row.id, "ready");
			row.assertionsPassed = true;
		} catch (error) {
			row.failure = error.message;
			row.requests = route?.records || [];
			throw error;
		} finally {
			if (route) await route.dispose();
			if (original)
				await api(
					page,
					"PUT",
					`/api/admin/members/${memberId}`,
					{
						name: original.name,
						department: original.department,
						studentNumber: original.studentNumber,
						role: original.role,
						color: original.color,
					},
					200,
				);
			await page.context().close();
		}
	}

	async function localChecks(viewport) {
		const page = await newPage(viewport);
		try {
			for (const id of [
				"attendance",
				"fees",
				"semesters",
				"teams",
				"member-detail",
			]) {
				const s = specs.find((x) => x.id === id);
				await goto(page, s.route);
				if (id === "member-detail") {
					await page.locator(".member-edit input").nth(1).fill("유지할 초안");
					await page.getByText("시트 연결", { exact: true }).click();
					await ready(page, { ...s, marker: ".member-mappings" });
				}
				const route = await intercept(page, {
					...s,
					queries:
						id === "member-detail"
							? [get(new RegExp(`^/api/admin/members/${memberId}/mappings$`))]
							: s.queries,
				});
				const row = { route: s.route, viewport, assertionsPassed: false };
				report.localControls.push(row);
				try {
					if (id === "attendance") {
						await page
							.getByPlaceholder("이름 / 학번 검색", { exact: true })
							.fill("김");
						await page
							.getByPlaceholder("이름 / 학번 검색", { exact: true })
							.press("Tab");
						row.controls = ["local search"];
					} else if (id === "fees") {
						for (const name of ["납부", "미납", "전체"])
							await page
								.locator(".fees-results")
								.getByRole("button", { name, exact: true })
								.click();
						await page
							.getByPlaceholder("이름 / 학번 검색", { exact: true })
							.fill("김");
						await page
							.getByPlaceholder("이름 / 학번 검색", { exact: true })
							.press("Tab");
						row.controls = ["paid/unpaid/all", "local search"];
					} else if (id === "semesters") {
						await page
							.locator(".semesters-list")
							.getByRole("button", { name: "수정", exact: true })
							.first()
							.click();
						row.controls = ["local edit selection"];
					} else if (id === "teams") {
						if (
							await page
								.getByRole("button", { name: "다시 뽑기", exact: true })
								.count()
						) {
							await page
								.getByRole("button", { name: "다시 뽑기", exact: true })
								.click();
							await page
								.locator(".confirm-box")
								.getByRole("button", { name: "다시 뽑기", exact: true })
								.click();
						}
						await page
							.getByRole("button", { name: "2팀", exact: true })
							.click();
						await page
							.locator('.teams-setup input[type="checkbox"]')
							.first()
							.click();
						row.controls = [
							"back to selection",
							"team count",
							"participant selection",
						];
					} else {
						await page.getByText("부원 수정", { exact: true }).click();
						assert.equal(
							await page.locator(".member-edit input").nth(1).inputValue(),
							"유지할 초안",
						);
						await page.getByText("시트 연결", { exact: true }).click();
						await ready(page, { ...s, marker: ".member-mappings" });
						row.controls = [
							"existing successful mapping reuse",
							"unrelated edit draft retained",
						];
					}
					await paint(page);
					assert.equal(
						route.records.length,
						0,
						`${id}: local controls add no requests`,
					);
					row.requests = route.records;
					row.assertionsPassed = true;
				} finally {
					await route.dispose();
				}
			}
			const s = specs.find((x) => x.id === "settings");
			const route = await intercept(page, s, { delayMs: 2000 });
			const row = {
				route: s.route,
				viewport,
				controls: ["independent key/new-sheet draft during load"],
				assertionsPassed: false,
			};
			report.localControls.push(row);
			try {
				await page.goto(base + s.route, { waitUntil: "domcontentloaded" });
				await page
					.locator('.settings-card[data-load-state="loading"]')
					.waitFor();
				await page.locator("textarea").fill("NOT-A-CREDENTIAL-DRAFT");
				await page
					.getByPlaceholder("출석부", { exact: true })
					.fill("유지할 시트 초안");
				await ready(page, s);
				assert.equal(
					await page.locator("textarea").inputValue(),
					"NOT-A-CREDENTIAL-DRAFT",
				);
				assert.equal(
					await page.getByPlaceholder("출석부", { exact: true }).inputValue(),
					"유지할 시트 초안",
				);
				row.requests = route.records.map((r) => ({
					path: r.path,
					method: r.method,
				}));
				row.assertionsPassed = true;
			} finally {
				await route.dispose();
			}
		} finally {
			await page.context().close();
		}
	}

	async function knownHomeRetry(viewport) {
		const page = await newPage(viewport);
		const s = specs.find((x) => x.id === "home");
		const row = {
			id: "known-home-retry",
			viewport,
			controlledOtpResponse: true,
			assertionsPassed: false,
		};
		report.races.push(row);
		let route;
		try {
			// Controlled read data exposes an open OTP form; real OTP writes remain in the original suite.
			const shape = (r, data) =>
				r.path === "/api/my/activity"
					? {
							...data,
							isAttended: false,
							checkedAt: null,
							status: 1,
							statusText: "활동 중",
						}
					: r.path === "/api/my/fees/current"
						? null
						: data;
			const setupRoute = await intercept(page, s, { mapResponse: shape });
			await goto(page, "/");
			await setupRoute.dispose();
			await page.locator(".home-teams .court-head").first().click();
			await page.getByRole("button", { name: "출석하기", exact: true }).click();
			await page.locator(".otp-input").fill("1234");
			await page.evaluate(() => {
				window.__attendanceIdentity = document.querySelector(
					".home-attendance > div:first-child > .card",
				);
			});
			route = await intercept(page, s, {
				fault: "500",
				faultMatch: /\/api\/my\/stats$/,
				mapResponse: shape,
			});
			await page.route("**/api/my/attendance", (r) =>
				r.fulfill({
					status: 200,
					contentType: "application/json",
					body: JSON.stringify(new Date().toISOString()),
				}),
			);
			await page
				.getByRole("button", { name: "출석 확인", exact: true })
				.click();
			await ready(page, s, "error");
			await route.dispose();
			const box = () =>
				page.evaluate(() =>
					Object.fromEntries(
						[
							".home-tiles",
							".home-attendance > div:first-child",
							".home-teams",
						].map((selector) => {
							const r = document
								.querySelector(selector)
								?.getBoundingClientRect();
							return [
								selector,
								r ? { width: r.width, height: r.height } : null,
							];
						}),
					),
				);
			row.before = await box();
			assert.equal(
				await page.locator(".tile.lilac").count(),
				0,
				"no-fee known shape",
			);
			route = await intercept(page, s, { delayMs: 2000, mapResponse: shape });
			await page.evaluate(collector, cfg(s));
			await page
				.getByRole("button", { name: "다시 불러오기", exact: true })
				.click();
			await page.locator('.home-tiles[data-load-state="loading"]').waitFor();
			row.pending = await box();
			for (const selector of Object.keys(row.before)) {
				assert.ok(row.before[selector] && row.pending[selector], selector);
				assert.ok(
					Math.abs(
						row.before[selector].height - row.pending[selector].height,
					) <= 1,
					`${selector}: known retry height retained`,
				);
			}
			assert.equal(
				await page.evaluate(
					() =>
						window.__attendanceIdentity ===
						document.querySelector(
							".home-attendance > div:first-child > .card",
						),
				),
				true,
				"attendance component DOM retained",
			);
			row.pendingScreenshot = await screenshot(page, row.id, "pending");
			await ready(page, s);
			assert.equal(
				await page.locator(".otp-input").inputValue(),
				"1234",
				"open OTP input retained",
			);
			row.frames = await frames(page);
			validateTrace(row.frames, row.id, 2000);
			row.requests = route.records;
			row.readyScreenshot = await screenshot(page, row.id, "ready");
			row.assertionsPassed = true;
		} catch (error) {
			row.failure = error.message;
			row.requests = route?.records || [];
			throw error;
		} finally {
			if (route) await route.dispose();
			await page.context().close();
		}
	}

	// Make pagination and all member-status controls real, rather than skipping empty branches.
	let otherMemberId;
	let hasCurrentFee;
	const setup = await newPage(report.viewports[0]);
	try {
		const first = await api(setup, "POST", "/api/admin/members/search", {
			page: 1,
		});
		otherMemberId = first.items.find((m) => m.userId !== memberId)?.userId;
		assert.ok(otherMemberId, "second real member for route ordering");
		hasCurrentFee = (await api(setup, "GET", "/api/my/fees/current")) !== null;
		if ((await api(setup, "GET", "/api/admin/semesters")).length === 0)
			await api(setup, "POST", "/api/admin/semesters", {
				name: "로딩 검증 학기",
				startDate: "2097-01-01",
				endDate: "2097-06-30",
			});
		for (let n = first.totalCount; n < 12; n++) {
			await api(setup, "POST", "/api/admin/members", {
				studentNumber: `88${String(n).padStart(7, "0")}`,
				name: `로딩 검증 ${n}`,
				department: "검증학과",
				password: "VerifyMember1234!",
				role: "MEMBER",
			});
		}
	} finally {
		await setup.context().close();
	}

	for (const viewport of report.viewports) {
		for (const delayMs of report.delaysMs) {
			for (const s of specs) await entry(s, viewport, delayMs);
			const page = await newPage(viewport);
			try {
				const records = specs.find((s) => s.id === "records");
				await goto(page, records.route);
				for (const [label, control] of [
					[
						"previous-month",
						() =>
							page
								.getByRole("button", { name: "이전 달", exact: true })
								.click(),
					],
					[
						"next-month",
						() =>
							page
								.getByRole("button", { name: "다음 달", exact: true })
								.click(),
					],
					[
						"more-previous-month",
						() =>
							page
								.getByRole("button", { name: "이전 달 기록 보기", exact: true })
								.click(),
					],
				]) {
					report.actions.records ||= [];
					if (!report.actions.records.includes(label))
						report.actions.records.push(label);
					await query(
						page,
						records,
						`${label}-${viewport.width}-${delayMs}`,
						delayMs,
						control,
					);
				}
				const members = specs.find((s) => s.id === "members");
				await goto(page, members.route);
				const search = page.getByPlaceholder("이름 / 학번 검색", {
					exact: true,
				});
				const memberActions = [
					["search", () => search.fill("김")],
					["clear-search", () => search.fill("")],
				];
				await page.getByRole("button", { name: /^필터/ }).click();
				for (const name of [
					"활동 부원",
					"탈퇴 부원",
					"퇴출 부원",
					"시트 연결",
					"시트 미연결",
					"회비 완납",
					"회비 미납",
				]) {
					memberActions.push(
						[
							`${name}-on`,
							() => page.getByRole("button", { name, exact: true }).click(),
						],
						[
							`${name}-off`,
							() => page.getByRole("button", { name, exact: true }).click(),
						],
					);
				}
				for (const name of ["총 출석", "이번 학기 출석", "이번 달 출석"]) {
					memberActions.push(
						[
							`${name}-descending`,
							() =>
								page
									.locator(".sort-strip button")
									.filter({ hasText: name })
									.click(),
						],
						[
							`${name}-ascending`,
							() =>
								page
									.locator(".sort-strip button")
									.filter({ hasText: name })
									.click(),
						],
					);
				}
				for (const [label, perform] of memberActions) {
					report.actions.members ||= [];
					if (!report.actions.members.includes(label))
						report.actions.members.push(label);
					await query(
						page,
						members,
						`${label}-${viewport.width}-${delayMs}`,
						delayMs,
						perform,
					);
				}
				for (const name of ["다음", "이전", "2"]) {
					await query(
						page,
						members,
						`page-${name}-${viewport.width}-${delayMs}`,
						delayMs,
						() => page.getByRole("button", { name, exact: true }).click(),
					);
					report.actions.members.includes(`page-${name}`) ||
						report.actions.members.push(`page-${name}`);
				}
				const fees = specs.find((s) => s.id === "fees");
				await goto(page, fees.route);
				const buttons = page.locator(".fees-periods > .filter-group button");
				assert.ok((await buttons.count()) >= 2, "two real fee periods needed");
				for (const n of [1, 0]) {
					await query(
						page,
						fees,
						`period-${n}-${viewport.width}-${delayMs}`,
						delayMs,
						() => buttons.nth(n).click(),
					);
				}
				report.actions.fees = ["select-other-period", "select-first-period"];
				const detail = specs.find((s) => s.id === "member-detail");
				await goto(page, detail.route);
				const mappings = {
					...detail,
					id: "member-mappings",
					marker: ".member-mappings",
					anchor: ".member-mappings",
					payload: [".member-mappings .field:has(select)"],
					queries: [
						get(new RegExp(`^/api/admin/members/${memberId}/mappings$`)),
					],
				};
				await query(
					page,
					mappings,
					`mapping-tab-${viewport.width}-${delayMs}`,
					delayMs,
					() => page.getByText("시트 연결", { exact: true }).click(),
				);
				const attendance = {
					...detail,
					id: "member-attendance",
					marker: ".member-attendance",
					anchor: ".member-attendance",
					payload: [".member-attendance .admin-load-values"],
					queries: [
						get(new RegExp(`^/api/admin/members/${memberId}/attendance$`)),
					],
				};
				for (const n of [1, 2]) {
					await page.getByText("부원 수정", { exact: true }).click();
					await query(
						page,
						attendance,
						`attendance-tab-${n}-${viewport.width}-${delayMs}`,
						delayMs,
						() => page.getByText("출석 기록", { exact: true }).click(),
					);
				}
				await goto(page, detail.route);
				await query(
					page,
					mappings,
					`mapping-error-retry-${viewport.width}-${delayMs}`,
					delayMs,
					() => page.getByText("시트 연결", { exact: true }).click(),
					{ fault: "500" },
				);
				await query(
					page,
					attendance,
					`attendance-error-retry-${viewport.width}-${delayMs}`,
					delayMs,
					() => page.getByText("출석 기록", { exact: true }).click(),
					{ fault: "500" },
				);
				report.actions["member-detail"] = [
					"mapping-first-entry",
					"attendance-entry",
					"attendance-reentry",
					"mapping-error-retry",
					"attendance-error-retry",
				];
			} finally {
				await page.context().close();
			}
		}
		for (const s of specs) await entry(s, viewport, 600, { fault: "500" });
		for (const s of specs.filter((s) => s.hold))
			await entry(s, viewport, 0, { hold: s.hold });
		for (const s of specs) {
			await entry(s, viewport, 600, { mode: "reload" });
			await entry(s, viewport, 600, { mode: "menu" });
		}
	}
	for (const id of ["home", "records", "members", "member-detail"])
		await entry(
			specs.find((s) => s.id === id),
			report.viewports[0],
			2000,
			{ motion: "no-preference" },
		);
	const missingTargets = [
		["home", /^\/api\/my\/activity$/],
		["home", /^\/api\/my\/teams$/],
		["home", /^\/api\/my\/stats$/],
		["records", /^\/api\/my\/stats$/],
		["records", /^\/api\/my\/records$/],
		["dashboard", /^\/api\/admin\/activity\/today$/],
		["dashboard", /^\/api\/admin\/teams$/],
		["fees", /^\/api\/admin\/fees$/],
		["fees", /^\/api\/admin\/fees\/\d+\/statuses$/],
	];
	if (hasCurrentFee) missingTargets.push(["home", /^\/api\/my\/fees$/]);
	for (const [n, [id, faultMatch]] of missingTargets.entries()) {
		for (const fault of ["null", "204"])
			await entry(
				specs.find((s) => s.id === id),
				report.viewports[0],
				600,
				{ fault, faultMatch, caseName: `required-${n}-${fault}` },
			);
	}
	await entry(
		specs.find((s) => s.id === "home"),
		report.viewports[0],
		600,
		{
			fault: "null",
			faultMatch: /\/api\/my\/fees\/current$/,
			expectError: false,
			caseName: "optional-current-fee-null",
		},
	);
	for (const viewport of report.viewports) await localChecks(viewport);
	await knownHomeRetry(report.viewports[0]);
	await readRaces(report.viewports[0]);
	await routeRaces(report.viewports[0], otherMemberId, false);
	await routeRaces(report.viewports[0], otherMemberId, true);
	await tabRouteRaces(report.viewports[0], otherMemberId);
	await postWriteRouteRace(report.viewports[0], otherMemberId);
	assert.equal(
		report.races.length,
		21,
		"all planned race/retention scenarios ran",
	);
	assert.ok(
		report.races.every((r) => r.assertionsPassed),
		"all controlled ordering cases passed",
	);
	assert.equal(
		report.localControls.length,
		report.viewports.length * 6,
		"all local/independent control cases ran",
	);
	assert.ok(
		report.localControls.every((r) => r.assertionsPassed),
		"local controls verified without added queries",
	);
	for (const s of specs)
		for (const viewport of report.viewports) {
			for (const delay of report.delaysMs)
				assert.ok(
					report.conditions.some(
						(c) =>
							c.id === `${s.id}-${viewport.width}-${delay}-initial` &&
							c.assertionsPassed,
					),
					`${s.id}: full initial delay matrix`,
				);
			for (const mode of ["menu", "reload"])
				assert.ok(
					report.conditions.some(
						(c) =>
							c.id === `${s.id}-${viewport.width}-600-${mode}` &&
							c.assertionsPassed,
					),
					`${s.id}: ${mode} navigation verified`,
				);
			if (!report.actions[s.id].includes("error-retry"))
				report.actions[s.id].push("error-retry");
		}
	assert.equal(report.inventory.filter((r) => r.applicable).length, 11);
	assert.equal(report.inventory.length, 16);
	assert.ok(
		report.conditions.every((c) => c.assertionsPassed),
		"no failed or skipped loading conditions",
	);
	report.coverage = {
		conditions: report.conditions.length,
		partialGroups: report.partialGroups.length,
		races: report.races.length,
		localControls: report.localControls.length,
	};
	report.complete = true;
	console.log(
		`PASS loading UX: ${report.conditions.length} conditions / ${report.partialGroups.length} partial groups`,
	);
};
