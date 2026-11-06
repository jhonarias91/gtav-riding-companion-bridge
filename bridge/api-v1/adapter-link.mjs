// File link with the v1 game adapter (%LOCALAPPDATA%\GTBikeVCompanion\v1). The adapter writes telemetry.json
// every 250 ms and catalog.json when the route list changes, both by temp file + replace. Commands go to
// commands\<requestId>.json with a server-assigned expiry; the adapter answers <requestId>.ack.json.
import { readFile, writeFile, rename, mkdir, unlink, lstat } from 'node:fs/promises';
import { join } from 'node:path';

const MAX_TELEMETRY = 256 * 1024;
const MAX_CATALOG = 4 * 1024 * 1024;
const MAX_WORKOUTS = 8 * 1024 * 1024;
export const STALE_AFTER_MS = 5000;
export const DISPATCH_TTL_MS = 3000;

export class AdapterLink {
  constructor({ directory, now = () => Date.now(), monotonic = () => performance.now() }) {
    this.directory = directory;
    this.commands = join(directory, 'commands');
    this.now = now;
    this.monotonic = monotonic;
    this.lastSequence = null;
    this.lastInstance = null;
    this.lastAdvanceAt = null;
    this.catalogCache = null;
  }

  /** Reads one adapter file. A replace in progress (Windows sharing errors, torn read) is retried briefly. */
  async readJson(name, max) {
    const file = join(this.directory, name);
    for (let attempt = 0; ; attempt++) {
      try {
        const info = await lstat(file);
        if (!info.isFile() || info.size > max) throw Object.assign(new Error('size'), { code: 'ESIZE' });
        return JSON.parse(await readFile(file, 'utf8'));
      } catch (e) {
        const transient = ['EPERM', 'EBUSY', 'EACCES'].includes(e.code) || e instanceof SyntaxError;
        if (!transient || attempt >= 3) throw e;
        await new Promise(r => setTimeout(r, 15));
      }
    }
  }

  /**
   * Returns { raw, ageMs, fresh } or null when there is no usable snapshot. Fresh needs a recent capture
   * time AND a sequence that keeps advancing on the bridge's monotonic clock (a frozen game keeps an old
   * file with a plausible timestamp only briefly).
   */
  async snapshot() {
    let raw;
    try {
      raw = await this.readJson('telemetry.json', MAX_TELEMETRY);
    } catch {
      return null;
    }
    if (raw?.schema !== 'gtbikev-adapter-v1' || typeof raw.adapterInstanceId !== 'string' || !Number.isInteger(raw.sequence)) return null;
    const captured = Date.parse(raw.capturedAtUtc);
    if (!Number.isFinite(captured)) return null;
    const mono = this.monotonic();
    if (raw.adapterInstanceId !== this.lastInstance || raw.sequence !== this.lastSequence) {
      this.lastInstance = raw.adapterInstanceId;
      this.lastSequence = raw.sequence;
      this.lastAdvanceAt = mono;
    }
    const ageMs = Math.max(0, Math.round(this.now() - captured));
    const stalled = mono - this.lastAdvanceAt > STALE_AFTER_MS;
    return { raw, ageMs, fresh: ageMs <= STALE_AFTER_MS && !stalled };
  }

  /** Route catalog written by the adapter; cached by revision. */
  async catalog() {
    try {
      const c = await this.readJson('catalog.json', MAX_CATALOG);
      if (typeof c?.catalogRevision !== 'string' || !Array.isArray(c.routes)) return null;
      if (this.catalogCache?.catalogRevision !== c.catalogRevision) this.catalogCache = c;
      return this.catalogCache;
    } catch {
      return null;
    }
  }

  /** Workout plans parsed by the adapter with the mod's own parser (workouts.json); cached by revision. */
  async workouts() {
    try {
      const c = await this.readJson('workouts.json', MAX_WORKOUTS);
      if (typeof c?.catalogRevision !== 'string' || !Array.isArray(c.plans)) return null;
      if (this.workoutsCache?.catalogRevision !== c.catalogRevision) this.workoutsCache = c;
      return this.workoutsCache;
    } catch {
      return null;
    }
  }

  /** Writes the command and waits for the adapter's ack. Returns the ack, or null when none arrived in time. */
  async dispatch(command, adapterInstanceId) {
    await mkdir(this.commands, { recursive: true });
    const id = command.requestId;
    const path = join(this.commands, `${id}.json`);
    const temp = join(this.commands, `${id}.tmp`);
    const ackPath = join(this.commands, `${id}.ack.json`);
    const payload = {
      requestId: id,
      adapterInstanceId,
      type: command.type,
      expectedRevision: command.expectedRevision,
      args: command.args,
      expiresAtUtc: new Date(this.now() + DISPATCH_TTL_MS).toISOString(),
    };
    try {
      await writeFile(temp, JSON.stringify(payload), { flag: 'wx' });
      await rename(temp, path);
      const deadline = this.monotonic() + DISPATCH_TTL_MS + 500;
      while (this.monotonic() < deadline) {
        try {
          const ack = JSON.parse(await readFile(ackPath, 'utf8'));
          if (ack.requestId !== id || ack.adapterInstanceId !== adapterInstanceId) return { status: 'rejected', reasonCode: 'INSTANCE_CHANGED' };
          return ack;
        } catch (e) {
          if (e.code !== 'ENOENT' && !(e instanceof SyntaxError)) throw e;
        }
        await new Promise(r => setTimeout(r, 60));
      }
      return null;
    } finally {
      for (const file of [path, temp, ackPath]) await unlink(file).catch(() => {});
    }
  }
}
