// Operation lifecycle (CONTRATO_PUENTE_V1.md §8): validated -> dispatched/pending -> applied | failed | unknown,
// or validated -> rejected. 'applied' always needs evidence read back from later snapshots; an adapter ack only
// means the native handler was invoked. A requestId never runs twice. Unknown outcomes are never retried.
import { randomBytes } from 'node:crypto';
import { cameraEvidence } from './camera.mjs';
import { gearEvidence } from './trainer.mjs';
import { addonEvidence } from './addons.mjs';

const RETENTION_MS = 10 * 60_000;
const MAX_OPERATIONS = 200;

/** How long each type may stay pending before it becomes 'unknown'. Route loading downloads and builds the course. */
export const EVIDENCE_TIMEOUT_MS = {
  'gears.shiftUp': 5000,
  'gears.shiftDown': 5000,
  'camera.next': 4000,
  'camera.setMode': 4000,
  'camera.look': 4000,
  'mod.setActive': 20_000,
  'routes.apply': 45_000,
  'routes.preview': 3_000,
  'ui.openPanel': 3_000,
  'ui.closePanel': 3_000,
  'profile.update': 3_000,
  'rideSettings.update': 3_000,
  'appearance.select': 8_000,
  'devices.configure': 5_000,
  'devices.assign': 5_000,
  'devices.disconnect': 8_000,
  'activities.finish': 15_000,
  'workouts.select': 8_000,
  'player.teleport': 15_000, // The adapter gives up after ~12 s of streaming and puts the rider back.
  'player.waypoint': 5_000,
  'player.clearWaypoint': 5_000,
  'addons.setEnabled': 10_000, // Starting locates the first pickup point on the adapter's frames (path nodes may stream in).
  'addons.configure': 5_000,
  'addons.saveSegment': 5_000,
  'addons.removeSegment': 5_000,
  'addons.savePlace': 5_000,
  'addons.removePlace': 5_000,
};
const TELEPORT_FAILURES = new Set(['VALUE_NOT_ALLOWED', 'GAME_NOT_IN_CONTROL', 'INTERNAL']);

const round2 = v => Math.round(v * 100) / 100;
/** The adapter's waypoint report, bounded: {x, y, routeFound, routeLengthM}. */
export const waypointOf = w => ({ x: round2(w.x), y: round2(w.y), routeFound: w.routeFound === true,
  routeLengthM: Number.isInteger(w.routeLengthM) && w.routeLengthM > 0 && w.routeLengthM < 100_000 ? w.routeLengthM : null });

const sameRoute = (a, b) => (a === null && b === null) || (a !== null && b !== null && a.id === b.id && a.loop === b.loop && a.reverse === b.reverse);

/**
 * Evidence per command type. Receives the command args, the baseline captured at dispatch and the current
 * documents. Returns { status: 'applied'|'failed', observed, code? } or null while still pending.
 */
const EVIDENCE = {
  'gears.shiftUp': gearEvidence,
  'gears.shiftDown': gearEvidence,
  'camera.next': cameraEvidence,
  'camera.setMode': cameraEvidence,
  'camera.look': cameraEvidence,
  'mod.setActive': (args, base, { state }) => {
    if (state.layers.adapter !== 'fresh') return null;
    const observed = { mod: state.layers.mod, antReceiver: state.layers.antReceiver, bleReceiver: state.layers.bleReceiver, sequence: state.sequence };
    if (state.layers.mod === 'error') return { status: 'failed', observed, code: 'INTERNAL' };
    if (args.enabled && state.layers.mod === 'dataReady') return { status: 'applied', observed };
    if (!args.enabled && state.layers.mod === 'inactive') return { status: 'applied', observed };
    return null;
  },
  'routes.apply': (args, base, { state }) => {
    if (state.layers.adapter !== 'fresh') return null;
    const target = args.routeId === null ? null : { id: args.routeId, loop: args.loop, reverse: args.reverse };
    if (!sameRoute(state.activeRoute, target)) return null;
    // Loading a course re-creates the activity (GTBikeVScript.InitializeCourse -> CreateNewActivity).
    if (target !== null && state.sessionGeneration === base.sessionGeneration) return null;
    return { status: 'applied', observed: { activeRoute: state.activeRoute, sessionGeneration: state.sessionGeneration, sequence: state.sequence } };
  },
  'routes.preview': (args, base, { preview }) =>
    preview.panelOpen && preview.routeId === args.routeId ? { status: 'applied', observed: { preview } } : null,
  'ui.openPanel': (args, base, { preview }) => (preview.panelOpen ? { status: 'applied', observed: { panelOpen: true } } : null),
  'ui.closePanel': (args, base, { preview }) => (!preview.panelOpen ? { status: 'applied', observed: { panelOpen: false } } : null),
  'profile.update': (args, base, { settings }) => {
    if (!settings) return null;
    const observed = {};
    for (const key of Object.keys(args)) {
      const value = settings.profile[key].value;
      if (value === null || Math.round(value) !== args[key]) return null;
      observed[key] = value;
    }
    return { status: 'applied', observed: { profile: observed, settingsRevision: settings.settingsRevision } };
  },
  'rideSettings.update': (args, base, { settings }) => {
    if (!settings) return null;
    const observed = {};
    for (const key of Object.keys(args)) {
      if (settings.ride[key].value !== args[key]) return null;
      observed[key] = args[key];
    }
    return { status: 'applied', observed: { ride: observed, settingsRevision: settings.settingsRevision } };
  },
  'devices.configure': (args, base, { devices }) => {
    const role = devices?.roles?.find(r => r.role === args.role);
    if (!role) return null;
    const same = e => e.deviceId === args.expectedDeviceId && e.source === args.expectedSource;
    const done = args.action === 'auto' ? role.policy === 'auto'
      : args.action === 'lock' ? role.entries.some(e => !e.excluded && same(e))
        : role.entries.some(e => e.excluded && same(e));
    return done ? { status: 'applied', observed: { role: args.role, policy: role.policy, current: role.current, devicesRevision: devices.devicesRevision } } : null;
  },
  // 'applied' = the mod's rule now names that BLE address. The PC connecting to it is a separate observation:
  // the phone keeps reading device-roles until current.deviceId matches (connected says whether it already does).
  'devices.assign': (args, base, { devices }) => {
    const role = devices?.roles?.find(r => r.role === args.role);
    if (!role || role.policy !== 'locked') return null;
    if (!role.entries.some(e => !e.excluded && e.source === 'BLE' && e.deviceId === args.deviceId)) return null;
    const connected = role.current?.source === 'BLE' && role.current.deviceId === args.deviceId;
    return { status: 'applied', observed: { role: args.role, policy: role.policy, current: role.current, connected, devicesRevision: devices.devicesRevision } };
  },
  // 'applied' = no role of the PC uses that BLE address any more.
  'devices.disconnect': (args, base, { devices }) => {
    if (!devices?.devicesRevision) return null;
    if (devices.roles.some(r => r.current?.source === 'BLE' && r.current.deviceId === args.deviceId)) return null;
    return { status: 'applied', observed: { deviceId: args.deviceId, connected: false, devicesRevision: devices.devicesRevision } };
  },
  'activities.finish': (args, base, { state, lastSaved }) => {
    // The adapter reports the file name the previous Activity wrote (Activity.ActivityFileName) once the
    // mod replaced it; the bridge then checks that file exists in the Activities folder.
    if (!lastSaved || lastSaved.fromSessionGeneration !== base.sessionGeneration) return null;
    if (lastSaved.fileName === null) return { status: 'failed', observed: { saved: false, sessionGeneration: state.sessionGeneration }, code: 'SAVE_TOO_SHORT' };
    if (!lastSaved.entry) return null; // Written but not visible yet.
    return { status: 'applied', observed: { activityId: lastSaved.entry.id, fileName: lastSaved.entry.fileName, sizeBytes: lastSaved.entry.sizeBytes, sessionGeneration: state.sessionGeneration } };
  },
  // 'applied' = the PC's current activity now carries a NEW selection (the adapter's workout generation moved on) that
  // is unfinished and has the selected name. Re-selecting the running workout to restart it therefore needs a new one.
  'workouts.select': (args, base, { snap, state }) => {
    if (!snap?.fresh || state.layers.adapter !== 'fresh') return null;
    const progress = snap.raw.workoutProgress;
    if (!progress || progress.name !== base.workoutName || progress.finished === true || state.workout.present !== true) return null;
    if (base.workoutGeneration !== null && progress.generation === base.workoutGeneration) return null;
    return { status: 'applied', observed: { workoutName: progress.name, started: progress.started === true, inCountdown: progress.inCountdown === true, sequence: state.sequence } };
  },
  // The adapter reports the outcome of its own multi-Tick job under the same requestId. 'failed' leaves the rider where
  // they were (open water, no ground found in time, rider gone).
  'player.teleport': (args, base, { snap }) => {
    const t = snap?.fresh ? snap.raw.teleport : null;
    if (!t || t.requestId !== base.requestId) return null;
    if (t.status === 'done' && [t.x, t.y, t.z].every(Number.isFinite)) {
      const r = v => Math.round(v * 100) / 100;
      return { status: 'applied', observed: { x: r(t.x), y: r(t.y), z: r(t.z), snappedToRoad: t.snappedToRoad === true } };
    }
    if (t.status === 'failed') return { status: 'failed', observed: { moved: false }, code: TELEPORT_FAILURES.has(t.reasonCode) ? t.reasonCode : 'INTERNAL' };
    return null;
  },
  // 'applied' = the game's map waypoint sits on the requested point (the blip snaps by a few metres at most).
  'player.waypoint': (args, base, { snap }) => {
    const w = snap?.fresh ? snap.raw.waypoint : null;
    if (!w || !Number.isFinite(w.x) || !Number.isFinite(w.y) || Math.hypot(w.x - args.x, w.y - args.y) > 5) return null;
    return { status: 'applied', observed: waypointOf(w) };
  },
  'player.clearWaypoint': (args, base, { snap }) => (snap?.fresh && !snap.raw.waypoint ? { status: 'applied', observed: { waypoint: null } } : null),
  // P18: the add-on's state (start/stop, tagged with the starting requestId) or its reported parameter values.
  'addons.setEnabled': (args, base, { snap }) => (snap?.fresh ? addonEvidence('addons.setEnabled', args, base, snap) : null),
  'addons.configure': (args, base, { snap }) => (snap?.fresh ? addonEvidence('addons.configure', args, base, snap) : null),
  'addons.saveSegment': (args, base, { snap }) => (snap?.fresh ? addonEvidence('addons.saveSegment', args, base, snap) : null),
  'addons.removeSegment': (args, base, { snap }) => (snap?.fresh ? addonEvidence('addons.removeSegment', args, base, snap) : null),
  'addons.savePlace': (args, base, { snap }) => (snap?.fresh ? addonEvidence('addons.savePlace', args, base, snap) : null),
  'addons.removePlace': (args, base, { snap }) => (snap?.fresh ? addonEvidence('addons.removePlace', args, base, snap) : null),
  'appearance.select': (args, base, { settings }) => {
    if (!settings) return null;
    const [key] = Object.keys(args);
    const field = key === 'bikeId' ? 'bike' : 'character';
    return settings.appearance[field].selectedId === args[key]
      ? { status: 'applied', observed: { [key]: args[key], settingsRevision: settings.settingsRevision } }
      : null;
  },
};

const errorBody = (code, operationId, details = {}) => ({ code, helpId: null, retryable: false, operationId, details });

export class OperationStore {
  constructor({ now = () => Date.now(), monotonic = () => performance.now() } = {}) {
    this.now = now;
    this.monotonic = monotonic;
    this.byRequest = new Map();
  }

  get(requestId) {
    this.prune();
    return this.byRequest.get(requestId)?.doc ?? null;
  }

  create(command) {
    this.prune();
    const iso = new Date(this.now()).toISOString();
    const doc = {
      operationId: `op-${randomBytes(8).toString('hex')}`,
      requestId: command.requestId,
      type: command.type,
      status: 'validated',
      createdAtUtc: iso,
      updatedAtUtc: iso,
      error: null,
      observed: null,
    };
    this.byRequest.set(command.requestId, { doc, args: command.args, base: null, deadline: null, createdMono: this.monotonic() });
    return doc;
  }

  update(requestId, patch) {
    const entry = this.byRequest.get(requestId);
    Object.assign(entry.doc, patch, { updatedAtUtc: new Date(this.now()).toISOString() });
    return entry.doc;
  }

  reject(requestId, code, details) {
    const entry = this.byRequest.get(requestId);
    return this.update(requestId, { status: 'rejected', error: errorBody(code, entry.doc.operationId, details) });
  }

  unknown(requestId) {
    const entry = this.byRequest.get(requestId);
    return this.update(requestId, { status: 'unknown', error: errorBody('OPERATION_UNKNOWN', entry.doc.operationId) });
  }

  /** Ack 'dispatched': start waiting for evidence from the given baseline. */
  pending(requestId, base) {
    const entry = this.byRequest.get(requestId);
    entry.base = base;
    entry.deadline = this.monotonic() + EVIDENCE_TIMEOUT_MS[entry.doc.type];
    return this.update(requestId, { status: 'pending' });
  }

  /** Re-evaluates every pending operation against the current documents. */
  observe(documents) {
    for (const [requestId, entry] of this.byRequest) {
      if (entry.doc.status !== 'pending') continue;
      const result = EVIDENCE[entry.doc.type](entry.args, entry.base, documents);
      if (result?.status === 'applied') this.update(requestId, { status: 'applied', observed: result.observed, error: null });
      else if (result?.status === 'failed') this.update(requestId, { status: 'failed', observed: result.observed, error: errorBody(result.code, entry.doc.operationId) });
      else if (this.monotonic() > entry.deadline) this.unknown(requestId);
    }
  }

  /** Evidence for an adapter 'noop' (state already matched): applied with what is observed now. */
  appliedNow(requestId, documents) {
    const entry = this.byRequest.get(requestId);
    const result = EVIDENCE[entry.doc.type](entry.args, { sessionGeneration: null }, documents);
    const { state } = documents;
    const fallback = entry.doc.type === 'mod.setActive'
      ? { mod: state.layers.mod, antReceiver: state.layers.antReceiver, bleReceiver: state.layers.bleReceiver, sequence: state.sequence ?? 0 }
      : { noop: true };
    return this.update(requestId, { status: 'applied', observed: result?.observed ?? fallback, error: null });
  }

  hasPending() {
    for (const entry of this.byRequest.values()) if (entry.doc.status === 'pending' || entry.doc.status === 'validated') return true;
    return false;
  }

  prune() {
    const limit = this.monotonic() - RETENTION_MS;
    for (const [id, entry] of this.byRequest) if (entry.createdMono < limit && entry.doc.status !== 'pending') this.byRequest.delete(id);
    while (this.byRequest.size > MAX_OPERATIONS) this.byRequest.delete(this.byRequest.keys().next().value);
  }
}
