// Small GitHub REST helper for the workflow scripts: GITHUB_TOKEN and GITHUB_REPOSITORY from the Actions environment.
export const repo = process.env.GITHUB_REPOSITORY ?? 'VlcekM/MeitouClient';
const token = process.env.GITHUB_TOKEN;

/** One request; a 422 (a label that already exists, say) is returned, not thrown. */
export async function api(path, { method = 'GET', body } = {}) {
  const headers = { Accept: 'application/vnd.github+json', 'User-Agent': 'meitou-workflows' };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body) headers['Content-Type'] = 'application/json';
  const r = await fetch(`https://api.github.com${path.replace('{repo}', repo)}`, { method, headers, body: body ? JSON.stringify(body) : undefined });
  if (!r.ok && r.status !== 422) throw new Error(`${method} ${path}: ${r.status} ${await r.text()}`);
  return r.status === 204 ? null : r.json();
}

/** Every page of a list endpoint. */
export async function all(path) {
  const items = [];
  for (let page = 1; ; page++) {
    const batch = await api(`${path}${path.includes('?') ? '&' : '?'}per_page=100&page=${page}`);
    items.push(...batch);
    if (batch.length < 100) return items;
  }
}
