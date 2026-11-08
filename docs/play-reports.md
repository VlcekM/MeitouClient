# Play reports and roadmap upkeep

The roadmap (`site/src/data/roadmap.ts`, page `/roadmap`) is the one copy of what works in Meitou. Its row `id`s are stable: reports,
issue forms and links point at them, so never change an id when a name is reworded. A row's status stays a person's call; reports
and checks only propose.

## Roadmap data

- Row `seen?: { build, date, by }` is the evidence for a status (`by`: `playtest`, an issue `#N`, a tester). The page shows "Last
  confirmed on ..." and, when `date` is older than the second newest full release, "Not rechecked since ...".
- `site/scripts/history.mjs` reads every git version of `roadmap.ts` into `site/src/data/history.json` (git-ignored; the site image
  and site check workflows run it). The page draws a share-per-status chart and Recent changes from it.
- Exports: `/roadmap.json` (the whole roadmap, rows with `category`), `/roadmap-changes.json`, `/roadmap-changes.xml` (RSS).
- Release dates come from the GitHub API at site build time (`site/src/data/releases.ts`); without network the page drops the
  staleness marks and offers only "Other" as build.

## Reports service

`services/Meitou.Reports` (ASP.NET minimal API, PostgreSQL), image `ghcr.io/vlcekm/meitou-reports` from `reports-image.yml`, runs on
Cloudovna. Published Native AOT on the chiseled `runtime-deps` image: Npgsql (slim builder) is the only package; JSON goes through the
`ReportsJson` source-generated context (add new wire types there), Discord signatures through the service's own `Ed25519.cs`. Table `reports` (feedback: see "Feedback"): row, verdict `works|broken`, build, platform `win|linux|mac|proton|other`, modded, note (300
chars, required for Broken), source `web|discord|launcher`, reporter, trusted, gzipped log (launcher only, last 512 KiB).

- Reporter = HMAC-SHA256(`REPORTS_SALT`, IP or Discord user id), 24 hex: counts distinct people, stores nothing personal.
- Limits: 10 posts per IP per 10 minutes, 30 reports per reporter per day; unknown row ids are refused (rows from `ROADMAP_URL`). Not the site itself: a container on cic cannot reach domains its own edge serves directly (DNS-only, like `meitou.kenshi.wiki`).
- Counting window: reports before the publish time of the second newest full game release (`v*`, not pre-release) stop counting.
  A Broken report also stops once the newest full release was published after its build (the build's own release time; a build
  with no release, such as `Other`, is dated by the report), so shipping a fix clears it and a still broken row is reported again.
- Rules (`GET /proposals`): Untested to Works on 3 distinct reporters or 1 trusted tester; any Broken flags the row for a look. A
  Broken that someone reproduces with steps is what moves Works to Bug, by hand.

| Endpoint | Who | What |
|---|---|---|
| `GET /summary` | public | per row since the cutoff: distinct works / broken, trusted works, newest verdict, build, time |
| `GET /proposals` | public | the status changes the rules suggest |
| `GET /latest` | public | `{release, any}`: the newest full game release's version and the newest of any kind (nightlies included), no `v` |
| `POST /reports` | public | `{row, verdict, build, platform, modded, note, source, turnstile?, log?}` |
| `POST /discord` | Discord | interactions endpoint, Ed25519-signed |
| `GET /reports?since&row&limit`, `GET /reports/{id}/log`, `DELETE /reports/{id}`, `POST /discord/register` | admin | `Authorization: Bearer REPORTS_ADMIN_TOKEN` |

Environment: `DATABASE_URL` (postgresql URL) or `ConnectionStrings__Reports` (ADO.NET), `REPORTS_SALT` (required, never change it:
distinct counts reset), `REPORTS_ADMIN_TOKEN`, `ROADMAP_URL` (default `roadmap.ts` on GitHub raw, read line by line; a `.json` URL is read as `/roadmap.json`), `ALLOWED_ORIGINS` (CORS, default
the site), `TURNSTILE_SECRET` (web reports need a Turnstile token when set), `DISCORD_PUBLIC_KEY`, `DISCORD_APP_ID`,
`DISCORD_BOT_TOKEN`, `DISCORD_TRUSTED_ROLES` (role ids whose reports are trusted), `GITHUB_REPO`. Client IP: `CF-Connecting-IP`, else
the last `X-Forwarded-For` entry. Port 8080.

## Feedback

The game's FEEDBACK button (top right in both UI modes, title included; the Interface setting "Feedback button" (`UserConfig.FeedbackButton`,
default on) hides it during play except with the Esc menu open; `PlayerInterface.Feedback.cs`, `GameHost.Feedback.cs`,
`FeedbackSender.cs`) opens a modal form: title (required, 100 chars), text (5000), and ticks for a screenshot, a navmesh screenshot, a save and log.txt (it
can show folder names), and the player's Discord name (64 chars, ticked by default). The ticks and the name are kept in meitou.user.json
(`feedback`) and saved on each send. The screenshot is the frame after the press, drawn without the game menu and what it opens, the form, the settings
panel, debug overlays and pointer; PNG (rows filtered), scaled to at most 2560 × 1440 and then by 0.75 until under 4.5 MB: GitHub's image proxy refuses images over 5 MB (**Verified** on a feedback issue: a 7.7 MB screenshot showed "Content length exceeded"; that 1440p frame is 5.6 MB filtered, 3.3 MB at 1080p), so the service links a picture over 5,000,000 bytes (from older games) instead of embedding it. The save is made only when Send is pressed (only
in a game), into a temporary folder, zipped and deleted after; over 95 MB zipped it is refused before anything is sent. The log is the whole
log.txt, gzipped. Opening the form also asks the service's `GET /latest` for the newest game versions (`LatestRelease.cs`, the answer reused for 15
minutes; the service reads GitHub's releases at most every 10 minutes, so players never meet GitHub's rate limit); a nightly compares with
the newest release of any kind, a release with the newest full release (SemVer, `CodeModVersion`). When a
newer one is out, the form says so in red and Send first asks the player to update and check there ("Send anyway" sends); offline, rate-limited or
an untagged build, nothing is shown. The navmesh screenshot (off by default, in a game only) is made when Send is pressed: Meitou's debug view (`DebugOverlays`) is switched on, the navmesh of the zones round the camera (ring 1) loads, and once the view is built one more frame is kept like the screenshot (form hidden), then the view goes back; it gives up after 10-30 s and the report goes without it. The camera's location (target, eye, yaw, pitch, boom, as text) is always sent from a game and shown as a "Camera:" line in the issue; outside a game there is none. **Observed** (built, not yet run against the live service). Also sent: `BuildVersion` tag and commit, platform (as the launcher), the load order's data files.

Service side (`Feedback.cs`, table `feedback`): `POST /feedback` (JSON form, which attachments follow) returns a 32-hex token; each
attachment is `PUT /feedback/{token}/{screenshot|navmesh|save|log}` with the raw body (save at most 95,000,000 bytes, others 16 MiB: Cloudflare
takes 100 MB a request); `POST /feedback/{token}/send` opens the issue on `GITHUB_REPO` labelled `feedback` (the Discord name, when sent, in its details table) (not `bug`, so the drift check
leaves it alone; @ in the player's text gets a zero-width space) and returns `{number, url}`, again on a repeat. Files live in
`FEEDBACK_DIR/{token}/` and are public at `GET /feedback/{token}/{screenshot.png|navmesh.png|save.zip|log.txt}` (the log stored gzipped, served with
`Content-Encoding: gzip`), kept forever; admin `DELETE /feedback/{token}` removes one. Limits: 10 forms per reporter (same hash as reports)
per day, 30 requests per IP per 10 minutes; forms never sent are deleted after a day. Env: `GITHUB_APP_ID` and
`GITHUB_APP_PRIVATE_KEY` (PEM; `\n` escapes allowed) of the GitHub App "Sane Cat-Lon" (Issues read/write, installed on the repo), so issues are
authored by its bot (`GithubAuth.cs`: JWT → installation token, cached for its hour); else `GITHUB_TOKEN` (fine-grained, Issues read/write;
issues then show its owner); with neither, sends are refused. `FEEDBACK_DIR` (default `/data/feedback`, a persistent volume: the image creates it
owned by the app user), `PUBLIC_URL` (the links' base, default the Cloudovna address).

## Ways in

- **Site**: "Works for me" / "Broken" under each row and a form dialog; `site/src/data/reports.ts` holds the service URL and the
  Turnstile site key (empty URL hides the buttons). Live at `https://gear-pastry-chorus.cloudovna.app` (Cloudovna stack
  `meitou-reports`, database `meitou-reports db`; redeployed by the `REPORTS_REDEPLOY_WEBHOOK_URL` secret).
- **Discord**: `/works system [build platform modded note]` and `/broken system what [build platform modded]`, system autocompleted
  from the rows; answers are visible to the reporter only. Setup: a Discord application, its Interactions Endpoint URL set to
  `<service>/discord`, the env above, then `POST /discord/register` once.
- **Launcher**: "Report to the roadmap" under the launcher version (`launcher/Meitou.Launcher/Report.cs`, hidden while `ReportsUrl`
  is empty): system, verdict, installed version, note, and optionally the end of that version's `log.txt`. Platform from the OS;
  a Windows build with the `HKCU\Software\Wine` key is `proton`. log.txt already appends runs up to 10 MiB (`GameLog`), so the bad
  run is still in it later.

## GitHub automation

- `site/scripts/issue-forms.mjs` generates `.github/ISSUE_TEMPLATE/bug.yml`, `works-in-play.yml` (row dropdown `Name [id]`) and
  `.github/labels.json` (`bug`, `parity`, `confirmed-works`, `roadmap-drift`, `area:<category id>`). Rerun it after adding or
  renaming a row or category.
- `issue-labels.yml`: syncs `labels.json` to GitHub on push; gives a new form issue its `area:` label (bug form Area, or the row's
  category).
- `roadmap-drift.yml` (started by the roadmap update after Claude's edits, or by hand): keeps one "Roadmap out of date" issue listing closed issues still named
  as bugs (a Known bugs entry whose state says closed is skipped), open `bug` issues the roadmap never names, row bug ids and Known
  bugs that do not name each other, `asOf.master` over 100 commits behind, stale issue forms, and the reports' proposals (repository
  variable `REPORTS_URL`). Closes it when clean. `DRY_RUN=1 node .github/scripts/roadmap-drift.mjs` prints the findings.
- `site-check.yml`: builds `site/` on pull requests.

## Roadmap updates

`roadmap-update.yml` runs after each successful Nightly or game Release (and on demand). `.github/scripts/roadmap-update.mjs` sends
Claude Haiku 5.5 (`claude-haiku-5-5`, effort high, structured output; it has no server-side fallback, so a classifier refusal
fails the run) the roadmap file, the commits and issues since `asOf`, the drift findings and the play reports; the model returns edits
(row status, note, bug ids; Known bugs and fixes to add or remove) with the evidence for each. The script applies them line by line
(row ids, names, categories, platforms and `seen` are never touched), sets `asOf` to the run's commit and checks the file still
loads with the same rows; the workflow builds the site and commits straight to master (the run summary lists the edits), then
starts the site image, since a push by `GITHUB_TOKEN` starts no workflow. Every run then starts the drift check, so its issue lists
only what Claude left. Nobody reviews it first: a wrong edit
is fixed by a normal commit. Needs the `ANTHROPIC_API_KEY` secret (skipped without it); billed to the linked Console org's monthly
credit. `EDITS_FILE=<json>` applies a saved answer offline.
