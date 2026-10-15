// Extends the existing owned-container browser harness. No live database or Sheets access.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const mime = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';

// Tiny stored-ZIP fixture containing ONLY synthetic values, not an Excel editor.
function syntheticWorkbook(student = '209900001') {
    const ns = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main';
    const rel = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships';
    const text = (ref, value) => `<c r="${ref}" t="inlineStr"><is><t>${value}</t></is></c>`;
    const files = {
        '[Content_Types].xml': '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/></Types>',
        '_rels/.rels': `<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="${rel}/officeDocument" Target="xl/workbook.xml"/></Relationships>`,
        'xl/workbook.xml': `<workbook xmlns="${ns}" xmlns:r="${rel}"><sheets><sheet name="부원 목록" sheetId="1" r:id="r1"/></sheets></workbook>`,
        'xl/_rels/workbook.xml.rels': `<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="${rel}/worksheet" Target="worksheets/sheet1.xml"/></Relationships>`,
        'xl/worksheets/sheet1.xml': `<worksheet xmlns="${ns}"><sheetData><row r="1">${text('A1','이름')}${text('B1','학과')}${text('C1','학번')}</row><row r="2">${text('A2','합성 업로드 부원')}${text('B2','합성 학과')}<c r="C2"><v>${student}</v></c></row></sheetData></worksheet>`,
    };
    const crc32 = data => {
        let crc = 0xffffffff;
        for (const byte of data) {
            crc ^= byte;
            for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
        }
        return (crc ^ 0xffffffff) >>> 0;
    };
    const local = [], central = [];
    let offset = 0;
    for (const [filename, xml] of Object.entries(files)) {
        const name = Buffer.from(filename), data = Buffer.from(xml), crc = crc32(data);
        const header = Buffer.alloc(30);
        header.writeUInt32LE(0x04034b50); header.writeUInt16LE(20, 4);
        header.writeUInt32LE(crc, 14); header.writeUInt32LE(data.length, 18); header.writeUInt32LE(data.length, 22); header.writeUInt16LE(name.length, 26);
        const directory = Buffer.alloc(46);
        directory.writeUInt32LE(0x02014b50); directory.writeUInt16LE(20, 4); directory.writeUInt16LE(20, 6);
        directory.writeUInt32LE(crc, 16); directory.writeUInt32LE(data.length, 20); directory.writeUInt32LE(data.length, 24);
        directory.writeUInt16LE(name.length, 28); directory.writeUInt32LE(offset, 42);
        local.push(header, name, data); central.push(directory, name);
        offset += header.length + name.length + data.length;
    }
    const directory = Buffer.concat(central), end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50); end.writeUInt16LE(Object.keys(files).length, 8); end.writeUInt16LE(Object.keys(files).length, 10);
    end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
    return Buffer.concat([...local, directory, end]);
}

async function synthetic(options) {
    const { page, api, action, evidence, evidenceDir, memberPassword } = options;
    const cases = evidence.memberBulk = [];
    const card = page.locator('.member-bulk-add');
    const rows = card.locator('.bulk-row');
    const count = async () => (await api(page, 'POST', '/api/admin/members/search', {})).totalCount;
    const baseline = await count();
    const report = (name, extra = {}) => cases.push({ name, passed: true, ...extra });
    const field = (row, name) => rows.nth(row).locator(`[data-field="${name}"]`);
    const add = () => card.getByRole('button', { name: '+ 행 추가', exact: true }).click();
    const submit = () => card.getByRole('button', { name: '일괄 추가', exact: true }).click();
    let submissions = 0;
    page.on('request', r => { if (new URL(r.url()).pathname === '/api/admin/members/bulk' && r.method() === 'POST') submissions++; });
    await card.getByRole('button', { name: '+ 부원 추가', exact: true }).click();
    assert.ok(await card.locator('#member-workbook').isDisabled());
    await submit();
    assert.equal(submissions, 0);
    assert.equal(await count(), baseline);
    report('upload gated / empty final list rejected', { dbDelta: 0 });
    const [download] = await Promise.all([page.waitForEvent('download'), card.getByRole('link', { name: '템플릿 다운로드' }).click()]);
    const downloaded = path.join(evidenceDir, 'member-template.xlsx');
    await download.saveAs(downloaded);
    const sha256 = crypto.createHash('sha256').update(fs.readFileSync(downloaded)).digest('hex');
    assert.equal(sha256, 'dc01a57fa3aea7eb08c42c8e064faf1288b99fecb239c06ca4e558659097a2e2');
    report('byte-identical empty template download', { sha256, file: 'member-template.xlsx' });
    await card.locator('#member-default-password').fill(memberPassword);
    assert.ok(await card.locator('#member-workbook').isEnabled());
    await add();
    await field(0, 'student').fill('202699999');
    await field(0, 'name').fill('WASM 검증 부원');
    await field(0, 'department').fill('검증학과');
    const importPassword = `Import-${crypto.randomUUID()}!`;
    await card.locator('#member-default-role').selectOption('ADMIN');
    await card.locator('#member-default-password').fill(importPassword);
    assert.equal(await field(0, 'role').inputValue(), 'MEMBER');
    assert.ok(await field(0, 'password').inputValue() === memberPassword, 'existing password default snapshot');
    assert.equal(await field(0, 'password').getAttribute('type'), 'password');
    const upload = async student => action(page, '/api/admin/members/import', 'POST', () => card.locator('#member-workbook').setInputFiles({ name: 'synthetic.xlsx', mimeType: mime, buffer: syntheticWorkbook(student) }));
    await upload('209900001');
    await rows.nth(1).waitFor();
    await upload('209900002');
    await rows.nth(2).waitFor();
    await add();
    assert.equal(await rows.count(), 4);
    assert.equal(await field(1, 'student').inputValue(), '209900001');
    assert.equal(await field(1, 'role').inputValue(), 'ADMIN');
    assert.ok(await field(1, 'password').inputValue() === importPassword, 'import password default snapshot');
    await card.locator('#member-default-role').selectOption('MEMBER');
    await card.locator('#member-default-password').fill(memberPassword);
    assert.equal(await field(1, 'role').inputValue(), 'ADMIN');
    assert.ok(await field(1, 'password').inputValue() === importPassword, 'changing defaults does not modify imported row');
    await field(1, 'student').fill('209900003');
    await field(1, 'name').fill('수정 합성 부원');
    await field(1, 'department').fill('');
    await field(1, 'role').selectOption('MEMBER');
    await field(1, 'password').fill(memberPassword);
    await rows.nth(3).getByRole('button', { name: '4 행 삭제' }).click();
    assert.equal(await rows.count(), 3);
    assert.equal(await count(), baseline);
    report('append two uploads + manual / edit all fields / delete / default snapshots', { previewRows: 3, dbDelta: 0 });
    const requestsBefore = submissions;
    await field(1, 'name').fill(''); await submit();
    assert.equal(submissions, requestsBefore);
    await field(1, 'name').fill('수정 합성 부원');
    await field(1, 'student').fill('202699999'); await submit();
    assert.equal(submissions, requestsBefore);
    assert.ok(await rows.nth(1).getByRole('alert').count() > 0);
    await field(1, 'student').fill('admin');
    const rejected = await action(page, '/api/admin/members/bulk', 'POST', submit);
    assert.equal(rejected.created.length, 0);
    assert.ok(rejected.errors.some(e => e.row === 2));
    await rows.nth(1).getByRole('alert').first().waitFor();
    assert.equal(await count(), baseline);
    await card.screenshot({ path: path.join(evidenceDir, 'member-bulk-errors.png') });
    report('row errors / preview duplicates / DB duplicate block whole list', { dbDelta: 0, screenshot: 'member-bulk-errors.png' });
    await field(1, 'student').fill('209900003');
    await page.setViewportSize({ width: 393, height: 852 });
    assert.ok(await card.evaluate(e => e.scrollWidth <= e.clientWidth + 1), 'bulk card mobile overflow');
    await card.screenshot({ path: path.join(evidenceDir, 'member-bulk-mobile.png') });
    await page.setViewportSize({ width: 1280, height: 900 });
    report('responsive synthetic editable preview', { screenshot: 'member-bulk-mobile.png' });
    await card.getByRole('button', { name: '추가 취소', exact: true }).click();
    assert.equal(await rows.count(), 0); assert.equal(await count(), baseline);
    report('cancel clears preview without writes', { dbDelta: 0 });
    // The old individual form is gone: register a single manual row through the same bulk endpoint.
    await card.getByRole('button', { name: '+ 부원 추가', exact: true }).click();
    await card.locator('#member-default-password').fill(memberPassword); await add();
    await field(0, 'student').fill('202699999'); await field(0, 'name').fill('WASM 검증 부원'); await field(0, 'department').fill('검증학과');
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    const routeHandler = async route => { await gate; await route.continue(); };
    await page.route('**/api/admin/members/bulk', routeHandler);
    const beforeSubmit = submissions;
    const pending = action(page, '/api/admin/members/bulk', 'POST', submit);
    await page.getByRole('button', { name: '처리 중…', exact: true }).waitFor();
    assert.ok(await page.getByRole('button', { name: '처리 중…', exact: true }).isDisabled());
    assert.ok(await card.getByRole('button', { name: '추가 취소', exact: true }).isDisabled());
    await page.getByRole('button', { name: '처리 중…', exact: true }).evaluate(button => button.click());
    release();
    const result = await pending;
    await page.unroute('**/api/admin/members/bulk', routeHandler);
    assert.equal(result.created.length, 1); assert.equal(result.errors.length, 0);
    assert.equal(submissions - beforeSubmit, 1); assert.equal(await count(), baseline + 1);
    report('single manual row atomic commit / duplicate in-flight submission disabled', { created: 1, dbDelta: 1, submissions: 1 });
    return result.created[0];
}

async function actual(options) {
    const { page, api, action, evidence, workbookPath, goto } = options;
    const cases = evidence.memberBulk;
    await goto(page, '/admin/members');
    const card = page.locator('.member-bulk-add'), rows = card.locator('.bulk-row');
    const count = async () => (await api(page, 'POST', '/api/admin/members/search', {})).totalCount;
    const auditCount = async () => (await api(page, 'GET', '/api/admin/audit?take=1000')).length;
    const password = `Roster-${crypto.randomUUID()}!`;
    const open = async () => {
        await card.getByRole('button', { name: '+ 부원 추가', exact: true }).click();
        await card.locator('#member-default-password').fill(password);
        await action(page, '/api/admin/members/import', 'POST', () => card.locator('#member-workbook').setInputFiles(workbookPath));
        await rows.nth(45).waitFor();
        assert.equal(await rows.count(), 46);
    };
    const baseline = await count();
    await open();
    // Do not persist input values, personal details, API payloads or real-roster screenshots.
    const students = await rows.locator('[data-field="student"]').evaluateAll(es => es.map(e => e.value));
    assert.ok(students.length === 46 && students.every(s => /^\d{9}$/.test(s)), '46 numeric student IDs preserved as decimal strings');
    assert.equal(await count(), baseline);
    cases.push({ name: 'actual workbook 46-row read-only preview / numeric IDs', passed: true, rows: 46, dbDelta: 0, workbookSha256: crypto.createHash('sha256').update(fs.readFileSync(workbookPath)).digest('hex') });
    const existing = [];
    for (let pageNumber = 1; ; pageNumber++) {
        const batch = await api(page, 'POST', '/api/admin/members/search', { page: pageNumber, pageSize: 100 });
        existing.push(...batch.items);
        if (existing.length >= batch.totalCount) break;
    }
    let collisions = 0;
    for (const member of existing.filter(m => students.includes(m.studentNumber))) {
        // Only this harness's disposable seeded DB. Never change the supplied workbook or duplicate rules.
        await api(page, 'PUT', `/api/admin/members/${member.userId}`, { name: member.name, department: member.department, studentNumber: `fixture-${crypto.randomUUID()}` });
        collisions++;
    }
    const auditsBefore = await auditCount();
    const committed = await action(page, '/api/admin/members/bulk', 'POST', () => card.getByRole('button', { name: '일괄 추가', exact: true }).click());
    assert.ok(committed.errors.length === 0 && committed.created.length === 46, 'whole actual workbook committed');
    assert.equal(await count() - baseline, 46); assert.equal(await auditCount() - auditsBefore, 46);
    assert.ok(committed.created.every(m => students.includes(m.studentNumber) && m.role === 'MEMBER'), 'actual workbook IDs and roles preserved');
    cases.push({ name: 'actual workbook atomic commit', passed: true, created: 46, dbDelta: 46, auditDelta: 46, isolatedSeedCollisionsRenamed: collisions });
    await open();
    const duplicate = await action(page, '/api/admin/members/bulk', 'POST', () => card.getByRole('button', { name: '일괄 추가', exact: true }).click());
    assert.ok(duplicate.created.length === 0 && new Set(duplicate.errors.map(e => e.row)).size === 46, 'all existing roster IDs rejected');
    assert.equal(await count() - baseline, 46); assert.equal(await auditCount() - auditsBefore, 46);
    cases.push({ name: 'actual workbook repeat blocked as whole / no updates', passed: true, dbDelta: 0, auditDelta: 0 });
    await card.getByRole('button', { name: '추가 취소', exact: true }).click();
    // Navigate away from personal data before the outer harness can take failure diagnostics.
    await goto(page, '/admin');
}
module.exports = { synthetic, actual, syntheticWorkbook };
