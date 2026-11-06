// P7: finished activities in the mod's Activities folder only (Config.UserActivitiesPath, reported by the adapter
// in paths.json and never sent to the phone). Top-level *.fit files, no recursion, no links, size-limited.
// Summaries come from the FIT session message; missing values stay null (no recomputation with today's FTP).
import { createHash } from 'node:crypto';
import { readFile, readdir, lstat, realpath } from 'node:fs/promises';
import { join, isAbsolute, basename, dirname } from 'node:path';
import { Decoder, Stream } from '@garmin/fitsdk';

export const MAX_FIT_BYTES = 20 * 1024 * 1024;
const MAX_FILES = 2000;
const NAME = /^[^\\/:*?"<>|]{1,120}\.fit$/i;
const POWER_POINTS = 600;

export const activityId = name => `a-${createHash('sha256').update(name.toLowerCase()).digest('hex').slice(0, 16)}`;

export class ActivityLibrary {
  constructor({ adapterDirectory }) {
    this.pathsFile = join(adapterDirectory, 'paths.json');
    this.summaries = new Map(); // id -> { key, detail }
  }

  /** Canonical Activities folder or null when unknown/invalid. */
  async root() {
    try {
      const info = await lstat(this.pathsFile);
      if (!info.isFile() || info.size > 8192) return null;
      const { activitiesPath } = JSON.parse(await readFile(this.pathsFile, 'utf8'));
      if (typeof activitiesPath !== 'string' || !isAbsolute(activitiesPath)) return null;
      const dir = await realpath(activitiesPath);
      return (await lstat(dir)).isDirectory() ? dir : null;
    } catch {
      return null;
    }
  }

  /** Every acceptable file, newest first. */
  async entries() {
    const root = await this.root();
    if (!root) return null;
    const out = [];
    for (const entry of await readdir(root, { withFileTypes: true })) {
      if (out.length >= MAX_FILES) break;
      if (!entry.isFile() || entry.isSymbolicLink() || !NAME.test(entry.name)) continue;
      const path = join(root, entry.name);
      try {
        const info = await lstat(path);
        if (!info.isFile() || info.size < 1 || info.size > MAX_FIT_BYTES) continue;
        // Reparse points/junctions resolve elsewhere: the canonical path must stay in the folder.
        const canonical = await realpath(path);
        if (dirname(canonical) !== root || basename(canonical).toLowerCase() !== entry.name.toLowerCase()) continue;
        out.push({ id: activityId(entry.name), fileName: entry.name, path: canonical, sizeBytes: info.size, mtimeMs: info.mtimeMs });
      } catch { /* vanished while listing */ }
    }
    return out.sort((a, b) => b.mtimeMs - a.mtimeMs);
  }

  async page(offset, limit) {
    const all = await this.entries();
    if (!all) return { available: false, total: 0, offset: 0, items: [] };
    return {
      available: true,
      total: all.length,
      offset,
      items: all.slice(offset, offset + limit).map(e => ({ id: e.id, fileName: e.fileName, modifiedUtc: new Date(e.mtimeMs).toISOString(), sizeBytes: e.sizeBytes })),
    };
  }

  async find(id) {
    if (!/^a-[a-f0-9]{16}$/.test(id)) return null;
    return (await this.entries())?.find(e => e.id === id) ?? null;
  }

  async findByName(name) {
    if (typeof name !== 'string' || !NAME.test(name)) return null;
    return (await this.entries())?.find(e => e.fileName.toLowerCase() === name.toLowerCase()) ?? null;
  }

  /** Bytes of a listed file, re-checked at read time. */
  async bytes(entry) {
    const data = await readFile(entry.path);
    if (data.length !== entry.sizeBytes || data.length > MAX_FIT_BYTES) throw Object.assign(new Error('changed'), { code: 'FIT_INVALID' });
    return data;
  }

  /** { activity } detail or throws FIT_INVALID. Cached per file version. */
  async detail(entry) {
    const key = `${entry.sizeBytes}:${entry.mtimeMs}`;
    const cached = this.summaries.get(entry.id);
    if (cached?.key === key) return cached.detail;
    const detail = { activity: { id: entry.id, fileName: entry.fileName, sizeBytes: entry.sizeBytes, ...summarize(await this.bytes(entry)) } };
    this.summaries.set(entry.id, { key, detail });
    if (this.summaries.size > 50) this.summaries.delete(this.summaries.keys().next().value);
    return detail;
  }
}

const num = (v, min = 0) => (typeof v === 'number' && Number.isFinite(v) && v >= min ? Math.round(v * 10) / 10 : null);

/** FIT bytes -> summary fields of activities.schema.json (source fit-session). */
export function summarize(bytes) {
  const decoder = new Decoder(Stream.fromBuffer(bytes));
  if (!decoder.isFIT() || !decoder.checkIntegrity()) throw Object.assign(new Error('integrity'), { code: 'FIT_INVALID' });
  const { messages, errors } = decoder.read();
  if (errors.length) throw Object.assign(new Error('decode'), { code: 'FIT_INVALID' });
  const s = messages.sessionMesgs?.[0];
  if (!s) throw Object.assign(new Error('no session'), { code: 'FIT_INVALID' });
  const sport = { cycling: 'cycling', running: 'running', rowing: 'rowing' }[s.sport] ?? 'unknown';
  const start = s.startTime instanceof Date ? s.startTime : null;
  return {
    source: 'fit-session',
    sport,
    startUtc: start && Number.isFinite(start.getTime()) ? start.toISOString() : null,
    elapsedS: num(s.totalElapsedTime),
    movingS: num(s.totalTimerTime),
    distanceM: num(s.totalDistance),
    ascentM: num(s.totalAscent),
    avgPowerW: num(s.avgPower),
    maxPowerW: num(s.maxPower),
    avgHeartRateBpm: num(s.avgHeartRate),
    maxHeartRateBpm: num(s.maxHeartRate),
    avgCadenceRpm: num(s.avgCadence),
    calories: num(s.totalCalories),
    power: powerSeries(messages.recordMesgs ?? [], start),
  };
}

/** Bucketed average of valid power samples; a bucket without samples is a real gap (null). */
function powerSeries(records, start) {
  const samples = records.filter(r => r.timestamp instanceof Date && typeof r.power === 'number' && Number.isFinite(r.power) && r.power >= 0);
  if (samples.length === 0) return null;
  const t0 = (start ?? samples[0].timestamp).getTime();
  const span = Math.max(1, (samples.at(-1).timestamp.getTime() - t0) / 1000);
  const bucketS = Math.max(1, Math.ceil(span / POWER_POINTS));
  const count = Math.min(POWER_POINTS, Math.floor(span / bucketS) + 1);
  const sums = new Array(count).fill(0);
  const counts = new Array(count).fill(0);
  for (const r of samples) {
    const i = Math.min(count - 1, Math.max(0, Math.floor((r.timestamp.getTime() - t0) / 1000 / bucketS)));
    sums[i] += r.power;
    counts[i]++;
  }
  return { bucketS, values: sums.map((sum, i) => (counts[i] ? Math.round(sum / counts[i]) : null)) };
}
