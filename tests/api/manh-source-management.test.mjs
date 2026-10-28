// Integration tests: real ASP.NET API + MySQL/MariaDB. Creates isolated users/projects per run.
// node tests/api/manh-source-management.test.mjs [http://localhost:5088]
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { BASE, call, client, registerOrLogin } from './lib.mjs';

const run = Date.now().toString(36);
const ctx = {};
const source = {
  home: { title: 'Ứng dụng đọc truyện', welcome: 'Xin chào {name}' },
  stable: 'Nội dung không đổi', removed: 'Khóa sắp bị xóa', draft: 'Nguồn của bản nháp',
  Title: 'Chữ hoa', title: 'Chữ thường', ten: 'Không dấu', 'tên': 'Có dấu',
  labels: Object.fromEntries(Array.from({ length: 105 }, (_, i) => ['label' + i.toString().padStart(3, '0'), 'Nhãn số ' + i]))
};
const keyCount = 114;
const message = r => 'HTTP ' + r.status + ' ' + JSON.stringify(r.data);
const expect = (r, status) => { assert.equal(r.status, status, message(r)); return r.data; };
const get = path => client(ctx.manager).get(path);
const post = (path, data) => client(ctx.manager).post(path, data);
const update = (path, data) => client(ctx.manager).put(path, data);
const remove = path => call('DELETE', path, { token: ctx.manager.token });
const importSource = (versionName, data = source, base = ctx.base) => post(base + '/source-versions/import', { versionName, data });
const keys = async (base = ctx.base) => expect(await get(base + '/translation-keys'), 200);
const translations = async () => expect(await get(ctx.base + '/translations?locale=en'), 200);
async function save(key, value, status = 'translated', base = ctx.base) {
  const items = expect(await get(base + '/translations?locale=en'), 200);
  const item = items.find(k => k.key === key);
  assert.ok(item, key);
  const result = expect(await post(base + '/translations', { stringId: item.stringId, localeCode: 'en', value, status, expectedRowVersion: item.rowVersion }), 200);
  return result;
}
async function approve(key, value, base = ctx.base) {
  const row = await save(key, value, 'translated', base);
  return expect(await post(base + '/translations/' + row.id + '/approve', { expectedRowVersion: row.rowVersion }), 200);
}

test('chuẩn bị hai dự án, ba ngôn ngữ và các vai trò', async () => {
  expect(await call('GET', '/health'), 200);
  ctx.manager = await registerOrLogin(`manh.mgr.${run}@test.local`, 'Quản lý Mạnh', 'Test@123456');
  ctx.translator = await registerOrLogin(`manh.tr.${run}@test.local`, 'Người dịch', 'Test@123456');
  ctx.outsider = await registerOrLogin(`manh.out.${run}@test.local`, 'Ngoài dự án', 'Test@123456');
  const first = expect(await post('/api/projects', { name: 'Nguồn Mạnh ' + run, slug: 'manh-' + run, sourceLocaleCode: 'vi' }), 201);
  const second = expect(await post('/api/projects', { name: 'Dự án độc lập ' + run, slug: 'manh-other-' + run, sourceLocaleCode: 'vi' }), 201);
  ctx.base = '/api/projects/' + first.id; ctx.other = '/api/projects/' + second.id;
  for (const base of [ctx.base, ctx.other]) expect(await post(base + '/locales', { localeCode: 'en' }), 200);
  expect(await post(ctx.base + '/locales', { localeCode: 'zh' }), 200);
  expect(await post(ctx.base + '/members', { userId: ctx.translator.id, role: 'translator' }), 201);
});

test('quyền API: chưa đăng nhập, ngoài dự án, translator chỉ đọc', async () => {
  for (const path of ['/locales', '/translation-keys', '/source-versions', '/source-versions/1/changes']) {
    expect(await call('GET', ctx.base + path), 401);
    expect(await client(ctx.outsider).get(ctx.base + path), 403);
  }
  for (const path of ['/locales', '/translation-keys', '/source-versions']) expect(await client(ctx.translator).get(ctx.base + path), 200);
  expect(await client(ctx.translator).post(ctx.base + '/locales', { localeCode: 'ja' }), 403);
  expect(await client(ctx.translator).put(ctx.base + '/locales/en', { name: 'Không được phép' }), 403);
  expect(await call('DELETE', ctx.base + '/locales/en', { token: ctx.translator.token }), 403);
  expect(await client(ctx.translator).post(ctx.base + '/source-versions/import', { versionName: 'forbidden', data: source }), 403);
});

test('ngôn ngữ: tên riêng từng dự án, validation và gỡ an toàn', async () => {
  expect(await update(ctx.base + '/locales/en', { name: 'Tiếng Anh cho truyện' }), 200);
  assert.equal(expect(await get(ctx.base + '/locales'), 200).find(l => l.code === 'en').name, 'Tiếng Anh cho truyện');
  assert.equal(expect(await get(ctx.other + '/locales'), 200).find(l => l.code === 'en').name, 'English');
  expect(await update(ctx.base + '/locales/vi', { name: 'Đổi nguồn' }), 400);
  expect(await remove(ctx.base + '/locales/vi'), 400);
  expect(await update(ctx.base + '/locales/en', { name: ' ' }), 400);
  expect(await update(ctx.base + '/locales/en', { name: 'x'.repeat(81) }), 400);
  expect(await post(ctx.base + '/locales', { localeCode: '../escape' }), 400);
  expect(await post(ctx.base + '/locales', { localeCode: 'EN' }), 409);
  expect(await post(ctx.base + '/locales', { localeCode: 'ja' }), 200);
  expect(await remove(ctx.base + '/locales/ja'), 204);
  expect(await remove(ctx.base + '/locales/ja'), 404);
});

test('JSON không hợp lệ không tạo phiên bản hoặc ghi một phần dữ liệu', async () => {
  for (const data of [null, [], {}, { a: [] }, { a: 1 }, { a: false }, { a: {} }, { 'a.b': 'x', a: { b: 'y' } }, { 'x ': 'x' }, { ['x'.repeat(501)]: 'x' }, { a: 'x'.repeat(10001) }]) {
    expect(await importSource('invalid', data), 400);
  }
  expect(await importSource('x'.repeat(121)), 400);
  expect(await post(ctx.base + '/source-versions/import', { versionName: 'missing-data' }), 400);
  const duplicate = await fetch(BASE + ctx.base + '/source-versions/import', { method: 'POST', headers: { Authorization: 'Bearer ' + ctx.manager.token, 'Content-Type': 'application/json' }, body: '{"versionName":"duplicate-key","data":{"group":{"a":"x"},"group":{"b":"y"}}}' });
  assert.equal(duplicate.status, 400);
  const oversized = await importSource('oversized', { a: 'x'.repeat(3 * 1024 * 1024) });
  assert.ok([400, 413].includes(oversized.status), message(oversized));
  assert.equal(expect(await get(ctx.base + '/source-versions'), 200).length, 0);
  assert.equal((await keys()).length, 0);
});

test('import hơn 100 khóa: Unicode, lồng nhau và phân biệt hoa/thường/dấu', async () => {
  ctx.v1 = expect(await importSource('source-v1'), 200);
  assert.equal(ctx.v1.total, keyCount); assert.equal(ctx.v1.added, keyCount);
  const rows = await keys();
  assert.equal(rows.length, keyCount);
  for (const key of ['Title', 'title', 'ten', 'tên', 'home.welcome', 'labels.label104']) assert.ok(rows.find(r => r.key === key));
  assert.equal(rows.find(r => r.key === 'home.welcome').sourceText, 'Xin chào {name}');
  assert.equal(expect(await get(ctx.base + '/translation-keys?q=label104&status=active'), 200).length, 1);
  expect(await get(ctx.base + '/translation-keys?status=invalid'), 400);
  ctx.versions = expect(await get(ctx.base + '/source-versions'), 200);
  assert.equal(ctx.versions[0].keyCount, keyCount); assert.equal(ctx.versions[0].importedBy, 'Quản lý Mạnh');
});

test('import cùng nội dung không nhân đôi, tên phiên bản trùng không đổi dữ liệu', async () => {
  const old = await keys();
  ctx.same = expect(await importSource('source-same'), 200);
  assert.equal(ctx.same.added, 0); assert.equal(ctx.same.unchanged, keyCount);
  assert.deepEqual((await keys()).map(r => r.id), old.map(r => r.id));
  assert.equal(expect(await get(ctx.base + '/source-versions/' + ctx.same.versionId + '/changes'), 200).length, 0);
  expect(await importSource('source-same', { wrong: 'Không được thay thế' }), 409);
  assert.equal((await keys()).length, keyCount);
});

test('nguồn đổi chỉ ảnh hưởng bản dịch liên quan; nháp cũ bị phát hiện xung đột', async () => {
  await approve('home.welcome', 'Hello {name}');
  await approve('stable', 'Unchanged');
  await approve('removed', 'To be removed');
  await save('draft', 'Draft text', 'draft');
  ctx.before = await translations();
  const before = key => ctx.before.find(r => r.key === key);
  ctx.v2data = structuredClone(source);
  ctx.v2data.home.welcome = 'Chào mừng {name}'; ctx.v2data.draft = 'Nguồn nháp mới'; delete ctx.v2data.removed; ctx.v2data.extra = 'Khóa mới';
  ctx.v2 = expect(await importSource('source-v2', ctx.v2data), 200);
  assert.equal(ctx.v2.added, 1); assert.equal(ctx.v2.changed, 2); assert.equal(ctx.v2.deleted, 1);
  const after = await translations(), find = key => after.find(r => r.key === key);
  assert.equal(find('home.welcome').status, 'needs_review');
  assert.equal(find('home.welcome').rowVersion, before('home.welcome').rowVersion + 1);
  assert.equal(find('stable').status, 'approved'); assert.equal(find('stable').rowVersion, before('stable').rowVersion);
  assert.equal(find('draft').status, 'draft'); assert.equal(find('draft').rowVersion, before('draft').rowVersion + 1);
  expect(await post(ctx.base + '/translations', { stringId: before('draft').stringId, localeCode: 'en', value: 'Stale draft', status: 'draft', expectedRowVersion: before('draft').rowVersion }), 409);
  assert.equal((await keys()).find(r => r.key === 'removed').status, 'deleted');
  const changes = expect(await get(ctx.base + '/source-versions/' + ctx.v2.versionId + '/changes'), 200);
  assert.equal(changes.length, 4); assert.ok(!changes.some(c => c.key === 'stable'));
  expect(await remove(ctx.base + '/locales/en'), 409);
  assert.equal(expect(await get(ctx.base + '/translations/stats'), 200).find(s => s.localeCode === 'en').localeName, 'Tiếng Anh cho truyện');
});

test('khôi phục khóa không nhân đôi và so sánh snapshot giữa hai phiên bản', async () => {
  const deleted = (await keys()).find(k => k.key === 'removed');
  ctx.v3data = { ...ctx.v2data, removed: source.removed };
  ctx.v3 = expect(await importSource('source-v3', ctx.v3data), 200);
  assert.equal(ctx.v3.changed, 1);
  const restored = (await keys()).find(k => k.key === 'removed');
  assert.equal(restored.id, deleted.id); assert.equal(restored.status, 'active');
  const changes = expect(await get(ctx.base + '/source-versions/' + ctx.v3.versionId + '/changes'), 200);
  assert.equal(changes.length, 1); assert.equal(changes[0].type, 'restored');
  const compared = expect(await get(ctx.base + '/source-versions/' + ctx.v3.versionId + '/changes?compareToVersionId=' + ctx.v1.versionId), 200);
  assert.equal(compared.length, 3); assert.ok(!compared.some(c => c.key === 'removed'));
  assert.equal(expect(await get(ctx.base + '/source-versions/' + ctx.v1.versionId + '/changes'), 200).length, keyCount);
  expect(await get(ctx.base + '/source-versions/9223372036854775807/changes'), 404);
  expect(await get(ctx.other + '/source-versions/' + ctx.v1.versionId + '/changes'), 404);
  expect(await get(ctx.other + '/source-versions/' + ctx.v1.versionId + '/changes?compareToVersionId=' + ctx.v1.versionId), 404);
});

test('hai lần import đồng thời được tuần tự hóa, nguồn khớp phiên bản cuối', async () => {
  const payloads = [ { ...ctx.v3data, concurrent: 'Giá trị A' }, { ...ctx.v3data, concurrent: 'Giá trị B' } ];
  const results = await Promise.all(payloads.map((data, i) => importSource('parallel-' + i, data)));
  results.forEach(r => expect(r, 200));
  assert.equal(results.reduce((n, r) => n + r.data.added, 0), 1);
  const latest = expect(await get(ctx.base + '/source-versions'), 200)[0];
  const rows = await keys();
  assert.equal(rows.filter(k => k.key === 'concurrent').length, 1);
  assert.ok(rows.filter(k => k.status === 'active').every(k => k.sourceVersionId === latest.id));
  const changes = expect(await get(ctx.base + '/source-versions/' + latest.id + '/changes'), 200);
  assert.equal(rows.find(k => k.key === 'concurrent').sourceText, changes.find(c => c.key === 'concurrent').newValue);
});

test('release bị chặn khi nguồn đổi; gói cũ và Unicode vẫn giữ nguyên', async () => {
  const first = { home: { title: 'Trang chủ', welcome: 'Xin chào {name}' } };
  expect(await importSource('release-source-v1', first, ctx.other), 200);
  await approve('home.title', 'Home', ctx.other); await approve('home.welcome', 'Hello {name}', ctx.other);
  assert.equal(expect(await get(ctx.other + '/releases/validate'), 200).ready, true);
  const release = expect(await post(ctx.other + '/releases', { versionName: '1.0.0' }), 201);
  const old = await client(ctx.manager).raw(ctx.other + '/releases/' + release.id + '/files/vi');
  assert.equal(old.status, 200); assert.equal(JSON.parse(old.text).home.title, 'Trang chủ');
  expect(await importSource('release-source-v2', { home: { ...first.home, welcome: 'Chào {name}!' } }, ctx.other), 200);
  assert.equal(expect(await get(ctx.other + '/releases/validate'), 200).ready, false);
  expect(await post(ctx.other + '/releases', { versionName: '2.0.0' }), 400);
  const preserved = await client(ctx.manager).raw(ctx.other + '/releases/' + release.id + '/files/vi');
  assert.equal(preserved.text, old.text);
  expect(await remove(ctx.other + '/locales/en'), 409);
});
