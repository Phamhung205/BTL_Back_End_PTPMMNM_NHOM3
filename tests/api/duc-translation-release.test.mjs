// API test module Translation / Review / Release (Phạm Thế Đức).
// Chạy: node tests/api/duc-translation-release.test.mjs [http://localhost:5088]
// Yêu cầu: backend đang chạy + MySQL/MariaDB (XAMPP). Mỗi lần chạy tạo user/dự án mới nên chạy lại được nhiều lần.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { BASE, client, registerOrLogin } from './lib.mjs';

const run = Date.now().toString(36);
const PASSWORD = 'Test@123456';
const ctx = {};

const SOURCE_V1 = {
  home: { title: 'Trang chủ', subtitle: 'Quản lý bản dịch' },
  login: { button: 'Đăng nhập' },
  welcome: 'Xin chào {name}, bạn có {count} tin nhắn mới'
};

const TARGET = {
  en: {
    'home.title': 'Home',
    'home.subtitle': 'Translation management',
    'login.button': 'Sign in',
    welcome: 'Hello {name}, you have {count} new messages'
  },
  zh: {
    'home.title': '主页',
    'home.subtitle': '翻译管理',
    'login.button': '登录',
    welcome: '你好 {name}，你有 {count} 条新消息'
  }
};

const sha256 = (buf) => createHash('sha256').update(buf).digest('hex');
const show = (r) => `HTTP ${r.status} ${JSON.stringify(r.data)}`;

async function list(user, locale, query = '') {
  const r = await client(user).get(`${ctx.base}/translations?locale=${locale}${query}`);
  assert.equal(r.status, 200, show(r));
  return r.data;
}

async function item(user, locale, key) {
  const found = (await list(user, locale)).find((x) => x.key === key);
  assert.ok(found, `không thấy khóa ${key}`);
  return found;
}

function save(user, it, locale, value, status, expectedRowVersion = it.rowVersion) {
  return client(user).post(`${ctx.base}/translations`, { stringId: it.stringId, localeCode: locale, value, status, expectedRowVersion });
}

test(`chuẩn bị dữ liệu (backend: ${BASE})`, async () => {
  const health = await client().get('/health');
  assert.equal(health.status, 200, 'Backend hoặc MySQL chưa chạy: ' + show(health));

  ctx.manager = await registerOrLogin(`mgr.${run}@test.local`, 'Quản lý Test', PASSWORD);
  ctx.tr1 = await registerOrLogin(`tr1.${run}@test.local`, 'Người dịch A', PASSWORD);
  ctx.tr2 = await registerOrLogin(`tr2.${run}@test.local`, 'Người dịch B', PASSWORD);
  ctx.rev = await registerOrLogin(`rev.${run}@test.local`, 'Người duyệt', PASSWORD);
  ctx.out = await registerOrLogin(`out.${run}@test.local`, 'Người ngoài', PASSWORD);

  const m = client(ctx.manager);
  let r = await m.post('/api/projects', { name: `Dự án test Đức ${run}`, slug: `duc-test-${run}`, sourceLocaleCode: 'vi' });
  assert.equal(r.status, 201, show(r));
  ctx.pid = r.data.id;
  ctx.base = `/api/projects/${ctx.pid}`;

  for (const code of ['en', 'zh']) {
    r = await m.post(`${ctx.base}/locales`, { localeCode: code });
    assert.equal(r.status, 200, show(r));
  }
  for (const [user, role] of [[ctx.tr1, 'translator'], [ctx.tr2, 'translator'], [ctx.rev, 'reviewer']]) {
    r = await m.post(`${ctx.base}/members`, { userId: user.id, role });
    assert.equal(r.status, 201, show(r));
  }

  r = await m.post(`${ctx.base}/source-versions/import`, { versionName: 'source-1.0.0', data: SOURCE_V1 });
  assert.equal(r.status, 200, show(r));
  assert.equal(r.data.added, 4);
});

test('danh sách bản dịch: dữ liệu, quyền và tham số', async () => {
  const items = await list(ctx.tr1, 'en');
  assert.equal(items.length, 4);
  assert.ok(items.every((x) => x.status === 'missing' && x.translationId === null));
  assert.deepEqual(items.find((x) => x.key === 'welcome').placeholders, ['{count}', '{name}']);

  assert.equal((await client(ctx.out).get(`${ctx.base}/translations?locale=en`)).status, 403, 'người ngoài dự án');
  assert.equal((await client().get(`${ctx.base}/translations?locale=en`)).status, 401, 'không có token');
  assert.equal((await client(ctx.tr1).get(`${ctx.base}/translations`)).status, 400, 'thiếu locale');
  assert.equal((await client(ctx.tr1).get(`${ctx.base}/translations?locale=vi`)).status, 400, 'ngôn ngữ nguồn');
  assert.equal((await client(ctx.tr1).get(`${ctx.base}/translations?locale=fr`)).status, 400, 'chưa gắn locale');
  assert.equal((await client(ctx.tr1).get(`${ctx.base}/translations?locale=en&status=abc`)).status, 400, 'status sai');

  // Tìm kiếm không phân biệt dấu/hoa thường theo collation; ký tự % được escape.
  assert.deepEqual((await list(ctx.tr1, 'en', '&q=' + encodeURIComponent('TRANG CHU'))).map((x) => x.key), ['home.title']);
  assert.equal((await list(ctx.tr1, 'en', '&q=' + encodeURIComponent('%'))).length, 0);
});

test('placeholder: gửi review thiếu/thừa biến bị chặn, lưu nháp được nhưng có cảnh báo', async () => {
  const welcome = await item(ctx.tr1, 'en', 'welcome');

  let r = await save(ctx.tr1, welcome, 'en', 'Hello {name}', 'translated');
  assert.equal(r.status, 400, show(r));
  assert.deepEqual(r.data.details.missing, ['{count}']);

  r = await save(ctx.tr1, welcome, 'en', 'Hello {name} {user}, {count}', 'translated');
  assert.equal(r.status, 400, show(r));
  assert.deepEqual(r.data.details.extra, ['{user}']);

  assert.equal((await save(ctx.tr1, welcome, 'en', '   ', 'draft')).status, 400, 'bản dịch rỗng');
  assert.equal((await save(ctx.tr1, welcome, 'en', 'Hello', 'approved')).status, 400, 'translator không tự duyệt');

  r = await save(ctx.tr1, welcome, 'en', 'Hello {name}', 'draft');
  assert.equal(r.status, 200, show(r));
  assert.equal(r.data.status, 'draft');
  assert.equal(r.data.rowVersion, 1);
  assert.equal(r.data.placeholderCheck.ok, false);
});

test('conflict: hai người cùng sửa một bản dịch → 409, không ghi đè âm thầm', async () => {
  const seenByA = await item(ctx.tr1, 'en', 'welcome');
  const seenByB = await item(ctx.tr2, 'en', 'welcome');
  assert.equal(seenByA.rowVersion, seenByB.rowVersion);

  let r = await save(ctx.tr1, seenByA, 'en', TARGET.en.welcome, 'translated');
  assert.equal(r.status, 200, show(r));
  assert.equal(r.data.rowVersion, seenByA.rowVersion + 1);

  r = await save(ctx.tr2, seenByB, 'en', 'Hi {name} ({count})', 'translated');
  assert.equal(r.status, 409, show(r));
  assert.equal(r.data.details.currentRowVersion, seenByA.rowVersion + 1);
  assert.equal(r.data.details.currentValue, TARGET.en.welcome);
  assert.equal(r.data.details.updatedBy, 'Người dịch A');
  assert.equal((await item(ctx.tr1, 'en', 'welcome')).value, TARGET.en.welcome, 'giá trị của A không bị ghi đè');

  // Cùng tạo mới một bản dịch chưa tồn tại.
  const title = await item(ctx.tr1, 'en', 'home.title');
  assert.equal((await save(ctx.tr1, title, 'en', 'Home', 'draft', 0)).status, 200);
  assert.equal((await save(ctx.tr2, title, 'en', 'Homepage', 'draft', 0)).status, 409);
});

test('phân quyền theo vai trò', async () => {
  const welcome = await item(ctx.rev, 'en', 'welcome');
  assert.equal((await save(ctx.rev, welcome, 'en', 'x', 'draft')).status, 403, 'reviewer không sửa bản dịch');
  assert.equal((await client(ctx.tr1).post(`${ctx.base}/translations/${welcome.translationId}/approve`, { expectedRowVersion: welcome.rowVersion })).status, 403, 'translator không duyệt');
  assert.equal((await client(ctx.tr1).put(`${ctx.base}/translations/assign`, { localeCode: 'en', stringIds: [welcome.stringId], userId: ctx.tr1.id })).status, 403, 'translator không phân công');
  assert.equal((await client(ctx.tr1).get(`${ctx.base}/translations?locale=en&assignee=${ctx.tr2.id}`)).status, 403, 'translator không lọc theo thành viên khác');
  assert.equal((await client(ctx.manager).get(`${ctx.base}/translations?locale=en&assignee=${ctx.tr2.id}`)).status, 200, 'manager được lọc theo thành viên trong dự án');
  assert.equal((await client(ctx.out).post(`${ctx.base}/translations/${welcome.translationId}/comments`, { body: 'hi' })).status, 403, 'người ngoài không bình luận');
});

test('phân công: hàng loạt cho khóa chưa dịch, chặn người không được giao', async () => {
  const m = client(ctx.manager);
  const button = await item(ctx.manager, 'en', 'login.button');
  const subtitle = await item(ctx.manager, 'en', 'home.subtitle');

  let r = await m.put(`${ctx.base}/translations/assign`, { localeCode: 'en', stringIds: [button.stringId, subtitle.stringId], userId: ctx.tr2.id });
  assert.equal(r.status, 200, show(r));
  assert.equal(r.data.assigned, 2);

  const mine = await list(ctx.tr2, 'en', '&assignee=me');
  assert.deepEqual(mine.map((x) => x.key), ['home.subtitle', 'login.button']);
  assert.ok(mine.every((x) => x.status === 'missing' && x.translationId !== null && x.assignedToName === 'Người dịch B'));
  assert.ok(!(await list(ctx.manager, 'en', '&assignee=none')).some((x) => x.key === 'login.button'));

  const assigned = await item(ctx.tr1, 'en', 'login.button');
  r = await save(ctx.tr1, assigned, 'en', 'Sign in', 'translated');
  assert.equal(r.status, 403, 'A không sửa khóa đã giao cho B: ' + show(r));
  r = await save(ctx.tr2, assigned, 'en', 'Sign in', 'translated');
  assert.equal(r.status, 200, show(r));

  assert.equal((await m.put(`${ctx.base}/translations/assign`, { localeCode: 'en', stringIds: [button.stringId], userId: ctx.rev.id })).status, 400, 'không giao cho reviewer');
  assert.equal((await m.put(`${ctx.base}/translations/assign`, { localeCode: 'en', stringIds: [button.stringId], userId: ctx.out.id })).status, 400, 'không giao cho người ngoài');
  assert.equal((await m.put(`${ctx.base}/translations/assign`, { localeCode: 'en', stringIds: [999999999], userId: ctx.tr1.id })).status, 400, 'khóa không tồn tại');

  const title = await item(ctx.manager, 'en', 'home.title');
  r = await m.put(`${ctx.base}/translations/${title.translationId}/assign`, { userId: ctx.tr1.id, expectedRowVersion: title.rowVersion + 5 });
  assert.equal(r.status, 409, 'phân công với rowVersion cũ: ' + show(r));
  r = await m.put(`${ctx.base}/translations/${title.translationId}/assign`, { userId: ctx.tr1.id, expectedRowVersion: title.rowVersion });
  assert.equal(r.status, 200, show(r));
  assert.equal(r.data.assignedToName, 'Người dịch A');
});

test('bình luận trên bản dịch', async () => {
  const welcome = await item(ctx.rev, 'en', 'welcome');
  const c = client(ctx.rev);
  const body = 'Nên giữ nguyên "new messages" 👍';

  let r = await c.post(`${ctx.base}/translations/${welcome.translationId}/comments`, { body });
  assert.equal(r.status, 201, show(r));
  assert.equal(r.data.body, body);
  assert.equal(r.data.userName, 'Người duyệt');

  r = await client(ctx.tr1).get(`${ctx.base}/translations/${welcome.translationId}/comments`);
  assert.equal(r.status, 200);
  assert.equal(r.data.length, 1);
  assert.equal((await item(ctx.tr1, 'en', 'welcome')).commentCount, 1);

  assert.equal((await c.post(`${ctx.base}/translations/${welcome.translationId}/comments`, { body: '  ' })).status, 400);
  assert.equal((await c.get(`${ctx.base}/translations/999999999/comments`)).status, 404);
});

test('review: approve/reject, trạng thái và conflict khi duyệt', async () => {
  const r1 = client(ctx.rev);
  const title = await item(ctx.rev, 'en', 'home.title');
  let r = await r1.post(`${ctx.base}/translations/${title.translationId}/approve`, { expectedRowVersion: title.rowVersion });
  assert.equal(r.status, 400, 'không duyệt bản nháp: ' + show(r));

  const welcome = await item(ctx.rev, 'en', 'welcome');
  assert.equal(welcome.status, 'translated');
  r = await r1.post(`${ctx.base}/translations/${welcome.translationId}/reject`, { expectedRowVersion: welcome.rowVersion });
  assert.equal(r.status, 400, 'từ chối phải có lý do');

  r = await r1.post(`${ctx.base}/translations/${welcome.translationId}/reject`, { note: 'Thiếu dấu phẩy', expectedRowVersion: welcome.rowVersion });
  assert.equal(r.status, 200, show(r));
  assert.equal(r.data.status, 'needs_review');

  r = await r1.post(`${ctx.base}/translations/${welcome.translationId}/approve`, { expectedRowVersion: welcome.rowVersion });
  assert.equal(r.status, 409, 'duyệt với rowVersion cũ');

  let current = await item(ctx.tr1, 'en', 'welcome');
  assert.equal(current.status, 'needs_review');
  assert.equal(current.lastReviewDecision, 'rejected');
  assert.equal(current.lastReviewNote, 'Thiếu dấu phẩy');

  r = await save(ctx.tr1, current, 'en', TARGET.en.welcome, 'translated');
  assert.equal(r.status, 200, show(r));
  r = await r1.post(`${ctx.base}/translations/${welcome.translationId}/approve`, { note: 'OK', expectedRowVersion: r.data.rowVersion });
  assert.equal(r.status, 200, show(r));
  assert.equal(r.data.status, 'approved');

  current = await item(ctx.rev, 'en', 'welcome');
  r = await r1.post(`${ctx.base}/translations/${welcome.translationId}/approve`, { expectedRowVersion: current.rowVersion });
  assert.equal(r.status, 400, 'đã duyệt rồi');

  r = await client(ctx.tr1).get(`${ctx.base}/translations/${welcome.translationId}/history`);
  assert.equal(r.status, 200);
  assert.deepEqual(r.data.map((h) => h.action), ['approved', 'submitted', 'rejected', 'submitted', 'saved_draft']);
});

test('kiểm tra phát hành khi còn thiếu bản dịch', async () => {
  const r = await client(ctx.manager).get(`${ctx.base}/releases/validate`);
  assert.equal(r.status, 200, show(r));
  assert.equal(r.data.ready, false);
  assert.equal(r.data.activeStrings, 4);
  assert.ok(r.data.problems.some((p) => p.startsWith('en:')));
  assert.ok(r.data.problems.some((p) => p.startsWith('zh:')));
  assert.equal((await client(ctx.manager).post(`${ctx.base}/releases`, { versionName: '0.9.0' })).status, 400);
});

test('dịch & duyệt toàn bộ en + zh (Unicode)', async () => {
  for (const locale of ['en', 'zh']) {
    for (const [key, value] of Object.entries(TARGET[locale])) {
      const it = await item(ctx.manager, locale, key);
      if (it.status === 'approved') continue;
      const translator = it.assignedTo === ctx.tr2.id ? ctx.tr2 : ctx.tr1;
      let r = await save(translator, it, locale, value, 'translated');
      assert.equal(r.status, 200, `${locale}/${key}: ${show(r)}`);
      r = await client(ctx.rev).post(`${ctx.base}/translations/${r.data.id}/approve`, { expectedRowVersion: r.data.rowVersion });
      assert.equal(r.status, 200, `${locale}/${key}: ${show(r)}`);
    }
  }
  const r = await client(ctx.manager).get(`${ctx.base}/releases/validate`);
  assert.equal(r.data.ready, true, show(r));
  assert.deepEqual(r.data.problems, []);
});

test('phát hành: quyền, tên phiên bản, snapshot JSON lồng nhau, SHA-256', async () => {
  const m = client(ctx.manager);
  assert.equal((await client(ctx.tr1).post(`${ctx.base}/releases`, { versionName: '1.0.0' })).status, 403, 'translator không phát hành');
  for (const bad of ['', '../hack', '1.0.', 'a b']) {
    assert.equal((await m.post(`${ctx.base}/releases`, { versionName: bad })).status, 400, `tên phiên bản "${bad}"`);
  }

  let r = await m.post(`${ctx.base}/releases`, { versionName: '1.0.0', note: 'Bản đầu tiên' });
  assert.equal(r.status, 201, show(r));
  ctx.release = r.data;
  assert.equal(r.data.keyCount, 4);
  assert.equal(r.data.sourceVersionName, 'source-1.0.0');
  assert.deepEqual(r.data.files.map((f) => f.localeCode), ['vi', 'en', 'zh']);
  assert.equal(r.data.files[0].isSource, true);

  const en = await m.raw(`${ctx.base}/releases/${ctx.release.id}/files/en`);
  assert.equal(en.status, 200);
  assert.deepEqual(JSON.parse(en.text), {
    home: { subtitle: 'Translation management', title: 'Home' },
    login: { button: 'Sign in' },
    welcome: 'Hello {name}, you have {count} new messages'
  });
  assert.equal(en.headers.get('x-content-sha256'), sha256(en.buf));
  assert.equal(ctx.release.files.find((f) => f.localeCode === 'en').sha256, sha256(en.buf));
  ctx.enSha = sha256(en.buf);

  const zh = await m.raw(`${ctx.base}/releases/${ctx.release.id}/files/zh`);
  assert.ok(zh.text.includes('主页') && zh.text.includes('你好 {name}'), 'Unicode giữ nguyên, không bị \\uXXXX');
  const vi = await m.raw(`${ctx.base}/releases/${ctx.release.id}/files/vi`);
  assert.equal(JSON.parse(vi.text).home.title, 'Trang chủ');

  assert.equal((await m.post(`${ctx.base}/releases`, { versionName: '1.0.0' })).status, 409, 'trùng phiên bản');
  assert.equal((await m.raw(`${ctx.base}/releases/${ctx.release.id}/files/fr`)).status, 404);
  assert.equal((await client(ctx.out).raw(`${ctx.base}/releases/${ctx.release.id}/files/en`)).status, 403);

  r = await client(ctx.tr1).get(`${ctx.base}/releases`);
  assert.equal(r.status, 200);
  assert.equal(r.data.length, 1);
  assert.equal(r.data[0].files.length, 3);
});

test('release cũ không đổi khi bản dịch bị sửa hoặc nguồn thay đổi', async () => {
  const m = client(ctx.manager);
  const title = await item(ctx.tr1, 'en', 'home.title');
  let r = await save(ctx.tr1, title, 'en', 'Homepage', 'translated');
  assert.equal(r.status, 200, show(r));
  assert.equal((await m.get(`${ctx.base}/releases/validate`)).data.ready, false);

  // Mạnh: import phiên bản nguồn mới, đổi chuỗi welcome => bản dịch liên quan chuyển needs_review.
  r = await m.post(`${ctx.base}/source-versions/import`, {
    versionName: 'source-1.1.0',
    data: { ...SOURCE_V1, welcome: 'Chào {name}, bạn có {count} tin nhắn' }
  });
  assert.equal(r.status, 200, show(r));
  assert.equal(r.data.changed, 1);
  assert.equal((await item(ctx.tr1, 'en', 'welcome')).status, 'needs_review');
  assert.equal((await item(ctx.tr1, 'zh', 'welcome')).status, 'needs_review');

  const en = await m.raw(`${ctx.base}/releases/${ctx.release.id}/files/en`);
  assert.equal(sha256(en.buf), ctx.enSha, 'nội dung release 1.0.0 giữ nguyên');
  assert.equal(JSON.parse(en.text).home.title, 'Home');

  r = await m.get(`${ctx.base}/releases/${ctx.release.id}`);
  assert.equal(r.status, 200);
  assert.deepEqual(r.data.files.map((f) => f.sha256), ctx.release.files.map((f) => f.sha256));
});

test('tải gói .zip và thống kê tiến độ', async () => {
  const m = client(ctx.manager);
  const zip = await m.raw(`${ctx.base}/releases/${ctx.release.id}/download`);
  assert.equal(zip.status, 200);
  assert.match(zip.headers.get('content-type'), /application\/zip/);
  assert.equal(zip.buf.subarray(0, 2).toString(), 'PK');
  for (const name of ['manifest.json', 'vi.json', 'en.json', 'zh.json']) assert.ok(zip.buf.includes(name), name);

  const r = await m.get(`${ctx.base}/translations/stats`);
  assert.equal(r.status, 200);
  const en = r.data.find((x) => x.localeCode === 'en');
  assert.deepEqual(
    { total: en.total, approved: en.approved, translated: en.translated, needsReview: en.needsReview, missing: en.missing },
    { total: 4, approved: 2, translated: 1, needsReview: 1, missing: 0 });
});
