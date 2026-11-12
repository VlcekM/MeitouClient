// Gives an issue its area:<id> label: Claude Haiku 5.5 reads the title and text and picks one of the area labels in
// .github/labels.json (or none). An issue that already has an area label is left alone. The issue is the one the event opened, or
// each of ISSUES (space-separated numbers, from a run by hand). ANTHROPIC_API_KEY, GITHUB_TOKEN, GITHUB_EVENT_PATH.
import { readFileSync } from 'node:fs';
import Anthropic from '@anthropic-ai/sdk';
import { betaZodOutputFormat } from '@anthropic-ai/sdk/helpers/beta/zod';
import { z } from 'zod';
import { api } from './github.mjs';

const areas = JSON.parse(readFileSync(new URL('../labels.json', import.meta.url), 'utf8')).filter((l) => l.name.startsWith('area:'));
const Answer = z.object({ area: z.enum([...areas.map((a) => a.name.slice('area:'.length)), 'none']) });
const system = 'MeitouClient is a C# reimplementation of the game Kenshi\'s engine. Pick the one area of the project the GitHub issue below '
  + 'is about, from this list, or "none" when no area fits (a question, a request about the project itself):\n'
  + areas.map((a) => `- ${a.name.slice('area:'.length)}: ${a.description}`).join('\n');
const client = new Anthropic();

const numbers = (process.env.ISSUES ?? '').split(/[\s,#]+/).filter(Boolean);
const issues = numbers.length
  ? await Promise.all(numbers.map((n) => api(`/repos/{repo}/issues/${n}`)))
  : [JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8')).issue];
for (const issue of issues) await label(issue);

/** Asks for the issue's area and adds its label. */
async function label(issue) {
  if (issue.labels.some((l) => l.name.startsWith('area:'))) {
    console.log(`#${issue.number} already has an area.`);
    return;
  }

  const area = await ask(issue);
  if (!area || area === 'none') {
    console.log(`#${issue.number}: no area${area ? ' fits' : ''}.`);
    return;
  }
  await api(`/repos/{repo}/issues/${issue.number}/labels`, { method: 'POST', body: { labels: [`area:${area}`] } });
  console.log(`#${issue.number}: area:${area}`);
}

/** The area id the model picks for the issue, "none", or null when it gives no answer. */
async function ask(issue) {
  // The issue text is cut to keep the request small; the title and the start say what it is about.
  const text = `# ${issue.title}\n\n${(issue.body ?? '').slice(0, 20000)}`;
  const response = await client.beta.messages.stream({
    model: 'claude-haiku-5-5',
    max_tokens: 4000,
    system,
    messages: [{ role: 'user', content: text }],
    output_config: { effort: 'low', format: betaZodOutputFormat(Answer) },
  }).finalMessage();

  // Haiku has no server-side fallback: a classifier refusal leaves the issue unlabelled, it does not fail the run.
  if (response.stop_reason !== 'end_turn') {
    console.log(`#${issue.number}: stop_reason ${response.stop_reason}.`);
    return null;
  }
  return Answer.parse(JSON.parse(response.content.filter((b) => b.type === 'text').map((b) => b.text).join(''))).area;
}
