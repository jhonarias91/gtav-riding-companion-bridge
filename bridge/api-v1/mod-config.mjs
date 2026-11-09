// P15 game options: an allowlisted subset of GTBike V's GTBikeVConfig.ini ([main] gameplay keys and two [network]
// switches), read and written by the bridge itself, never through the adapter.
// GTBike V keeps the whole file in memory (SHVDN ScriptSettings) and rewrites it on every save, so edits are refused
// while GTA V runs and apply the next time the game starts. Every write checks the revision the client read, keeps a
// backup and replaces only the allowlisted lines; any other line, comment or key order stays as it was.
import { createHash, randomUUID } from 'node:crypto';
import { execFile } from 'node:child_process';
import { mkdir, readFile, readdir, rename, unlink, writeFile } from 'node:fs/promises';
import { basename, dirname, join } from 'node:path';

const MAX_FILE = 256 * 1024;
const KEEP_BACKUPS = 10;
const WRITING_FILES = new Set(); // All allowlisted views of this INI share one write lock.
const DEFAULT_SERVER = 'multiplayer.gtbikev.com:4499';
export const TRAFFIC_STYLES = ['NORMAL', 'AGRESSIVE', 'BREAKLAWS']; // Spelled as GTBike V parses them.
export const BOT_TYPES = [0, 1, 2, 3, 4]; // random, companion, climber, sprinter, time trialer

export class ModConfigError extends Error {
  constructor(code, status, details = {}) {
    super(code);
    this.code = code;
    this.status = status;
    this.details = details;
  }
}

/** Defaults GTBike V 0.8.0.4 writes into a new file (Config.cs readConfig). */
export const DEFAULTS = Object.freeze({
  trafficBikesPercent: 50, trafficStyle: 'NORMAL', crowdsEnabled: true, crowdsSlopePercent: 6, crowdsAmount: 50,
  bots: [1, 0, 0], defensiveLevel: 1, waypointColor: 'FFADFF2F', waypointRadius: 20, fineSteering: 0.7,
  screenshotIntervalKm: 5, screenshotXOffset: 0, menuAutoOpen: true, showClock: true, debugWindow: false, logLevel: 3,
  gpsOriginLat: -19.10637, gpsOriginLon: -169.871, telemetry: true, multiplayer: true,
});

const int = (min, max) => v => (Number.isInteger(v) && v >= min && v <= max ? v : undefined);
const num = (min, max, decimals) => v => (typeof v === 'number' && Number.isFinite(v) && v >= min && v <= max ? Math.round(v * 10 ** decimals) / 10 ** decimals : undefined);
const bool = v => (typeof v === 'boolean' ? v : undefined);
const parseBool = s => (/^\s*true\s*$/i.test(s) ? true : /^\s*false\s*$/i.test(s) ? false : undefined);
const parseNum = s => (/^\s*-?\d+(\.\d+)?\s*$/.test(s) ? Number(s) : undefined);
const fmt = n => String(n); // JS numbers print with '.' like the invariant culture GTBike V reads with.

/**
 * Each option: INI section/key, how to read it from the raw value and how to validate a client value.
 * Options sharing one INI key (traffic, crowds, GPS origin) are written together.
 */
const OPTIONS = {
  trafficBikesPercent: { key: 'ChangeCarsForBikes', check: int(0, 100), read: s => parseNum(s.split(',')[0]) },
  trafficStyle: { key: 'ChangeCarsForBikes', check: v => (TRAFFIC_STYLES.includes(v) ? v : undefined), read: s => { const v = (s.split(',')[1] ?? '').trim().toUpperCase(); return TRAFFIC_STYLES.includes(v) ? v : 'NORMAL'; } },
  crowdsEnabled: { key: 'CheeringCrowds', check: bool, read: s => parseBool(s.split(',')[0]) },
  crowdsSlopePercent: { key: 'CheeringCrowds', check: int(0, 20), read: s => parseNum(s.split(',')[1] ?? '') },
  crowdsAmount: { key: 'CheeringCrowds', check: int(0, 100), read: s => parseNum(s.split(',')[2] ?? '') },
  bots: {
    key: 'BotDefinition',
    check: v => (Array.isArray(v) && v.length <= 5 && v.every(b => BOT_TYPES.includes(b)) ? [...v] : undefined),
    read: s => (s.trim() === '' ? [] : s.split(',').map(x => { const n = Number(x.trim()); return BOT_TYPES.includes(n) ? n : 0; }).slice(0, 5)),
  },
  defensiveLevel: { key: 'DefensiveLevel', check: int(0, 2), read: parseNum },
  waypointColor: { key: 'WayPointDefaultColor', check: v => (typeof v === 'string' && /^[0-9A-Fa-f]{8}$/.test(v) ? v.toUpperCase() : undefined), read: s => (/^[0-9A-Fa-f]{8}$/.test(s.trim()) ? s.trim().toUpperCase() : undefined) },
  waypointRadius: { key: 'WayPointDefaultRadius', check: int(0, 40), read: s => { const n = parseNum(s); return n === undefined ? undefined : Math.round(n); } },
  fineSteering: { key: 'FineSteeringValue', check: num(0.1, 1, 2), read: parseNum },
  screenshotIntervalKm: { key: 'ScreenshotIntervalKm', check: int(1, 100), read: parseNum },
  screenshotXOffset: { key: 'ScreenshotXOffset', check: int(-20000, 20000), read: parseNum },
  menuAutoOpen: { key: 'MenuAutoOpen', check: bool, read: parseBool },
  showClock: { key: 'ShowRTC', check: bool, read: parseBool },
  debugWindow: { key: 'DebugWindow', check: bool, read: parseBool },
  logLevel: { key: 'LogLevel', check: int(0, 5), read: parseNum },
  gpsOriginLat: { key: 'InitialGPSPointLat', check: num(-90, 90, 6), read: parseNum },
  gpsOriginLon: { key: 'InitialGPSPointLong', check: num(-180, 180, 6), read: parseNum },
  telemetry: { section: 'network', key: 'TelemetryReporting', check: bool, read: parseBool },
  // On/off only: the server address itself is never exposed or accepted. Off = GTBike V's documented "NONE".
  multiplayer: { section: 'network', key: 'MultiplayerServerURL', check: bool, read: s => s.trim().toUpperCase() !== 'NONE' },
};
export const OPTION_NAMES = Object.keys(OPTIONS);

/** INI text for the keys touched by [values] (a full option set). */
function iniValues(values, touched) {
  const out = new Map();
  for (const name of touched) {
    const { key, section = 'main' } = OPTIONS[name];
    const id = `${section}/${key}`;
    if (out.has(id)) continue;
    let text;
    switch (key) {
      case 'ChangeCarsForBikes': text = `${values.trafficBikesPercent},${values.trafficStyle}`; break;
      case 'CheeringCrowds': text = `${values.crowdsEnabled ? 'True' : 'False'}, ${values.crowdsSlopePercent}, ${values.crowdsAmount}`; break;
      case 'BotDefinition': text = values.bots.join(','); break;
      case 'MultiplayerServerURL': text = values.multiplayer ? values.serverWhenOn : 'NONE'; break;
      default: {
        const v = values[name];
        text = typeof v === 'boolean' ? (v ? 'True' : 'False') : typeof v === 'number' ? fmt(v) : String(v);
      }
    }
    out.set(id, { section, key, text });
  }
  return [...out.values()];
}

export function decodeIni(bytes) {
  if (bytes[0] === 0xff && bytes[1] === 0xfe) return { text: bytes.subarray(2).toString('utf16le'), encoding: 'utf16le', bom: true };
  const bom = bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf;
  const content = bom ? bytes.subarray(3) : bytes;
  const text = content.toString('utf8');
  if (!Buffer.from(text, 'utf8').equals(content)) throw new ModConfigError('INVALID_REQUEST', 422, { field: 'encoding' });
  return { text, encoding: 'utf8', bom };
}

function encodeIni(text, { encoding, bom }) {
  if (encoding === 'utf16le') return Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(text, 'utf16le')]);
  return Buffer.concat([bom ? Buffer.from([0xef, 0xbb, 0xbf]) : Buffer.alloc(0), Buffer.from(text, 'utf8')]);
}

/** { section -> { key(lowercase) -> { line, value } } } plus the line array. Section and key names are case-insensitive. */
export function parseIni(text) {
  const lines = text.split(/\r?\n/);
  const sections = new Map();
  let section = '';
  lines.forEach((line, index) => {
    const header = /^\s*\[([^\]]+)\]\s*$/.exec(line);
    if (header) { section = header[1].trim().toLowerCase(); if (!sections.has(section)) sections.set(section, { header: index, keys: new Map() }); return; }
    const pair = /^\s*([^=;#\s][^=]*?)\s*=(.*)$/.exec(line);
    if (pair && sections.has(section)) {
      const keys = sections.get(section).keys;
      const key = pair[1].toLowerCase();
      if (!keys.has(key)) keys.set(key, { index, value: pair[2].trim() });
    }
  });
  return { lines, sections, eol: text.includes('\r\n') ? '\r\n' : '\n' };
}

/** Replaces (or appends) only the given key lines. */
export function patchIni(text, entries) {
  const { lines, sections, eol } = parseIni(text);
  const appended = new Map(); // section -> new lines
  for (const { section, key, text: value } of entries) {
    const found = sections.get(section)?.keys.get(key.toLowerCase());
    if (found) {
      const name = /^\s*([^=]*?)\s*=/.exec(lines[found.index])[1];
      lines[found.index] = `${name} = ${value}`;
    } else {
      if (!appended.has(section)) appended.set(section, []);
      appended.get(section).push(`${key} = ${value}`);
    }
  }
  for (const [section, extra] of appended) {
    const info = sections.get(section);
    if (!info) {
      if (lines.length && lines[lines.length - 1] !== '') lines.push('');
      lines.push(`[${section}]`, ...extra);
      continue;
    }
    // Insert after the last non-empty line of that section.
    let at = info.header + 1;
    for (let i = info.header + 1; i < lines.length && !/^\s*\[/.test(lines[i]); i++) if (lines[i].trim() !== '') at = i + 1;
    lines.splice(at, 0, ...extra);
    for (const other of sections.values()) if (other.header >= at) other.header += extra.length; // Keep later sections' headers valid.
  }
  return lines.join(eol);
}

/** Option values found in the file; null where the line is missing or unreadable (the mod then uses its default). */
export function readOptions(text, definitions = OPTIONS) {
  const { sections } = parseIni(text);
  const options = {};
  for (const [name, { key, section = 'main', read }] of Object.entries(definitions)) {
    const raw = sections.get(section)?.keys.get(key.toLowerCase())?.value;
    const value = raw === undefined ? undefined : read(raw);
    options[name] = value === undefined || (typeof value === 'number' && Number.isNaN(value)) ? null : value;
  }
  return options;
}

/** Windows: is GTA V (Legacy or Enhanced) running? */
export function gtaRunning() {
  if (process.platform !== 'win32') return Promise.resolve(false);
  return new Promise(resolve => {
    execFile('tasklist.exe', ['/FO', 'CSV', '/NH'], { windowsHide: true, timeout: 5000, maxBuffer: 4 * 1024 * 1024 }, (error, stdout) => {
      if (error) return resolve(false);
      resolve(/^"(GTA5|GTA5_Enhanced)\.exe"/im.test(stdout));
    });
  });
}

export class ModConfigStore {
  /**
   * path: GTBikeVConfig.ini (null = unknown); backupDirectory: where copies go before each write;
   * isGameRunning: async () => boolean (process check, plus a fresh adapter in the server).
   */
  constructor({ path, backupDirectory, isGameRunning = gtaRunning, definitions = OPTIONS, defaults = DEFAULTS, serialize = iniValues, resource = 'modConfig', extra = () => ({}) }) {
    this.path = path;
    this.backupDirectory = backupDirectory;
    this.isGameRunning = isGameRunning;
    this.definitions = definitions;
    this.defaults = defaults;
    this.serialize = serialize;
    this.resource = resource;
    this.extra = extra;
    this.writing = false;
  }

  async readFile() {
    if (!this.path) throw new ModConfigError('NOT_FOUND', 404, { field: 'file' });
    let bytes;
    try { bytes = await readFile(this.path); } catch (e) {
      if (e.code === 'ENOENT') throw new ModConfigError('NOT_FOUND', 404, { field: 'file' });
      throw e;
    }
    if (bytes.length > MAX_FILE) throw new ModConfigError('PAYLOAD_TOO_LARGE', 413);
    return { bytes, ...decodeIni(bytes), revision: `modcfg-${createHash('sha256').update(bytes).digest('hex').slice(0, 24)}` };
  }

  /** GET /api/v1/mod-config */
  async document() {
    const running = await this.isGameRunning();
    let file;
    try { file = await this.readFile(); } catch (e) {
      if (e instanceof ModConfigError && e.code === 'NOT_FOUND') {
        return { available: false, reasonCode: 'NOT_FOUND', revision: null, gameRunning: running, editable: false, options: null, defaults: this.defaults, applyMode: 'nextGameStart', ...this.extra(null) };
      }
      throw e;
    }
    return {
      available: true, reasonCode: running ? 'GAME_RUNNING' : null, revision: file.revision, gameRunning: running,
      editable: !running, options: readOptions(file.text, this.definitions), defaults: this.defaults, applyMode: 'nextGameStart', ...this.extra(file.text),
    };
  }

  /** POST /api/v1/mod-config {expectedRevision, options:{...partial}} */
  async update(body) {
    if (!body || typeof body !== 'object' || Array.isArray(body) || Object.keys(body).some(k => !['expectedRevision', 'options'].includes(k))) throw new ModConfigError('INVALID_REQUEST', 422);
    const { expectedRevision, options } = body;
    if (typeof expectedRevision !== 'string' || !/^modcfg-[a-f0-9]{24}$/.test(expectedRevision)) throw new ModConfigError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (!options || typeof options !== 'object' || Array.isArray(options) || Object.keys(options).length === 0) throw new ModConfigError('INVALID_REQUEST', 422, { field: 'options' });
    const clean = {};
    for (const [name, value] of Object.entries(options)) {
      if (!Object.hasOwn(this.definitions, name)) throw new ModConfigError('INVALID_REQUEST', 422, { field: name });
      const checked = this.definitions[name].check(value);
      if (checked === undefined) throw new ModConfigError(typeof value === typeof this.defaults[name] || Array.isArray(value) ? 'VALUE_OUT_OF_RANGE' : 'INVALID_REQUEST', 422, { field: name });
      clean[name] = checked;
    }
    if (this.writing || WRITING_FILES.has(this.path)) throw new ModConfigError('BRIDGE_BUSY', 409);
    this.writing = true;
    WRITING_FILES.add(this.path);
    try {
      if (await this.isGameRunning()) throw new ModConfigError('GAME_RUNNING', 409);
      const current = await this.readFile();
      if (current.revision !== expectedRevision) throw new ModConfigError('REVISION_CONFLICT', 409, { resource: this.resource });
      // Options that share an INI key are rewritten from the file's current values plus the changes.
      const existing = readOptions(current.text, this.definitions);
      const merged = {};
      for (const name of Object.keys(this.definitions)) merged[name] = clean[name] ?? existing[name] ?? this.defaults[name];
      const server = parseIni(current.text).sections.get('network')?.keys.get('multiplayerserverurl')?.value;
      merged.serverWhenOn = server && server.toUpperCase() !== 'NONE' ? server : DEFAULT_SERVER; // Turning it on restores the official server.
      const next = encodeIni(patchIni(current.text, this.serialize(merged, Object.keys(clean), current.text)), current);
      await this.backup(current.bytes);
      const temp = join(dirname(this.path), `.${basename(this.path)}.${randomUUID()}.tmp`);
      try {
        await writeFile(temp, next, { flag: 'wx' });
        if ((await this.readFile()).revision !== expectedRevision) throw new ModConfigError('REVISION_CONFLICT', 409, { resource: this.resource });
        await rename(temp, this.path);
      } finally {
        await unlink(temp).catch(() => {});
      }
      return { ...(await this.document()), saved: Object.keys(clean) };
    } finally {
      this.writing = false;
      WRITING_FILES.delete(this.path);
    }
  }

  async backup(bytes) {
    await mkdir(this.backupDirectory, { recursive: true });
    const name = `GTBikeVConfig-${new Date().toISOString().replace(/[:.]/g, '-')}-${randomUUID().slice(0, 8)}.ini`;
    await writeFile(join(this.backupDirectory, name), bytes, { flag: 'wx', mode: 0o600 });
    const old = (await readdir(this.backupDirectory)).filter(n => /^GTBikeVConfig-.*\.ini$/.test(n)).sort().reverse().slice(KEEP_BACKUPS);
    for (const n of old) await unlink(join(this.backupDirectory, n)).catch(() => {});
  }
}
