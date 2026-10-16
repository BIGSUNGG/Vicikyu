// Real WASM + real application/SQLite/sync; mutable synthetic Google boundary, never live Google.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const out = path.join(root, 'artifacts/admin-activity-management/browser');
fs.mkdirSync(out, { recursive: true });
const { chromium } = require(path.join(root, '.diag/pw/node_modules/playwright'));
const name = `vicikyu-activity-check-${crypto.randomUUID()}`;
const image = `${name}:local`, network = `${name}-network`;
const password = `Verify-${crypto.randomUUID()}!`;
const report = { startedAt: new Date().toISOString(), success: false, fakeGoogle: true, liveGoogle: false,
  realIPhone: false, engine: 'Chromium / WASM (not physical iPhone or WebKit)',
  os: { platform: os.platform(), release: os.release(), arch: os.arch() },
  viewports: [{ width: 1280, height: 900 }, { width: 390, height: 844 }],
  logs: { build: path.join(out, 'build.log'), server: path.join(out, 'server.log') },
  cases: [], measurements: [], screenshots: [], wasmResponses: 0, browserErrors: [] };
let ownedContainer = false, ownedNetwork = false, ownedImage = false, browser, page, base, browserTimer;
function docker(args, timeout = 30000, log) {
  let fd;
  try {
    if (log) fd = fs.openSync(log, 'w');
    return execFileSync('docker', args, { cwd: root, timeout, encoding: 'utf8',
      stdio: ['ignore', fd ?? 'pipe', fd ?? 'pipe'], maxBuffer: 8 * 1024 * 1024 })?.trim();
  } catch (e) {
    // Never print argv: the disposable admin password is supplied only to our container.
    throw new Error(`Docker ${args[0]} failed (exit ${e.status ?? 'timeout'}); ${log ? `see ${log}` : String(e.stderr || '').slice(-1500)}`);
  } finally { if (fd !== undefined) fs.closeSync(fd); }
}
function cleanup() {
  const failures = [];
  if (ownedContainer) {
    try { docker(['logs', name], 15000, report.logs.server); } catch (e) { failures.push(e.message); }
    try { docker(['rm', '-f', name]); ownedContainer = false; } catch (e) { failures.push(e.message); }
  }
  if (ownedNetwork) try { docker(['network', 'rm', network]); ownedNetwork = false; } catch (e) { failures.push(e.message); }
  if (ownedImage) try { docker(['image', 'rm', image]); ownedImage = false; } catch (e) { failures.push(e.message); }
  report.cleanupErrors = failures;
  return failures;
}
function save() { fs.writeFileSync(path.join(out, 'results.json'), JSON.stringify(report, null, 2)); }
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => {
  report.failure = signal; cleanup(); save(); process.exit(1);
});
const deadline = setTimeout(() => {
  report.failure = 'Whole verification exceeded 960 seconds'; cleanup(); save(); process.exit(1);
}, 960000);
async function api(method, resource, body, expected = 200) {
  const result = await page.evaluate(async ({ method, resource, body }) => {
    const headers = {};
    if (method !== 'GET') {
      const csrf = await fetch('/api/auth/csrf');
      if (!csrf.ok) throw new Error('CSRF endpoint failed');
      headers['X-CSRF-TOKEN'] = (await csrf.json()).token;
    }
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(resource, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    const text = await response.text();
    return { status: response.status, data: text ? JSON.parse(text) : null };
  }, { method, resource, body });
  assert.equal(result.status, expected, `${method} ${resource}: ${JSON.stringify(result.data)}`);
  return result.data;
}
const fixture = '/api/verification/activity-management';
const state = () => api('GET', `${fixture}/state`);
const activities = () => api('GET', '/api/admin/activities');
const resource = key => `/api/admin/activities/${encodeURIComponent(key)}`;
const row = key => page.locator(`.activity-list [data-activity-key="${key}"]`);
const memberRow = key => page.locator(`.member-attendance [data-activity-key="${key}"]`);
async function ready() { await page.locator('.activity-list[data-load-state="ready"]').waitFor(); }
async function goto(route) {
  const response = await page.goto(base + route, { waitUntil: 'load' });
  assert.equal(response.status(), 200);
  await page.locator('.app-shell').waitFor();
  await page.waitForLoadState('networkidle');
  assert.equal(await page.locator('#blazor-error-ui').isVisible(), false);
}
async function action(method, url, perform, expected = 200) {
  const [response] = await Promise.all([
    page.waitForResponse(r => r.url() === base + url && r.request().method() === method), perform()
  ]);
  if (response.status() !== expected)
    assert.fail(`${method} ${url}: expected ${expected}, got ${response.status()}: ${await response.text()}`);
  await page.waitForLoadState('networkidle');
  if (page.url().endsWith('/admin/activities')) await ready();
  return response.status() === 204 ? null : response.json();
}
async function select(key) {
  await row(key).getByRole('button', { name: /수정$/ }).click();
  await page.locator('.activity-members[data-load-state="ready"]').waitFor();
  assert.equal(await page.locator('.activity-edit').getAttribute('data-activity-key'), key);
}
async function create(target, date, kind, reason) {
  await page.locator('#activity-create-target').selectOption({ label: target });
  await page.locator('#activity-create-date').fill(date);
  await page.locator('#activity-create-state').selectOption(String(kind));
  if (reason !== undefined) await page.locator('#activity-create-reason').fill(reason);
  return action('POST', '/api/admin/activities', () => page.locator('.activity-create button[type="submit"]').click());
}
async function edit(key, date, kind, reason, expected = 200) {
  await page.locator('#activity-edit-date').fill(date);
  await page.locator('#activity-edit-state').selectOption(String(kind));
  if (reason !== undefined) await page.locator('#activity-edit-reason').fill(reason);
  return action('PUT', resource(key), () => page.getByRole('button', { name: '변경 저장', exact: true }).click(), expected);
}
function sourceColumn(s, source) {
  const sheet = s.sheets.find(x => x.id === source.sheetDefinitionId);
  const tab = s.source.documents.find(x => x.document === sheet.spreadsheetId && x.tab.name === source.tabName).tab;
  const column = Object.entries(tab.columnKeys).find(([, key]) => key === source.id);
  assert.ok(column, `Missing real source column ${source.id}`);
  const index = Number(column[0]) - 1;
  return { tab, index, raw: tab.rawValues.map(r => r[index] ?? '{}'), rows: tab.rows.map(r => r[index] ?? '') };
}
function assertAttendance(s, sourceId, userId, attended) {
  const source = s.sources.find(x => x.id === sourceId);
  assert.equal(s.attendances.some(x => x.activityId === source.activityId && x.userId === userId), attended, 'DB attendance');
  const mapping = s.mappings.find(x => x.sheetDefinitionId === source.sheetDefinitionId && x.tabName === source.tabName && x.userId === userId);
  const column = sourceColumn(s, source);
  const rowIndex = column.tab.rows.findIndex(r => r[0] === mapping.rowLabel);
  assert.equal(column.rows[rowIndex], attended ? '출석' : '미출석', 'actual mutable source attendance');
  assert.equal(s.journalCount, 0, 'no pending recovery journal');
}
async function measure(label, dialog = false) {
  const measurement = await page.evaluate(({ dialog }) => {
    const rect = element => { const r = element.getBoundingClientRect(); return { x: r.x, y: r.y, right: r.right, bottom: r.bottom, width: r.width, height: r.height }; };
    const selectors = dialog ? ['[role="dialog"]'] : ['.app-shell', '.activity-list', '.activity-edit', '.member-attendance'];
    const boxes = selectors.flatMap(selector => [...document.querySelectorAll(selector)].map(element => ({ selector, ...rect(element) })));
    const modal = document.querySelector('[role="dialog"]');
    return { viewport: { width: innerWidth, height: innerHeight }, scrollWidth: document.scrollingElement.scrollWidth,
      clientWidth: document.scrollingElement.clientWidth, boxes, focusInDialog: modal ? modal.contains(document.activeElement) : null,
      activeElement: document.activeElement?.outerHTML.slice(0, 240) };
  }, { dialog });
  report.measurements.push({ label, ...measurement });
  assert.ok(measurement.scrollWidth <= measurement.clientWidth + 1, `horizontal overflow ${label}`);
  for (const box of measurement.boxes) {
    assert.ok(box.x >= -1 && box.right <= measurement.viewport.width + 1, `box overflow ${label}: ${JSON.stringify(box)}`);
    if (dialog) assert.ok(box.y >= -1 && box.bottom <= measurement.viewport.height + 1, `dialog fit ${label}`);
  }
  if (dialog) assert.equal(measurement.focusInDialog, true, `dialog focus ${label}`);
  const file = `${label}.png`;
  await page.screenshot({ path: path.join(out, file), fullPage: true });
  report.screenshots.push(file);
}
async function dialogMeasure(label) {
  await page.getByRole('dialog').waitFor();
  await page.waitForFunction(() => document.querySelector('[role="dialog"]')?.contains(document.activeElement));
  // Tab must stay inside the trap; Escape is exercised separately by cancellation.
  for (let i = 0; i < 4; i++) await page.keyboard.press('Tab');
  await measure(label, true);
}
function caseDone(name, evidence) { report.cases.push({ name, passed: true, evidence }); }

async function verify() {
  const anonymous = await browser.newContext();
  const response = await anonymous.request.get(base + fixture + '/state');
  assert.equal(response.status(), 401, 'fixture state is not anonymous');
  await anonymous.close();
  await goto('/Account/Login');
  await page.locator('#student-number').fill('admin');
  await page.locator('#password').fill(password);
  await page.getByRole('button', { name: '로그인', exact: true }).click();
  await page.waitForURL(base + '/');
  await goto('/admin');
  await page.locator('a[href="/admin/activities"]:visible').click();
  await page.waitForURL(base + '/admin/activities');
  await ready();
  let s = await state();
  assert.equal(s.database, 'Data Source=/tmp/vicikyu-wasm-audit.db');
  assert.equal(s.fakeGoogle, true); assert.equal(s.liveGoogle, false);
  const member = s.members.find(x => x.studentNumber === '202512001');
  const first = s.sheets.find(x => x.spreadsheetId === 'verification-activity-first');
  const second = s.sheets.find(x => x.spreadsheetId === 'verification-activity-second');
  let list = await activities();
  const initialCompleted = list.find(x => x.sheetId === first.id && x.date === '2026-10-01');
  assert.equal(initialCompleted.attendanceCount, 0);
  // Use keys returned by the real API; do not invent DOM selectors from display text.
  const sourceId = key => {
    const item = s.sources.find(x => key.endsWith(x.id));
    assert.ok(item, `Unknown source key ${key}`); return item.id;
  };
  const deletedSourceId = sourceId(initialCompleted.key);
  for (const item of list.filter(x => [1, 2].includes(x.kind) && x.activityId === null)) {
    await select(item.key);
    assert.equal(await page.locator('.activity-members [role="switch"]:enabled').count(), 0);
  }
  assert.ok(list.some(x => x.tabName === '2026-1') && list.some(x => x.sheetId === second.id));
  const keys = await page.locator('.activity-list [data-activity-key]').evaluateAll(rows => rows.map(r => r.dataset.activityKey));
  assert.deepEqual(keys, list.map(x => x.key));
  assert.deepEqual(list.map(x => x.date), list.map(x => x.date).sort().reverse());
  caseDone('adminnav / all terms+documents latest-first / scheduled+none disabled', { keys });

  const scheduled = await create('합성 출석부 A / 2026-2', '2026-10-24', 1);
  const noneEmpty = await create('합성 출석부 B / 2026-2', '2026-10-25', 2);
  const editable = await create('합성 출석부 A / 2026-2', '2026-10-26', 2, '합성 휴관');
  s = await state();
  for (const item of [scheduled, noneEmpty, editable]) {
    const src = s.sources.find(x => item.key.endsWith(x.id));
    assert.equal(src.sheetDefinitionId, item.sheetId); assert.equal(src.date, item.date);
    assert.ok(sourceColumn(s, src).rows.some(x => x.includes(item.kind === 1 ? '활동 예정' : '활동 없음')));
  }
  assert.equal(noneEmpty.reason, null);
  assert.equal(editable.reason, '합성 휴관');
  caseDone('create correct sheet/tab scheduled + none with/without optional reason', { scheduled, noneEmpty, editable });
  await select(editable.key);
  await edit(editable.key, '2026-10-27', 2, '합성 사유 변경');
  await edit(editable.key, '2026-10-28', 1);
  await page.reload({ waitUntil: 'load' }); await ready();
  await api('POST', '/api/admin/sheets/resync');
  await page.getByRole('button', { name: '새로고침', exact: true }).click(); await ready();
  list = await activities();
  let changed = list.find(x => x.key === editable.key);
  assert.equal(changed.date, '2026-10-28'); assert.equal(changed.kind, 1); assert.equal(changed.reason, null);
  await select(editable.key);
  await edit(editable.key, '2026-10-28', 2, '합성 보존 사유');
  await api('POST', '/api/admin/sheets/resync');
  await page.reload({ waitUntil: 'load' }); await ready();
  changed = (await activities()).find(x => x.key === editable.key);
  assert.equal(changed.reason, '합성 보존 사유'); assert.equal(changed.kind, 2);
  await select(editable.key);
  await edit(editable.key, '2026-10-28', 0);
  changed = (await activities()).find(x => x.key === editable.key);
  assert.equal(changed.attendanceCount, 0); assert.equal(changed.canEditAttendance, true);
  s = await state(); const editedSourceId = sourceId(editable.key);
  caseDone('date/reason/status persisted on reload + real resync; empty completed conversion', { changed });

  const memberSwitch = () => page.locator(`.activity-members [data-user-id="${member.id}"] [role="switch"]`);
  const toggleUrl = `${resource(editable.key)}/members/${member.id}`;
  await memberSwitch().focus();
  await action('PUT', toggleUrl, () => page.keyboard.press('Space'), 204);
  assertAttendance(await state(), editedSourceId, member.id, true);
  assert.equal((await activities()).find(x => x.key === editable.key).attendanceCount, 1);
  await edit(editable.key, '2026-10-28', 2, '', 409);
  await page.locator('.activity-write-error[role="alert"]').waitFor();
  assertAttendance(await state(), editedSourceId, member.id, true);
  await memberSwitch().click();
  await dialogMeasure('desktop-attendance-dialog');
  await action('PUT', toggleUrl, () => page.getByRole('dialog').getByRole('button', { name: '출석 취소 확인', exact: true }).click(), 204);
  assertAttendance(await state(), editedSourceId, member.id, false);
  assert.equal((await activities()).find(x => x.key === editable.key).attendanceCount, 0);
  caseDone('activity editor keyboard on/off / count / server refuses none with attendance', { memberId: member.id });

  await goto(`/admin/members/${member.id}`);
  await page.getByRole('link', { name: '출석 기록', exact: true }).click();
  await page.locator('.member-attendance[data-load-state="ready"]').waitFor();
  const beforeStats = await api('GET', `/api/admin/members/${member.id}/activities`);
  assert.deepEqual(beforeStats.items.map(x => x.date), beforeStats.items.map(x => x.date).sort().reverse());
  for (const record of beforeStats.items.filter(x => !x.canEditAttendance))
    assert.equal(await memberRow(record.key).getByRole('switch').isDisabled(), true);
  await action('PUT', toggleUrl, () => memberRow(editable.key).getByRole('switch').click(), 204);
  assertAttendance(await state(), editedSourceId, member.id, true);
  const afterStats = await api('GET', `/api/admin/members/${member.id}/activities`);
  for (const count of ['totalCount', 'semesterCount', 'monthCount']) assert.equal(afterStats[count], beforeStats[count] + 1);
  await action('PUT', toggleUrl, () => memberRow(editable.key).getByRole('switch').click(), 204);
  assertAttendance(await state(), editedSourceId, member.id, false);
  assert.deepEqual(await api('GET', `/api/admin/members/${member.id}/activities`), beforeStats);
  await page.setViewportSize(report.viewports[1]);
  await measure('mobile-member-detail');
  caseDone('member detail latest list / disabled states / on+off source+DB+counts', { beforeStats, afterStats });

  await goto('/admin/activities'); await ready();
  await select(initialCompleted.key);
  await action('PUT', `${resource(initialCompleted.key)}/members/${member.id}`,
    () => page.locator(`.activity-members [data-user-id="${member.id}"] [role="switch"]`).click(), 204);
  assertAttendance(await state(), deletedSourceId, member.id, true);
  await measure('mobile-activities');
  const beforeDelete = await state();
  await page.getByRole('button', { name: '활동 삭제', exact: true }).click();
  await dialogMeasure('mobile-delete-dialog');
  await page.keyboard.press('Escape');
  await page.getByRole('dialog').waitFor({ state: 'hidden' });
  assert.deepEqual(await state(), beforeDelete, 'cancel leaves source and all DB rows unchanged');
  await page.setViewportSize(report.viewports[0]);
  await measure('desktop-activities');
  await page.getByRole('button', { name: '활동 삭제', exact: true }).click();
  await dialogMeasure('desktop-delete-dialog');
  await action('DELETE', resource(initialCompleted.key), () => page.getByRole('dialog').getByRole('button', { name: '삭제', exact: true }).click(), 204);
  s = await state();
  assert.deepEqual(s.sources, beforeDelete.sources.filter(x => x.id !== deletedSourceId));
  for (const name of ['activities', 'attendances', 'guests', 'teams']) {
    const preserved = beforeDelete[name].filter(x => (name === 'activities' ? x.id : x.activityId) !== initialCompleted.activityId);
    assert.deepEqual(s[name], preserved, `delete preserves unrelated ${name}`);
  }
  const deletedTeams = beforeDelete.teams.filter(x => x.activityId === initialCompleted.activityId).map(x => x.id);
  assert.deepEqual(s.teamMembers, beforeDelete.teamMembers.filter(x => !deletedTeams.includes(x.teamId)));
  assert.deepEqual(s.members, beforeDelete.members); assert.deepEqual(s.semesters, beforeDelete.semesters);
  const relativeValues = raw => raw.map(value => {
    const cell = JSON.parse(value);
    if (cell.formulaValue) cell.formulaValue = cell.formulaValue.replace(/(\$?)[A-Z]+(?=\$?\d)/g, '$1COLUMN');
    return cell;
  });
  for (const src of beforeDelete.sources.filter(x => x.id !== deletedSourceId))
    assert.deepEqual(relativeValues(sourceColumn(s, src).raw), relativeValues(sourceColumn(beforeDelete, src).raw), 'independent source values and relative formulas preserved');
  assert.equal(s.source.documents.some(d => Object.values(d.tab.columnKeys).includes(deletedSourceId)), false);
  assert.ok(beforeDelete.auditIds.every(id => s.auditIds.includes(id)), 'existing audit rows preserved');
  assert.ok(s.auditCount >= beforeDelete.auditCount);
  caseDone('delete cancel + approve removes exact source column and dependent DB rows only', { deletedSourceId, activityId: initialCompleted.activityId });

  const beforeFailure = await state();
  await api('POST', `${fixture}/fault`, { mode: 'afterApply' }, 204);
  await page.locator('#activity-create-target').selectOption({ label: '합성 출석부 A / 2026-2' });
  await page.locator('#activity-create-date').fill('2026-10-30');
  await page.locator('#activity-create-state').selectOption('1');
  await action('POST', '/api/admin/activities', () => page.locator('.activity-create button[type="submit"]').click(), 500);
  await page.locator('.activity-write-error[role="alert"]').waitFor();
  const failed = await state();
  assert.deepEqual(failed.source.documents, beforeFailure.source.documents, 'post-apply failure restored exact source including formulas, identities and marker');
  for (const table of ['sources', 'activities', 'attendances', 'guests', 'teams', 'teamMembers', 'members', 'semesters'])
    assert.deepEqual(failed[table], beforeFailure[table], `failure restored ${table}`);
  assert.equal(failed.source.failed, beforeFailure.source.failed + 1);
  assert.equal(failed.source.restored, beforeFailure.source.restored + 1);
  assert.equal(failed.source.armedFault, null); assert.equal(failed.journalCount, 0);
  await measure('desktop-source-failure');
  const retried = await action('POST', '/api/admin/activities', () => page.locator('.activity-create button[type="submit"]').click());
  s = await state();
  const retrySource = s.sources.find(x => retried.key.endsWith(x.id));
  assert.equal(retrySource.date, '2026-10-30');
  assert.ok(sourceColumn(s, retrySource).rows.includes('활동 예정'));
  assert.equal(await page.locator('.activity-write-error').count(), 0);
  caseDone('one-shot after-apply source failure visible / exact compensation / UI retry', { restored: failed.source.restored, retried });

  await goto('/admin/attendance');
  await page.locator('.attendance-card[data-load-state="ready"]').waitFor();
  const oldRow = page.locator('.attendance-card .member-row').filter({ hasText: member.studentNumber });
  const todayBefore = await state();
  await oldRow.locator('input[type="checkbox"]').click();
  await page.locator('.confirm-box').waitFor();
  await action('DELETE', `/api/admin/attendance/${member.id}`, () => page.getByRole('button', { name: '취소하기', exact: true }).click(), 204);
  await action('POST', `/api/admin/attendance/${member.id}`, () => oldRow.locator('input[type="checkbox"]').click(), 204);
  const todayAfter = await state();
  const today = todayAfter.activities.find(x => x.activityDate === '2026-10-16');
  assert.equal(todayAfter.attendances.some(x => x.activityId === today.id && x.userId === member.id), true);
  assert.equal(todayAfter.attendances.length, todayBefore.attendances.length);
  caseDone('existing /admin/attendance old UI remove+add flow remains functional', { activityId: today.id });
  report.finalState = todayAfter;
  assert.ok(report.wasmResponses > 0, 'actual WASM binary loaded');
  assert.deepEqual(report.browserErrors, [], 'no JS errors or server circuits');
}

(async () => {
  try {
    // Unique resources only. No mounts except /tmp tmpfs; no production volumes or credentials.
    ownedImage = true;
    docker(['build', '--progress=plain', '-t', image, '.'], 600000, report.logs.build);
    report.imageId = docker(['image', 'inspect', image, '--format', '{{.Id}}']);
    // Docker Desktop does not publish ports on --internal bridges. The client itself rejects all non-fixture document IDs.
    docker(['network', 'create', '--driver', 'bridge', network]); ownedNetwork = true;
    docker(['create', '--name', name, '--network', network, '--tmpfs', '/tmp', '-p', '127.0.0.1::8080',
      '-e', 'ASPNETCORE_ENVIRONMENT=Verification',
      '-e', 'Verification__ActivityManagement=true',
      '-e', 'ConnectionStrings__DefaultConnection=Data Source=/tmp/vicikyu-wasm-audit.db',
      '-e', `Seed__AdminPassword=${password}`,
      '-e', 'Google__ServiceAccountJson=', '-e', 'Google__SpreadsheetId=', '-e', 'Sheets__SyncOnStartup=false', image]);
    ownedContainer = true; docker(['start', name]);
    const binding = docker(['port', name, '8080/tcp']);
    assert.match(binding, /^127\.0\.0\.1:\d+$/);
    base = `http://${binding}`; report.url = base;
    let healthy = false;
    for (let i = 0; i < 60; i++) {
      try { healthy = (await fetch(base + '/Account/Login', { signal: AbortSignal.timeout(1000) })).ok; } catch { /* bounded startup poll */ }
      if (healthy) break;
      await new Promise(resolve => setTimeout(resolve, 500));
    }
    assert.ok(healthy, 'isolated container startup failed; see server.log');
    browserTimer = setTimeout(() => { report.browserDeadlineExceeded = true; browser?.close().catch(() => {}); }, 240000);
    browser = await chromium.launch({ headless: true, timeout: 30000,
      ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : { channel: process.env.BROWSER_CHANNEL || 'msedge' }) });
    report.browserVersion = browser.version();
    const context = await browser.newContext({ viewport: report.viewports[0], timezoneId: 'Asia/Seoul', reducedMotion: 'reduce' });
    page = await context.newPage(); page.setDefaultTimeout(15000); page.setDefaultNavigationTimeout(30000);
    page.on('pageerror', e => report.browserErrors.push(e.message));
    page.on('websocket', ws => report.browserErrors.push(`Unexpected server circuit ${ws.url()}`));
    page.on('response', r => { if (r.url().includes('.wasm') && r.ok()) report.wasmResponses++; });
    await verify();
    report.success = true;
    console.log(`PASS ${report.cases.length} browser cases; ${report.wasmResponses} WASM responses; fakeGoogle=true liveGoogle=false`);
  } catch (error) {
    report.failure = error.message;
    if (page && !page.isClosed()) {
      try {
        report.failurePage = { url: page.url(), text: (await page.locator('body').innerText()).slice(-4000) };
        await page.screenshot({ path: path.join(out, 'failure.png'), fullPage: true });
      } catch { /* browser deadline may already have closed it */ }
    }
    console.error(error.message); process.exitCode = 1;
  } finally {
    clearTimeout(browserTimer);
    try {
      if (browser) await browser.close();
    } catch (error) {
      report.browserCloseError = error.message; report.success = false; process.exitCode = 1;
    } finally {
      const failures = cleanup();
      if (failures.length) { report.success = false; process.exitCode = 1; }
      report.finishedAt = new Date().toISOString(); save(); clearTimeout(deadline);
    }
  }
})();
