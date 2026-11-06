// Bridge API v1 (increment 2a + P3/P4 endpoints) against a simulated v1 adapter. No GTA required.
// Every JSON response is validated against contracts/v1 with ajv.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import http from 'node:http';
import { validateCommand } from '../bridge/api-v1/validate.mjs';
import { fixtures, readJson, schemas, setup, pair, command, waitFor } from './support/bridge-v1.mjs';

test('runtime command validator agrees with the schema verdict on every command fixture', async () => {
  const index = await readJson(join(fixtures, 'index.json'));
  for (const entry of index.cases.filter(c => c.file && c.schema === 'command')) {
    const doc = await readJson(join(fixtures, entry.file));
    let ok = true;
    try { validateCommand(doc); } catch { ok = false; }
    assert.equal(ok, entry.valid, entry.file);
  }
});

test('hello is minimal; pairing is native, single-use and rate limited; tokens are scoped to v1', async t => {
  const check = await schemas();
  const { request } = await setup(t);
  const hello = await (await request('/api/v1/hello')).json();
  check('hello', hello);
  assert.equal(hello.transport, 'http-dev');
  assert.equal((await request('/api/v1/state')).status, 401);
  // A browser page cannot use this surface, even with a valid PIN.
  const browser = await request('/api/v1/pair', { method: 'POST', headers: { Origin: 'http://evil.example', 'Content-Type': 'application/json' }, body: '{"pin":"246810"}' });
  assert.equal(browser.status, 403);
  const port = new URL((await request('/api/v1/hello')).url).port;
  const rebindingStatus = await new Promise((resolve, reject) => {
    http.get({ host: '127.0.0.1', port, path: '/api/v1/hello', headers: { Host: 'evil.example' } }, res => { res.resume(); resolve(res.statusCode); }).on('error', reject);
  });
  assert.equal(rebindingStatus, 403, 'DNS-rebinding host names are refused');
  const wrong = await request('/api/v1/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"pin":"000000"}' });
  assert.equal(wrong.status, 401);
  check('error', await wrong.json());
  const headers = await pair(request);
  assert.equal((await request('/api/v1/state', { headers })).status, 200);
  for (let i = 0; i < 5; i++) await request('/api/v1/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"pin":"111111"}' });
  const limited = await request('/api/v1/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"pin":"246810"}' });
  assert.equal(limited.status, 429);
  assert.ok(limited.headers.get('retry-after'));
  assert.equal((await request('/api/commands', { headers })).status, 404, 'legacy routes do not exist on v1');
  assert.equal((await request('/api/v1/unpair', { method: 'POST', headers })).status, 204);
  assert.equal((await request('/api/v1/state', { headers })).status, 401);
});

test('state, capabilities, routes, preview and settings validate against the contract', async t => {
  const check = await schemas();
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  const state = await (await request('/api/v1/state', { headers })).json();
  check('state', state);
  assert.equal(state.layers.adapter, 'fresh');
  assert.equal(state.metrics.heartRateBpm.quality, 'unavailable');
  assert.equal(state.metrics.heartRateBpm.reasonCode, 'SENSOR_MISSING');
  assert.equal(state.metrics.speedMps.value, 0, '0 is a real value');
  // Observed in game (2026-11-05): active mod, rider not moved yet -> no Activity, hence no workout.
  const activity = adapter.raw.activity;
  adapter.raw.activity = null;
  await adapter.write();
  const idle = await (await request('/api/v1/state', { headers })).json();
  check('state', idle);
  assert.deepEqual(idle.workout, { present: false, finished: null });
  assert.equal(idle.metrics.movingTimeMs.quality, 'unavailable');
  adapter.raw.activity = activity;
  await adapter.write();
  const caps = await (await request('/api/v1/capabilities', { headers })).json();
  check('capabilities', caps);
  assert.ok(!caps.capabilities.some(c => /erg|target|bias/i.test(c.id)));
  const page = await (await request('/api/v1/routes?limit=1', { headers })).json();
  check('routes', page);
  assert.equal(page.total, 2);
  assert.equal(page.items.length, 1);
  const search = await (await request('/api/v1/routes?q=climb', { headers })).json();
  assert.deepEqual(search.items.map(r => r.id), ['36']);
  const detail = await (await request('/api/v1/routes/35', { headers })).json();
  check('routes', detail);
  assert.equal((await request('/api/v1/routes/99', { headers })).status, 404);
  check('route-preview', await (await request('/api/v1/route-preview', { headers })).json());
  const settings = await (await request('/api/v1/settings', { headers })).json();
  check('settings', settings);
  assert.equal(settings.ride.avoidObstacles.editable, true);
});

test('stale adapter never reports live values and blocks commands', async t => {
  const check = await schemas();
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  await (await request('/api/v1/state', { headers })).json(); // remember last fresh values
  adapter.tick = async () => {}; // game frozen
  await adapter.write(new Date(Date.now() - 10_000));
  const state = await (await request('/api/v1/state', { headers })).json();
  check('state', state);
  assert.equal(state.layers.adapter, 'stale');
  assert.ok(Object.values(state.metrics).every(m => m.quality !== 'valid'));
  assert.equal(state.metrics.powerW.quality, 'stale');
  const { res, body } = await command(request, headers, 'mod.setActive', { enabled: false });
  assert.equal(res.status, 503);
  check('error', body);
  assert.equal(body.error.code, 'ADAPTER_STALE');
});

test('mod.setActive reaches applied only after the observed mod layer changes; requestId never runs twice', async t => {
  const check = await schemas();
  const { request } = await setup(t);
  const headers = await pair(request);
  const { res, body, requestId } = await command(request, headers, 'mod.setActive', { enabled: false });
  assert.equal(res.status, 202);
  check('operation', body);
  const op = await waitFor(request, headers, requestId);
  check('operation', op);
  assert.equal(op.status, 'applied');
  assert.equal(op.observed.mod, 'inactive');
  const again = await request('/api/v1/commands', { method: 'POST', headers, body: JSON.stringify({ requestId, adapterInstanceId: 'adapter-test01', type: 'mod.setActive', expectedRevision: null, args: { enabled: false } }) });
  assert.equal(again.status, 200);
  assert.equal((await again.json()).operationId, op.operationId);
});

test('adapter rejection and missing ack are reported, never retried', async t => {
  const check = await schemas();
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  adapter.behaviour['mod.setActive'] = 'reject';
  const rejected = await command(request, headers, 'mod.setActive', { enabled: false });
  check('operation', rejected.body);
  assert.equal(rejected.body.status, 'rejected');
  assert.equal(rejected.body.error.code, 'MOD_TRANSITION_IN_PROGRESS');
  adapter.behaviour['mod.setActive'] = 'ignore';
  const lost = await command(request, headers, 'mod.setActive', { enabled: false });
  check('operation', lost.body);
  assert.equal(lost.body.status, 'unknown');
});

test('routes: apply needs the browsed catalog revision, allowed flags and a new session; allowed during a workout', async t => {
  const check = await schemas();
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  const stale = await command(request, headers, 'routes.apply', { routeId: '35', loop: true, reverse: false }, 'catalog-old');
  assert.equal(stale.res.status, 409);
  assert.equal(stale.body.error.code, 'REVISION_CONFLICT');
  const notAllowed = await command(request, headers, 'routes.apply', { routeId: '36', loop: true, reverse: false }, 'catalog-a1');
  assert.equal(notAllowed.body.error.code, 'VALUE_NOT_ALLOWED');
  const ok = await command(request, headers, 'routes.apply', { routeId: '35', loop: true, reverse: true }, 'catalog-a1');
  const op = await waitFor(request, headers, ok.requestId);
  check('operation', op);
  assert.equal(op.status, 'applied');
  assert.deepEqual(op.observed.activeRoute, { id: '35', loop: true, reverse: true });
  const state = await (await request('/api/v1/state', { headers })).json();
  assert.deepEqual(state.activeRoute, { id: '35', loop: true, reverse: true });
  // A running workout no longer blocks it (the mod's menu allows it; the workout restarts on the new route).
  adapter.raw.activity.workoutPresent = true;
  adapter.raw.activity.workoutFinished = false;
  await adapter.write();
  const caps = await (await request('/api/v1/capabilities', { headers })).json();
  assert.equal(caps.capabilities.find(c => c.id === 'routes.apply').available, true);
  assert.equal(caps.capabilities.find(c => c.id === 'activities.finish').reasonCode !== null, true, 'saving stays guarded');
  const during = await command(request, headers, 'routes.apply', { routeId: '36', loop: false, reverse: false }, 'catalog-a1');
  assert.equal((await waitFor(request, headers, during.requestId)).status, 'applied');
});

test('routes: PC preview only while the selector is open, and it never changes the active route', async t => {
  const { request } = await setup(t);
  const headers = await pair(request);
  const closed = await command(request, headers, 'routes.preview', { routeId: '35' }, 'catalog-a1');
  assert.equal(closed.body.error.code, 'PANEL_CLOSED');
  const open = await command(request, headers, 'ui.openPanel', { panel: 'routes' });
  assert.equal((await waitFor(request, headers, open.requestId)).status, 'applied');
  const preview = await command(request, headers, 'routes.preview', { routeId: '35' }, 'catalog-a1');
  assert.equal((await waitFor(request, headers, preview.requestId)).status, 'applied');
  const doc = await (await request('/api/v1/route-preview', { headers })).json();
  assert.equal(doc.routeId, '35');
  assert.equal((await (await request('/api/v1/state', { headers })).json()).activeRoute, null);
});

test('settings: readback confirms changes; FTP is locked during a PC workout; appearance needs a stopped rider', async t => {
  const check = await schemas();
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  let settings = await (await request('/api/v1/settings', { headers })).json();
  const conflict = await command(request, headers, 'profile.update', { ftpW: 240 }, 'settings-old');
  assert.equal(conflict.body.error.code, 'REVISION_CONFLICT');
  const update = await command(request, headers, 'profile.update', { ftpW: 240, weightKg: 70 }, settings.settingsRevision);
  const op = await waitFor(request, headers, update.requestId);
  check('operation', op);
  assert.equal(op.status, 'applied');
  settings = await (await request('/api/v1/settings', { headers })).json();
  assert.equal(settings.profile.ftpW.value, 240);
  const ride = await command(request, headers, 'rideSettings.update', { difficulty: 50, autopilot: true }, settings.settingsRevision);
  assert.equal((await waitFor(request, headers, ride.requestId)).status, 'applied');
  settings = await (await request('/api/v1/settings', { headers })).json();
  assert.equal(settings.ride.avoidObstacles.reasonCode, 'AUTOPILOT_ON');
  adapter.raw.activity.workoutPresent = true;
  adapter.raw.activity.workoutFinished = false;
  adapter.raw.metrics.speedMps = 6;
  await adapter.write();
  settings = await (await request('/api/v1/settings', { headers })).json();
  check('settings', settings);
  assert.equal(settings.profile.ftpW.reasonCode, 'WORKOUT_PC_CONTROL_REQUIRED');
  assert.equal(settings.profile.weightKg.editable, true);
  assert.equal(settings.appearance.bike.reasonCode, 'RIDER_MOVING');
  const ftp = await command(request, headers, 'profile.update', { ftpW: 250 }, settings.settingsRevision);
  assert.equal(ftp.body.error.code, 'WORKOUT_PC_CONTROL_REQUIRED');
  const bike = await command(request, headers, 'appearance.select', { bikeId: 'Gravel' }, settings.settingsRevision);
  assert.equal(bike.body.error.code, 'RIDER_MOVING');
});

test('commands outside the allowlist are refused before reaching the adapter', async t => {
  const { request, dir } = await setup(t);
  const headers = await pair(request);
  for (const type of ['erg', 'workouts.start', 'config.write', 'ui.key']) {
    const res = await request('/api/v1/commands', { method: 'POST', headers, body: JSON.stringify({ requestId: randomUUID(), adapterInstanceId: 'adapter-test01', type, expectedRevision: null, args: {} }) });
    assert.equal(res.status, 422, type);
  }
  const panel = await command(request, headers, 'ui.openPanel', { panel: 'workouts' });
  assert.equal(panel.res.status, 422);
  const files = await readdir(join(dir, 'v1', 'commands')).catch(() => []);
  assert.equal(files.filter(f => f.endsWith('.json')).length, 0);
});
