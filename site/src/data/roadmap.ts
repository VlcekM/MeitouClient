// The roadmap (page /roadmap): what each Meitou version brings, kept by hand. The page shows the versions top to bottom in the order
// listed here (1.0 first, then 2.0 below it). A version lists its features directly, by area, or both; name, blurb and areas are
// optional. Each feature has a status: done = in Meitou, wip = in Meitou with known gaps, planned = not started. A feature's `id` is
// its link (/roadmap#id): keep it when the name is reworded. A note may hold links written as [text](https://...).

export type Status = 'done' | 'wip' | 'planned';
export interface Feature { id: string; name: string; status: Status; note: string; }
export interface Area { id: string; name: string; blurb?: string; features: Feature[]; }
export interface Version { version: string; name?: string; blurb?: string; features?: Feature[]; areas?: Area[]; }

export const statuses: Record<Status, { label: string; desc: string }> = {
  done: { label: 'Done', desc: 'in Meitou' },
  wip: { label: 'In progress', desc: 'in Meitou with known gaps' },
  planned: { label: 'Planned', desc: 'not started' },
};
export const order: Status[] = ['done', 'wip', 'planned'];

export const versions: Version[] = [
  { version: "1.0", features: [
    { id: "feature-parity", name: "Feature parity", status: "wip", note: "" },
    { id: "re-kenshi-mods", name: "Selected RE_Kenshi mod compatibility", status: "planned", note: "Which RE_Kenshi mods are covered: [the compatibility sheet](https://docs.google.com/spreadsheets/d/1h6HXRgT1MbioPtgj3enuXIdevj2JWaRU8qSz-99Haoo/edit?gid=0#gid=0). Some we port ourselves, and Meitou substitutes their DLLs when you play them." },
  ] },
  // A version with a name, a blurb and its features by area (copy it, remove the //):
  // { version: "2.0", name: "...", blurb: "...", areas: [
  //   { id: "startup", name: "Startup and saves", blurb: "New games, the main menu, loading and saving.", features: [
  //     { id: "load-a-save", name: "Load an original Kenshi save", status: "done", note: "Large modded saves load and play." },
  //   ] },
  // ] },
];

export const featuresOf = (v: Version) => [...(v.features ?? []), ...(v.areas ?? []).flatMap((a) => a.features)];
export const allFeatures = versions.flatMap(featuresOf);
export const countBy = (features: Feature[]) =>
  Object.fromEntries(order.map((k) => [k, features.filter((f) => f.status === k).length])) as Record<Status, number>;
/** The anchor of a version's section: "v1-0" for 1.0. */
export const versionId = (v: Version) => `v${v.version.replace(/\W/g, '-')}`;
