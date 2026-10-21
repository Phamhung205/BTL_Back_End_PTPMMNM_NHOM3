// Tạo dữ liệu demo cho luồng Dịch -> Review -> Approve -> Release (module Phạm Thế Đức).
// Chạy: node tests/api/seed-demo.mjs [http://localhost:5088]
// Chạy lại nhiều lần được: tài khoản/dự án đã có thì dùng lại.
// CHỈ dùng cho môi trường local/demo — mật khẩu demo không phải mật khẩu thật.
import { BASE, client, registerOrLogin } from './lib.mjs';

const PASSWORD = 'Demo@123';
const SLUG = 'demo-app';

const SOURCE = {
  app: { name: 'Quản lý bản dịch', tagline: 'Dịch nhanh, phát hành an toàn' },
  home: { title: 'Trang chủ', welcome: 'Xin chào {name}!', unread: 'Bạn có {count} thông báo chưa đọc' },
  login: { title: 'Đăng nhập', email: 'Địa chỉ email', password: 'Mật khẩu', submit: 'Đăng nhập', forgot: 'Quên mật khẩu?' },
  errors: { required: '{field} là bắt buộc', network: 'Không thể kết nối máy chủ' }
};

function expect(r, ok, label) {
  if (!ok.includes(r.status)) throw new Error(`${label}: HTTP ${r.status} ${JSON.stringify(r.data)}`);
  return r;
}

const manager = await registerOrLogin('manager.demo@example.com', 'Quản lý Demo', PASSWORD);
const translator = await registerOrLogin('translator.demo@example.com', 'Người dịch Demo', PASSWORD);
const translator2 = await registerOrLogin('translator2.demo@example.com', 'Người dịch 2 Demo', PASSWORD);
const reviewer = await registerOrLogin('reviewer.demo@example.com', 'Người duyệt Demo', PASSWORD);
const m = client(manager);

let r = await m.post('/api/projects', { name: 'Ứng dụng Demo', slug: SLUG, sourceLocaleCode: 'vi' });
let projectId;
if (r.status === 201) projectId = r.data.id;
else {
  expect(r, [409], 'Tạo dự án');
  const mine = expect(await m.get('/api/projects'), [200], 'Danh sách dự án').data;
  const found = mine.find((p) => p.slug === SLUG);
  if (!found) throw new Error(`Slug ${SLUG} đã tồn tại nhưng manager.demo không phải thành viên.`);
  projectId = found.id;
}
const base = `/api/projects/${projectId}`;

for (const code of ['en', 'zh']) expect(await m.post(`${base}/locales`, { localeCode: code }), [200, 409], `Gắn ngôn ngữ ${code}`);
for (const [user, role] of [[translator, 'translator'], [translator2, 'translator'], [reviewer, 'reviewer']]) {
  expect(await m.post(`${base}/members`, { userId: user.id, role }), [200], `Thêm ${user.email}`);
}
expect(await m.post(`${base}/source-versions/import`, { versionName: 'source-1.0.0', data: SOURCE }), [200, 409], 'Import nguồn');

// Một ít dữ liệu có sẵn để màn hình có đủ các trạng thái.
const items = expect(await m.get(`${base}/translations?locale=en`), [200], 'Danh sách bản dịch').data;
const byKey = Object.fromEntries(items.map((x) => [x.key, x]));
const save = (user, key, value, status) => client(user).post(`${base}/translations`, {
  stringId: byKey[key].stringId, localeCode: 'en', value, status, expectedRowVersion: byKey[key].rowVersion
});

if (byKey['app.name'].status === 'missing') {
  const saved = expect(await save(translator, 'app.name', 'Translation Manager', 'translated'), [200], 'Dịch app.name');
  expect(await client(reviewer).post(`${base}/translations/${saved.data.id}/approve`, { expectedRowVersion: saved.data.rowVersion }), [200], 'Duyệt app.name');
}
if (byKey['app.tagline'].status === 'missing') expect(await save(translator, 'app.tagline', 'Translate fast, release safely', 'translated'), [200], 'Dịch app.tagline');
if (byKey['home.title'].status === 'missing') expect(await save(translator, 'home.title', 'Home', 'draft'), [200], 'Nháp home.title');

const loginKeys = items.filter((x) => x.key.startsWith('login.') && !x.assignedTo).map((x) => x.stringId);
if (loginKeys.length) {
  expect(await m.put(`${base}/translations/assign`, { localeCode: 'en', stringIds: loginKeys, userId: translator2.id }), [200], 'Phân công login.*');
}

console.log(`
Đã tạo dữ liệu demo trên ${BASE}
Dự án: "Ứng dụng Demo" (id ${projectId}, slug ${SLUG}) — nguồn vi, đích en + zh, ${items.length} khóa

Tài khoản (mật khẩu chung: ${PASSWORD})
  Manager     : manager.demo@example.com
  Translator  : translator.demo@example.com
  Translator 2: translator2.demo@example.com   (được giao các khóa login.* của en)
  Reviewer    : reviewer.demo@example.com
`);
