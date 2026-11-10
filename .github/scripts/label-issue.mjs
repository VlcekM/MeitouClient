// Gives a newly opened issue its area:<id> label: Claude Haiku 5.5 reads the title and text and picks one of the area labels in
// .github/labels.json (or none). An issue that already has an area label is left alone. ANTHROPIC_API_KEY, GITHUB_TOKEN, GITHUB_EVENT_PATH.
import { readFileSync } from 'node:fs';
import Anthropic from '@anthropic-ai/sdk';
import { betaZodOutputFormat } from '@anthropic-ai/sdk/helpers/beta/zod';
import { z } from 'zod';
import { api } from './github.mjs';

const issue = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8')).issue;
const areas = JSON.parse(readFileSync(new URL('../labels.json', import.meta.url), 'utf8')).filter((l) => l.name.startsWith('area:'));
if (issue.labels.some((l) => l.name.startsWith('area:'))) {
  console.log(`#${issue.number} already has an area.`);
  process.exit(0);
}

const Answer = z.object({ area: z.enum([...areas.map((a) => a.name.slice('area:'.length)), 'none']) });
const system = 'MeitouClient is a C# reimplementation of the game Kenshi\'s engine. Pick the one area of the project the GitHub issue below '
  + 'is about, from this list, or "none" when no area fits (a question, a request about the project itself):\n'
  + areas.map((a) => `- ${a.name.slice('area:'.length)}: ${a.description}`).join('\n');
// The issue text is cut to keep the request small; the title and the start say what it is about.
const text = `# ${issue.title}\n\n${(issue.body ?? '').slice(0, 20000)}`;

// Haiku has no server-side fallback: a classifier refusal leaves the issue unlabelled, it does not fail the run.
const response = await new Anthropic().beta.messages.stream({
  model: 'claude-haiku-5-5',
  max_tokens: 4000,
  system,
  messages: [{ role: 'user', content: text }],
  output_config: { effort: 'low', format: betaZodOutputFormat(Answer) },
}).finalMessage();
if (response.stop_reason !== 'end_turn') {
  console.log(`#${issue.number}: no answer (stop_reason ${response.stop_reason}).`);
  process.exit(0);
}

const { area } = Answer.parse(JSON.parse(response.content.filter((b) => b.type === 'text').map((b) => b.text).join('')));
if (area === 'none') {
  console.log(`#${issue.number}: no area fits.`);
  process.exit(0);
}
await api(`/repos/{repo}/issues/${issue.number}/labels`, { method: 'POST', body: { labels: [`area:${area}`] } });
console.log(`#${issue.number}: area:${area}`);
