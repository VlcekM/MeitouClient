// Run manually with npm run refresh:workshop. The optional argument selects a different output snapshot.
import { readFile, rename, rm, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const data = new URL('../src/data/', import.meta.url);
const output = process.argv[2] ?? fileURLToPath(new URL('workshop.json', data));
const catalog = JSON.parse(await readFile(new URL('mods.json', data), 'utf8'));
const ids = catalog.map(mod => mod.workshopId).filter(id => id != null);
if (!ids.length || new Set(ids).size !== ids.length || !ids.every(id => typeof id === 'string' && /^[1-9]\d*$/.test(id))) {
  throw new Error('Expected unique Workshop IDs in the mod catalog.');
}

const body = new URLSearchParams({ itemcount: String(ids.length) });
ids.forEach((id, index) => body.set(`publishedfileids[${index}]`, id));
const response = await fetch('https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/', {
  method: 'POST', body, signal: AbortSignal.timeout(20_000),
});
if (!response.ok) throw new Error(`Steam returned HTTP ${response.status}.`);

const payload = (await response.json()).response;
const details = payload?.publishedfiledetails;
if (payload?.result !== 1 || payload.resultcount !== ids.length || !Array.isArray(details) || details.length !== ids.length) {
  throw new Error('Incomplete Workshop response.');
}
const snapshot = {};
for (const item of details) {
  const id = item.publishedfileid;
  if (!ids.includes(id) || Object.hasOwn(snapshot, id) || item.result !== 1 || item.consumer_app_id !== 233860) {
    throw new Error(`Unexpected or unavailable Kenshi Workshop item: ${id}`);
  }
  if (typeof item.title !== 'string' || !item.title.trim() || !URL.canParse(item.preview_url) || new URL(item.preview_url).protocol !== 'https:') {
    throw new Error(`Missing title or HTTPS preview for Workshop item ${id}.`);
  }
  snapshot[id] = { title: item.title.trim(), previewUrl: item.preview_url };
}

// Replace only a complete snapshot. Keep the editorial catalog independent of refreshes.
const temporary = `${output}.${randomUUID()}.tmp`;
try {
  await writeFile(temporary, JSON.stringify(snapshot, null, 2) + '\n', { flag: 'wx' });
  await rename(temporary, output);
} finally {
  await rm(temporary, { force: true });
}
console.log(`Refreshed ${ids.length} Workshop listings in ${output}.`);
