# Gettext UI help

- **Verified** (`main.mo`/`main.po` of the eight shipped locales agree): other languages ship `locale/<code>/LC_MESSAGES/main.mo` (gettext
  binary, either byte order, `\0`-separated plurals) and a `main.po`. Plural forms come from the header `Plural-Forms`; placeholders are
  `{N}`, `{N,num}`, `{N,num=fix,p=K}`. Meitou reads the `.mo` (else `.po`) and translates the source strings that exist as msgids; see
  [localisation](localisation.md). The pot has no `msgctxt`. `StatHelpCatalog` reads the translated entries; the longest-entry choice uses the English length.
