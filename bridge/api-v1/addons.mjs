// P18 add-ons: optional game modes that run in the v1 adapter (GtbikeV1Addons.cs) with GTA natives only, next to
// GTBike V. The registry below is the contract every client renders from (PC window, PWA, Android): an add-on is an
// id, texts, and typed parameters. A client draws one control per parameter from its descriptor, so a new add-on
// needs no client change. The adapter mirrors ids, ranges and defaults (GtbikeV1Addons.cs) and reports the values
// it holds; it persists them itself in v1\addons-settings.json.
import { revision } from './normalize.mjs';

/** Parameter descriptor: {id, label, description, type: integer|number|boolean|enum, min?, max?, step?, unit?, options?, default, liveEdit}. */
export const ADDONS = [
  {
    id: 'alleycat',
    name: 'Alleycat courier',
    category: 'mission',
    helpId: 'addons.alleycat',
    allowedDuringCourse: false, // A GTBike V course has its own route and guidance; loading one ends the mission.
    summary: 'Pick up and deliver fragile packages through Downtown Los Santos traffic against the clock.',
    description: 'Random pickup and drop-off points in Downtown Los Santos, marked on the map with a GPS route; you choose your own line through traffic. '
      + 'Packages are fragile: hitting a car, a kerb or a wall, or falling, damages the one you carry, and at 0% it is lost. '
      + 'Heavy packages bring a storm and a headwind that GTBike V sends to your trainer as extra resistance; rain also lowers grip. '
      + 'The clock starts at the first pickup. Starting cancels the current course or other add-on first; loading a course ends the mission.',
    parameters: [
      { id: 'packages', label: 'Packages', description: 'How many packages to pick up and deliver.', type: 'integer', min: 1, max: 10, step: 1, unit: null, default: 5, liveEdit: false },
      { id: 'timeLimitMin', label: 'Time limit', description: 'Total time, counted from the first pickup.', type: 'integer', min: 5, max: 60, step: 1, unit: 'min', default: 20, liveEdit: false },
      { id: 'fragility', label: 'Fragility', description: 'How much damage a hit does to the package.', type: 'enum', options: [{ id: 'low', label: 'Low' }, { id: 'normal', label: 'Normal' }, { id: 'high', label: 'High' }], default: 'normal', liveEdit: true },
      { id: 'heavyChancePct', label: 'Heavy packages', description: 'Chance that a package is heavy (storm and headwind while you carry it).', type: 'integer', min: 0, max: 100, step: 10, unit: '%', default: 40, liveEdit: false },
      { id: 'stormWhenHeavy', label: 'Storm with heavy packages', description: 'Thunderstorm in Downtown and rain elsewhere while a heavy package is carried. Rain lowers grip in GTBike V.', type: 'boolean', default: true, liveEdit: true },
      { id: 'headwindMps', label: 'Headwind with heavy packages', description: 'Wind speed set against the rider while a heavy package is carried; 0 turns it off. GTBike V sends it to the trainer as resistance.', type: 'number', min: 0, max: 12, step: 0.5, unit: 'm/s', default: 10, liveEdit: true },
      { id: 'showHud', label: 'On-screen status', description: 'Package, integrity, time and distance as a subtitle in the game.', type: 'boolean', default: true, liveEdit: true },
    ],
  },
  {
    id: 'survivalSprint',
    name: 'Survival sprint',
    category: 'training',
    helpId: 'addons.survivalSprint',
    allowedDuringCourse: true, // The chaser follows the rider's own trail, wherever they ride.
    summary: 'Pass the target speed and a truck appears behind you. Hold the speed and keep pedalling or it runs you down.',
    description: 'For threshold and high-intensity intervals, best on long roads such as the canals or the coast highway. '
      + 'Once you pass the target speed a chaser appears behind you. While you hold the target speed and keep pedalling it keeps its distance, and riding faster opens the gap a little. '
      + 'Below the target speed, for example on a climb, or coasting for longer than the grace time (GTBike V power and cadence near zero, even downhill at speed), it closes in fast. '
      + 'If it reaches you it rams you. Survive the set time to win. Starting cancels the current course or other add-on first.',
    parameters: [
      { id: 'targetKmh', label: 'Target speed', description: 'Speed that starts the chase and that you must hold.', type: 'integer', min: 15, max: 60, step: 1, unit: 'km/h', default: 30, liveEdit: true },
      { id: 'startGapM', label: 'Start distance', description: 'How far behind you the chaser appears.', type: 'integer', min: 10, max: 80, step: 5, unit: 'm', default: 30, liveEdit: false },
      { id: 'closingKmh', label: 'Closing speed', description: 'How much faster than you (or than the target) the chaser drives while you are too slow or coasting.', type: 'integer', min: 2, max: 30, step: 1, unit: 'km/h', default: 12, liveEdit: true },
      { id: 'coastGraceS', label: 'Coasting allowed', description: 'Seconds without pedalling before the chaser closes in.', type: 'integer', min: 1, max: 10, step: 1, unit: 's', default: 3, liveEdit: true },
      { id: 'minPowerW', label: 'Pedalling threshold', description: 'Power above this counts as pedalling (a cadence of 10 rpm or more also does). Without power or cadence data only the speed rule applies.', type: 'integer', min: 0, max: 200, step: 5, unit: 'W', default: 20, liveEdit: true },
      { id: 'durationMin', label: 'Survive for', description: 'Length of the chase, counted from when the chaser appears.', type: 'integer', min: 1, max: 60, step: 1, unit: 'min', default: 10, liveEdit: false },
      { id: 'chaser', label: 'Chaser', description: 'Vehicle behind you. The Phantom truck is used when the chosen one cannot be loaded.', type: 'enum', options: [{ id: 'phantomWedge', label: 'Phantom Wedge' }, { id: 'police', label: 'Police cruiser' }, { id: 'truck', label: 'Phantom truck' }], default: 'phantomWedge', liveEdit: false },
      { id: 'ramOnCatch', label: 'Ram when caught', description: 'The chaser drives into you when it catches you. Off: the run simply ends.', type: 'boolean', default: true, liveEdit: true },
      { id: 'showHud', label: 'On-screen status', description: 'Time, distance bar, speed and a pedalling warning as a subtitle in the game.', type: 'boolean', default: true, liveEdit: true },
    ],
  },
  {
    id: 'ghost',
    name: 'Ghost segments',
    category: 'training',
    helpId: 'addons.ghost',
    allowedDuringCourse: true,
    segments: true, // Has a list of timed segments (addons.saveSegment / addons.removeSegment).
    summary: 'Timed segments you mark on the map. Race a see-through ghost of your best attempt and beat your record.',
    description: 'Mark segments on the PC window map (right-click: Ghost segment start here, then Ghost segment finish here; the same point twice makes a loop). '
      + 'With this add-on on, riding through a start circle starts the clock and a see-through ghost rider replays your best attempt (or your last one); the screen shows the live gap, green when you are ahead. '
      + 'The finish saves the attempt, and a faster one becomes the new record and the new ghost. Records keep the date, average power and speed. '
      + 'Teleporting cancels an attempt. Starting cancels the current course or other add-on first.',
    parameters: [
      { id: 'ghostSource', label: 'Race against', description: 'The ghost replays your best attempt or your last one.', type: 'enum', options: [{ id: 'best', label: 'My best' }, { id: 'last', label: 'My last' }], default: 'best', liveEdit: true },
      { id: 'showGhost', label: 'Show the ghost rider', description: 'Off: only the live gap is shown.', type: 'boolean', default: true, liveEdit: true },
      { id: 'ghostOpacityPct', label: 'Ghost opacity', description: 'How visible the ghost rider is.', type: 'integer', min: 20, max: 100, step: 10, unit: '%', default: 60, liveEdit: true },
      { id: 'gateRadiusM', label: 'Gate size', description: 'Radius of the start and finish circles.', type: 'integer', min: 8, max: 30, step: 1, unit: 'm', default: 12, liveEdit: true },
      { id: 'maxAttemptMin', label: 'Give up after', description: 'An attempt longer than this is cancelled.', type: 'integer', min: 5, max: 120, step: 5, unit: 'min', default: 60, liveEdit: true },
      { id: 'showHud', label: 'On-screen status', description: 'Segment, time, gap to the ghost and distance to the finish as a subtitle in the game.', type: 'boolean', default: true, liveEdit: true },
    ],
  },
  {
    id: 'discover',
    name: 'Discover',
    category: 'exploration',
    helpId: 'addons.discover',
    allowedDuringCourse: false, // Free ride: a course has its own route.
    places: true,   // Has saved places (addons.savePlace / addons.removePlace).
    progress: true, // Keeps XP and levels in a local progress file.
    summary: 'Find a hidden spot with only a hint: the zone and the distance, hot or cold. Every find earns XP.',
    description: 'Each round hides a spot: one of your places saved on the PC window map (right-click: Save place here) or a random street spot around you. '
      + 'You only get the zone it is in and the straight-line distance, with hot or cold and closer or farther as you ride; optionally a yellow search circle on the game map that contains it, not centred on it. '
      + 'Reach it to find it. XP grows with the distance, finding it fast, a first visit to a place, a new zone and your daily streak; playing without the search circle earns more. '
      + 'Your XP, level, finds and history stay on this PC in the progress file. Starting cancels the current course or other add-on first.',
    parameters: [
      { id: 'source', label: 'Spots', description: 'Your saved places, random street spots, or your places first and random ones when none is in range.', type: 'enum', options: [{ id: 'mixed', label: 'My places, then random' }, { id: 'myPlaces', label: 'Only my places' }, { id: 'random', label: 'Only random spots' }], default: 'mixed', liveEdit: false },
      { id: 'rounds', label: 'Spots per run', description: 'How many spots to find in one run.', type: 'integer', min: 1, max: 20, step: 1, unit: null, default: 3, liveEdit: false },
      { id: 'minDistanceKm', label: 'Minimum distance', description: 'Closest a spot may be when its round starts.', type: 'number', min: 0.5, max: 10, step: 0.5, unit: 'km', default: 1, liveEdit: false },
      { id: 'maxDistanceKm', label: 'Maximum distance', description: 'Farthest a spot may be when its round starts.', type: 'number', min: 1, max: 20, step: 0.5, unit: 'km', default: 4, liveEdit: false },
      { id: 'roundTimeMin', label: 'Time per spot', description: 'After this the spot is revealed and the next round starts.', type: 'integer', min: 5, max: 90, step: 5, unit: 'min', default: 30, liveEdit: false },
      { id: 'searchAreaM', label: 'Search circle', description: 'Radius of the yellow circle on the game map that contains the spot; 0 hides it and earns 25% more XP.', type: 'integer', min: 0, max: 1000, step: 50, unit: 'm', default: 300, liveEdit: true },
      { id: 'discoverRadiusM', label: 'Find radius', description: 'How close you must get to find the spot.', type: 'integer', min: 10, max: 60, step: 5, unit: 'm', default: 25, liveEdit: true },
      { id: 'showHud', label: 'On-screen status', description: 'Hint zone, distance, hot or cold, time left and level as a subtitle in the game.', type: 'boolean', default: true, liveEdit: true },
    ],
  },
];
export const MAX_PLACES = 100;
export const PLACE_ID = /^pl-[a-f0-9]{8,20}$/;
export const MAX_SEGMENTS = 50;

export const ADDON_IDS = ADDONS.map(a => a.id);
const byId = id => ADDONS.find(a => a.id === id) ?? null;
export const ADDON_STATES = ['idle', 'preparing', 'running', 'finished', 'aborted', 'failed'];
const ACTIVE = new Set(['preparing', 'running']);
export const isActive = state => ACTIVE.has(state);
/** Why a run ended. */
export const OUTCOMES = ['completed', 'timeUp', 'caught', 'stopped', 'courseLoaded', 'modInactive', 'riderLost', 'noPoints', 'internal'];
export const PHASES = ['locating', 'toPickup', 'toDropoff', 'armed', 'chased', 'waiting', 'onSegment', 'searching', 'found'];
const POINT_KINDS = ['pickup', 'dropoff', 'start', 'finish'];
/** Reason codes the adapter may give for a failed start. */
const START_FAILURES = new Set(['ADDON_NO_POINTS', 'ADDON_BUSY', 'RIDER_NOT_ON_BIKE', 'GAME_NOT_IN_CONTROL', 'MOD_INACTIVE', 'VALUE_NOT_ALLOWED', 'INTERNAL']);

const isNum = v => typeof v === 'number' && Number.isFinite(v);
const round = (v, d = 2) => Math.round(v * 10 ** d) / 10 ** d;
const bounded = (v, max) => (typeof v === 'string' ? v.slice(0, max) : null);

/** Returns [code, details] when the value is not acceptable for the descriptor, else null. */
function checkValue(p, v) {
  if (p.type === 'boolean') return typeof v === 'boolean' ? null : ['INVALID_REQUEST', { field: p.id }];
  if (p.type === 'enum') {
    if (typeof v !== 'string') return ['INVALID_REQUEST', { field: p.id }];
    return p.options.some(o => o.id === v) ? null : ['VALUE_NOT_ALLOWED', { field: p.id }];
  }
  if (!isNum(v) || (p.type === 'integer' && !Number.isInteger(v))) return ['INVALID_REQUEST', { field: p.id }];
  if (v < p.min || v > p.max) return ['VALUE_OUT_OF_RANGE', { field: p.id }];
  // Steps are checked with a tolerance so 0.1-based floats from clients still match.
  const steps = (v - p.min) / p.step;
  return Math.abs(steps - Math.round(steps)) < 1e-6 ? null : ['VALUE_NOT_ALLOWED', { field: p.id }];
}

/** Shape check for addons.configure args (validate.mjs). Throws nothing; returns [code, details] or null. */
export function checkParams(addonId, params) {
  const addon = byId(addonId);
  if (!addon) return ['NOT_FOUND', { field: 'addonId' }];
  const keys = Object.keys(params);
  if (keys.length === 0) return ['INVALID_REQUEST', { field: 'params' }];
  for (const key of keys) {
    const p = addon.parameters.find(x => x.id === key);
    if (!p) return ['INVALID_REQUEST', { field: key }];
    const bad = checkValue(p, params[key]);
    if (bad) return bad;
  }
  return null;
}

/** The values the adapter reports, completed with defaults for anything missing or invalid. */
function valuesOf(addon, reported) {
  const out = {};
  for (const p of addon.parameters) {
    const v = reported?.[p.id];
    out[p.id] = v !== undefined && checkValue(p, v) === null ? v : p.default;
  }
  return out;
}

function rawAddon(snap, id) {
  const raw = snap?.fresh ? snap.raw.addons : null;
  return raw && typeof raw === 'object' ? raw[id] ?? null : null;
}

/** Start preconditions from one fresh snapshot (the adapter re-checks them on its Tick). */
export function startReason(snap, id) {
  const raw = snap.raw;
  const a = rawAddon(snap, id);
  if (!a) return 'CAPABILITY_UNAVAILABLE';
  if (isActive(a.state)) return null; // Enabling a running add-on is a no-op.
  if (raw.isDtOk !== true) return 'MOD_INACTIVE';
  if (raw.gameControl !== 'inControl') return 'GAME_NOT_IN_CONTROL';
  // The adapter replaces the current course/add-on. It must be able to leave a loaded course first.
  if (raw.activeRoute && !raw.commands?.includes('routes.apply')) return 'CAPABILITY_UNAVAILABLE';
  if (raw.addonContext?.onBicycle === false) return 'RIDER_NOT_ON_BIKE';
  if (raw.teleport?.status === 'running') return 'BRIDGE_BUSY';
  return null;
}

/** addonsRevision: changes only when a parameter value changes (not with run progress). */
export function addonsRevision(snap) {
  if (!snap?.fresh || !snap.raw.addons) return null;
  return revision('addons', ADDONS.map(a => [a.id, valuesOf(a, rawAddon(snap, a.id)?.params)]));
}

const point = n => (n && isNum(n.x) && isNum(n.y) && Math.abs(n.x) <= 100_000 && Math.abs(n.y) <= 100_000
  ? { x: round(n.x), y: round(n.y), kind: POINT_KINDS.includes(n.kind) ? n.kind : 'pickup', distanceM: isNum(n.distanceM) ? Math.round(n.distanceM) : null }
  : null);
const stat = (id, label, value, unit = null) => ({ id, label, value, unit });
const intOr0 = v => (Number.isInteger(v) && v >= 0 && v <= 1000 ? v : 0);

const num = (v, d = 0) => (isNum(v) ? round(v, d) : null);
const bool = v => (typeof v === 'boolean' ? v : null);
const runningStats = {
  alleycat: r => {
    const total = intOr0(r.packagesTotal);
    return [
      stat('package', 'Package', Math.min(total, intOr0(r.delivered) + intOr0(r.damaged) + 1)),
      stat('packagesTotal', 'Packages', total),
      stat('delivered', 'Delivered', intOr0(r.delivered)),
      stat('damaged', 'Lost', intOr0(r.damaged)),
      stat('carrying', 'Carrying', r.carrying === true),
      stat('heavy', 'Heavy', r.carrying === true && r.heavy === true),
      stat('integrityPct', 'Integrity', r.carrying === true && isNum(r.integrityPct) ? Math.max(0, Math.min(100, Math.round(r.integrityPct))) : null, '%'),
      stat('score', 'Score', isNum(r.score) ? Math.round(r.score) : 0),
      stat('zone', 'Zone', bounded(r.zoneName, 48) ?? bounded(r.zone, 16)),
      stat('windMps', 'Headwind', num(r.windMps, 1), 'm/s'),
      stat('weather', 'Weather', bounded(r.weather, 24)),
    ];
  },
  survivalSprint: r => [
    stat('gapM', 'Gap', num(r.gapM, 1), 'm'),
    stat('stabilityPct', 'Stability', isNum(r.stabilityPct) ? Math.max(0, Math.min(100, Math.round(r.stabilityPct))) : null, '%'),
    stat('speedKmh', 'Speed', num(r.speedKmh, 1), 'km/h'),
    stat('targetKmh', 'Target', num(r.targetKmh), 'km/h'),
    stat('pedaling', 'Pedalling', bool(r.pedaling)),
    stat('coastingS', 'Coasting', num(r.coastingS, 1), 's'),
    stat('accelMps2', 'Acceleration', num(r.accelMps2, 2), 'm/s2'),
    stat('distanceM', 'Distance', num(r.distanceM), 'm'),
    stat('chaser', 'Chaser', bounded(r.chaser, 32)),
  ],
  ghost: r => [
    stat('segment', 'Segment', bounded(r.segment, 40)),
    stat('deltaS', 'Gap to ghost', num(r.deltaS, 1), 's'),
    stat('bestS', 'Best', num(r.bestS, 2), 's'),
    stat('ghost', 'Ghost visible', r.ghost === true),
    stat('distanceM', 'Distance', num(r.distanceM), 'm'),
    stat('lastSegment', 'Last segment', bounded(r.last?.segment, 40)),
    stat('lastTimeS', 'Last time', num(r.last?.timeS, 2), 's'),
    stat('lastDeltaS', 'Last vs best', num(r.last?.deltaS, 2), 's'),
    stat('lastRecord', 'Last was a record', typeof r.last?.record === 'boolean' ? r.last.record : null),
  ],
  // Never the spot's position or name while it is hidden: only the hint.
  discover: r => [
    stat('round', 'Spot', intOr0(r.round)),
    stat('rounds', 'Spots', intOr0(r.rounds)),
    stat('hintZone', 'Hint', bounded(r.hintZone, 48)),
    stat('distanceM', 'Distance', num(r.distanceM), 'm'),
    stat('heat', 'Heat', ['burning', 'hot', 'warm', 'cool', 'cold'].includes(r.heat) ? r.heat : null),
    stat('trend', 'Trend', ['closer', 'farther', 'steady'].includes(r.trend) ? r.trend : null),
    stat('spotKind', 'Spot kind', r.targetSource === 'place' ? 'my place' : r.targetSource === 'random' ? 'random' : null),
    stat('found', 'Found', intOr0(r.found)),
    stat('missed', 'Missed', intOr0(r.missed)),
    stat('xp', 'XP this run', intOr0(r.xp)),
  ],
};
const resultStats = {
  alleycat: r => [
    stat('delivered', 'Delivered', intOr0(r.delivered)),
    stat('damaged', 'Lost', intOr0(r.damaged)),
    stat('packagesTotal', 'Packages', intOr0(r.packagesTotal)),
    stat('score', 'Score', isNum(r.score) ? Math.round(r.score) : 0),
    stat('elapsedS', 'Time', isNum(r.elapsedS) ? Math.round(r.elapsedS) : 0, 's'),
  ],
  survivalSprint: r => [
    stat('survivedS', 'Survived', isNum(r.survivedS) ? Math.round(r.survivedS) : 0, 's'),
    stat('distanceM', 'Distance', num(r.distanceM), 'm'),
    stat('maxSpeedKmh', 'Top speed', num(r.maxSpeedKmh, 1), 'km/h'),
    stat('closestGapM', 'Closest gap', num(r.closestGapM, 1), 'm'),
    stat('targetKmh', 'Target', num(r.targetKmh), 'km/h'),
  ],
  ghost: r => [
    stat('attempts', 'Attempts', intOr0(r.attempts)),
    stat('records', 'New records', intOr0(r.records)),
  ],
  discover: r => [
    stat('found', 'Found', intOr0(r.found)),
    stat('missed', 'Missed', intOr0(r.missed)),
    stat('rounds', 'Spots', intOr0(r.rounds)),
    stat('xp', 'XP earned', intOr0(r.xp)),
    stat('level', 'Level', intOr0(r.level)),
  ],
};

/** Saved places of add-ons that have them (discover), bounded. */
function placesOf(a) {
  const list = Array.isArray(a?.places) ? a.places.slice(0, MAX_PLACES) : [];
  return list.map(p => {
    const point = point2(p?.point);
    if (typeof p?.id !== 'string' || !PLACE_ID.test(p.id) || typeof p.name !== 'string' || !point) return null;
    return { id: p.id, name: p.name.slice(0, 40), point, found: intOr0(p.found), bestTimeS: num(p.bestTimeS, 1),
      lastFoundAtUtc: typeof p.lastFoundAtUtc === 'string' && !Number.isNaN(Date.parse(p.lastFoundAtUtc)) ? p.lastFoundAtUtc : null };
  }).filter(Boolean);
}

const bigInt = v => (Number.isInteger(v) && v >= 0 && v <= 1e12 ? v : 0);
const isoOrNull = v => (typeof v === 'string' && !Number.isNaN(Date.parse(v)) ? v : null);
const ROUTE_ID = /^(0|[1-9][0-9]{0,4})$/;
/** Completed GTBike V routes from the progress file, most recent first. */
function routesOf(p) {
  const list = Array.isArray(p?.routes) ? p.routes.slice(0, 500) : [];
  return list.map(r => (r && typeof r.routeId === 'string' && ROUTE_ID.test(r.routeId) ? {
    routeId: r.routeId, name: bounded(r.name, 64) ?? r.routeId, completions: intOr0(r.completions), laps: intOr0(r.laps),
    bestTimeS: num(r.bestTimeS, 1), bestAvgPowerW: num(r.bestAvgPowerW), bestAvgSpeedKmh: num(r.bestAvgSpeedKmh, 1), distanceM: num(r.distanceM),
    firstAtUtc: isoOrNull(r.firstAtUtc), lastAtUtc: isoOrNull(r.lastAtUtc),
  } : null)).filter(Boolean).sort((x, y) => String(y.lastAtUtc ?? '').localeCompare(String(x.lastAtUtc ?? '')));
}

/** GET /api/v1/progress: the rider's XP from Discover finds and completed routes (rider-progress.json in the adapter). */
export function toProgress(snap) {
  const read = !snap ? 'ADAPTER_ABSENT' : !snap.fresh ? 'ADAPTER_STALE' : !snap.raw.riderProgress ? 'CAPABILITY_UNAVAILABLE' : null;
  return { reasonCode: read, progress: read ? null : progressOf({ progress: snap.raw.riderProgress }) };
}

/** XP, level and totals from the adapter's progress file, plus the last finds and completed routes. */
function progressOf(a) {
  const p = a?.progress;
  if (!p || typeof p !== 'object') return null;
  const xp = bigInt(p.xp), level = Math.max(1, intOr0(p.level)), start = bigInt(p.levelStartXp), next = bigInt(p.nextLevelXp);
  const find = f => (f && typeof f === 'object' ? {
    kind: f.kind === 'route' ? 'route' : 'discover',
    atUtc: isoOrNull(f.atUtc), label: bounded(f.name, 64) ?? (f.kind === 'route' ? 'Route' : 'Spot'), zone: bounded(f.zone, 48), timeS: num(f.timeS, 1), distanceM: num(f.distanceM), xp: bigInt(f.xp),
  } : null);
  const last = find(p.lastFind);
  return {
    xp, level, levelName: bounded(p.levelName, 32) ?? '',
    xpToNextLevel: next > xp ? next - xp : 0,
    levelProgressPct: next > start ? Math.max(0, Math.min(100, Math.round(((xp - start) / (next - start)) * 100))) : 0,
    stats: [
      stat('discoveries', 'Spots found', intOr0(p.discoveries)),
      stat('placesFound', 'My places found', intOr0(p.placesFound)),
      stat('zonesFound', 'Zones found', intOr0(p.zonesFound)),
      stat('streakDays', 'Daily streak', intOr0(p.streakDays), 'days'),
      stat('bestStreakDays', 'Best streak', intOr0(p.bestStreakDays), 'days'),
      stat('missed', 'Missed', intOr0(p.missed)),
      stat('routesCompleted', 'Routes completed', intOr0(p.routesCompleted)),
      stat('routeCompletions', 'Route finishes and laps', intOr0(p.routeCompletions)),
      stat('totalDistanceKm', 'Distance with XP', isNum(p.totalDistanceM) ? round(p.totalDistanceM / 1000, 1) : 0, 'km'),
    ],
    recent: (Array.isArray(p.recent) ? p.recent.slice(0, 5) : []).map(find).filter(Boolean),
    lastFind: last && p.lastFind ? { ...last, stats: [
      stat('baseXp', 'Distance', bigInt(Math.round(p.lastFind.baseXp ?? 0)), 'XP'),
      stat('speedBonusXp', 'Speed bonus', bigInt(Math.round(p.lastFind.speedBonusXp ?? 0)), 'XP'),
      stat('noHintBonusXp', 'No circle bonus', bigInt(Math.round(p.lastFind.noHintBonusXp ?? 0)), 'XP'),
      stat('firstTimeBonusXp', 'First visit', bigInt(p.lastFind.firstTimeBonusXp ?? 0), 'XP'),
      stat('newZoneBonusXp', 'New zone', bigInt(p.lastFind.newZoneBonusXp ?? 0), 'XP'),
      stat('streakDays', 'Streak', intOr0(p.lastFind.streakDays), 'days'),
    ] } : null,
    lastRoute: p.lastRoute && find(p.lastRoute) ? { ...find(p.lastRoute), stats: [
      stat('baseXp', 'Distance', bigInt(Math.round(p.lastRoute.baseXp ?? 0)), 'XP'),
      stat('firstTimeBonusXp', 'First time', bigInt(p.lastRoute.firstTimeBonusXp ?? 0), 'XP'),
      stat('personalBestBonusXp', 'Personal best', bigInt(p.lastRoute.personalBestBonusXp ?? 0), 'XP'),
      stat('avgPowerW', 'Average power', intOr0(p.lastRoute.avgPowerW), 'W'),
      stat('streakDays', 'Streak', intOr0(p.lastRoute.streakDays), 'days'),
    ] } : null,
    routes: routesOf(p),
  };
}

const point2 = p => (p && isNum(p.x) && isNum(p.y) && Math.abs(p.x) <= 10_000 && Math.abs(p.y) <= 10_000 ? { x: round(p.x), y: round(p.y) } : null);
/** Segments the adapter reports, bounded and with derived straight distance and loop flag. */
function segmentsOf(a) {
  const list = Array.isArray(a?.segments) ? a.segments.slice(0, MAX_SEGMENTS) : [];
  return list.map(s => {
    const start = point2(s?.start), finish = point2(s?.finish);
    if (typeof s?.id !== 'string' || !SEGMENT_ID.test(s.id) || typeof s.name !== 'string' || !start || !finish) return null;
    const straightM = Math.round(Math.hypot(finish.x - start.x, finish.y - start.y));
    const b = s.best && isNum(s.best.timeS) ? s.best : null;
    return {
      id: s.id, name: s.name.slice(0, 40), start, finish, loop: straightM < 30, straightM,
      attempts: intOr0(s.attempts), lastTimeS: num(s.lastTimeS, 2),
      best: b ? { timeS: round(b.timeS, 2), setAtUtc: typeof b.setAtUtc === 'string' && !Number.isNaN(Date.parse(b.setAtUtc)) ? b.setAtUtc : null,
        avgPowerW: num(b.avgPowerW), avgSpeedKmh: num(b.avgSpeedKmh, 1), distanceM: num(b.distanceM) } : null,
    };
  }).filter(Boolean);
}
export const SEGMENT_ID = /^seg-[a-f0-9]{8,20}$/;

/** Generic progress block: phase, clock, stats a client can list as is, and the next map point. */
function runOf(id, a) {
  const r = a?.run;
  if (!isActive(a?.state) || !r || typeof r !== 'object' || !runningStats[id]) return null;
  return {
    phase: PHASES.includes(r.phase) ? r.phase : id === 'survivalSprint' ? 'armed' : 'locating',
    elapsedS: isNum(r.elapsedS) ? Math.round(r.elapsedS) : 0,
    timeLeftS: isNum(r.timeLeftS) ? Math.max(0, Math.round(r.timeLeftS)) : null,
    stats: runningStats[id](r),
    nextPoint: point(r.next),
  };
}

function resultOf(id, a) {
  const r = a?.result;
  if (!r || typeof r !== 'object' || !resultStats[id]) return null;
  return {
    outcome: OUTCOMES.includes(r.outcome) ? r.outcome : 'internal',
    finishedAtUtc: typeof r.finishedAtUtc === 'string' && !Number.isNaN(Date.parse(r.finishedAtUtc)) ? r.finishedAtUtc : null,
    stats: resultStats[id](r),
  };
}

/** GET /api/v1/addons. `reasons` are the capability reasons for addons.setEnabled / addons.configure. */
export function toAddons(snap, reasons = {}) {
  const read = !snap ? 'ADAPTER_ABSENT' : !snap.fresh ? 'ADAPTER_STALE' : !snap.raw.addons ? 'CAPABILITY_UNAVAILABLE' : null;
  return {
    addonsRevision: read ? null : addonsRevision(snap),
    reasonCode: read,
    addons: ADDONS.map(addon => {
      const a = read ? null : rawAddon(snap, addon.id);
      const state = ADDON_STATES.includes(a?.state) ? a.state : 'idle';
      const values = valuesOf(addon, a?.params);
      const active = isActive(state);
      const writeReason = reasons['addons.setEnabled'] ?? read;
      return {
        id: addon.id, name: addon.name, category: addon.category, summary: addon.summary, description: addon.description, helpId: addon.helpId,
        state, enabled: active,
        // Why "Start" is unavailable now (null = allowed); stopping a running add-on is always allowed while the adapter is fresh.
        startReasonCode: writeReason ?? (a ? startReason(snap, addon.id) : 'CAPABILITY_UNAVAILABLE'),
        configureReasonCode: reasons['addons.configure'] ?? read,
        parameters: addon.parameters.map(p => ({ ...p, value: read || !a ? null : values[p.id], editable: !read && !!a && (p.liveEdit || !active) })),
        segments: addon.segments ? (read || !a ? [] : segmentsOf(a)) : null,
        places: addon.places ? (read || !a ? [] : placesOf(a)) : null,
        progress: addon.progress ? (read || !a ? null : progressOf(a)) : null,
        run: runOf(addon.id, a),
        result: active ? null : resultOf(addon.id, a),
      };
    }),
  };
}

/** Precheck for addons.setEnabled / addons.configure against the documents (server.mjs). [code, details] or null. */
export function addonPrecheck(command, snap) {
  const a = command.args;
  const addon = byId(a.addonId);
  if (!addon) return ['NOT_FOUND', { field: 'addonId' }];
  const raw = rawAddon(snap, a.addonId);
  if (!raw) return ['CAPABILITY_UNAVAILABLE', { field: 'addonId' }];
  if (command.type === 'addons.saveSegment' || command.type === 'addons.removeSegment') {
    if (!addon.segments) return ['CAPABILITY_UNAVAILABLE', { field: 'addonId' }];
    const list = segmentsOf(raw);
    const exists = list.some(s => s.id === a.segmentId);
    if (isActive(raw.state) && raw.run?.segmentId === a.segmentId) return ['VALUE_NOT_ALLOWED', { field: 'segmentId' }]; // Its attempt is running.
    if (command.type === 'addons.removeSegment') return exists ? null : ['NOT_FOUND', { field: 'segmentId' }];
    return exists || list.length < MAX_SEGMENTS ? null : ['VALUE_NOT_ALLOWED', { field: 'segmentId' }];
  }
  if (command.type === 'addons.savePlace' || command.type === 'addons.removePlace') {
    if (!addon.places) return ['CAPABILITY_UNAVAILABLE', { field: 'addonId' }];
    const list = placesOf(raw);
    const exists = list.some(p => p.id === a.placeId);
    const searched = isActive(raw.state) && raw.run?.targetPlaceId === a.placeId; // The hidden spot right now.
    if (command.type === 'addons.removePlace') return !exists ? ['NOT_FOUND', { field: 'placeId' }] : searched ? ['VALUE_NOT_ALLOWED', { field: 'placeId' }] : null;
    if (searched) {
      const old = list.find(p => p.id === a.placeId);
      if (Math.abs(old.point.x - a.point.x) > 1 || Math.abs(old.point.y - a.point.y) > 1) return ['VALUE_NOT_ALLOWED', { field: 'placeId' }];
    }
    return exists || list.length < MAX_PLACES ? null : ['VALUE_NOT_ALLOWED', { field: 'placeId' }];
  }
  if (command.type === 'addons.setEnabled') {
    if (!a.enabled) return null; // Stopping needs no revision and is always allowed.
    if (command.expectedRevision !== addonsRevision(snap)) return ['REVISION_CONFLICT', { resource: 'addons' }];
    const reason = startReason(snap, a.addonId);
    return reason ? [reason] : null;
  }
  if (command.expectedRevision !== addonsRevision(snap)) return ['REVISION_CONFLICT', { resource: 'addons' }];
  if (isActive(raw.state)) {
    const locked = Object.keys(a.params).find(k => !addon.parameters.find(p => p.id === k).liveEdit);
    if (locked) return ['VALUE_NOT_ALLOWED', { field: locked }];
  }
  return null;
}

/** Evidence (operations.mjs). */
export function addonEvidence(type, args, base, snap) {
  const a = rawAddon(snap, args.addonId);
  if (!a) return null;
  if (type === 'addons.savePlace' || type === 'addons.removePlace') {
    const found = placesOf(a).find(p => p.id === args.placeId) ?? null;
    if (type === 'addons.removePlace') return found ? null : { status: 'applied', observed: { addonId: args.addonId, placeId: args.placeId, removed: true } };
    if (!found || found.name !== args.name || Math.abs(found.point.x - args.point.x) > 0.5 || Math.abs(found.point.y - args.point.y) > 0.5) return null;
    return { status: 'applied', observed: { addonId: args.addonId, placeId: found.id, name: found.name, found: found.found } };
  }
  if (type === 'addons.saveSegment' || type === 'addons.removeSegment') {
    const found = segmentsOf(a).find(s => s.id === args.segmentId) ?? null;
    if (type === 'addons.removeSegment') return found ? null : { status: 'applied', observed: { addonId: args.addonId, segmentId: args.segmentId, removed: true } };
    const near = (p, q) => Math.abs(p.x - q.x) <= 0.5 && Math.abs(p.y - q.y) <= 0.5;
    if (!found || found.name !== args.name || !near(found.start, args.start) || !near(found.finish, args.finish)) return null;
    return { status: 'applied', observed: { addonId: args.addonId, segmentId: found.id, name: found.name, best: found.best } };
  }
  if (type === 'addons.configure') {
    const addon = byId(args.addonId);
    const values = valuesOf(addon, a.params);
    for (const key of Object.keys(args.params)) if (values[key] !== args.params[key]) return null;
    return { status: 'applied', observed: { addonId: args.addonId, params: Object.fromEntries(Object.keys(args.params).map(k => [k, values[k]])), addonsRevision: addonsRevision(snap) } };
  }
  if (!args.enabled) return isActive(a.state) ? null : { status: 'applied', observed: { addonId: args.addonId, state: a.state } };
  // The adapter tags the run with the requestId that started it; 'preparing' means it is still locating the first point.
  if (a.requestId !== base.requestId) return null;
  // A new run is only applied once the previous route and add-on have actually stopped.
  if (a.state !== 'failed' && (snap.raw.activeRoute || ADDONS.some(other => other.id !== args.addonId && isActive(rawAddon(snap, other.id)?.state)))) return null;
  if (a.state === 'running') return { status: 'applied', observed: { addonId: args.addonId, state: 'running' } };
  if (a.state === 'failed') return { status: 'failed', observed: { addonId: args.addonId, state: 'failed' }, code: START_FAILURES.has(a.reasonCode) ? a.reasonCode : 'INTERNAL' };
  if (a.state === 'finished' || a.state === 'aborted') return { status: 'applied', observed: { addonId: args.addonId, state: a.state } }; // Started and already over.
  return null;
}

/** PC map: the next point of every running add-on. */
export function addonPoints(snap) {
  if (!snap?.fresh || !snap.raw.addons) return [];
  return ADDONS.map(addon => {
    const a = rawAddon(snap, addon.id);
    const run = runOf(addon.id, a);
    return run?.nextPoint ? { addonId: addon.id, ...run.nextPoint } : null;
  }).filter(Boolean);
}

/** Does a running add-on forbid the PC teleport (it would skip the mission)? */
export const addonBlocksTeleport = raw => ADDONS.some(addon => {
  const a = raw?.addons?.[addon.id];
  return typeof a?.blocksTeleport === 'boolean' ? a.blocksTeleport : isActive(a?.state); // Ghost: only during an attempt.
});

/** PC map: every saved place (name, point, how often found) of add-ons that have them. */
export function addonPlaces(snap) {
  if (!snap?.fresh || !snap.raw.addons) return [];
  return ADDONS.filter(addon => addon.places).flatMap(addon => placesOf(rawAddon(snap, addon.id)).map(p => ({ addonId: addon.id, id: p.id, name: p.name, point: p.point, found: p.found })));
}

/** PC map: every segment (start, finish, name, best time) of add-ons that have them. */
export function addonSegments(snap) {
  if (!snap?.fresh || !snap.raw.addons) return [];
  return ADDONS.filter(addon => addon.segments).flatMap(addon => segmentsOf(rawAddon(snap, addon.id)).map(s => ({
    addonId: addon.id, id: s.id, name: s.name, start: s.start, finish: s.finish, loop: s.loop, bestTimeS: s.best?.timeS ?? null,
  })));
}

/** Capability reasons for the three add-on ids, from the shared write preconditions. */
export function addonReasons(snap, writeBlock) {
  const raw = snap?.raw ?? {};
  const read = !snap ? 'ADAPTER_ABSENT' : !snap.fresh ? 'ADAPTER_STALE' : null;
  return {
    'addons.read': read ?? (raw.addons == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'addons.setEnabled': writeBlock(snap, 'addons.setEnabled') ?? (raw.addons == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'addons.configure': writeBlock(snap, 'addons.configure') ?? (raw.addons == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'addons.saveSegment': writeBlock(snap, 'addons.saveSegment') ?? (raw.addons == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'addons.removeSegment': writeBlock(snap, 'addons.removeSegment') ?? (raw.addons == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'addons.savePlace': writeBlock(snap, 'addons.savePlace') ?? (raw.addons == null ? 'CAPABILITY_UNAVAILABLE' : null),
    'addons.removePlace': writeBlock(snap, 'addons.removePlace') ?? (raw.addons == null ? 'CAPABILITY_UNAVAILABLE' : null),
  };
}

export const describeAddon = byId;
