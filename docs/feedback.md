# Roadmap, feedback and issue labels

## Roadmap

`site/src/data/roadmap.ts`, page `/roadmap`, kept by hand: `versions` (oldest first), each with areas and their features. A feature
has a stable `id` (its link `/roadmap#id`), a name, a status (`done`, `wip`, `planned`) and a note. A new version is a new entry in
`versions`; the page draws its section, its nav link and its status bar. Nothing edits the file automatically.

## Reports service

`services/Meitou.Reports` (ASP.NET minimal API, PostgreSQL), image `ghcr.io/vlcekm/meitou-reports` from `reports-image.yml`, runs on
Cloudovna (stack `meitou-reports`, database `meitou-reports db`; redeployed by the `REPORTS_REDEPLOY_WEBHOOK_URL` secret). Published
Native AOT on the chiseled `runtime-deps` image: Npgsql (slim builder) is the only package; JSON goes through the `ReportsJson`
source-generated context (add new wire types there). It serves the game's feedback button (below) and `GET /latest`: `{release, any}`,
the newest full game release's version and the newest of any kind (nightlies included), no `v`; GitHub's releases are read at most
every 10 minutes.

Environment: `DATABASE_URL` (postgresql URL) or `ConnectionStrings__Reports` (ADO.NET), `REPORTS_SALT` (required; reporters are
stored as HMAC-SHA256 of it and the IP, 24 hex), `REPORTS_ADMIN_TOKEN` (`Authorization: Bearer` for admin `DELETE /feedback/{token}`),
`GITHUB_REPO`, and the feedback variables below. Client IP: `CF-Connecting-IP`, else the last `X-Forwarded-For` entry. Port 8080.

## Feedback

The game's FEEDBACK button (top right in both UI modes, title included; the Interface setting "Feedback button" (`UserConfig.FeedbackButton`,
default on) hides it during play except with the Esc menu open; `FeedbackPanel`, `FeedbackCapture`,
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
takes 100 MB a request); `POST /feedback/{token}/send` opens the issue on `GITHUB_REPO` labelled `feedback` (the Discord name, when sent, in its details table) (@ in the player's text gets a zero-width space) and returns `{number, url}`, again on a repeat. Files live in
`FEEDBACK_DIR/{token}/` and are public at `GET /feedback/{token}/{screenshot.png|navmesh.png|save.zip|log.txt}` (the log stored gzipped, served with
`Content-Encoding: gzip`), kept forever; admin `DELETE /feedback/{token}` removes one. Limits: 20 forms per reporter (the salted IP hash)
per day, 60 requests per IP per 10 minutes; forms never sent are deleted after a day. Env: `GITHUB_APP_ID` and
`GITHUB_APP_PRIVATE_KEY` (PEM; `\n` escapes allowed) of the GitHub App "Sane Cat-Lon" (Issues read/write, installed on the repo), so issues are
authored by its bot (`GithubAuth.cs`: JWT → installation token, cached for its hour); else `GITHUB_TOKEN` (fine-grained, Issues read/write;
issues then show its owner); with neither, sends are refused. `FEEDBACK_DIR` (default `/data/feedback`, a persistent volume: the image creates it
owned by the app user), `PUBLIC_URL` (the links' base, default the Cloudovna address).

Checking a report: unzip its `save.zip` and run `meitou-tools probe <save folder> <mode>` (headless load under the install's load order, which
may not be the reporter's; modes in its usage: nearby characters, a character's AI over time, the squads nearby over time (`squads`; `bait` also
sends the player's squad after the nearest one to start a fight), everyone near the leader over time (`crowd`), a character's saved squad memory, pending crime, bounties and who targets it under which memory tags (`crime <name>`), one character's fight with its attackers' gaps and blows (`fight <slot>`; `--nav` for the real navmesh, which a fight stuck on the terrain needs), the town's homes with their public flags and shoo goals and the building the leader stands in (`premises`, with `--nav`), slaves, cages, dialogue, storage, research benches with their power grid, building re-save, navmesh heights, a town building's layout names and furniture as laid out now (`layout <key>`, with `--nav`); `--at x,z`
puts the player's squad elsewhere first, to look at a report without a save) or `meitou-tools objects <x> <z> <radius>` (towns, foliage, level buildings and map features at the report's camera target; foliage also by how near its mesh reaches, `reach`).
Both take `--load-order <file>` (the report's load order list, one mod per line) and `--mod-dir <folder>` (more mods, e.g. a steamcmd download of
the reporter's workshop items) to run under the reporter's mods instead of the install's.

## GitHub automation

- `.github/labels.json` (kept by hand): `bug`, `parity`, `feedback` and the `area:<id>` labels, each described in one line.
  `issue-labels.yml` syncs it to GitHub on push (labels missing from the file are left alone, not deleted).
- New issues: `issue-labels.yml` runs `.github/scripts/label-issue.mjs`, where Claude Haiku 5.5 (`claude-haiku-5-5`, effort low,
  structured output) reads the title and text and picks one `area:` label from `labels.json`, or none. An issue that already has an
  area is skipped; a refusal (Haiku has no server-side fallback) leaves it unlabelled. Feedback issues, opened by the GitHub App,
  are labelled too. Needs the `ANTHROPIC_API_KEY` secret (skipped without it). Run by hand with `issues` (space-separated
  numbers) to label older issues.
- `.github/ISSUE_TEMPLATE/bug.yml`: the bug form (version, platform, copy, mods, what happened, the original, steps, log.txt).
- `site-check.yml`: builds `site/` on pull requests.
