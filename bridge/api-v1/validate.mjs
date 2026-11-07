// Runtime validation of POST /api/v1/commands. Mirrors contracts/v1/command.schema.json without depending on
// the ajv dev dependency; tests run both against the same fixtures so they cannot drift apart.
// Strict: no coercion, unknown fields rejected, ranges checked here because the native setters silently
// replace out-of-range values with defaults (Config.cs:90-140).
import { randomBytes } from 'node:crypto';
import { checkParams, SEGMENT_ID, PLACE_ID } from './addons.mjs';

export class CommandError extends Error {
  constructor(code, status = 400, details = undefined) {
    super(code);
    this.code = code;
    this.status = status;
    this.details = details;
  }
}

const OPAQUE = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;
const REQUEST_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const ROUTE_ID = /^(0|[1-9][0-9]{0,4})$/;
const DEVICE_ID = /^(0|[1-9][0-9]{0,19})$/;
export const SENSOR_ROLES = ['power', 'resistance', 'cadence', 'heartRate', 'steering', 'controls', 'runningSpeed'];
/** P11: BLE sensor types the mod derives from an advertisement (BLE_Receiver DeviceFactory), as GTBikeV names them. */
export const ASSIGN_SENSOR_TYPES = ['FEC', 'CPS', 'HR', 'CAD', 'BTS', 'KICKR', 'CTRLS', 'FPOD'];
/** P11: sensor types able to serve each role (BLE DeviceFactory.DevicePreference). Mirrored in GtbikeV1More.RoleTypes. */
export const ROLE_SENSOR_TYPES = {
  power: ['CPS', 'KICKR', 'FEC', 'CAD'],
  resistance: ['KICKR', 'FEC'],
  cadence: ['CPS', 'FEC', 'KICKR', 'CAD'],
  heartRate: ['HR', 'FEC', 'FPOD'],
  steering: ['BTS', 'CTRLS', 'KICKR'],
  controls: ['CTRLS', 'KICKR'],
  runningSpeed: ['FPOD', 'FEC', 'HR'],
};
/** 48-bit BLE address as decimal string, 1..2^48-1 (0 is the receiver's "any device" wildcard). */
const BLE_ADDRESS = /^[1-9][0-9]{0,14}$/;
const MAX_BLE_ADDRESS = 2n ** 48n - 1n;
export const isBleAddress = v => typeof v === 'string' && BLE_ADDRESS.test(v) && BigInt(v) <= MAX_BLE_ADDRESS;
export const DIFFICULTY_LEVELS = [0, 25, 50, 75, 100, 255];
export const PANELS = ['routes'];
export const PROFILE_RANGES = { weightKg: [40, 150], ftpW: [50, 500], hrMaxBpm: [50, 220] };
/** |x| and |y| bound for player.teleport and player.waypoint (the map spans about -4000..8000 m). Mirrored in the adapter. */
export const TELEPORT_LIMIT_M = 10_000;
/** Commands only the PC window's loopback desktop token may send. */
export const DESKTOP_ONLY_COMMANDS = new Set(['player.teleport', 'player.waypoint', 'player.clearWaypoint']);

const isObject = v => v !== null && typeof v === 'object' && !Array.isArray(v);
const isAddonId = v => typeof v === 'string' && /^[a-z][a-zA-Z0-9]{1,31}$/.test(v);
const isInt = v => typeof v === 'number' && Number.isInteger(v);
const isBool = v => typeof v === 'boolean';
const isRouteId = v => typeof v === 'string' && ROUTE_ID.test(v) && Number(v) <= 32767;
const isCatalogKey = v => typeof v === 'string' && v.length >= 1 && v.length <= 80;

function onlyKeys(obj, allowed, required = []) {
  for (const key of Object.keys(obj)) if (!allowed.includes(key)) throw new CommandError('INVALID_REQUEST', 422, { field: key });
  for (const key of required) if (!(key in obj)) throw new CommandError('INVALID_REQUEST', 422, { field: key });
}

/** Argument rules per command type. Each returns a normalized copy of args. */
const ARGS = {
  'mod.setActive': (a) => {
    onlyKeys(a, ['enabled'], ['enabled']);
    if (!isBool(a.enabled)) throw new CommandError('INVALID_REQUEST', 422, { field: 'enabled' });
    return { enabled: a.enabled };
  },
  'routes.apply': (a, revision) => {
    onlyKeys(a, ['routeId', 'loop', 'reverse'], ['routeId', 'loop', 'reverse']);
    if (revision === null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (a.routeId !== null && !isRouteId(a.routeId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'routeId' });
    if (!isBool(a.loop) || !isBool(a.reverse)) throw new CommandError('INVALID_REQUEST', 422, { field: 'loop' });
    if (a.routeId === null && (a.loop || a.reverse)) throw new CommandError('INVALID_REQUEST', 422, { field: 'loop' });
    return { routeId: a.routeId, loop: a.loop, reverse: a.reverse };
  },
  'routes.preview': (a, revision) => {
    onlyKeys(a, ['routeId'], ['routeId']);
    if (revision === null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (a.routeId !== null && !isRouteId(a.routeId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'routeId' });
    return { routeId: a.routeId };
  },
  'ui.openPanel': (a) => panelArgs(a),
  'ui.closePanel': (a) => panelArgs(a),
  'profile.update': (a, revision) => {
    onlyKeys(a, Object.keys(PROFILE_RANGES));
    if (Object.keys(a).length === 0) throw new CommandError('INVALID_REQUEST', 422, { field: 'args' });
    if (revision === null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    const out = {};
    for (const [key, [min, max]] of Object.entries(PROFILE_RANGES)) {
      if (!(key in a)) continue;
      if (!isInt(a[key]) || a[key] < min || a[key] > max) throw new CommandError('VALUE_OUT_OF_RANGE', 422, { field: key });
      out[key] = a[key];
    }
    return out;
  },
  'rideSettings.update': (a, revision) => {
    onlyKeys(a, ['autopilot', 'avoidObstacles', 'difficulty', 'roadFeel']);
    if (Object.keys(a).length === 0) throw new CommandError('INVALID_REQUEST', 422, { field: 'args' });
    if (revision === null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    for (const key of ['autopilot', 'avoidObstacles', 'roadFeel']) if (key in a && !isBool(a[key])) throw new CommandError('INVALID_REQUEST', 422, { field: key });
    if ('difficulty' in a && !DIFFICULTY_LEVELS.includes(a.difficulty)) throw new CommandError('VALUE_OUT_OF_RANGE', 422, { field: 'difficulty' });
    return { ...a };
  },
  'devices.configure': (a, revision) => {
    onlyKeys(a, ['role', 'action', 'expectedDeviceId', 'expectedSource'], ['role', 'action', 'expectedDeviceId', 'expectedSource']);
    if (revision === null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (!SENSOR_ROLES.includes(a.role)) throw new CommandError('INVALID_REQUEST', 422, { field: 'role' });
    if (!['auto', 'lock', 'skip'].includes(a.action)) throw new CommandError('INVALID_REQUEST', 422, { field: 'action' });
    if (a.action === 'auto') {
      if (a.expectedDeviceId !== null || a.expectedSource !== null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedDeviceId' });
    } else {
      if (typeof a.expectedDeviceId !== 'string' || !DEVICE_ID.test(a.expectedDeviceId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedDeviceId' });
      if (a.expectedSource !== 'ANT' && a.expectedSource !== 'BLE') throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedSource' });
    }
    return { role: a.role, action: a.action, expectedDeviceId: a.expectedDeviceId, expectedSource: a.expectedSource };
  },
  'devices.assign': (a, revision) => {
    onlyKeys(a, ['role', 'sensorType', 'deviceId'], ['role', 'sensorType', 'deviceId']);
    if (revision === null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (!SENSOR_ROLES.includes(a.role)) throw new CommandError('INVALID_REQUEST', 422, { field: 'role' });
    if (!ASSIGN_SENSOR_TYPES.includes(a.sensorType)) throw new CommandError('INVALID_REQUEST', 422, { field: 'sensorType' });
    if (!isBleAddress(a.deviceId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'deviceId' });
    return { role: a.role, sensorType: a.sensorType, deviceId: a.deviceId };
  },
  'devices.disconnect': (a, revision) => {
    onlyKeys(a, ['sensorType', 'deviceId'], ['sensorType', 'deviceId']);
    if (revision === null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (!ASSIGN_SENSOR_TYPES.includes(a.sensorType)) throw new CommandError('INVALID_REQUEST', 422, { field: 'sensorType' });
    if (!isBleAddress(a.deviceId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'deviceId' });
    return { sensorType: a.sensorType, deviceId: a.deviceId };
  },
  // P13: the native Select path only. There is no start/pause/skip/end/ERG/target/bias command.
  'workouts.select': (a, revision) => {
    onlyKeys(a, ['workoutId'], ['workoutId']);
    if (revision === null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (typeof a.workoutId !== 'string' || !/^w-[a-f0-9]{16}$/.test(a.workoutId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'workoutId' });
    return { workoutId: a.workoutId };
  },
  // P16: PC window only (server.mjs refuses phones). Raw GTA world metres; the adapter finds the ground itself.
  'player.teleport': (a, revision) => worldPointArgs(a, revision),
  // P17: GTA's own map waypoint (the game draws the GPS route on the minimap). PC window only, like teleport.
  'player.waypoint': (a, revision) => worldPointArgs(a, revision),
  'player.clearWaypoint': (a, revision) => {
    onlyKeys(a, []);
    if (revision !== null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    return {};
  },
  // P18: start or stop an add-on (GtbikeV1Addons.cs). Starting needs the addonsRevision the client saw; stopping never does.
  'addons.setEnabled': (a, revision) => {
    onlyKeys(a, ['addonId', 'enabled'], ['addonId', 'enabled']);
    if (!isAddonId(a.addonId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'addonId' });
    if (!isBool(a.enabled)) throw new CommandError('INVALID_REQUEST', 422, { field: 'enabled' });
    if (a.enabled === (revision === null)) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    return { addonId: a.addonId, enabled: a.enabled };
  },
  // P18: change some parameters of one add-on, typed by its descriptors (addons.mjs). Only liveEdit ones while it runs.
  'addons.configure': (a, revision) => {
    onlyKeys(a, ['addonId', 'params'], ['addonId', 'params']);
    if (revision === null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (!isAddonId(a.addonId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'addonId' });
    if (!isObject(a.params)) throw new CommandError('INVALID_REQUEST', 422, { field: 'params' });
    const bad = checkParams(a.addonId, a.params);
    if (bad) throw new CommandError(bad[0], bad[0] === 'NOT_FOUND' ? 404 : 422, bad[1]);
    return { addonId: a.addonId, params: { ...a.params } };
  },
  // P18 ghost: a timed segment (start/finish in GTA world metres; the same point twice is a loop). segmentId null = new,
  // the bridge assigns it. Saving an existing id renames it; moving its points also resets its record.
  'addons.saveSegment': (a, revision) => {
    onlyKeys(a, ['addonId', 'segmentId', 'name', 'start', 'finish'], ['addonId', 'segmentId', 'name', 'start', 'finish']);
    if (revision !== null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (!isAddonId(a.addonId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'addonId' });
    if (a.segmentId !== null && (typeof a.segmentId !== 'string' || !SEGMENT_ID.test(a.segmentId))) throw new CommandError('INVALID_REQUEST', 422, { field: 'segmentId' });
    const name = typeof a.name === 'string' ? a.name.trim() : '';
    if (name.length < 1 || name.length > 40 || /[\u0000-\u001f\u007f]/.test(name)) throw new CommandError('INVALID_REQUEST', 422, { field: 'name' });
    const start = mapPoint(a.start, 'start'), finish = mapPoint(a.finish, 'finish');
    if (Math.hypot(finish.x - start.x, finish.y - start.y) > 30_000) throw new CommandError('VALUE_OUT_OF_RANGE', 422, { field: 'finish' });
    return { addonId: a.addonId, segmentId: a.segmentId ?? `seg-${randomBytes(4).toString('hex')}`, name, start, finish };
  },
  'addons.removeSegment': (a, revision) => {
    onlyKeys(a, ['addonId', 'segmentId'], ['addonId', 'segmentId']);
    if (revision !== null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (!isAddonId(a.addonId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'addonId' });
    if (typeof a.segmentId !== 'string' || !SEGMENT_ID.test(a.segmentId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'segmentId' });
    return { addonId: a.addonId, segmentId: a.segmentId };
  },
  // P18 discover: a saved place (GTA world metres). placeId null = new, the bridge assigns it; an existing id renames or moves it.
  'addons.savePlace': (a, revision) => {
    onlyKeys(a, ['addonId', 'placeId', 'name', 'point'], ['addonId', 'placeId', 'name', 'point']);
    if (revision !== null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (!isAddonId(a.addonId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'addonId' });
    if (a.placeId !== null && (typeof a.placeId !== 'string' || !PLACE_ID.test(a.placeId))) throw new CommandError('INVALID_REQUEST', 422, { field: 'placeId' });
    const name = typeof a.name === 'string' ? a.name.trim() : '';
    if (name.length < 1 || name.length > 40 || /[\u0000-\u001f\u007f]/.test(name)) throw new CommandError('INVALID_REQUEST', 422, { field: 'name' });
    return { addonId: a.addonId, placeId: a.placeId ?? `pl-${randomBytes(4).toString('hex')}`, name, point: mapPoint(a.point, 'point') };
  },
  'addons.removePlace': (a, revision) => {
    onlyKeys(a, ['addonId', 'placeId'], ['addonId', 'placeId']);
    if (revision !== null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (!isAddonId(a.addonId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'addonId' });
    if (typeof a.placeId !== 'string' || !PLACE_ID.test(a.placeId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'placeId' });
    return { addonId: a.addonId, placeId: a.placeId };
  },
  'activities.finish': (a, revision) => {
    onlyKeys(a, []);
    if (revision === null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    return {};
  },
  'appearance.select': (a, revision) => {
    onlyKeys(a, ['bikeId', 'characterId']);
    const keys = Object.keys(a);
    if (keys.length !== 1) throw new CommandError('INVALID_REQUEST', 422, { field: 'args' });
    if (revision === null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
    if (!isCatalogKey(a[keys[0]])) throw new CommandError('INVALID_REQUEST', 422, { field: keys[0] });
    return { [keys[0]]: a[keys[0]] };
  },
};

function mapPoint(p, field) {
  if (!isObject(p)) throw new CommandError('INVALID_REQUEST', 422, { field });
  onlyKeys(p, ['x', 'y'], ['x', 'y']);
  for (const key of ['x', 'y']) {
    if (typeof p[key] !== 'number' || !Number.isFinite(p[key])) throw new CommandError('INVALID_REQUEST', 422, { field });
    if (Math.abs(p[key]) > TELEPORT_LIMIT_M) throw new CommandError('VALUE_OUT_OF_RANGE', 422, { field });
  }
  return { x: Math.round(p.x * 100) / 100, y: Math.round(p.y * 100) / 100 };
}

function worldPointArgs(a, revision) {
  onlyKeys(a, ['x', 'y'], ['x', 'y']);
  if (revision !== null) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
  for (const key of ['x', 'y']) {
    if (typeof a[key] !== 'number' || !Number.isFinite(a[key])) throw new CommandError('INVALID_REQUEST', 422, { field: key });
    if (Math.abs(a[key]) > TELEPORT_LIMIT_M) throw new CommandError('VALUE_OUT_OF_RANGE', 422, { field: key });
  }
  return { x: Math.round(a.x * 100) / 100, y: Math.round(a.y * 100) / 100 };
}

function panelArgs(a) {
  onlyKeys(a, ['panel'], ['panel']);
  if (!PANELS.includes(a.panel)) throw new CommandError('CAPABILITY_UNAVAILABLE', 422, { field: 'panel' });
  return { panel: a.panel };
}

export const COMMAND_TYPES = Object.keys(ARGS);

/** Returns a normalized command or throws CommandError. */
export function validateCommand(body) {
  if (!isObject(body)) throw new CommandError('INVALID_REQUEST', 400);
  onlyKeys(body, ['requestId', 'adapterInstanceId', 'type', 'expectedRevision', 'args'], ['requestId', 'adapterInstanceId', 'type', 'expectedRevision', 'args']);
  if (typeof body.requestId !== 'string' || !REQUEST_ID.test(body.requestId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'requestId' });
  if (typeof body.adapterInstanceId !== 'string' || !OPAQUE.test(body.adapterInstanceId)) throw new CommandError('INVALID_REQUEST', 422, { field: 'adapterInstanceId' });
  if (body.expectedRevision !== null && (typeof body.expectedRevision !== 'string' || !OPAQUE.test(body.expectedRevision))) throw new CommandError('INVALID_REQUEST', 422, { field: 'expectedRevision' });
  const rule = Object.hasOwn(ARGS, body.type) ? ARGS[body.type] : null;
  if (!rule) throw new CommandError('CAPABILITY_UNAVAILABLE', 422, { field: 'type' });
  if (!isObject(body.args)) throw new CommandError('INVALID_REQUEST', 422, { field: 'args' });
  return {
    requestId: body.requestId,
    adapterInstanceId: body.adapterInstanceId,
    type: body.type,
    expectedRevision: body.expectedRevision,
    args: rule(body.args, body.expectedRevision),
  };
}
