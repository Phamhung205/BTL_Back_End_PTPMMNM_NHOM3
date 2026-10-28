// API test: nhập bản dịch có sẵn từ JSON và hồi quy lỗi ghi bản dịch sang khóa của dự án khác (Phạm Thế Đức).
// Chạy: node tests/api/duc-import-security.test.mjs [http://localhost:5088]
// Chỉ chạy trên CSDL thử nghiệm riêng; mỗi lần chạy tạo user/dự án mới.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { BASE, client, registerOrLogin } from './lib.mjs';

const run = Date.now().toString(36);
const PASSWORD = 'Test@123456';
const ctx = {};
const show = (r) => `HTTP ${r.status} ${JSON.stringify(r.data)}`;

const SOURCE = {
  common: { nav: { login: 'Đăng nhập', home: 'Trang chủ' } },
  login: { welcome: 'Chào mừng {name} quay lại!', error: 'Bạn còn {count} lần thử.' },
  cart: { empty: 'Giỏ hàng trống' }
};

async function list(user, base, locale) {
  const r = await client(user).get(`${base}/translations?locale=${locale}`);
  assert.equal(r.status, 200, show(r));
  return Object.fromEntries(r.data.map((row) => [row.key, row]));
}
const importJson = (user, data, extra = {}) =>
  client(user).post(`${ctx.base}/translations/import`, { localeCode: 'en', rawJson: JSON.stringify(data), ...extra });

test(`chuẩn bị hai dự án (backend: ${BASE})`, async () => {
  assert.equal((await client().get('/health')).status, 200, 'Backend hoặc MySQL chưa chạy');
  ctx.manager = await registerOrLogin(`imp.mgr.${run}@test.local`, 'Manager Import', PASSWORD);
  ctx.translator = await registerOrLogin(`imp.tr.${run}@test.local`, 'Translator Import', PASSWORD);
  ctx.reviewer = await registerOrLogin(`imp.rev.${run}@test.local`, 'Reviewer Import', PASSWORD);
  ctx.other = await registerOrLogin(`imp.other.${run}@test.local`, 'Manager khác', PASSWORD);

  const m = client(ctx.manager);
  let r = await m.post('/api/projects', { name: `Import ${run}`, slug: `import-${run}`, sourceLocaleCode: 'vi', targetLocaleCodes: ['en', 'zh'],
    members: [{ email: ctx.translator.email, role: 'translator' }, { email: ctx.reviewer.email, role: 'reviewer' }] });
  assert.equal(r.status, 201, show(r));
  ctx.base = `/api/projects/${r.data.id}`;
  r = await m.post(`${ctx.base}/source-versions/import`, { versionName: 'v1', rawJson: JSON.stringify(SOURCE) });
  assert.equal(r.status, 200, show(r));
  assert.equal(r.data.added, 5);

  r = await client(ctx.other).post('/api/projects', { name: `Khác ${run}`, slug: `other-${run}`, sourceLocaleCode: 'vi', targetLocaleCodes: ['en'] });
  assert.equal(r.status, 201, show(r));
  ctx.otherBase = `/api/projects/${r.data.id}`;
  r = await client(ctx.other).post(`${ctx.otherBase}/source-versions/import`, { versionName: 'v1', rawJson: JSON.stringify({ secret: { title: 'Bí mật' } }) });
  assert.equal(r.status, 200, show(r));
});

test('hồi quy: không ghi bản dịch vào khóa của dự án khác, khóa đã xóa hoặc không tồn tại', async () => {
  const foreign = (await list(ctx.other, ctx.otherBase, 'en'))['secret.title'];
  const m = client(ctx.manager);
  let r = await m.post(`${ctx.base}/translations`, { stringId: foreign.stringId, localeCode: 'en', value: 'WRITTEN_BY_NON_MEMBER', status: 'draft', expectedRowVersion: 0 });
  assert.equal(r.status, 404, 'khóa của dự án khác: ' + show(r));
  assert.equal((await list(ctx.other, ctx.otherBase, 'en'))['secret.title'].value, null, 'dự án khác không bị ghi dữ liệu');

  r = await m.post(`${ctx.base}/translations`, { stringId: 999999999, localeCode: 'en', value: 'x', status: 'draft', expectedRowVersion: 0 });
  assert.equal(r.status, 404, 'khóa không tồn tại: ' + show(r));

  // Khóa bị xóa ở nguồn v1b nhưng vẫn còn trong dữ liệu cũ phía client.
  const before = await list(ctx.manager, ctx.base, 'en');
  const withoutCart = structuredClone(SOURCE); delete withoutCart.cart;
  r = await m.post(`${ctx.base}/source-versions/import`, { versionName: 'v1b', rawJson: JSON.stringify(withoutCart) });
  assert.equal(r.status, 200, show(r));
  r = await m.post(`${ctx.base}/translations`, { stringId: before['cart.empty'].stringId, localeCode: 'en', value: 'Cart is empty', status: 'draft', expectedRowVersion: 0 });
  assert.equal(r.status, 404, 'khóa đã xóa: ' + show(r));
  r = await m.post(`${ctx.base}/source-versions/import`, { versionName: 'v1c', rawJson: JSON.stringify(SOURCE) });
  assert.equal(r.status, 200, show(r));
  assert.equal(r.data.changed, 1, 'khôi phục khóa cart.empty');
});

test('nhập bản dịch: báo cáo đúng, không tự duyệt, sai biến lưu nháp', async () => {
  const file = {
    common: { nav: { login: 'Log in', home: 'Home' } },
    login: { welcome: 'Welcome back!', error: 'You have {count} attempts left.' },
    cart: { empty: '   ' },
    footer: { copyright: '© 2026' }
  };
  const r = await importJson(ctx.manager, file);
  assert.equal(r.status, 200, show(r));
  assert.deepEqual({ total: r.data.total, created: r.data.created, updated: r.data.updated, notInSource: r.data.notInSource, empty: r.data.empty, placeholderMismatch: r.data.placeholderMismatch },
    { total: 6, created: 4, updated: 0, notInSource: 1, empty: 1, placeholderMismatch: 1 });
  assert.deepEqual(r.data.notInSourceKeys, ['footer.copyright']);
  assert.deepEqual(r.data.emptyKeys, ['cart.empty']);
  assert.deepEqual(r.data.placeholderIssues, [{ key: 'login.welcome', missing: ['{name}'], extra: [] }]);

  const rows = await list(ctx.manager, ctx.base, 'en');
  assert.equal(rows['common.nav.login'].status, 'translated', 'khớp biến → chờ duyệt, không tự duyệt');
  assert.equal(rows['login.error'].status, 'translated');
  assert.equal(rows['login.welcome'].status, 'draft', 'thiếu {name} → lưu nháp');
  assert.equal(rows['cart.empty'].status, 'missing', 'nội dung rỗng không được nhập');
  assert.equal(Object.values(rows).filter((row) => row.status === 'approved').length, 0);

  const history = await client(ctx.manager).get(`${ctx.base}/translations/${rows['common.nav.login'].translationId}/history`);
  assert.equal(history.data[0].action, 'imported');
  const validation = await client(ctx.manager).get(`${ctx.base}/releases/validate`);
  assert.equal(validation.data.ready, false, 'bản nhập chưa duyệt không đủ điều kiện phát hành');
});

test('nhập lại cùng tệp không tạo trùng; bản đang có chỉ bị thay khi chọn ghi đè', async () => {
  const same = { common: { nav: { login: 'Log in', home: 'Home' } }, login: { error: 'You have {count} attempts left.' } };
  let r = await importJson(ctx.manager, same);
  assert.equal(r.status, 200, show(r));
  assert.deepEqual([r.data.created, r.data.updated, r.data.unchanged], [0, 0, 3], 'nhập lại cùng nội dung không thay đổi gì');

  // Reviewer duyệt "Log in"; nhập lại nội dung khác không có ghi đè thì giữ bản đã duyệt.
  let rows = await list(ctx.manager, ctx.base, 'en');
  r = await client(ctx.reviewer).post(`${ctx.base}/translations/${rows['common.nav.login'].translationId}/approve`, { expectedRowVersion: rows['common.nav.login'].rowVersion });
  assert.equal(r.status, 200, show(r));
  r = await importJson(ctx.manager, { common: { nav: { login: 'Sign in' } } });
  assert.deepEqual([r.data.updated, r.data.keptExisting], [0, 1]);
  assert.deepEqual(r.data.keptExistingKeys, ['common.nav.login']);
  rows = await list(ctx.manager, ctx.base, 'en');
  assert.deepEqual([rows['common.nav.login'].value, rows['common.nav.login'].status], ['Log in', 'approved']);

  // Ghi đè: nội dung mới phải duyệt lại; người đang sửa từ phiên bản cũ bị 409, không ghi đè âm thầm.
  const seen = rows['common.nav.login'];
  r = await importJson(ctx.manager, { common: { nav: { login: 'Sign in' } } }, { overwrite: true });
  assert.deepEqual([r.data.updated, r.data.keptExisting], [1, 0]);
  rows = await list(ctx.manager, ctx.base, 'en');
  assert.deepEqual([rows['common.nav.login'].value, rows['common.nav.login'].status], ['Sign in', 'translated']);
  r = await client(ctx.manager).post(`${ctx.base}/translations`, { stringId: seen.stringId, localeCode: 'en', value: 'Login', status: 'translated', expectedRowVersion: seen.rowVersion });
  assert.equal(r.status, 409, 'lưu từ phiên bản trước khi nhập: ' + show(r));
  assert.equal(r.data.details.currentValue, 'Sign in');
});

test('nhập bản dịch: quyền và dữ liệu không hợp lệ', async () => {
  const data = { common: { nav: { home: 'Home page' } } };
  assert.equal((await importJson(ctx.translator, data)).status, 403, 'translator không nhập hàng loạt');
  assert.equal((await importJson(ctx.reviewer, data)).status, 403, 'reviewer không nhập');
  assert.equal((await importJson(ctx.other, data)).status, 403, 'người ngoài dự án');
  assert.equal((await importJson(ctx.manager, data, { status: 'approved' })).status, 400, 'không cho tự duyệt khi nhập');
  assert.equal((await importJson(ctx.manager, data, { localeCode: 'vi' })).status, 400, 'không nhập vào ngôn ngữ nguồn');
  assert.equal((await importJson(ctx.manager, data, { localeCode: 'fr' })).status, 400, 'ngôn ngữ chưa gắn vào dự án');
  assert.equal((await client(ctx.manager).post(`${ctx.base}/translations/import`, { localeCode: 'en', rawJson: '{"a": [1, 2]}' })).status, 400, 'mảng không được hỗ trợ');
  assert.equal((await client(ctx.manager).post(`${ctx.base}/translations/import`, { localeCode: 'en', rawJson: '{bad json' })).status, 400, 'JSON sai cú pháp');
  assert.equal((await client(ctx.manager).post(`${ctx.base}/translations/import`, { localeCode: 'en', rawJson: '{"a":"x","a.b":"y"}' })).status, 400, 'khóa cha/con xung đột');
  const rows = await list(ctx.manager, ctx.base, 'en');
  assert.equal(rows['common.nav.home'].value, 'Home', 'các yêu cầu bị từ chối không ghi dữ liệu');
});

test('báo cáo nhập phân biệt bản giữ nguyên, sai biến đã lưu nháp và chuỗi quá dài', async () => {
  const m = client(ctx.manager);
  let rows = await list(ctx.manager, ctx.base, 'en');
  const original = rows['login.error'];
  let r = await client(ctx.reviewer).post(`${ctx.base}/translations/${original.translationId}/approve`, { expectedRowVersion: original.rowVersion });
  assert.equal(r.status, 200, show(r));
  const approved = (await list(ctx.manager, ctx.base, 'en'))['login.error'];

  r = await importJson(ctx.manager, { login: { error: 'Try again.' } });
  assert.equal(r.status, 200, show(r));
  assert.deepEqual([r.data.keptExisting, r.data.placeholderMismatch, r.data.savedDraft], [1, 1, 0]);
  rows = await list(ctx.manager, ctx.base, 'en');
  assert.deepEqual([rows['login.error'].value, rows['login.error'].status, rows['login.error'].rowVersion], [approved.value, 'approved', approved.rowVersion]);

  r = await importJson(ctx.manager, { login: { error: 'Try again.' } }, { overwrite: true });
  assert.equal(r.status, 200, show(r));
  assert.deepEqual([r.data.updated, r.data.placeholderMismatch, r.data.savedDraft], [1, 1, 1]);
  assert.equal((await list(ctx.manager, ctx.base, 'en'))['login.error'].status, 'draft');
  r = await importJson(ctx.manager, { login: { error: 'Try again.' } }, { overwrite: true });
  assert.deepEqual([r.data.unchanged, r.data.savedDraft], [1, 0], 'nhập lại không tự báo có bản mới lưu nháp');

  r = await importJson(ctx.manager, { common: { nav: { home: 'x'.repeat(10001) } }, cart: { empty: 'Your cart is empty.' } }, { overwrite: true });
  assert.equal(r.status, 200, show(r));
  assert.deepEqual([r.data.created, r.data.updated, r.data.tooLong], [1, 0, 1]);
  assert.deepEqual(r.data.tooLongKeys, ['common.nav.home']);
  rows = await list(ctx.manager, ctx.base, 'en');
  assert.equal(rows['common.nav.home'].value, 'Home');
  assert.equal(rows['cart.empty'].value, 'Your cart is empty.');
  const keysBefore = await m.get(`${ctx.base}/translation-keys?status=all`);
  r = await m.post(`${ctx.base}/source-versions/import`, { versionName: 'too-long', rawJson: JSON.stringify({ x: 'x'.repeat(10001) }) });
  assert.equal(r.status, 400, 'nguồn vẫn phải tuân theo giới hạn 10.000 ký tự');
  assert.deepEqual((await m.get(`${ctx.base}/translation-keys?status=all`)).data, keysBefore.data, 'nguồn sai không thay đổi khóa hiện có');
});
