# Gettext UI help

- **Observed** in the configured 1.0.68 install's
  `locale/en_GB/LC_MESSAGES/main.pot`: gettext entries have `#:` source references,
  quoted `msgid` text with quoted continuation lines, and `msgstr`. Escaped newlines
  and quotes occur in text. One message can reference multiple source locations;
  one source location can identify both a short label and a longer description.
- **Observed**: `kenshi/statswindow.cpp` references 937–989 identify stat descriptions;
  499–870 identify affected-stat and training groups. Weapons share the 681/684
  headings and associated entries. UI headings also reference 220 (next level),
  440 (description), and 451 (racial XP bonus). These are catalog metadata, not
  original source code. Original prose stays in the installation.
- The UI reads English help at runtime, joins continuations, retains all matching
  references and selects the longer entry for a description. Source-reference
  numbering in other game versions and the precise original
  numeric affected-stat readouts remain **Unknown** / unimplemented.
- **Verified** (`main.mo`/`main.po` of the eight shipped locales agree): other languages ship `locale/<code>/LC_MESSAGES/main.mo` (gettext
  binary, either byte order, `\0`-separated plurals) and a `main.po`. Plural forms come from the header `Plural-Forms`; placeholders are
  `{N}`, `{N,num}`, `{N,num=fix,p=K}`. Meitou reads the `.mo` (else `.po`) and translates the source strings that exist as msgids; see
  [localisation](localisation.md). The pot has no `msgctxt`. `StatHelpCatalog` reads the translated entries; the longest-entry choice uses the English length.
