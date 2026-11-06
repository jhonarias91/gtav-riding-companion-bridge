// Adapter snapshot (gtbikev-adapter-v1) -> contract v1 documents. The adapter reports raw readings and native
// menu availability; this module owns the contract rules: units, quality, reasons, revisions and capabilities.
import { createHash } from 'node:crypto';
import { DIFFICULTY_LEVELS, PROFILE_RANGES } from './validate.mjs';
import { worldReasons } from './world.mjs';

const RECEIVERS = ['uninitialized', 'initialized', 'started', 'complete', 'ready', 'stopping', 'stopped', 'error', 'unknown'];
const MOD_LAYERS = ['inactive', 'initializing', 'dataReady', 'stopping', 'error', 'unknown'];
const GAME_CONTROL = ['inControl', 'paused', 'loading', 'notInControl', 'unknown'];
const SPORTS = ['cycling', 'running', 'rowing', 'unknown'];
const ROLES = ['power', 'resistance', 'cadence', 'heartRate', 'steering', 'controls', 'runningSpeed'];
const SENSOR_TYPES = ['FEC', 'CTRLS', 'CPS', 'HR', 'CAD', 'SPD', 'FPOD', 'BTS', 'STZ', 'KICKR'];
const ROUTE_ID = /^(0|[1-9][0-9]{0,4})$/;
const OPAQUE = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;
const RIDER_STOPPED_MPS = 0.5;

export const revision = (prefix, value) => `${prefix}-${createHash('sha256').update(JSON.stringify(value)).digest('hex').slice(0, 12)}`;
const pick = (value, allowed, fallback = 'unknown') => (allowed.includes(value) ? value : fallback);
const finite = (v, min, max) => (typeof v === 'number' && Number.isFinite(v) && v >= min && v <= max ? v : null);
const text = (v, max) => (typeof v === 'string' && v.trim() ? v.trim().slice(0, max) : null);
const opaque = (v, prefix) => (v === null || v === undefined ? null : OPAQUE.test(`${prefix}${v}`) ? `${prefix}${v}` : null);

/** Metric limits mirror state.schema.json. */
const METRICS = {
  powerW: [0, 5000], speedMps: [0, 40], cadenceRpm: [0, 254], heartRateBpm: [1, 254], distanceM: [0, 1e9],
  ridingTimeMs: [0, 1e12], movingTimeMs: [0, 1e12], elevationGainM: [0, 1e6], elevationLossM: [0, 1e6],
  slopeFraction: [-1, 1], windSpeedMps: [0, 100], windDirRad: [-7, 7],
};
const GAME_METRICS = new Set(['distanceM', 'ridingTimeMs', 'movingTimeMs', 'elevationGainM', 'elevationLossM', 'slopeFraction', 'windSpeedMps', 'windDirRad']);

function sensors(raw) {
  if (!Array.isArray(raw.sensors)) return [];
  const seen = new Set();
  const out = [];
  for (const s of raw.sensors) {
    if (!s || !ROLES.includes(s.role) || seen.has(s.role)) continue;
    seen.add(s.role);
    const source = s.source === 'ANT' || s.source === 'BLE' ? s.source : 'unknown';
    const deviceId = typeof s.deviceId === 'string' && /^(0|[1-9][0-9]{0,19})$/.test(s.deviceId) ? s.deviceId : null;
    const present = s.present === true && deviceId !== null && source !== 'unknown' ? true : s.present === false || s.present === true ? false : null;
    out.push({ role: s.role, present, source: present ? source : source, deviceId: present ? deviceId : null, sensorType: SENSOR_TYPES.includes(s.sensorType) ? s.sensorType : 'unknown',
      name: present && source === 'BLE' ? deviceName(s.name) : null });
  }
  return out;
}

function sourceFor(key, raw, sensorList) {
  if (GAME_METRICS.has(key)) return 'game';
  const role = { powerW: 'power', cadenceRpm: 'cadence', heartRateBpm: 'heartRate' }[key];
  if (key === 'speedMps') return raw.metrics?.speedVirtual === true ? 'virtual' : 'unknown';
  const sensor = sensorList.find(s => s.role === role && s.present);
  return sensor ? sensor.source : 'unknown';
}

const ABSENT_LAYERS = { adapter: 'absent', binding: 'unknown', mod: 'unknown', antReceiver: 'unknown', bleReceiver: 'unknown', gameControl: 'unknown' };

/** GET /api/v1/state. `last` keeps the previous fresh values so a stale snapshot shows them as stale, never live. */
export function toState(snap, last = null) {
  if (!snap) {
    const metrics = Object.fromEntries(Object.keys(METRICS).map(k => [k, { value: null, quality: 'unavailable', reasonCode: 'ADAPTER_ABSENT' }]));
    return {
      adapterInstanceId: null, sequence: null, stateRevision: null, capturedAtUtc: null, snapshotAgeMs: null, sessionGeneration: null,
      modVersion: null, bindingProfile: null, layers: { ...ABSENT_LAYERS }, sport: 'unknown', metrics, sensors: [],
      workout: { present: null, finished: null }, activeRoute: null,
    };
  }
  const { raw, ageMs, fresh } = snap;
  const sensorList = sensors(raw);
  const active = raw.isDtOk === true;
  const activity = raw.activity && typeof raw.activity === 'object' ? raw.activity : null;
  const values = {
    ...Object.fromEntries(Object.keys(METRICS).map(k => [k, raw.metrics?.[k]])),
    movingTimeMs: activity?.movingTimeMs,
    elevationGainM: activity?.elevationGainM,
    elevationLossM: activity?.elevationLossM,
  };
  const metrics = {};
  for (const [key, [min, max]] of Object.entries(METRICS)) {
    const value = finite(values[key], min, max);
    if (fresh) {
      if (value !== null) metrics[key] = { value, quality: 'valid', source: sourceFor(key, raw, sensorList) };
      else {
        const hrMissing = key === 'heartRateBpm' && !sensorList.some(s => s.role === 'heartRate' && s.present);
        const reasonCode = raw.modLoaded === false ? 'MOD_NOT_LOADED' : !active && !GAME_METRICS.has(key) ? 'MOD_INACTIVE' : hrMissing ? 'SENSOR_MISSING' : 'NOT_CAPTURED';
        metrics[key] = { value: null, quality: 'unavailable', reasonCode };
      }
    } else {
      const previous = last?.metrics?.[key];
      metrics[key] = previous && previous.value !== null
        ? { value: previous.value, quality: 'stale', source: previous.source ?? 'unknown' }
        : { value: null, quality: 'unavailable', reasonCode: 'ADAPTER_STALE' };
    }
  }
  const layers = {
    adapter: fresh ? 'fresh' : 'stale',
    binding: pick(raw.binding, ['compatible', 'limited', 'incompatible', 'unknown']),
    mod: fresh ? pick(raw.modLayer, MOD_LAYERS) : 'unknown',
    antReceiver: fresh ? pick(raw.receivers?.ant, RECEIVERS) : 'unknown',
    bleReceiver: fresh ? pick(raw.receivers?.ble, RECEIVERS) : 'unknown',
    gameControl: fresh ? pick(raw.gameControl, GAME_CONTROL) : 'unknown',
  };
  // The mod creates its Activity only when the rider first moves, a course loads or a workout is chosen
  // (GTBikeVScript.cs:757-767, 1747): no activity therefore means no workout, not "unknown".
  const workout = activity
    ? { present: activity.workoutPresent === true, finished: activity.workoutPresent === true ? activity.workoutFinished === true : null }
    : { present: false, finished: null };
  const ar = raw.activeRoute;
  const activeRoute = ar && ROUTE_ID.test(ar.id) ? { id: ar.id, loop: ar.loop === true, reverse: ar.reverse === true } : null;
  const sessionGeneration = opaque(raw.sessionGeneration, 'session-');
  const semantic = { layers, sessionGeneration, workout, activeRoute, binding: raw.bindingProfile };
  return {
    adapterInstanceId: OPAQUE.test(raw.adapterInstanceId) ? raw.adapterInstanceId : null,
    sequence: raw.sequence,
    stateRevision: revision('state', semantic),
    capturedAtUtc: new Date(Date.parse(raw.capturedAtUtc)).toISOString(),
    snapshotAgeMs: ageMs,
    sessionGeneration,
    modVersion: text(raw.modVersion, 32),
    bindingProfile: raw.bindingProfile === 'gtbikev-0804' ? 'gtbikev-0804' : null,
    layers,
    sport: fresh ? pick(raw.sport, SPORTS) : 'unknown',
    metrics,
    sensors: fresh ? sensorList : [],
    workout: fresh ? workout : { present: null, finished: null },
    activeRoute: fresh ? activeRoute : null,
  };
}

/** Shared preconditions for writes; null when allowed. */
function writeBlock(snap, type) {
  if (!snap) return 'ADAPTER_ABSENT';
  if (!snap.fresh) return 'ADAPTER_STALE';
  const raw = snap.raw;
  if (raw.modLoaded === false) return 'MOD_NOT_LOADED';
  if (!Array.isArray(raw.commands) || !raw.commands.includes(type)) return raw.binding === 'compatible' ? 'CAPABILITY_UNAVAILABLE' : 'UNSUPPORTED_VERSION';
  return null;
}
const workoutBlocks = raw => raw.activity?.workoutPresent === true && raw.activity?.workoutFinished !== true;
const inControl = raw => raw.gameControl === 'inControl';
const riderStopped = raw => typeof raw.metrics?.speedMps === 'number' ? raw.metrics.speedMps < RIDER_STOPPED_MPS : raw.isDtOk !== true;

/** reasonCode or null for each capability, from one snapshot. */
export function capabilityReasons(snap, catalog) {
  const raw = snap?.raw ?? {};
  const fresh = snap?.fresh === true;
  const read = () => (!snap ? 'ADAPTER_ABSENT' : !fresh ? 'ADAPTER_STALE' : null);
  const reasons = {
    'metrics.read': read(),
    'sensors.read': read() ?? (raw.isDtOk !== true ? 'MOD_INACTIVE' : raw.sensors == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'activity.read': read() ?? (raw.activity == null ? 'MOD_INACTIVE' : null),
    'mod.setActive': writeBlock(snap, 'mod.setActive') ?? (inControl(raw) ? null : 'GAME_NOT_IN_CONTROL'),
    'routes.read': read() ?? (!catalog || raw.routes == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'routes.preview': writeBlock(snap, 'routes.preview') ?? (raw.routes?.panelOpen === true ? null : 'PANEL_CLOSED'),
    // Allowed during a workout like the mod's menu (6 Nov 2026): the workout restarts on the new route; the phone warns.
    'routes.apply': writeBlock(snap, 'routes.apply')
      ?? (raw.isDtOk !== true ? 'MOD_INACTIVE' : !inControl(raw) ? 'GAME_NOT_IN_CONTROL' : null),
    'ui.openPanel': writeBlock(snap, 'ui.openPanel') ?? (raw.routes?.routesEnabled !== true ? 'MOD_INACTIVE' : !inControl(raw) ? 'GAME_NOT_IN_CONTROL' : null),
    'ui.closePanel': writeBlock(snap, 'ui.closePanel') ?? (raw.routes?.panelOpen === true ? null : 'PANEL_CLOSED'),
    'profile.read': read() ?? (raw.settings == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'profile.update': writeBlock(snap, 'profile.update') ?? (raw.settings == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'rideSettings.update': writeBlock(snap, 'rideSettings.update') ?? (raw.settings == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'appearance.select': writeBlock(snap, 'appearance.select') ?? (raw.settings == null ? 'CAPABILITY_UNAVAILABLE' : !riderStopped(raw) ? 'RIDER_MOVING' : null),
    'devices.configure': writeBlock(snap, 'devices.configure') ?? (raw.isDtOk !== true ? 'MOD_INACTIVE' : raw.devices == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'devices.assign': writeBlock(snap, 'devices.assign') ?? (raw.isDtOk !== true ? 'MOD_INACTIVE' : raw.devices == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'devices.disconnect': writeBlock(snap, 'devices.disconnect') ?? (raw.isDtOk !== true ? 'MOD_INACTIVE' : raw.devices == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'workouts.read': read() ?? (raw.workouts == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'workouts.observe': read() ?? (raw.workouts == null ? 'CAPABILITY_UNAVAILABLE' : null),
    // P13: same preconditions as the mod's workout menu (active mod, plans parsed; it stays enabled during a workout, so
    // a running workout is replaced, and the phone warns first), and the game must be in control (the mod's handler
    // spawns the workout bot on a route).
    'workouts.select': writeBlock(snap, 'workouts.select')
      ?? (raw.isDtOk !== true ? 'MOD_INACTIVE' : raw.workouts?.ready !== true ? 'CAPABILITY_UNAVAILABLE'
        : !inControl(raw) ? 'GAME_NOT_IN_CONTROL' : null),
    'activities.finish': snap ? finishReason(snap) : 'ADAPTER_ABSENT',
  };
  return reasons;
}

const CATALOG = [
  ['metrics.read', 'connection.status', null], ['sensors.read', 'devices.policy', null], ['activity.read', 'metrics.movingTime', null],
  ['mod.setActive', 'mod.activate', 'async'], ['routes.read', 'routes.browse', null], ['routes.preview', 'routes.pcPreview', 'immediate'],
  ['routes.apply', 'routes.apply', 'async'], ['ui.openPanel', 'routes.pcPreview', 'immediate'], ['ui.closePanel', 'routes.pcPreview', 'immediate'],
  ['profile.read', 'profile.ftp', null], ['profile.update', 'profile.ftp', 'immediate'], ['rideSettings.update', 'ride.difficulty', 'immediate'],
  ['appearance.select', 'appearance.bike', 'immediate'],
  // Optional phases remain discoverable when their resources are unavailable.
  ['devices.configure', 'devices.policy', 'async'], ['workouts.read', 'training.catalog', null], ['workouts.observe', 'training.progress', null],
  ['activities.list', 'activity.history', null], ['activities.download', 'activity.download', null], ['activities.finish', 'activity.save', 'async'],
  ['riders.read', 'multiplayer.riders', null], ['multiplayer.joinBusiestRoute', 'multiplayer.join', 'async'], ['map.position', 'map.gameWorld', null],
  ['advancedSettings.update', 'advanced.bots', 'nextActivity'],
  ['devices.assign', 'devices.phoneScan', 'async'],
  ['devices.disconnect', 'devices.phoneScan', 'async'],
  ['workouts.select', 'training.select', 'async'],
  // P15: allowlisted GTBikeVConfig.ini options, edited only with GTA V closed; they apply when the game starts.
  ['modConfig.read', 'modConfig.options', null],
  ['modConfig.update', 'modConfig.options', 'scriptReload'],
];

/** GET /api/v1/capabilities. */
/** world: P8 map/riders configuration; extra: reasons computed by the server (activities folder). */
export function toCapabilities(snap, catalog, world = {}, extra = {}) {
  const reasons = { ...capabilityReasons(snap, catalog), ...worldReasons(snap, world), ...extra };
  const raw = snap?.raw ?? {};
  return {
    modVersion: text(raw.modVersion, 32),
    bindingProfile: raw.bindingProfile === 'gtbikev-0804' ? 'gtbikev-0804' : null,
    adapterInstanceId: snap && OPAQUE.test(raw.adapterInstanceId) ? raw.adapterInstanceId : null,
    capabilities: CATALOG.map(([id, helpId, applyMode]) => {
      const reasonCode = id in reasons ? reasons[id] : 'PHASE_NOT_AVAILABLE';
      return { id, available: reasonCode === null, reasonCode, helpId, applyMode };
    }),
  };
}

// ---- routes (P3) ----

function routeSummary(r) {
  if (!r || !ROUTE_ID.test(String(r.id)) || Number(r.id) > 32767) return null;
  const name = text(r.name, 120);
  if (!name) return null;
  const list = v => (Array.isArray(v) ? v.map(x => text(x, 40)).filter(Boolean).slice(0, 8) : []);
  const loopAllowed = r.loopAllowed === true;
  return {
    id: String(r.id),
    name,
    author: text(r.author, 120),
    distanceM: finite(r.distanceM, 0, 1e7),
    elevationGainM: finite(r.elevationGainM, 0, 1e5),
    elevationLossM: finite(r.elevationLossM, 0, 1e5),
    maxElevationM: finite(r.maxElevationM, -1000, 1e4),
    categories: list(r.categories),
    types: list(r.types),
    loopAllowed,
    reverseAllowed: loopAllowed && r.reverseAllowed !== false,
    version: text(r.version, 32),
  };
}

/** GET /api/v1/routes?offset=&limit=&q= ; q filters by name/author/category, case-insensitive. */
export function toRoutesPage(catalog, { offset = 0, limit = 50, q = '' } = {}) {
  if (!catalog) return { catalogRevision: null, total: 0, offset: 0, items: [] };
  const needle = q.trim().toLowerCase().slice(0, 60);
  const all = catalog.routes.map(routeSummary).filter(Boolean)
    .filter(r => !needle || [r.name, r.author ?? '', ...r.categories].some(s => s.toLowerCase().includes(needle)));
  return { catalogRevision: catalog.catalogRevision, total: Math.min(all.length, 5000), offset, items: all.slice(offset, offset + limit) };
}

/** GET /api/v1/routes/{id}; null when unknown. */
export function toRouteDetail(catalog, id) {
  if (!catalog) return null;
  const raw = catalog.routes.find(r => String(r?.id) === id);
  const summary = routeSummary(raw);
  if (!summary) return null;
  return { catalogRevision: catalog.catalogRevision, route: { ...summary, description: text(raw.description, 1200), notes: text(raw.notes, 1200) } };
}

/** GET /api/v1/route-preview. */
export function toPreview(snap, catalog) {
  const r = snap?.fresh ? snap.raw.routes : null;
  const open = r?.panelOpen === true;
  const routeId = open && ROUTE_ID.test(String(r.previewRouteId ?? '')) ? String(r.previewRouteId) : null;
  const doc = {
    catalogRevision: catalog?.catalogRevision ?? null,
    panelOpen: open,
    routeId,
    loop: open && routeId ? r.previewLoop === true : null,
    reverse: open && routeId ? r.previewReverse === true : null,
  };
  return { ...doc, previewRevision: open ? revision('preview', doc) : null };
}

// ---- settings (P4) ----

/** GET /api/v1/settings; null when the adapter does not report settings. */
export function toSettings(snap) {
  const s = snap?.raw?.settings;
  if (!s || typeof s !== 'object') return null;
  const raw = snap.raw;
  const enabled = s.enabled ?? {};
  const field = (type, flag, extra = null) => writeBlock(snap, type) ?? extra ?? (flag === true ? null : 'MOD_INACTIVE');
  const num = (key, value, reasonCode) => {
    const [min, max] = PROFILE_RANGES[key];
    return { value: finite(value, 0, 10000), min, max, editable: reasonCode === null, reasonCode };
  };
  const bool = (value, reasonCode) => ({ value: typeof value === 'boolean' ? value : null, editable: reasonCode === null, reasonCode });
  const choice = (c, reasonCode) => {
    const options = Array.isArray(c?.options) ? c.options.map(o => text(o, 80)).filter(Boolean).slice(0, 128).map(o => ({ id: o, label: o })) : [];
    const selectedId = options.some(o => o.id === c?.selected) ? c.selected : null;
    const reason = reasonCode ?? (options.length === 0 ? 'CAPABILITY_UNAVAILABLE' : null);
    return { selectedId, options, editable: reason === null, reasonCode: reason };
  };
  const workout = workoutBlocks(raw) ? 'WORKOUT_PC_CONTROL_REQUIRED' : null;
  const moving = riderStopped(raw) ? null : 'RIDER_MOVING';
  const difficulty = DIFFICULTY_LEVELS.includes(s.difficulty) ? s.difficulty : null;
  const values = {
    profile: { weightKg: s.weightKg, ftpW: s.ftpW, hrMaxBpm: s.hrMaxBpm },
    ride: { autopilot: s.autopilot, avoidObstacles: s.avoidObstacles, difficulty, roadFeel: s.roadFeel },
    appearance: { bike: s.bike, character: s.character },
  };
  const diffReason = field('rideSettings.update', enabled.difficulty);
  return {
    settingsRevision: revision('settings', values),
    profile: {
      weightKg: num('weightKg', s.weightKg, field('profile.update', enabled.weight)),
      // FTP drives workout targets: changing it during a PC workout would change the trainer's target power.
      ftpW: num('ftpW', s.ftpW, field('profile.update', enabled.ftp, workout)),
      // No native menu item; Config.UserHRMax only needs the adapter command.
      hrMaxBpm: num('hrMaxBpm', s.hrMaxBpm, writeBlock(snap, 'profile.update')),
    },
    ride: {
      autopilot: bool(s.autopilot, field('rideSettings.update', enabled.autopilot)),
      avoidObstacles: bool(s.avoidObstacles, field('rideSettings.update', enabled.avoidObstacles, s.autopilot === true ? 'AUTOPILOT_ON' : null)),
      difficulty: { value: difficulty, editable: diffReason === null, reasonCode: diffReason, allowed: [...DIFFICULTY_LEVELS] },
      roadFeel: bool(s.roadFeel, field('rideSettings.update', enabled.roadFeel)),
    },
    appearance: {
      bike: choice(s.bike, field('appearance.select', enabled.bike, moving)),
      character: choice(s.character, field('appearance.select', enabled.character, moving)),
    },
  };
}

// ---- devices (P5) ----

const SOURCES = ['ANT', 'BLE'];
const sensorType = t => (SENSOR_TYPES.includes(t) ? t : 'unknown');
const deviceOf = d => (d && SOURCES.includes(d.source) && typeof d.deviceId === 'string' && /^(0|[1-9][0-9]{0,19})$/.test(d.deviceId)
  ? { deviceId: d.deviceId, source: d.source, sensorType: sensorType(d.sensorType) } : null);
// The advertised name of a BLE sensor the PC uses now (P12); printable text only, at most 64 characters.
const deviceName = n => {
  if (typeof n !== 'string') return null;
  const clean = n.replace(/[\u0000-\u001f\u007f]/g, '').trim().slice(0, 64);
  return clean.length ? clean : null;
};

/** GET /api/v1/device-roles. Mirrors the native Device Selection buttons (DeviceTypeViewWidget). */
export function toDevices(snap) {
  const raw = snap?.raw;
  const reason = !snap ? 'ADAPTER_ABSENT' : !snap.fresh ? 'ADAPTER_STALE' : raw.isDtOk !== true ? 'MOD_INACTIVE' : !raw.devices ? 'CAPABILITY_UNAVAILABLE' : null;
  if (reason) return { devicesRevision: null, reasonCode: reason, roles: [] };
  const seen = new Set();
  const roles = [];
  for (const r of Array.isArray(raw.devices.roles) ? raw.devices.roles : []) {
    if (!ROLES.includes(r?.role) || seen.has(r.role)) continue;
    seen.add(r.role);
    const entries = (Array.isArray(r.entries) ? r.entries : []).slice(0, 8).map(e => {
      const d = deviceOf(e);
      return d ? { ...d, excluded: e.excluded === true } : null;
    }).filter(Boolean);
    const seenCurrent = deviceOf(r.current);
    const current = seenCurrent && { ...seenCurrent, name: seenCurrent.source === 'BLE' ? deviceName(r.current.name) : null };
    const policy = r.isAuto === true ? 'auto' : entries.some(e => !e.excluded) ? 'locked' : 'filtered';
    roles.push({
      role: r.role,
      visible: r.visible === true,
      current,
      policy,
      entries,
      actions: { auto: r.isAuto !== true, lock: r.isLocked !== true && current !== null, skip: r.skippable === true && current !== null },
    });
  }
  return { devicesRevision: revision('devices', roles), reasonCode: null, roles };
}

// ---- workouts (P6, read only) ----

const SEGMENT_TYPES = { Warmup: 'warmup', Cooldown: 'cooldown', Interval: 'interval', Steady: 'steady', Ramp: 'ramp', FreeRide: 'freeRide' };
const watts = v => (Number.isInteger(v) && v >= 0 && v <= 5000 ? v : null);
const cadenceOf = v => (Number.isInteger(v) && v >= 1 && v <= 254 ? v : null);

function segmentOf(s, index) {
  const start = finite(s?.start, 0, 1e9);
  const end = finite(s?.end, 0, 1e9);
  if (start === null || end === null) return null;
  return { index, type: SEGMENT_TYPES[s.type] ?? 'unknown', start, end, startPowerW: watts(s.startPowerW), endPowerW: watts(s.endPowerW), cadenceRpm: cadenceOf(s.cadenceRpm) };
}

function workoutSummary(w, planId) {
  const name = text(w?.name, 120);
  if (!name || !/^w-[a-f0-9]{16}$/.test(w.id ?? '')) return null;
  const segments = Array.isArray(w.segments) ? w.segments.slice(0, 2000) : [];
  return {
    id: w.id, planId, name, author: text(w.author, 120),
    durationType: w.durationType === 'distance' ? 'distance' : 'time',
    total: finite(w.total, 0, 1e9) ?? 0,
    segmentCount: segments.length,
  };
}

const plansOf = catalog => (Array.isArray(catalog?.plans) ? catalog.plans : []).filter(p => /^p-[a-f0-9]{16}$/.test(p?.id ?? '') && text(p.name, 120));

/** GET /api/v1/workout-plans. */
export function toWorkoutPlans(snap, catalog) {
  const loading = snap?.raw?.workouts;
  return {
    catalogRevision: catalog?.catalogRevision ?? null,
    ready: !!catalog && loading?.ready !== false,
    ftpReferenceW: finite(catalog?.ftpReferenceW, 0, 2000),
    plans: plansOf(catalog).slice(0, 200).map(p => ({
      id: p.id,
      name: text(p.name, 120),
      description: text(p.description, 1200),
      workoutCount: Array.isArray(p.workouts) ? Math.min(p.workouts.length, 1000) : 0,
    })),
  };
}

/** GET /api/v1/workouts?planId= ; null when the plan is unknown. */
export function toWorkoutList(catalog, planId) {
  const plan = plansOf(catalog).find(p => p.id === planId);
  if (!plan) return null;
  return { catalogRevision: catalog.catalogRevision, planId, workouts: (plan.workouts ?? []).map(w => workoutSummary(w, planId)).filter(Boolean).slice(0, 1000) };
}

/** GET /api/v1/workouts/{id} ; null when unknown. */
export function toWorkoutDetail(catalog, id) {
  for (const plan of plansOf(catalog)) {
    const w = (plan.workouts ?? []).find(x => x?.id === id);
    const summary = w && workoutSummary(w, plan.id);
    if (!summary) continue;
    const segments = (w.segments ?? []).slice(0, 2000).map(segmentOf).filter(Boolean);
    return {
      catalogRevision: catalog.catalogRevision,
      ftpReferenceW: finite(catalog.ftpReferenceW, 0, 2000),
      workout: { ...summary, segmentCount: segments.length, description: text(w.description, 2000), segments },
    };
  }
  return null;
}

const NO_PROGRESS = { present: false, name: null, started: null, inCountdown: null, finished: null, durationType: null, elapsed: null, total: null, segmentCount: null, segment: null };

/** GET /api/v1/workout-progress. Observation only: no target, bias or ERG field exists here. */
export function toWorkoutProgress(snap) {
  const p = snap?.fresh ? snap.raw.workoutProgress : null;
  if (!p) return { ...NO_PROGRESS };
  const bool = v => (typeof v === 'boolean' ? v : null);
  const index = Number.isInteger(p.segment?.index) && p.segment.index >= 0 ? p.segment.index : 0;
  return {
    present: true,
    name: text(p.name, 120),
    started: bool(p.started),
    inCountdown: bool(p.inCountdown),
    finished: bool(p.finished),
    durationType: p.durationType === 'distance' ? 'distance' : 'time',
    elapsed: finite(p.elapsed, 0, 1e9),
    total: finite(p.total, 0, 1e9),
    segmentCount: Number.isInteger(p.segmentCount) && p.segmentCount >= 0 ? p.segmentCount : null,
    segment: p.segment ? segmentOf(p.segment, index) : null,
  };
}

// ---- activities (P7) ----

const SAVE_MIN_DISTANCE_M = 100; // Activity.MINIMUM_DISTANCE_TO_SAVE_ACTIVITY (Activity.cs:536), strictly greater.

/** reasonCode for activities.finish, or null when saving is allowed now. */
export function finishReason(snap) {
  const blocked = writeBlock(snap, 'activities.finish');
  if (blocked) return blocked;
  const raw = snap.raw;
  if (raw.isDtOk !== true || raw.activities?.saveEnabled !== true) return 'MOD_INACTIVE';
  if (!raw.activity) return 'SAVE_TOO_SHORT';
  if (workoutBlocks(raw)) return 'WORKOUT_PC_CONTROL_REQUIRED';
  if (!(finite(raw.activities?.activityDistanceM, 0, 1e9) > SAVE_MIN_DISTANCE_M)) return 'SAVE_TOO_SHORT';
  if (!riderStopped(raw)) return 'RIDER_MOVING';
  if (!inControl(raw)) return 'GAME_NOT_IN_CONTROL';
  return null;
}
