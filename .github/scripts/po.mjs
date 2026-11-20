// Reading and writing gettext .po/.pot text for the localisation scripts (docs/formats/localisation.md).

/** The entries of a .po file: { msgctxt, msgid, msgidPlural, msgstr: [], comments: [] }; the header (empty msgid) is left out. */
export function parsePo(text) {
  const entries = [];
  let entry = fresh();
  let field = null;
  const flush = () => {
    if (entry.msgid !== null && entry.msgid !== '') entries.push(entry);
    entry = fresh();
    field = null;
  };

  for (const line of text.split('\n').map((l) => l.trimEnd())) {
    if (line === '') { flush(); continue; }
    if (line.startsWith('#')) {
      if (field) flush();
      entry.comments.push(line);
      continue;
    }

    const m = line.match(/^(msgctxt|msgid_plural|msgid|msgstr(?:\[(\d+)\])?)\s+"(.*)"$/);
    if (m) {
      const starts = m[1] === 'msgctxt' || m[1] === 'msgid';
      if (starts && entry.msgid !== null) flush();
      field = m[1];
      set(entry, field, m[2], unquote(m[3]), false);
    } else if (line.startsWith('"') && field) set(entry, field, null, unquote(line.slice(1, -1)), true);
  }
  flush();
  return entries;
}

/** A .pot file of <messages> ({ context, id, plural, refs }), sorted by id then context, with <note> as its first comment. */
export function writePot(messages, note) {
  const sorted = [...messages].sort((a, b) => compare(a.id, b.id) || compare(a.context ?? '', b.context ?? ''));
  const parts = [`# ${note}\nmsgid ""\nmsgstr ""\n"Content-Type: text/plain; charset=UTF-8\\n"\n`];
  for (const m of sorted) {
    const lines = [`#: ${m.refs.join(' ')}`];
    if (m.context !== null) lines.push(`msgctxt ${quote(m.context)}`);
    lines.push(`msgid ${quote(m.id)}`);
    if (m.plural) lines.push(`msgid_plural ${quote(m.plural)}`, 'msgstr[0] ""', 'msgstr[1] ""');
    else lines.push('msgstr ""');
    parts.push(lines.join('\n') + '\n');
  }
  return parts.join('\n');
}

/** One .po entry's text: comments, then msgctxt, msgid, msgid_plural and the translations. */
export function writeEntry(e) {
  const lines = [...(e.comments ?? [])];
  if (e.msgctxt !== null && e.msgctxt !== undefined) lines.push(`msgctxt ${quote(e.msgctxt)}`);
  lines.push(`msgid ${quote(e.msgid)}`);
  if (e.msgidPlural) {
    lines.push(`msgid_plural ${quote(e.msgidPlural)}`);
    e.msgstr.forEach((s, i) => lines.push(`msgstr[${i}] ${quote(s)}`));
  } else lines.push(`msgstr ${quote(e.msgstr[0] ?? '')}`);
  return lines.join('\n') + '\n';
}

/** An empty entry. */
function fresh() {
  return { msgctxt: null, msgid: null, msgidPlural: null, msgstr: [], comments: [] };
}

/** Sets or extends one field of <entry>. */
function set(entry, field, index, value, append) {
  if (field === 'msgctxt') entry.msgctxt = append ? entry.msgctxt + value : value;
  else if (field === 'msgid') entry.msgid = append ? entry.msgid + value : value;
  else if (field === 'msgid_plural') entry.msgidPlural = append ? entry.msgidPlural + value : value;
  else {
    const i = index == null ? (append ? entry.msgstr.length - 1 : 0) : Number(index);
    entry.msgstr[i] = append ? (entry.msgstr[i] ?? '') + value : value;
  }
}

/** Ordinal comparison, so the order does not depend on the machine's locale. */
function compare(a, b) {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** A gettext quoted string: long or multi-line text split after each line break. */
function quote(s) {
  const escaped = s.replaceAll('\\', '\\\\').replaceAll('"', '\\"').replaceAll('\t', '\\t').replaceAll('\r', '\\r');
  const pieces = escaped.split('\n').map((p, i, all) => (i < all.length - 1 ? p + '\\n' : p)).filter((p) => p.length > 0);
  if (pieces.length <= 1) return `"${pieces[0] ?? ''}"`;
  return '""\n' + pieces.map((p) => `"${p}"`).join('\n');
}

/** The text of a gettext quoted string's inside. */
function unquote(s) {
  return s.replace(/\\(.)/g, (_, e) => ({ n: '\n', t: '\t', r: '\r', '"': '"', '\\': '\\' }[e] ?? e));
}
