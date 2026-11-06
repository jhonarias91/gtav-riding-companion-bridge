// Shared fixture for bridge API v1 tests: simulated v1 adapter, bridge instance, pairing and command helpers.
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, readdir, rename, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import Ajv2020Module from 'ajv/dist/2020.js';
import { createBridgeV1 } from '../../bridge/api-v1/server.mjs';

export const Ajv2020 = Ajv2020Module.default ?? Ajv2020Module;
export const root = dirname(dirname(dirname(fileURLToPath(import.meta.url))));
export const contracts = join(root, 'bridge', 'contracts', 'v1');
export const fixtures = join(root, 'bridge', 'fixtures', 'v1');
export const readJson = async p => JSON.parse(await readFile(p, 'utf8'));

export async function schemas() {
  const ajv = new Ajv2020({ strict: true, strictTypes: false, allErrors: true });
  const ids = {};
  for (const name of ['common', 'hello', 'capabilities', 'state', 'command', 'operation', 'error', 'routes', 'route-preview', 'settings', 'devices', 'workouts', 'activities', 'map', 'riders', 'mod-config']) {
    const s = await readJson(join(contracts, `${name}.schema.json`));
    ajv.addSchema(s);
    ids[name] = s.$id;
  }
  return (name, doc) => {
    const validate = ajv.getSchema(ids[name]);
    assert.ok(validate(doc), `${name}: ${JSON.stringify(validate.errors?.slice(0, 3))}\n${JSON.stringify(doc).slice(0, 400)}`);
  };
}

/** Simulated adapter: owns telemetry.json/catalog.json and answers commands like GTBikeVCompanionV1. */
export class FakeAdapter {
  constructor(dir) {
    this.dir = dir;
    this.instance = 'adapter-test01';
    this.sequence = 0;
    this.generation = 1;
    this.raw = {
      schema: 'gtbikev-adapter-v1', modLoaded: true, modVersion: '0.8.0.4', binding: 'compatible', bindingProfile: 'gtbikev-0804', bindingIssues: [],
      gameControl: 'inControl', isDtOk: true, modLayer: 'dataReady', receivers: { ant: 'ready', ble: 'ready' }, sport: 'cycling',
      commands: ['mod.setActive', 'routes.apply', 'routes.preview', 'ui.openPanel', 'ui.closePanel', 'profile.update', 'rideSettings.update', 'appearance.select', 'devices.configure', 'devices.assign', 'devices.disconnect', 'activities.finish', 'workouts.select'],
      metrics: { powerW: 180, speedMps: 0, speedVirtual: true, cadenceRpm: 85, heartRateBpm: null, distanceM: 120.5, ridingTimeMs: 60000, slopeFraction: 0.01, windSpeedMps: 0, windDirRad: 0 },
      activity: { movingTimeMs: 55000, elevationGainM: 3, elevationLossM: 1, workoutPresent: false, workoutFinished: null },
      sensors: [{ role: 'power', present: true, source: 'ANT', deviceId: '54321', sensorType: 'FEC' }, { role: 'heartRate', present: false, source: 'unknown', deviceId: null, sensorType: 'unknown' }],
      activeRoute: null,
      routes: { catalogRevision: 'catalog-a1', panelOpen: false, previewRouteId: null, previewLoop: null, previewReverse: null, routesEnabled: true },
      settings: {
        weightKg: 72, ftpW: 230, hrMaxBpm: 185, autopilot: false, avoidObstacles: true, difficulty: 100, roadFeel: true,
        bike: { selected: 'Road', options: ['Road', 'Gravel'] }, character: { selected: 'Rider A', options: ['Rider A'] },
        enabled: { weight: true, ftp: true, autopilot: true, avoidObstacles: true, difficulty: true, roadFeel: true, bike: true, character: true },
      },
      devices: {
        roles: [
          { role: 'power', visible: true, current: { deviceId: '54321', source: 'ANT', sensorType: 'FEC' }, entries: [], isAuto: true, isLocked: false, skippable: true },
          { role: 'heartRate', visible: true, current: { deviceId: '777', source: 'BLE', sensorType: 'HR' }, entries: [], isAuto: true, isLocked: false, skippable: true },
          { role: 'steering', visible: true, current: null, entries: [], isAuto: true, isLocked: true, skippable: false },
          { role: 'runningSpeed', visible: false, current: null, entries: [], isAuto: true, isLocked: true, skippable: false },
        ],
      },
      workouts: { ready: true },
      workoutProgress: null,
      activities: { saveEnabled: true, activityDistanceM: 120.5, lastSaved: null },
    };
    this.workouts = {
      catalogRevision: 'workouts-a1',
      ftpReferenceW: 230,
      plans: [{
        id: 'p-00000000000000aa', name: 'Plan A', description: 'Synthetic.',
        workouts: [{
          id: 'w-00000000000000bb', name: 'Sweet Spot', author: 'Coach', description: 'Synthetic.', durationType: 'time', total: 1200,
          segments: [{ type: 'Warmup', start: 0, end: 300, startPowerW: 115, endPowerW: 172, cadenceRpm: null }, { type: 'Steady', start: 300, end: 1200, startPowerW: 207, endPowerW: 207, cadenceRpm: 90 }],
        }],
      }],
    };
    this.savedFit = null; // Buffer written into Activities on activities.finish
    this.catalog = {
      catalogRevision: 'catalog-a1',
      routes: [
        { id: 35, name: 'Coastal Loop', author: 'Someone', distanceM: 24300, elevationGainM: 310, elevationLossM: 310, maxElevationM: 120, categories: ['Flat'], types: ['Road'], loopAllowed: true, reverseAllowed: true, version: '1', description: 'Loop.', notes: null },
        { id: 36, name: 'Hill Climb', author: null, distanceM: 8100, elevationGainM: 640, elevationLossM: 12, maxElevationM: 690, categories: ['Climb'], types: [], loopAllowed: false, reverseAllowed: false, version: null, description: null, notes: null },
      ],
    };
    this.behaviour = {}; // type -> 'ignore' | 'reject' | 'noop' | 'apply' (default apply)
  }

  /**
   * Writes are queued and each snapshot is built when its turn comes, like the real adapter writing from one game Tick.
   * Without the queue the background tick and a test could rename their files out of order, so an older snapshot
   * (taken before the test changed raw) replaced a newer one and the bridge briefly saw the old state.
   */
  write(at = null) {
    const run = async () => {
      const doc = { ...this.raw, adapterInstanceId: this.instance, sequence: ++this.sequence, capturedAtUtc: (at ?? new Date()).toISOString(), sessionGeneration: this.generation };
      await atomic(join(this.dir, 'telemetry.json'), doc);
      await atomic(join(this.dir, 'catalog.json'), this.catalog);
      await atomic(join(this.dir, 'workouts.json'), this.workouts);
      await atomic(join(this.dir, 'paths.json'), { activitiesPath: join(this.dir, '..', 'Activities') });
    };
    this.writes = (this.writes ?? Promise.resolve()).then(run, run);
    return this.writes;
  }

  /** Mirrors DeviceInfo.ChangeDeviceConfig for one role. */
  configureDevice(a) {
    const r = this.raw.devices.roles.find(x => x.role === a.role);
    if (a.action === 'auto') { r.entries = []; r.isAuto = true; r.isLocked = r.current === null; r.skippable = r.current !== null; return; }
    r.entries.push({ deviceId: a.expectedDeviceId, source: a.expectedSource, sensorType: r.current.sensorType, excluded: a.action === 'skip' });
    r.isAuto = false;
    r.isLocked = a.action === 'lock';
    r.skippable = a.action !== 'lock';
    if (a.action === 'skip') r.current = null;
  }

  /** Mirrors GtbikeV1More.AssignDevice: native Auto + Lock with the phone's BLE device. The PC connects later (connectAssigned). */
  assignDevice(a) {
    const r = this.raw.devices.roles.find(x => x.role === a.role);
    r.entries = [{ deviceId: a.deviceId, source: 'BLE', sensorType: a.sensorType, excluded: false }];
    r.isAuto = false;
    r.isLocked = true;
    r.skippable = false;
  }

  /** Mirrors GtbikeV1More.DisconnectDevice: locked roles back to Auto, the PC drops the link (current = null). */
  disconnectDevice(a) {
    for (const r of this.raw.devices.roles) {
      if (r.entries.some(e => !e.excluded && e.source === 'BLE' && e.deviceId === a.deviceId)) { r.entries = []; r.isAuto = true; }
      if (r.current?.source === 'BLE' && r.current.deviceId === a.deviceId) { r.current = null; r.isLocked = r.isAuto; r.skippable = false; }
    }
  }

  /** The PC's BLE receiver found the expected address and connected. */
  connectAssigned(role) {
    const r = this.raw.devices.roles.find(x => x.role === role);
    const e = r.entries.find(x => !x.excluded);
    r.current = { deviceId: e.deviceId, source: e.source, sensorType: e.sensorType };
  }

  /** Processes pending commands once, like one adapter Tick. */
  async tick() {
    const dir = join(this.dir, 'commands');
    let names = [];
    try { names = await readdir(dir); } catch { return; }
    for (const name of names.filter(n => /^[0-9a-f-]{36}\.json$/.test(n))) {
      let cmd;
      try { cmd = await readJson(join(dir, name)); } catch { continue; } // Bridge removed it after its own timeout.
      const mode = this.behaviour[cmd.type] ?? 'apply';
      if (mode === 'ignore') continue;
      let ack = { status: 'dispatched', reasonCode: null };
      if (mode === 'reject') ack = { status: 'rejected', reasonCode: 'MOD_TRANSITION_IN_PROGRESS' };
      else if (mode === 'noop') ack = { status: 'noop', reasonCode: null };
      else this.apply(cmd);
      await atomic(join(dir, `${cmd.requestId}.ack.json`), { ...ack, requestId: cmd.requestId, adapterInstanceId: this.instance });
      await rm(join(dir, name), { force: true });
    }
    await this.saveIfPending();
    await this.write();
  }

  apply(cmd) {
    const a = cmd.args;
    const s = this.raw.settings;
    if (cmd.type === 'mod.setActive') { this.raw.isDtOk = a.enabled; this.raw.modLayer = a.enabled ? 'dataReady' : 'inactive'; }
    if (cmd.type === 'routes.apply') { this.raw.activeRoute = a.routeId === null ? null : { id: a.routeId, loop: a.loop, reverse: a.reverse }; if (a.routeId !== null) this.generation++; }
    if (cmd.type === 'ui.openPanel') this.raw.routes.panelOpen = true;
    if (cmd.type === 'ui.closePanel') this.raw.routes.panelOpen = false;
    if (cmd.type === 'routes.preview') { this.raw.routes.previewRouteId = a.routeId; this.raw.routes.previewLoop = true; this.raw.routes.previewReverse = false; }
    if (cmd.type === 'profile.update') Object.assign(s, a);
    if (cmd.type === 'rideSettings.update') Object.assign(s, a);
    if (cmd.type === 'appearance.select') { if (a.bikeId) s.bike.selected = a.bikeId; if (a.characterId) s.character.selected = a.characterId; }
    if (cmd.type === 'devices.configure') this.configureDevice(a);
    if (cmd.type === 'devices.assign') this.assignDevice(a);
    if (cmd.type === 'devices.disconnect') this.disconnectDevice(a);
    if (cmd.type === 'activities.finish') this.pendingSave = true;
    if (cmd.type === 'workouts.select') {
      // Mirrors GTBikeVScript.OnWorkoutSelected: the current activity gets the (reset) workout, replacing any running
      // one; its countdown starts. A new Workout object means a new generation (GtbikeV1More).
      const w = this.workouts.plans.flatMap(p => p.workouts).find(x => x.id === a.workoutId);
      this.raw.activity.workoutPresent = true;
      this.raw.activity.workoutFinished = false;
      const generation = (this.raw.workoutProgress?.generation ?? 0) + 1;
      this.raw.workoutProgress = { name: w.name, started: false, inCountdown: true, finished: false, durationType: w.durationType, elapsed: 0, total: w.total, segmentCount: w.segments.length, segment: null, generation };
    }
  }

  /** Like SaveCurrentActivity on the next Tick: writes the FIT, reports its name and starts a new Activity. */
  async saveIfPending() {
    if (!this.pendingSave) return;
    this.pendingSave = false;
    const name = 'GTBikeV_20261105_101500.fit';
    await mkdir(join(this.dir, '..', 'Activities'), { recursive: true });
    await writeFile(join(this.dir, '..', 'Activities', name), this.savedFit);
    this.raw.activities.lastSaved = { fromSessionGeneration: this.generation, fileName: name };
    this.generation++;
    this.raw.activities.activityDistanceM = 0;
  }
}

export async function atomic(path, value) {
  await mkdir(dirname(path), { recursive: true });
  const temp = `${path}.${randomUUID()}.tmp`;
  await writeFile(temp, JSON.stringify(value));
  // Windows refuses to replace a file another handle is reading; the real adapter also retries on its next Tick.
  for (let attempt = 0; ; attempt++) {
    try { await rename(temp, path); return; } catch (e) {
      if (attempt >= 20 || !['EPERM', 'EBUSY', 'EACCES'].includes(e.code)) throw e;
      await new Promise(r => setTimeout(r, 10));
    }
  }
}

export async function setup(t, { desktopToken = null, modConfig = {} } = {}) {
  const dir = await mkdtemp(join(tmpdir(), 'gtbv-v1-'));
  const adapter = new FakeAdapter(join(dir, 'v1'));
  await adapter.write();
  const bridge = await createBridgeV1({ adapterDirectory: join(dir, 'v1'), stateDirectory: join(dir, 'bridge-v1'), pin: '246810', desktopToken, modConfig });
  await new Promise(r => bridge.server.listen(0, '127.0.0.1', r));
  const base = `http://127.0.0.1:${bridge.server.address().port}`;
  // Adapter loop: one Tick every 50 ms while the test runs.
  let running = true;
  const loop = (async () => { while (running) { await adapter.tick(); await new Promise(r => setTimeout(r, 50)); } })();
  t.after(async () => {
    running = false;
    await loop;
    await bridge.close();
    await rm(dir, { recursive: true, force: true });
  });
  const request = (path, init = {}) => fetch(base + path, init);
  return { dir, adapter, bridge, request };
}

export async function pair(request) {
  const res = await request('/api/v1/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ pin: '246810', clientName: 'Test phone' }) });
  assert.equal(res.status, 200);
  const body = await res.json();
  return { Authorization: `Bearer ${body.token}`, 'Content-Type': 'application/json' };
}

export async function command(request, headers, type, args, expectedRevision = null) {
  const state = await (await request('/api/v1/state', { headers })).json();
  const body = { requestId: randomUUID(), adapterInstanceId: state.adapterInstanceId, type, expectedRevision, args };
  const res = await request('/api/v1/commands', { method: 'POST', headers, body: JSON.stringify(body) });
  return { res, body: await res.json(), requestId: body.requestId };
}

export async function waitFor(request, headers, requestId, statuses = ['applied', 'rejected', 'failed', 'unknown']) {
  for (let i = 0; i < 100; i++) {
    const op = await (await request(`/api/v1/operations/${requestId}`, { headers })).json();
    if (statuses.includes(op.status)) return op;
    await new Promise(r => setTimeout(r, 50));
  }
  throw new Error('operation did not settle');
}
