// Tiện ích gọi API cho script test/seed (Node >= 18, dùng fetch có sẵn, không cần npm install).

export const BASE = (process.env.API_BASE_URL || process.argv[2] || 'http://localhost:5088').replace(/\/$/, '');

export async function call(method, path, { token, body, raw } = {}) {
  const headers = { Accept: 'application/json' };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers['Content-Type'] = 'application/json; charset=utf-8';

  const res = await fetch(BASE + path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body)
  });

  if (raw) {
    const buf = Buffer.from(await res.arrayBuffer());
    return { status: res.status, headers: res.headers, buf, text: buf.toString('utf8') };
  }

  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  return { status: res.status, headers: res.headers, data };
}

export function client(user) {
  const token = user?.token;
  return {
    get: (path) => call('GET', path, { token }),
    post: (path, body) => call('POST', path, { token, body }),
    put: (path, body) => call('PUT', path, { token, body }),
    raw: (path) => call('GET', path, { token, raw: true })
  };
}

/** Đăng ký; nếu email đã tồn tại (409) thì đăng nhập. */
export async function registerOrLogin(email, displayName, password) {
  let r = await call('POST', '/api/auth/register', { body: { email, displayName, password } });
  if (r.status === 409) r = await call('POST', '/api/auth/login', { body: { email, password } });
  if (r.status !== 200) throw new Error(`Không tạo/đăng nhập được ${email}: ${r.status} ${JSON.stringify(r.data)}`);
  return { id: r.data.userId, email, name: r.data.displayName, token: r.data.token };
}
