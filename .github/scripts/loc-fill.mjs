// Fills the gaps loc-strings.mjs finds: asks Claude to translate each message a built-in catalog
// (src/Meitou.Core/Localisation/locale/<code>.po) lacks, and appends the translations to that catalog marked "machine-translated" so a
// native speaker can find them. The catalog's own entries are the glossary, so new text uses the words the old text uses.
// ANTHROPIC_API_KEY; LOC_MODEL picks the model (default claude-opus-5-5).
import { readFileSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import Anthropic from '@anthropic-ai/sdk';
import { betaZodOutputFormat } from '@anthropic-ai/sdk/helpers/beta/zod';
import { z } from 'zod';
import { parsePo, writeEntry } from './po.mjs';

const root = new URL('../../', import.meta.url).pathname;
const localeDir = join(root, 'src/Meitou.Core/Localisation/locale');
const model = process.env.LOC_MODEL || 'claude-opus-5-5';
const batchSize = 60;
const glossarySize = 400;

/** The language each locale code names, for the prompt. */
const LANGUAGES = {
  de_DE: 'German', es_ES: 'Spanish (Spain)', fr_FR: 'French', it_IT: 'Italian', ja_JP: 'Japanese', ko_KR: 'Korean',
  pt_BR: 'Brazilian Portuguese', ru_RU: 'Russian', zh_CN: 'Simplified Chinese',
};

const Answer = z.object({ translations: z.array(z.object({ index: z.number().int(), forms: z.array(z.string()) })) });
const client = new Anthropic();

const findings = JSON.parse(execFileSync('node', [join(root, '.github/scripts/loc-strings.mjs'), '--json'], { encoding: 'utf8' }));
for (const catalog of findings.catalogs.filter((c) => c.missing.length)) await fill(catalog);

/** Translates and appends every message <catalog> lacks, a batch at a time. */
async function fill(catalog) {
  const path = join(localeDir, `${catalog.code}.po`);
  const text = readFileSync(path, 'utf8');
  const nplurals = Number(text.match(/nplurals=(\d+)/)?.[1] ?? 2);
  const glossary = glossaryOf(parsePo(text));
  let added = 0;
  for (let i = 0; i < catalog.missing.length; i += batchSize) {
    const batch = catalog.missing.slice(i, i + batchSize);
    const forms = await translate(catalog.code, batch, glossary, nplurals);
    const entries = batch.map((m, k) => forms[k] && entryOf(m, forms[k], nplurals)).filter(Boolean);
    if (entries.length) appendFileSync(path, '\n' + entries.map(writeEntry).join('\n'));
    added += entries.length;
  }
  console.log(`${catalog.code}.po: ${added} of ${catalog.missing.length} messages added.`);
}

/** A .po entry for message <m> with translation <forms>, or null when the forms do not keep the message's placeholders. */
function entryOf(m, forms, nplurals) {
  const wanted = m.plural ? nplurals : 1;
  if (forms.length !== wanted || forms.some((f) => !samePlaceholders(m.id, f))) return null;
  return { comments: ['#. machine-translated'], msgctxt: m.context, msgid: m.id, msgidPlural: m.plural, msgstr: forms };
}

/** Whether <translated> holds exactly the {N...} placeholders of <english>. */
function samePlaceholders(english, translated) {
  const holes = (s) => (s.match(/\{\d+[^{}]*\}/g) ?? []).sort().join();
  return holes(english) === holes(translated);
}

/** Up to glossarySize short translated entries of the catalog, as "English => translation" lines. */
function glossaryOf(entries) {
  return entries
    .filter((e) => e.msgstr[0] && e.msgid.length <= 60 && !e.msgidPlural)
    .slice(0, glossarySize)
    .map((e) => `${e.msgid} => ${e.msgstr[0]}`)
    .join('\n');
}

/** The translations of <batch> into <code>: one array of forms per message (null where the model gave none). */
async function translate(code, batch, glossary, nplurals) {
  const response = await client.beta.messages.stream({
    model,
    max_tokens: 32000,
    betas: ['server-side-fallback-2026-07-01'],
    fallbacks: 'default',
    system: systemPrompt(code, glossary, nplurals),
    messages: [{ role: 'user', content: JSON.stringify(batch.map((m, index) => ({ index, text: m.id, plural: m.plural ?? undefined }))) }],
    output_config: { format: betaZodOutputFormat(Answer) },
  }).finalMessage();

  if (response.stop_reason !== 'end_turn') {
    console.log(`${code}: a batch stopped (${response.stop_reason}); its messages stay missing.`);
    return batch.map(() => null);
  }
  const answer = Answer.parse(JSON.parse(response.content.filter((b) => b.type === 'text').map((b) => b.text).join('')));
  const byIndex = new Map(answer.translations.map((t) => [t.index, t.forms]));
  return batch.map((_, k) => byIndex.get(k) ?? null);
}

/** The instructions for translating Meitou's interface into <code>. */
function systemPrompt(code, glossary, nplurals) {
  return [
    `Translate user interface text of Meitou, a reimplementation of the game Kenshi, from English into ${LANGUAGES[code] ?? code}.`,
    'The texts are setting names and hints, menu captions, launcher messages and in-game notices. "Faithful" means as the original game does'
      + ' it; "Meitou" and product names (Kenshi, Steam, Vulkan, Metal, DLSS, FSR) stay as they are.',
    'Keep every placeholder such as {1}, {2,num} or {1,num=fix,p=2} exactly as written; you may move them. Keep leading and trailing spaces,'
      + ' line breaks and capitalisation style (ALL CAPS stays all caps). Translate a text that looks like a fragment as a fragment.',
    `A text with "plural" set needs ${nplurals} forms, in the order of the language's gettext plural forms; every other text needs one.`,
    'Answer with one entry per input index.',
    'Use the same words as these existing translations:',
    glossary,
  ].join('\n');
}
