// The roadmap (page /roadmap): what each Meitou version brings, kept by hand. Each version lists its features by area, and each
// feature has a status: done = in Meitou, wip = in Meitou with known gaps, planned = not started. Add a version as a new entry in
// `versions` (oldest first), a feature as a line in its area. A feature's `id` is its link (/roadmap#id): keep it when the name is reworded.

export type Status = 'done' | 'wip' | 'planned';
export interface Feature { id: string; name: string; status: Status; note: string; }
export interface Area { id: string; name: string; blurb: string; features: Feature[]; }
export interface Version { version: string; name: string; blurb: string; areas: Area[]; }

export const statuses: Record<Status, { label: string; desc: string }> = {
  done: { label: 'Done', desc: 'in Meitou' },
  wip: { label: 'In progress', desc: 'in Meitou with known gaps' },
  planned: { label: 'Planned', desc: 'not started' },
};
export const order: Status[] = ['done', 'wip', 'planned'];

export const versions: Version[] = [
  // An example version, with one area and one feature (copy it, remove the //):
  // { version: "1.0", name: "Kenshi, unchanged", blurb: "Every system of unmodded Kenshi 1.0.68.", areas: [
  //   { id: "startup", name: "Startup and saves", blurb: "New games, the main menu, loading and saving.", features: [
  //     { id: "load-a-save", name: "Load an original Kenshi save", status: "done", note: "Large modded saves load and play." },
  //   ] },
  // ] },
];

export const featuresOf = (v: Version) => v.areas.flatMap((a) => a.features);
export const allFeatures = versions.flatMap(featuresOf);
export const countBy = (features: Feature[]) =>
  Object.fromEntries(order.map((k) => [k, features.filter((f) => f.status === k).length])) as Record<Status, number>;
/** The anchor of a version's section: "v1-0" for 1.0. */
export const versionId = (v: Version) => `v${v.version.replace(/\W/g, '-')}`;
