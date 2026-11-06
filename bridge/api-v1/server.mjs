// Bridge API v1 for the native Android client. Separate surface from the legacy web bridge: own port, own
// tokens, no Origin-based browser flow and no route to /api/* legacy commands or INI writes.
import http from 'node:http';
import https from 'node:https';
import { randomBytes, createHash } from 'node:crypto';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { NativePairing } from './auth.mjs';
import { AdapterLink } from './adapter-link.mjs';
import { OperationStore } from './operations.mjs';
import { validateCommand, CommandError, ROLE_SENSOR_TYPES } from './validate.mjs';
import { toState, toCapabilities, capabilityReasons, toRoutesPage, toRouteDetail, toPreview, toSettings, toDevices, toWorkoutPlans, toWorkoutList, toWorkoutDetail, toWorkoutProgress } from './normalize.mjs';
import { ActivityLibrary } from './activities.mjs';
import { toMap, toRiders, worldReasons } from './world.mjs';

export const API_MINOR = 10; // 7: P11 phone-chosen BLE devices, 8: pinned HTTPS, 9: P12 device names and devices.disconnect, 10: P13 workouts.select.
export const BRIDGE_VERSION = '0.5.0-dev';
const MAX_BODY = 64 * 1024;
const SERVER_CODES = new Set(['INVALID_REQUEST', 'PAYLOAD_TOO_LARGE', 'AUTH_REQUIRED', 'FORBIDDEN_SCOPE', 'PAIRING_PIN_INVALID', 'PAIRING_PIN_EXPIRED', 'RATE_LIMITED', 'NOT_FOUND', 'ADAPTER_ABSENT', 'ADAPTER_STALE', 'UNSUPPORTED_VERSION', 'CAPABILITY_UNAVAILABLE', 'REVISION_CONFLICT', 'INSTANCE_CHANGED', 'MOD_TRANSITION_IN_PROGRESS', 'GAME_NOT_IN_CONTROL', 'DEVICE_CHANGED', 'WORKOUT_PC_CONTROL_REQUIRED', 'SAVE_TOO_SHORT', 'OPERATION_UNKNOWN', 'FIT_INVALID', 'ASSET_UNAVAILABLE', 'MOD_INACTIVE', 'MOD_NOT_LOADED', 'PANEL_CLOSED', 'RIDER_MOVING', 'AUTOPILOT_ON', 'VALUE_OUT_OF_RANGE', 'VALUE_NOT_ALLOWED', 'BRIDGE_BUSY', 'INTERNAL']);
const STATUS = {
  ADAPTER_ABSENT: 503, ADAPTER_STALE: 503, GAME_NOT_IN_CONTROL: 503, MOD_INACTIVE: 503, MOD_NOT_LOADED: 503,
  UNSUPPORTED_VERSION: 501, CAPABILITY_UNAVAILABLE: 501, PHASE_NOT_AVAILABLE: 501,
  VALUE_OUT_OF_RANGE: 422, VALUE_NOT_ALLOWED: 422, INVALID_REQUEST: 422, NOT_FOUND: 404, FIT_INVALID: 422,
};
const RETRYABLE = new Set(['ADAPTER_STALE', 'GAME_NOT_IN_CONTROL', 'BRIDGE_BUSY', 'RATE_LIMITED', 'MOD_TRANSITION_IN_PROGRESS', 'PANEL_CLOSED', 'RIDER_MOVING']);
const HELP = { WORKOUT_PC_CONTROL_REQUIRED: 'routes.apply', ADAPTER_STALE: 'connection.stale', UNSUPPORTED_VERSION: 'connection.compatibility', AUTH_REQUIRED: 'connection.pairing', PAIRING_PIN_INVALID: 'connection.pairing', PAIRING_PIN_EXPIRED: 'connection.pairing', MOD_TRANSITION_IN_PROGRESS: 'mod.activate', AUTOPILOT_ON: 'ride.obstacles', RIDER_MOVING: 'appearance.bike', PANEL_CLOSED: 'routes.pcPreview', DEVICE_CHANGED: 'devices.lock', SAVE_TOO_SHORT: 'activity.save', FIT_INVALID: 'activity.download' };

const safeCode = code => (SERVER_CODES.has(code) ? code : code === 'PHASE_NOT_AVAILABLE' ? 'CAPABILITY_UNAVAILABLE' : 'INTERNAL');
const errorEnvelope = (code, { operationId = null, details = {} } = {}) => {
  const c = safeCode(code);
  return { error: { code: c, helpId: HELP[c] ?? null, retryable: RETRYABLE.has(c), operationId, details } };
};

/** Only IP literals and localhost: a browser page reached through DNS rebinding carries its own host name. */
const allowedHost = host => /^(localhost|127\.0\.0\.1|\[::1\]|(\d{1,3}\.){3}\d{1,3})(:\d{1,5})?$/.test(host ?? '');

async function loadBridgeId(stateDirectory) {
  const file = join(stateDirectory, 'bridge.json');
  try {
    const saved = JSON.parse(await readFile(file, 'utf8'));
    if (/^bridge-[a-f0-9]{12}$/.test(saved.bridgeId)) return saved.bridgeId;
  } catch { /* first start */ }
  const bridgeId = `bridge-${randomBytes(6).toString('hex')}`;
  await mkdir(stateDirectory, { recursive: true });
  await writeFile(file, JSON.stringify({ bridgeId }), { mode: 0o600 });
  return bridgeId;
}

export async function createBridgeV1({ adapterDirectory, stateDirectory, world, tls,
  now = () => Date.now(), monotonic = () => performance.now(), pin = null, onPairingExpired = null, onPaired = null }) {
  const bridgeId = await loadBridgeId(stateDirectory);
  const pairing = new NativePairing({ stateDirectory, now, pin });
  await pairing.load();
  const link = new AdapterLink({ directory: adapterDirectory, now, monotonic });
  const operations = new OperationStore({ now, monotonic });
  const library = new ActivityLibrary({ adapterDirectory });
  let lastFreshState = null;
  let busy = false;

  /** One consistent read of every document derived from the adapter files. */
  async function documents() {
    const snap = await link.snapshot();
    const catalog = await link.catalog();
    const state = toState(snap, lastFreshState);
    if (snap?.fresh) lastFreshState = state;
    const saved = snap?.fresh ? snap.raw.activities?.lastSaved : null;
    const lastSaved = saved && Number.isInteger(saved.fromSessionGeneration)
      ? {
          fromSessionGeneration: `session-${saved.fromSessionGeneration}`,
          fileName: typeof saved.fileName === 'string' ? saved.fileName : null,
          entry: typeof saved.fileName === 'string' ? await library.findByName(saved.fileName) : null,
        }
      : null;
    return { snap, catalog, state, preview: toPreview(snap, catalog), settings: toSettings(snap), devices: toDevices(snap), lastSaved };
  }

  const observer = setInterval(async () => {
    if (!operations.hasPending()) return;
    try { operations.observe(await documents()); } catch { /* next tick */ }
  }, 500);
  observer.unref();

  function send(res, status, body, extraHeaders = {}) {
    const payload = body === null ? '' : JSON.stringify(body);
    res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', ...extraHeaders });
    res.end(payload);
  }
  const fail = (res, code, status = STATUS[code] ?? 409, opts = {}, headers = {}) => send(res, status, errorEnvelope(code, opts), headers);

  async function readBody(req) {
    const chunks = [];
    let size = 0;
    for await (const chunk of req) {
      size += chunk.length;
      if (size > MAX_BODY) throw new CommandError('PAYLOAD_TOO_LARGE', 413);
      chunks.push(chunk);
    }
    try {
      return JSON.parse(Buffer.concat(chunks).toString('utf8') || 'null');
    } catch {
      throw new CommandError('INVALID_REQUEST', 400);
    }
  }

  /** Semantic checks against the current catalog/settings. Returns [code, details] or null. */
  function precheck(command, docs) {
    const { snap, catalog, settings } = docs;
    if (!snap) return ['ADAPTER_ABSENT'];
    if (command.adapterInstanceId !== snap.raw.adapterInstanceId) return ['INSTANCE_CHANGED'];
    const reason = capabilityReasons(snap, catalog)[command.type];
    if (reason) return [reason];
    const a = command.args;
    switch (command.type) {
      case 'routes.apply':
      case 'routes.preview': {
        if (!catalog || command.expectedRevision !== catalog.catalogRevision) return ['REVISION_CONFLICT', { resource: 'catalog' }];
        if (a.routeId === null) return null;
        const route = toRouteDetail(catalog, a.routeId)?.route;
        if (!route) return ['NOT_FOUND', { field: 'routeId' }];
        if (command.type === 'routes.apply' && ((a.loop && !route.loopAllowed) || (a.reverse && !route.reverseAllowed))) return ['VALUE_NOT_ALLOWED', { field: a.reverse ? 'reverse' : 'loop' }];
        return null;
      }
      case 'devices.configure': {
        const devices = docs.devices;
        if (command.expectedRevision !== devices.devicesRevision) return ['REVISION_CONFLICT', { resource: 'devices' }];
        const role = devices.roles.find(r => r.role === a.role);
        if (!role || !role.visible) return ['VALUE_NOT_ALLOWED', { field: 'role' }];
        if (!role.actions[a.action]) return ['VALUE_NOT_ALLOWED', { field: 'action' }];
        // Lock/Skip act on the device the user saw. If the PC switched source meanwhile, ask again.
        if (a.action !== 'auto' && (role.current?.deviceId !== a.expectedDeviceId || role.current?.source !== a.expectedSource)) return ['DEVICE_CHANGED', { role: a.role }];
        return null;
      }
      case 'devices.assign': {
        const devices = docs.devices;
        if (command.expectedRevision !== devices.devicesRevision) return ['REVISION_CONFLICT', { resource: 'devices' }];
        const role = devices.roles.find(r => r.role === a.role);
        if (!role || !role.visible) return ['VALUE_NOT_ALLOWED', { field: 'role' }];
        if (!ROLE_SENSOR_TYPES[a.role].includes(a.sensorType)) return ['VALUE_NOT_ALLOWED', { field: 'sensorType' }];
        return null;
      }
      case 'devices.disconnect': {
        const devices = docs.devices;
        if (command.expectedRevision !== devices.devicesRevision) return ['REVISION_CONFLICT', { resource: 'devices' }];
        // Only a BLE sensor the PC uses now, with the type the phone saw.
        const inUse = devices.roles.some(r => r.current?.source === 'BLE' && r.current.deviceId === a.deviceId && r.current.sensorType === a.sensorType);
        return inUse ? null : ['DEVICE_CHANGED', { field: 'deviceId' }];
      }
      case 'activities.finish':
        return command.expectedRevision === docs.state.sessionGeneration ? null : ['REVISION_CONFLICT', { resource: 'session' }];
      case 'workouts.select': {
        const library = docs.workouts;
        if (!library || command.expectedRevision !== library.catalogRevision) return ['REVISION_CONFLICT', { resource: 'workouts' }];
        return toWorkoutDetail(library, a.workoutId) ? null : ['NOT_FOUND', { field: 'workoutId' }];
      }
      case 'profile.update':
      case 'rideSettings.update':
      case 'appearance.select': {
        if (!settings) return ['CAPABILITY_UNAVAILABLE'];
        if (command.expectedRevision !== settings.settingsRevision) return ['REVISION_CONFLICT', { resource: 'settings' }];
        const group = command.type === 'profile.update' ? settings.profile : command.type === 'rideSettings.update' ? settings.ride : settings.appearance;
        for (const key of Object.keys(a)) {
          const field = command.type === 'appearance.select' ? group[key === 'bikeId' ? 'bike' : 'character'] : group[key];
          if (!field.editable) return [field.reasonCode, { field: key }];
          if (command.type === 'appearance.select' && !field.options.some(o => o.id === a[key])) return ['VALUE_NOT_ALLOWED', { field: key }];
        }
        return null;
      }
      default:
        return null;
    }
  }

  async function handleCommand(req, res) {
    let command;
    try {
      command = validateCommand(await readBody(req));
    } catch (e) {
      if (e instanceof CommandError) return fail(res, e.code, e.status, { details: e.details ?? {} });
      throw e;
    }
    const existing = operations.get(command.requestId);
    if (existing) return send(res, 200, existing); // Same requestId never runs twice.
    if (busy) return fail(res, 'BRIDGE_BUSY', 409);
    busy = true;
    try {
      const docs = await documents();
      // The workout library (up to 8 MB) is read only for the command that needs it.
      if (command.type === 'workouts.select') docs.workouts = await link.workouts();
      operations.create(command);
      const blocked = precheck(command, docs);
      if (blocked) {
        const op = operations.reject(command.requestId, safeCode(blocked[0]), blocked[1] ?? {});
        return fail(res, op.error.code, STATUS[blocked[0]] ?? 409, { operationId: op.operationId, details: blocked[1] ?? {} });
      }
      operations.update(command.requestId, { status: 'dispatched' });
      const ack = await link.dispatch(command, docs.snap.raw.adapterInstanceId);
      if (!ack) return send(res, 202, operations.unknown(command.requestId));
      if (ack.status === 'rejected') return send(res, 202, operations.reject(command.requestId, safeCode(ack.reasonCode)));
      if (ack.status === 'noop') return send(res, 202, operations.appliedNow(command.requestId, await documents()));
      if (ack.status !== 'dispatched') return send(res, 202, operations.unknown(command.requestId));
      const workoutName = command.type === 'workouts.select' ? toWorkoutDetail(docs.workouts, command.args.workoutId)?.workout.name ?? null : null;
      const workoutGeneration = docs.snap.raw.workoutProgress?.generation ?? null; // the selection the PC had before
      operations.pending(command.requestId, { sessionGeneration: docs.state.sessionGeneration, workoutName, workoutGeneration });
      operations.observe(await documents());
      return send(res, 202, operations.get(command.requestId));
    } finally {
      busy = false;
    }
  }

  const handler = async (req, res) => {
    try {
      if (!allowedHost(req.headers.host)) return fail(res, 'FORBIDDEN_SCOPE', 403);
      // Native-only surface: browsers always attach Origin to cross-origin requests.
      if (req.headers.origin !== undefined) return fail(res, 'FORBIDDEN_SCOPE', 403);
      const url = new URL(req.url, 'http://bridge.invalid');
      const path = url.pathname;
      const route = `${req.method} ${path}`;

      if (route === 'GET /api/v1/hello') {
        return send(res, 200, { service: 'gtav-riding-companion-bridge', apiMajor: 1, apiMinor: API_MINOR, bridgeId, bridgeVersion: BRIDGE_VERSION, authRequired: true, transport: tls ? 'https' : 'http-dev', nativePairing: true });
      }
      if (route === 'POST /api/v1/pair') {
        let body;
        try { body = await readBody(req); } catch (e) { return fail(res, e.code ?? 'INVALID_REQUEST', e.status ?? 400); }
        // Either {pin, clientName} (typed code) or {pairingKey, clientName} (scanned QR), never both.
        if (!body || typeof body !== 'object' || Object.keys(body).some(k => !['pin', 'pairingKey', 'clientName'].includes(k))
          || ('pin' in body) === ('pairingKey' in body)) return fail(res, 'INVALID_REQUEST', 422);
        const result = await pairing.pair(req.socket.remoteAddress ?? 'unknown', body.pin, body.clientName, 'pairingKey' in body ? body.pairingKey : null);
        if (result.error) return fail(res, result.error, result.status, {}, result.retryAfterS ? { 'Retry-After': String(result.retryAfterS) } : {});
        onPaired?.({ clientName: String(body.clientName ?? 'Android').slice(0, 40), expiresAtUtc: result.expiresAtUtc, viaQr: 'pairingKey' in body });
        return send(res, 200, { token: result.token, expiresAtUtc: result.expiresAtUtc, bridgeId });
      }

      const tokenHash = pairing.verify(req.headers.authorization);
      if (!tokenHash) {
        // Same answer for unknown and expired tokens; the console tells the user a known phone needs a new code.
        const expired = onPairingExpired && pairing.expired(req.headers.authorization);
        if (expired) onPairingExpired(expired);
        return fail(res, 'AUTH_REQUIRED', 401);
      }

      if (route === 'POST /api/v1/unpair') {
        await pairing.revoke(tokenHash);
        return send(res, 204, null);
      }
      if (route === 'POST /api/v1/commands') return await handleCommand(req, res);
      if (req.method !== 'GET') return fail(res, 'NOT_FOUND', 404);
      if (/^\/api\/v1\/assets\/map-[a-f0-9]{64}$/.test(path)) {
        const map = world?.map;
        if (!map || path !== `/api/v1/assets/${map.id}`) return fail(res, 'ASSET_UNAVAILABLE', 404);
        res.writeHead(200, {
          'Content-Type': 'image/png', 'Content-Length': map.bytes.length, 'Cache-Control': 'private, no-store',
          'X-Content-Type-Options': 'nosniff',
        });
        return res.end(map.bytes);
      }

      const docs = await documents();
      if (path === '/api/v1/state') return send(res, 200, docs.state);
      if (path === '/api/v1/capabilities') {
        const folder = (await library.root()) ? null : 'CAPABILITY_UNAVAILABLE';
        return send(res, 200, toCapabilities(docs.snap, docs.catalog, world, { 'activities.list': folder, 'activities.download': folder }));
      }
      if (path === '/api/v1/map' || path === '/api/v1/riders') {
        const map = path === '/api/v1/map';
        const blocked = worldReasons(docs.snap, world)[map ? 'map.position' : 'riders.read'];
        if (blocked) return fail(res, blocked, blocked === 'ASSET_UNAVAILABLE' ? 404 : undefined);
        return send(res, 200, map ? toMap(docs.snap, world.map) : toRiders(docs.snap));
      }
      if (path === '/api/v1/device-roles') return send(res, 200, docs.devices);
      if (path === '/api/v1/workout-progress') return send(res, 200, toWorkoutProgress(docs.snap));
      if (path === '/api/v1/workout-plans') return send(res, 200, toWorkoutPlans(docs.snap, await link.workouts()));
      if (path === '/api/v1/workouts') {
        const list = toWorkoutList(await link.workouts(), url.searchParams.get('planId') ?? '');
        return list ? send(res, 200, list) : fail(res, 'NOT_FOUND', 404);
      }
      const workoutMatch = /^\/api\/v1\/workouts\/(w-[a-f0-9]{16})$/.exec(path);
      if (workoutMatch) {
        const detail = toWorkoutDetail(await link.workouts(), workoutMatch[1]);
        return detail ? send(res, 200, detail) : fail(res, 'NOT_FOUND', 404);
      }
      if (path === '/api/v1/activities') {
        const offset = Math.max(0, Math.min(5000, Number.parseInt(url.searchParams.get('offset') ?? '0', 10) || 0));
        const limit = Math.max(1, Math.min(100, Number.parseInt(url.searchParams.get('limit') ?? '50', 10) || 50));
        return send(res, 200, await library.page(offset, limit));
      }
      const activityMatch = /^\/api\/v1\/activities\/(a-[a-f0-9]{16})(\/fit)?$/.exec(path);
      if (activityMatch) {
        const entry = await library.find(activityMatch[1]);
        if (!entry) return fail(res, 'NOT_FOUND', 404);
        if (!activityMatch[2]) {
          try { return send(res, 200, await library.detail(entry)); } catch { return fail(res, 'FIT_INVALID', 422); }
        }
        let bytes;
        try { bytes = await library.bytes(entry); } catch { return fail(res, 'FIT_INVALID', 422); }
        res.writeHead(200, {
          'Content-Type': 'application/octet-stream',
          'Content-Length': bytes.length,
          'Content-Disposition': `attachment; filename="${entry.fileName.replace(/[^A-Za-z0-9._-]/g, '_')}"`,
          'X-Content-SHA256': createHash('sha256').update(bytes).digest('hex'),
          'Cache-Control': 'no-store',
          'X-Content-Type-Options': 'nosniff',
        });
        return res.end(bytes);
      }
      if (path === '/api/v1/route-preview') return send(res, 200, docs.preview);
      if (path === '/api/v1/settings') return docs.settings ? send(res, 200, docs.settings) : fail(res, docs.snap ? 'CAPABILITY_UNAVAILABLE' : 'ADAPTER_ABSENT');
      if (path === '/api/v1/routes') {
        const offset = Math.max(0, Math.min(5000, Number.parseInt(url.searchParams.get('offset') ?? '0', 10) || 0));
        const limit = Math.max(1, Math.min(100, Number.parseInt(url.searchParams.get('limit') ?? '50', 10) || 50));
        return send(res, 200, toRoutesPage(docs.catalog, { offset, limit, q: url.searchParams.get('q') ?? '' }));
      }
      const routeMatch = /^\/api\/v1\/routes\/(0|[1-9][0-9]{0,4})$/.exec(path);
      if (routeMatch) {
        const detail = toRouteDetail(docs.catalog, routeMatch[1]);
        return detail ? send(res, 200, detail) : fail(res, 'NOT_FOUND', 404);
      }
      const opMatch = /^\/api\/v1\/operations\/([0-9a-f-]{36})$/.exec(path);
      if (opMatch) {
        operations.observe(docs);
        const op = operations.get(opMatch[1]);
        return op ? send(res, 200, op) : fail(res, 'NOT_FOUND', 404);
      }
      return fail(res, 'NOT_FOUND', 404);
    } catch {
      if (!res.headersSent) fail(res, 'INTERNAL', 500);
      else res.end();
    }
  };
  const server = tls ? https.createServer({ ...tls, minVersion: 'TLSv1.2' }, handler) : http.createServer(handler);
  server.requestTimeout = 15_000;
  server.headersTimeout = 10_000;

  return {
    server,
    bridgeId,
    pairing,
    operations,
    close: () => new Promise(resolve => { clearInterval(observer); server.close(() => resolve()); }),
  };
}
