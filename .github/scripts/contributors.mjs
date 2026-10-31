// Renders assets/branding/contributors.png for the README: the core team on top (the creator first), then everyone else by commit count.
// Run by the nightly workflow; locally: node .github/scripts/contributors.mjs (GITHUB_TOKEN optional, ANTHROPIC_API_KEY for new areas).
import { existsSync, readFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import Anthropic from '@anthropic-ai/sdk';
import { betaZodOutputFormat } from '@anthropic-ai/sdk/helpers/beta/zod';
import { Resvg } from '@resvg/resvg-js';
import { z } from 'zod';
import { all, api } from './github.mjs';

const creator = 'VlcekM';
const core = [creator, 'Bazouz660', 'Demitrix2k'];
// Commit authors that are not people on the project: Co-Authored-By trailers map to these accounts.
const excluded = new Set(['claude']);
// What a contributor outside the core team is known for, shown under their name.
const roles = { spacecowboyserver: 'macOS port' };
const out = new URL('../../assets/branding/contributors.png', import.meta.url);

const W = 1200, P = 4;
// Transparent: text outside the cards is a mid tone that reads on both GitHub themes.
const muted = '#8b7355';
const font = "Segoe UI, DejaVu Sans, Liberation Sans, sans-serif";
const amber = '#e0a030';
const esc = s => s.replace(/[&<>"]/g, c => `&${{ '&': 'amp', '<': 'lt', '>': 'gt', '"': 'quot' }[c]};`);

const listed = (await all('/repos/{repo}/contributors'))
  .filter(c => c.type === 'User' && !excluded.has(c.login.toLowerCase()));
const byLogin = new Map(listed.map(c => [c.login.toLowerCase(), c]));
const team = await Promise.all(core.map(async l => byLogin.get(l.toLowerCase()) ?? await api(`/users/${l}`)));
const others = listed.filter(c => !core.some(l => l.toLowerCase() === c.login.toLowerCase()))
  .sort((a, b) => b.contributions - a.contributions || a.login.localeCompare(b.login));

// Two or three words on what each contributor outside the core team worked on, by Claude Haiku from their commit subjects. Saved in
// contributors.json and asked again only when someone's commit count changed; without ANTHROPIC_API_KEY the saved ones are drawn.
const areasFile = new URL('../../assets/branding/contributors.json', import.meta.url);
const saved = existsSync(areasFile) ? JSON.parse(readFileSync(areasFile, 'utf8')) : {};
const key = u => u.login.toLowerCase();
const stale = others.filter(u => !roles[key(u)] && saved[key(u)]?.commits !== u.contributions);
if (stale.length && process.env.ANTHROPIC_API_KEY) {
  const work = await Promise.all(stale.map(async u => [`## ${u.login}`,
    ...(await all(`/repos/{repo}/commits?author=${u.login}`)).slice(0, 80).map(c => `- ${c.commit.message.split('\n')[0]}`)].join('\n')));
  const Areas = z.object({ contributors: z.array(z.object({ login: z.string(), areas: z.string() })) });
  const response = await new Anthropic().beta.messages.stream({
    model: 'claude-haiku-5-5',
    max_tokens: 4000,
    system: 'MeitouClient is a C# reimplementation of the game Kenshi\'s engine. For each contributor below, name the areas of the project '
      + 'their commits worked on in 2 or 3 words, at most 24 characters, comma-separated where it lists several, e.g. "Navmesh, Metal" '
      + 'or "Linux packaging". Plain and factual: no praise, no verbs, no "fixes".',
    messages: [{ role: 'user', content: work.join('\n\n') }],
    output_config: { format: betaZodOutputFormat(Areas) },
  }).finalMessage();
  const answer = Areas.parse(JSON.parse(response.content.filter(b => b.type === 'text').map(b => b.text).join('')));
  for (const u of stale) {
    const a = answer.contributors.find(c => c.login.toLowerCase() === key(u));
    if (a) saved[key(u)] = { commits: u.contributions, areas: a.areas.trim() };
  }
  await writeFile(areasFile, `${JSON.stringify(saved, null, 2)}\n`);
}
const areas = u => roles[key(u)] ?? saved[key(u)]?.areas;

async function avatar(user, size) {
  const r = await fetch(`${user.avatar_url}${user.avatar_url.includes('?') ? '&' : '?'}s=${size * 2}`);
  if (!r.ok) throw new Error(`avatar ${user.login}: ${r.status}`);
  const type = r.headers.get('content-type') ?? 'image/png';
  return `data:${type};base64,${Buffer.from(await r.arrayBuffer()).toString('base64')}`;
}

let defs = '', body = '', clip = 0;
async function face(user, cx, cy, r, ring) {
  const id = `c${clip++}`;
  defs += `<clipPath id="${id}"><circle cx="${cx}" cy="${cy}" r="${r}"/></clipPath>`;
  body += `<image href="${await avatar(user, r * 2)}" x="${cx - r}" y="${cy - r}" width="${r * 2}" height="${r * 2}" clip-path="url(#${id})"/>`;
  body += `<circle cx="${cx}" cy="${cy}" r="${r}" fill="none" stroke="${ring}" stroke-width="${ring === amber ? 4 : 2}"/>`;
}
const text = (x, y, size, fill, s, weight = 400) =>
  `<text x="${x}" y="${y}" font-family="${font}" font-size="${size}" font-weight="${weight}" fill="${fill}" text-anchor="middle">${esc(s)}</text>`;

let y = 4;

// Core team: one cell each, centred, the creator's marked as such.
const gap = 16, cardW = 190, cardH = 152, x0 = (W - team.length * cardW - (team.length - 1) * gap) / 2;
for (const [i, u] of team.entries()) {
  const x = x0 + i * (cardW + gap), cx = x + cardW / 2;
  await face(u, cx, y + 54, 40, amber);
  body += text(cx, y + 118, 17, muted, u.login, 700);
  body += text(cx, y + 138, 12, amber, u.login === creator ? 'Creator · Project lead' : 'Core team', 600);
}
y += cardH + 20;

// Everyone else, in rows of equal cells, the last row centred, their areas under the name; those with a role first, larger, in amber.
if (others.length) {
  const role = u => roles[key(u)];
  others.sort((a, b) => !!role(b) - !!role(a));
  const cell = 150, perRow = Math.floor((W - 2 * P) / cell), r = 36, rr = 40;
  for (let i = 0; i < others.length; i += perRow) {
    const row = others.slice(i, i + perRow), x0 = (W - row.length * cell) / 2;
    const R = row.some(role) ? rr : r;
    for (const [j, u] of row.entries()) {
      const cx = x0 + j * cell + cell / 2, name = u.login.length > 18 ? `${u.login.slice(0, 17)}…` : u.login;
      await face(u, cx, y + R + 8, role(u) ? rr : r, role(u) ? amber : muted);
      body += role(u) ? text(cx, y + 2 * R + 36, 16, muted, name, 700) : text(cx, y + 2 * R + 36, 15, muted, name);
      if (areas(u)) body += role(u) ? text(cx, y + 2 * R + 56, 14, amber, role(u), 600) : text(cx, y + 2 * R + 55, 12, muted, areas(u));
    }
    y += 2 * R + (row.some(areas) ? 76 : 60);
  }
}
const H = y - 16;

const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">`
  + `<defs>${defs}</defs>${body}</svg>`;
const png = new Resvg(svg, { font: { loadSystemFonts: true, defaultFontFamily: 'DejaVu Sans' } }).render().asPng();
await writeFile(out, png);
console.log(`${team.length} core, ${others.length} others -> ${out.pathname}`);
