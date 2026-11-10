// Creates or updates the labels in .github/labels.json (kept by hand; label-issue.mjs reads its area: labels). Labels not in the file are left alone.
import { readFileSync } from 'node:fs';
import { all, api } from './github.mjs';

const wanted = JSON.parse(readFileSync(new URL('../labels.json', import.meta.url), 'utf8'));
const have = new Map((await all('/repos/{repo}/labels')).map((l) => [l.name, l]));
for (const l of wanted) {
  const old = have.get(l.name);
  if (!old) {
    await api('/repos/{repo}/labels', { method: 'POST', body: l });
    console.log(`created ${l.name}`);
  } else if (old.color !== l.color || (old.description ?? '') !== l.description) {
    await api(`/repos/{repo}/labels/${encodeURIComponent(l.name)}`, { method: 'PATCH', body: { color: l.color, description: l.description } });
    console.log(`updated ${l.name}`);
  }
}
