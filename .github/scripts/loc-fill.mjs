// Fills the gaps loc-strings.mjs finds: asks Claude to translate each message a built-in catalog
// (src/Meitou.Core/Localisation/locale/<code>.po) lacks or holds unusably, and writes the translations to that catalog marked
// "machine-translated" so a native speaker can find them (an unusable entry is replaced, not duplicated). The catalog's own entries are the
// glossary, so new text uses the words the old text uses. A batch that fails is split and retried; what still fails stays missing, for the
// check to report. ANTHROPIC_API_KEY; LOC_MODEL picks the model (default claude-opus-5-5); LOC_MAX_MESSAGES caps one run (default 3000).
import { readFileSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import Anthropic from '@anthropic-ai/sdk';
import { betaZodOutputFormat } from '@anthropic-ai/sdk/helpers/beta/zod';
import { z } from 'zod';
import { parsePo, writeEntry } from './po.mjs';

const root = fileURLToPath(new URL('../../', import.meta.url));
const localeDir = join(root, 'src/Meitou.Core/Localisation/locale');
const model = process.env.LOC_MODEL || 'claude-opus-5-5';
const batchSize = 60;
const glossarySize = 400;
let budget = Number(process.env.LOC_MAX_MESSAGES || 3000);

/** The language each locale code names, for the prompt. */
const LANGUAGES = {
  de_DE: 'German', es_ES: 'Spanish (Spain)', fr_FR: 'French', it_IT: 'Italian', ja_JP: 'Japanese', ko_KR: 'Korean',
  pt_BR: 'Brazilian Portuguese', ru_RU: 'Russian', zh_CN: 'Simplified Chinese',
};

const Answer = z.object({ translations: z.array(z.object({ index: z.number().int(), forms: z.array(z.string()) })) });
const client = new Anthropic();

const report = execFileSync('node', [join(root, '.github/scripts/loc-strings.mjs'), '--json'], { encoding: 'utf8', maxBuffer: 64 << 20 });
const findings = JSON.parse(report);
for (const catalog of findings.catalogs.filter((c) => c.missing.length)) await fill(catalog);

/** Translates every message <catalog> lacks (within the run's budget) and writes them to its file, replacing unusable entries. */
async function fill(catalog) {
  const path = join(localeDir, `${catalog.code}.po`);
  const text = readFileSync(path, 'utf8');
  const pluralForms = text.match(/Plural-Forms:\s*([^"\\]*)/)?.[1]?.trim() ?? 'nplurals=2; plural=(n != 1);';
  const nplurals = Number(pluralForms.match(/nplurals=(\d+)/)?.[1] ?? 2);
  const glossary = glossaryOf(parsePo(text));
  const todo = catalog.missing.slice(0, Math.max(0, budget));
  budget -= todo.length;

  const entries = [];
  for (let i = 0; i < todo.length; i += batchSize) {
    const batch = todo.slice(i, i + batchSize);
    entries.push(...await translateSafely(catalog.code, batch, { glossary, pluralForms, nplurals }));
  }

  if (entries.length) writeFileSync(path, withEntries(text, entries));
  console.log(`${catalog.code}.po: ${entries.length} of ${catalog.missing.length} messages written.`);
}

/** The usable entries for <batch>; a batch whose request fails is split in two and each half tried again, down to single messages. */
async function translateSafely(code, batch, lang) {
  try {
    const forms = await translate(code, batch, lang);
    return batch.map((m, k) => forms[k] && entryOf(m, forms[k], lang.nplurals)).filter(Boolean);
  } catch (error) {
    console.log(`${code}: a batch of ${batch.length} failed (${error.message}).`);
    if (batch.length === 1) return [];
    const half = Math.ceil(batch.length / 2);
    return [...await translateSafely(code, batch.slice(0, half), lang), ...await translateSafely(code, batch.slice(half), lang)];
  }
}

/** <text> with the entries of the same context and id dropped and <entries> appended. */
function withEntries(text, entries) {
  const replaced = new Set(entries.map((e) => key(e.msgctxt, e.msgid)));
  const blocks = text.trimEnd().split(/\n\s*\n/);
  const kept = blocks.filter((block) => {
    const [entry] = parsePo(block);
    return !entry || !replaced.has(key(entry.msgctxt, entry.msgid));
  });
  return [...kept, ...entries.map((e) => writeEntry(e).trimEnd())].join('\n\n') + '\n';
}

/** The lookup key of a context and id. */
function key(context, id) {
  return `${context ?? ''}\u0004${id}`;
}

/** A .po entry for message <m> with translation <forms>, or null when the forms are unusable (count, empty, placeholders, edge spaces). */
function entryOf(m, forms, nplurals) {
  const wanted = m.plural ? nplurals : 1;
  if (forms.length !== wanted || forms.some((f) => f.trim() === '')) return null;
  const allowed = new Set([...holes(m.id), ...holes(m.plural ?? '')]);
  const keeps = (f) => (m.plural ? holes(f).every((h) => allowed.has(h)) : holes(f).sort().join() === holes(m.id).sort().join());
  if (!forms.every(keeps) || !forms.every((f) => edges(f) === edges(m.id))) return null;
  return { comments: ['#. machine-translated'], msgctxt: m.context, msgid: m.id, msgidPlural: m.plural, msgstr: forms };
}

/** The {N...} placeholders of <s>. */
function holes(s) {
  return s.match(/\{\d+[^{}]*\}/g) ?? [];
}

/** Whether <s> starts and ends with whitespace, as a short signature. */
function edges(s) {
  return `${/^\s/.test(s)}${/\s$/.test(s)}`;
}

/** Up to glossarySize short translated entries of the catalog, as "English => translation" lines. */
function glossaryOf(entries) {
  return entries
    .filter((e) => e.msgstr[0] && e.msgid.length <= 60 && !e.msgidPlural)
    .slice(0, glossarySize)
    .map((e) => `${e.msgid} => ${e.msgstr[0]}`)
    .join('\n');
}

/** The translations of <batch> into <code>: one array of forms per message (null where the model gave none); throws when the request fails. */
async function translate(code, batch, lang) {
  const input = batch.map((m, index) => ({ index, text: m.id, plural: m.plural ?? undefined, context: m.context ?? undefined, where: m.refs }));
  const response = await client.beta.messages.stream({
    model,
    max_tokens: 32000,
    betas: ['server-side-fallback-2026-07-01'],
    fallbacks: 'default',
    system: systemPrompt(code, lang),
    messages: [{ role: 'user', content: JSON.stringify(input) }],
    output_config: { format: betaZodOutputFormat(Answer) },
  }).finalMessage();

  if (response.stop_reason !== 'end_turn') throw new Error(`stopped: ${response.stop_reason}`);
  const answer = response.parsed_output ?? Answer.parse(JSON.parse(response.content.filter((b) => b.type === 'text').map((b) => b.text).join('')));
  const byIndex = new Map(answer.translations.map((t) => [t.index, t.forms]));
  return batch.map((_, k) => byIndex.get(k) ?? null);
}

/** The instructions for translating Meitou's interface into <code>. */
function systemPrompt(code, { glossary, pluralForms, nplurals }) {
  return [
    `Translate user interface text of Meitou, a reimplementation of the game Kenshi, from English into ${LANGUAGES[code] ?? code}.`,
    'The texts are setting names and hints, menu captions, launcher messages and in-game notices. "Faithful" means as the original game does'
      + ' it; "Meitou" and product names (Kenshi, Steam, Vulkan, Metal, DLSS, FSR) stay as they are. "context" disambiguates a text and'
      + ' "where" names the source files that show it.',
    'Keep every placeholder such as {1}, {2,num} or {1,num=fix,p=2} exactly as written; you may move them. Keep leading and trailing spaces,'
      + ' line breaks and capitalisation style (ALL CAPS stays all caps). Translate a text that looks like a fragment as a fragment.',
    `A text with "plural" set needs ${nplurals} forms, in the order of this gettext rule: ${pluralForms}. Every other text needs one.`,
    'Answer with one entry per input index.',
    'Use the same words as these existing translations:',
    glossary,
  ].join('\n');
}
