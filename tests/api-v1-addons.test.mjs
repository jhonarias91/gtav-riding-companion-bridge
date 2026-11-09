// P18: add-ons (Alleycat courier). GET /api/v1/addons is the registry clients render from; addons.setEnabled starts and
// stops a run, addons.configure changes typed parameters. Phones and the PC window alike.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { setup, pair, command, waitFor, schemas, root } from './support/bridge-v1.mjs';
import { ADDONS, addonEvidence } from '../bridge/api-v1/addons.mjs';

const desktopToken = 'd'.repeat(43);
const desktop = { Authorization: `Bearer ${desktopToken}`, 'Content-Type': 'application/json' };
const addons = async (request, headers) => (await request('/api/v1/addons', { headers })).json();
const alleycat = doc => doc.addons.find(a => a.id === 'alleycat');
const sprint = doc => doc.addons.find(a => a.id === 'survivalSprint');
const ghost = doc => doc.addons.find(a => a.id === 'ghost');
const discover = doc => doc.addons.find(a => a.id === 'discover');

test('the registry lists Alleycat with typed parameters and the values the adapter holds', async t => {
  const { request } = await setup(t);
  const phone = await pair(request);
  const check = await schemas();
  const doc = await addons(request, phone);
  check('addons', doc);
  assert.equal(doc.reasonCode, null);
  assert.match(doc.addonsRevision, /^addons-[a-f0-9]{12}$/);
  const a = alleycat(doc);
  assert.equal(a.state, 'idle');
  assert.equal(a.enabled, false);
  assert.equal(a.startReasonCode, null);
  assert.deepEqual(a.parameters.map(p => p.id), ['packages', 'timeLimitMin', 'fragility', 'heavyChancePct', 'stormWhenHeavy', 'headwindMps', 'showHud']);
  assert.ok(a.parameters.every(p => p.editable && p.value === p.default));
  const caps = await (await request('/api/v1/capabilities', { headers: phone })).json();
  check('capabilities', caps);
  for (const id of ['addons.read', 'addons.setEnabled', 'addons.configure']) assert.equal(caps.capabilities.find(c => c.id === id).available, true, id);
});

test('a phone starts the mission, sees its progress and stops it; teleport waits meanwhile', async t => {
  const { request } = await setup(t, { desktopToken });
  const phone = await pair(request);
  const check = await schemas();
  const before = await addons(request, phone);
  const start = await command(request, phone, 'addons.setEnabled', { addonId: 'alleycat', enabled: true }, before.addonsRevision);
  assert.equal(start.res.status, 202);
  const op = await waitFor(request, phone, start.requestId);
  check('operation', op);
  assert.equal(op.status, 'applied');
  assert.deepEqual(op.observed, { addonId: 'alleycat', state: 'running' });

  const running = await addons(request, phone);
  check('addons', running);
  const a = alleycat(running);
  assert.equal(a.state, 'running');
  assert.equal(a.enabled, true);
  assert.equal(running.addonsRevision, before.addonsRevision, 'progress does not change the revision');
  assert.deepEqual(a.run.nextPoint, { x: 120.5, y: -760.25, kind: 'pickup', distanceM: 812 });
  assert.equal(a.run.timeLeftS, null);
  assert.equal(a.run.stats.find(s => s.id === 'zone').value, 'Pillbox Hill');
  assert.equal(a.parameters.find(p => p.id === 'packages').editable, false, 'not while running');
  assert.equal(a.parameters.find(p => p.id === 'fragility').editable, true, 'live');
  assert.equal(a.result, null);

  const world = await (await request('/api/v1/desktop/world', { headers: desktop })).json();
  assert.equal(world.teleport.reasonCode, 'VALUE_NOT_ALLOWED');
  assert.deepEqual(world.addonPoints, [{ addonId: 'alleycat', x: 120.5, y: -760.25, kind: 'pickup', distanceM: 812 }]);
  assert.equal((await command(request, desktop, 'player.teleport', { x: 1, y: 2 })).body.error.code, 'VALUE_NOT_ALLOWED');

  // Enabling again is a no-op; stopping needs no revision.
  const again = await command(request, phone, 'addons.setEnabled', { addonId: 'alleycat', enabled: true }, before.addonsRevision);
  assert.equal((await waitFor(request, phone, again.requestId)).status, 'applied');
  const stop = await command(request, phone, 'addons.setEnabled', { addonId: 'alleycat', enabled: false });
  assert.equal((await waitFor(request, phone, stop.requestId)).status, 'applied');
  const after = alleycat(await addons(request, phone));
  assert.equal(after.state, 'aborted');
  assert.equal(after.run, null);
  assert.equal(after.result.outcome, 'stopped');
  assert.equal((await (await request('/api/v1/desktop/world', { headers: desktop })).json()).teleport.reasonCode, null);
});

test('parameters change with the revision; ranges, steps, unknown ids and running locks are refused', async t => {
  const { request } = await setup(t);
  const phone = await pair(request);
  const doc = await addons(request, phone);
  const set = await command(request, phone, 'addons.configure', { addonId: 'alleycat', params: { packages: 3, headwindMps: 7.5, fragility: 'high', showHud: false } }, doc.addonsRevision);
  const op = await waitFor(request, phone, set.requestId);
  assert.equal(op.status, 'applied');
  assert.deepEqual(op.observed.params, { packages: 3, headwindMps: 7.5, fragility: 'high', showHud: false });
  const changed = await addons(request, phone);
  assert.notEqual(changed.addonsRevision, doc.addonsRevision);
  assert.equal(alleycat(changed).parameters.find(p => p.id === 'packages').value, 3);

  const rev = changed.addonsRevision;
  const code = async (params, revision = rev) => (await command(request, phone, 'addons.configure', { addonId: 'alleycat', params }, revision)).body.error?.code;
  assert.equal(await code({ packages: 3 }, doc.addonsRevision), 'REVISION_CONFLICT');
  assert.equal(await code({ packages: 11 }), 'VALUE_OUT_OF_RANGE');
  assert.equal(await code({ packages: 2.5 }), 'INVALID_REQUEST');
  assert.equal(await code({ headwindMps: 7.25 }), 'VALUE_NOT_ALLOWED');
  assert.equal(await code({ fragility: 'extreme' }), 'VALUE_NOT_ALLOWED');
  assert.equal(await code({ showHud: 'yes' }), 'INVALID_REQUEST');
  assert.equal(await code({ turbo: true }), 'INVALID_REQUEST');
  assert.equal(await code({}), 'INVALID_REQUEST');
  assert.equal((await command(request, phone, 'addons.configure', { addonId: 'tron', params: { a: 1 } }, rev)).body.error.code, 'NOT_FOUND');

  const start = await command(request, phone, 'addons.setEnabled', { addonId: 'alleycat', enabled: true }, rev);
  assert.equal((await waitFor(request, phone, start.requestId)).status, 'applied');
  const locked = await command(request, phone, 'addons.configure', { addonId: 'alleycat', params: { timeLimitMin: 30 } }, rev);
  assert.equal(locked.body.error.code, 'VALUE_NOT_ALLOWED');
  assert.deepEqual(locked.body.error.details, { field: 'timeLimitMin' });
  const live = await command(request, phone, 'addons.configure', { addonId: 'alleycat', params: { headwindMps: 12 } }, rev);
  assert.equal((await waitFor(request, phone, live.requestId)).status, 'applied');
});

test('start preconditions: mod, course, bicycle and revision rules', async t => {
  const { request, adapter } = await setup(t);
  const phone = await pair(request);
  const rev = (await addons(request, phone)).addonsRevision;
  const start = async (revision = rev) => (await command(request, phone, 'addons.setEnabled', { addonId: 'alleycat', enabled: true }, revision)).body.error?.code;
  assert.equal(await start(null), 'INVALID_REQUEST');
  assert.equal((await command(request, phone, 'addons.setEnabled', { addonId: 'alleycat', enabled: false }, rev)).body.error.code, 'INVALID_REQUEST');
  assert.equal(await start('addons-000000000000'), 'REVISION_CONFLICT');

  adapter.raw.activeRoute = { id: '35', loop: false, reverse: false };
  await adapter.write();
  assert.equal(alleycat(await addons(request, phone)).startReasonCode, null, 'the course will be cancelled before starting');
  adapter.raw.commands = adapter.raw.commands.filter(c => c !== 'routes.apply');
  await adapter.write();
  assert.equal(await start(), 'CAPABILITY_UNAVAILABLE', 'cannot replace a course without its native clear path');
  adapter.raw.commands.push('routes.apply');
  adapter.raw.addonContext.onBicycle = false;
  await adapter.write();
  assert.equal(await start(), 'RIDER_NOT_ON_BIKE');
  adapter.raw.addonContext.onBicycle = true;
  adapter.raw.isDtOk = false;
  await adapter.write();
  assert.equal(alleycat(await addons(request, phone)).startReasonCode, 'MOD_INACTIVE');
  assert.equal(await start(), 'MOD_INACTIVE');
});

test('a start that finds no Downtown street points fails with ADDON_NO_POINTS', async t => {
  const { request, adapter } = await setup(t);
  const phone = await pair(request);
  adapter.addonStart = 'noPoints';
  const rev = (await addons(request, phone)).addonsRevision;
  const start = await command(request, phone, 'addons.setEnabled', { addonId: 'alleycat', enabled: true }, rev);
  const op = await waitFor(request, phone, start.requestId);
  assert.equal(op.status, 'failed');
  assert.equal(op.error.code, 'ADDON_NO_POINTS');
  const a = alleycat(await addons(request, phone));
  assert.equal(a.state, 'failed');
  assert.equal(a.result.outcome, 'noPoints');
});

test('Survival sprint replaces a course and another add-on replaces the sprint, with one mission at a time', async t => {
  const { request, adapter } = await setup(t);
  const phone = await pair(request);
  const check = await schemas();
  adapter.raw.activeRoute = { id: '35', loop: false, reverse: false };
  await adapter.write();
  const doc = await addons(request, phone);
  assert.equal(sprint(doc).startReasonCode, null, 'can replace the course');
  assert.equal(alleycat(doc).startReasonCode, null, 'can replace the course');
  assert.deepEqual(sprint(doc).parameters.find(p => p.id === 'chaser').options.map(o => o.id), ['phantomWedge', 'police', 'truck']);

  const start = await command(request, phone, 'addons.setEnabled', { addonId: 'survivalSprint', enabled: true }, doc.addonsRevision);
  assert.equal((await waitFor(request, phone, start.requestId)).status, 'applied');
  assert.equal(adapter.raw.activeRoute, null, 'the previous course has been cancelled');
  const running = await addons(request, phone);
  check('addons', running);
  const run = sprint(running).run;
  assert.equal(run.phase, 'armed');
  assert.equal(run.nextPoint, null);
  assert.equal(run.stats.find(s => s.id === 'speedKmh').value, 22.4);
  assert.equal(run.stats.find(s => s.id === 'pedaling').value, true);
  assert.equal(run.stats.find(s => s.id === 'gapM').value, null);

  // Another add-on is startable; the bridge replaces the current one rather than requiring a manual stop.
  assert.equal(alleycat(await addons(request, phone)).startReasonCode, null);
  assert.equal((await command(request, phone, 'addons.configure', { addonId: 'survivalSprint', params: { durationMin: 20 } }, running.addonsRevision)).body.error.code, 'VALUE_NOT_ALLOWED');
  const live = await command(request, phone, 'addons.configure', { addonId: 'survivalSprint', params: { targetKmh: 34, coastGraceS: 2 } }, running.addonsRevision);
  assert.equal((await waitFor(request, phone, live.requestId)).status, 'applied');

  const replace = await command(request, phone, 'addons.setEnabled', { addonId: 'alleycat', enabled: true }, (await addons(request, phone)).addonsRevision);
  assert.equal((await waitFor(request, phone, replace.requestId)).status, 'applied');
  const after = await addons(request, phone);
  check('addons', after);
  assert.equal(sprint(after).result.outcome, 'stopped');
  assert.equal(sprint(after).state, 'aborted');
  assert.equal(sprint(after).run, null);
  assert.equal(alleycat(after).state, 'running');
  assert.equal(after.addons.filter(a => a.enabled).length, 1);
  assert.deepEqual(sprint(after).result.stats.map(s => s.id), ['survivedS', 'distanceM', 'maxSpeedKmh', 'closestGapM', 'targetKmh']);
  assert.equal(alleycat(after).startReasonCode, null);
});

test('every add-on replaces a loaded course from both phone and Windows clients', async t => {
  const { request, adapter } = await setup(t, { desktopToken });
  const phone = await pair(request);
  for (const headers of [phone, desktop]) for (const addon of ADDONS) {
    adapter.raw.activeRoute = { id: '35', loop: false, reverse: false };
    await adapter.write();
    const doc = await addons(request, headers);
    const start = await command(request, headers, 'addons.setEnabled', { addonId: addon.id, enabled: true }, doc.addonsRevision);
    assert.equal(start.res.status, 202, addon.id);
    assert.equal((await waitFor(request, headers, start.requestId)).status, 'applied', addon.id);
    assert.equal(adapter.raw.activeRoute, null, addon.id);
    const after = await addons(request, headers);
    assert.deepEqual(after.addons.filter(a => a.enabled).map(a => a.id), [addon.id]);
  }
});

test('start evidence waits for the previous course and mission to disappear', () => {
  const args = { addonId: 'alleycat', enabled: true };
  const base = { requestId: 'new-run' };
  const raw = { activeRoute: { id: '35' }, addons: {
    alleycat: { state: 'running', requestId: 'new-run' },
    discover: { state: 'running', requestId: 'old-run' },
  } };
  const observe = () => addonEvidence('addons.setEnabled', args, base, { fresh: true, raw });
  assert.equal(observe(), null);
  raw.activeRoute = null;
  assert.equal(observe(), null);
  raw.addons.discover.state = 'aborted';
  assert.equal(observe().status, 'applied');
});

test('ghost segments are saved, renamed, moved and removed from the PC map; the mode shows the nearest start', async t => {
  const { request, adapter } = await setup(t, { desktopToken });
  const check = await schemas();
  const save = (args) => command(request, desktop, 'addons.saveSegment', { addonId: 'ghost', ...args });
  const created = await save({ segmentId: null, name: '  Vinewood climb ', start: { x: 120.123, y: 540.5 }, finish: { x: 410, y: 1250.25 } });
  assert.equal(created.res.status, 202);
  const op = await waitFor(request, desktop, created.requestId);
  check('operation', op);
  assert.equal(op.status, 'applied');
  const id = op.observed.segmentId;
  assert.match(id, /^seg-[a-f0-9]{8}$/);
  assert.equal(op.observed.name, 'Vinewood climb');

  let doc = await addons(request, desktop);
  check('addons', doc);
  const segment = ghost(doc).segments[0];
  assert.deepEqual(segment, { id, name: 'Vinewood climb', start: { x: 120.12, y: 540.5 }, finish: { x: 410, y: 1250.25 }, loop: false, straightM: 767, attempts: 0, lastTimeS: null, best: null });
  assert.equal(alleycat(doc).segments, null, 'only add-ons with segments carry the list');
  const world = await (await request('/api/v1/desktop/world', { headers: desktop })).json();
  assert.deepEqual(world.segments, [{ addonId: 'ghost', id, name: 'Vinewood climb', start: { x: 120.12, y: 540.5 }, finish: { x: 410, y: 1250.25 }, loop: false, bestTimeS: null }]);

  // A record exists; renaming keeps it, moving a point resets it.
  adapter.raw.addons.ghost.segments[0].best = { timeS: 251.4, setAtUtc: '2026-11-07T18:00:00.000Z', avgPowerW: 245, avgSpeedKmh: 21.3, distanceM: 1490 };
  adapter.raw.addons.ghost.segments[0].attempts = 3;
  const renamed = await save({ segmentId: id, name: 'Vinewood KOM', start: { x: 120.12, y: 540.5 }, finish: { x: 410, y: 1250.25 } });
  assert.equal((await waitFor(request, desktop, renamed.requestId)).observed.best.timeS, 251.4);
  const moved = await save({ segmentId: id, name: 'Vinewood KOM', start: { x: 180, y: 540.5 }, finish: { x: 410, y: 1250.25 } });
  assert.equal((await waitFor(request, desktop, moved.requestId)).observed.best, null);

  // A loop: the same point twice.
  const loop = await save({ segmentId: null, name: 'Lap', start: { x: -1000, y: -1000 }, finish: { x: -1000, y: -1000 } });
  assert.equal((await waitFor(request, desktop, loop.requestId)).status, 'applied');
  assert.equal(ghost(await addons(request, desktop)).segments[1].loop, true);

  // Bad input.
  const code = async args => (await save(args)).body.error?.code;
  assert.equal(await code({ segmentId: null, name: '', start: { x: 1, y: 1 }, finish: { x: 2, y: 2 } }), 'INVALID_REQUEST');
  assert.equal(await code({ segmentId: null, name: 'x'.repeat(41), start: { x: 1, y: 1 }, finish: { x: 2, y: 2 } }), 'INVALID_REQUEST');
  assert.equal(await code({ segmentId: null, name: 'Far', start: { x: 1, y: 1 }, finish: { x: 2, y: 20000 } }), 'VALUE_OUT_OF_RANGE');
  assert.equal(await code({ segmentId: 'seg-XYZ', name: 'Bad id', start: { x: 1, y: 1 }, finish: { x: 2, y: 2 } }), 'INVALID_REQUEST');
  assert.equal((await command(request, desktop, 'addons.saveSegment', { addonId: 'alleycat', segmentId: null, name: 'No', start: { x: 1, y: 1 }, finish: { x: 2, y: 2 } })).body.error.code, 'CAPABILITY_UNAVAILABLE');

  // The mode: nearest start as next point; teleport is only blocked during an attempt.
  const rev = (await addons(request, desktop)).addonsRevision;
  const start = await command(request, desktop, 'addons.setEnabled', { addonId: 'ghost', enabled: true }, rev);
  assert.equal((await waitFor(request, desktop, start.requestId)).status, 'applied');
  doc = await addons(request, desktop);
  check('addons', doc);
  assert.equal(ghost(doc).run.phase, 'waiting');
  assert.deepEqual(ghost(doc).run.nextPoint, { x: 180, y: 540.5, kind: 'start', distanceM: 420 });
  assert.equal((await (await request('/api/v1/desktop/world', { headers: desktop })).json()).teleport.reasonCode, null);
  adapter.raw.addons.ghost.run = { ...adapter.raw.addons.ghost.run, phase: 'onSegment', segmentId: id, segment: 'Vinewood KOM', deltaS: -2.4, bestS: 251.4, ghost: true };
  adapter.raw.addons.ghost.blocksTeleport = true;
  await adapter.write();
  doc = await addons(request, desktop);
  assert.equal(ghost(doc).run.stats.find(s => s.id === 'deltaS').value, -2.4);
  assert.equal((await (await request('/api/v1/desktop/world', { headers: desktop })).json()).teleport.reasonCode, 'VALUE_NOT_ALLOWED');
  const busy = await command(request, desktop, 'addons.removeSegment', { addonId: 'ghost', segmentId: id });
  assert.equal(busy.body.error.code, 'VALUE_NOT_ALLOWED', 'not while its attempt runs');
  adapter.raw.addons.ghost.run = { ...adapter.raw.addons.ghost.run, phase: 'waiting', segmentId: null };
  adapter.raw.addons.ghost.blocksTeleport = false;
  await adapter.write();

  const removed = await command(request, desktop, 'addons.removeSegment', { addonId: 'ghost', segmentId: id });
  assert.deepEqual((await waitFor(request, desktop, removed.requestId)).observed, { addonId: 'ghost', segmentId: id, removed: true });
  assert.equal((await command(request, desktop, 'addons.removeSegment', { addonId: 'ghost', segmentId: id })).body.error.code, 'NOT_FOUND');
  const stop = await command(request, desktop, 'addons.setEnabled', { addonId: 'ghost', enabled: false });
  assert.equal((await waitFor(request, desktop, stop.requestId)).status, 'applied');
  assert.deepEqual(ghost(await addons(request, desktop)).result.stats.map(x => [x.id, x.value]), [['attempts', 2], ['records', 1]]);
});

test('Discover: places from the PC map, XP progress, and the hidden spot never leaks', async t => {
  const { request, adapter } = await setup(t, { desktopToken });
  const phone = await pair(request);
  const check = await schemas();
  let doc = await addons(request, phone);
  check('addons', doc);
  const progress = discover(doc).progress;
  assert.equal(progress.xp, 1240);
  assert.equal(progress.level, 3);
  assert.equal(progress.levelName, 'Explorer');
  assert.equal(progress.xpToNextLevel, 1010);
  assert.equal(progress.levelProgressPct, 19);
  assert.equal(progress.stats.find(s => s.id === 'streakDays').value, 2);
  assert.equal(progress.lastFind.stats.find(s => s.id === 'newZoneBonusXp').value, 50);
  assert.equal(alleycat(doc).progress, null);
  assert.equal(alleycat(doc).places, null);

  const save = await command(request, desktop, 'addons.savePlace', { addonId: 'discover', placeId: null, name: ' Chiliad summit ', point: { x: 501.25, y: 5604.5 } });
  const op = await waitFor(request, desktop, save.requestId);
  assert.equal(op.status, 'applied');
  const id = op.observed.placeId;
  assert.match(id, /^pl-[a-f0-9]{8}$/);
  doc = await addons(request, phone);
  assert.deepEqual(discover(doc).places, [{ id, name: 'Chiliad summit', point: { x: 501.25, y: 5604.5 }, found: 0, bestTimeS: null, lastFoundAtUtc: null }]);
  const world = await (await request('/api/v1/desktop/world', { headers: desktop })).json();
  assert.deepEqual(world.places, [{ addonId: 'discover', id, name: 'Chiliad summit', point: { x: 501.25, y: 5604.5 }, found: 0 }]);

  // Running: only the hint, never where the spot is or which place it is.
  adapter.raw.activeRoute = { id: '35', loop: false, reverse: false };
  await adapter.write();
  assert.equal(discover(await addons(request, phone)).startReasonCode, null, 'the course is cancelled automatically');
  const start = await command(request, phone, 'addons.setEnabled', { addonId: 'discover', enabled: true }, doc.addonsRevision);
  assert.equal((await waitFor(request, phone, start.requestId)).status, 'applied');
  doc = await addons(request, phone);
  assert.equal(adapter.raw.activeRoute, null, 'Discover removed the previous course');
  check('addons', doc);
  const run = discover(doc).run;
  assert.equal(run.phase, 'searching');
  assert.equal(run.nextPoint, null);
  assert.equal(run.stats.find(s => s.id === 'hintZone').value, 'Vinewood Hills');
  assert.equal(run.stats.find(s => s.id === 'heat').value, 'warm');
  assert.ok(!JSON.stringify(doc).includes('targetPlaceId'));
  const w2 = await (await request('/api/v1/desktop/world', { headers: desktop })).json();
  assert.deepEqual(w2.addonPoints, [], 'the PC map does not show the hidden spot');

  // The searched place cannot be removed or moved; renaming is fine.
  assert.equal((await command(request, desktop, 'addons.removePlace', { addonId: 'discover', placeId: id })).body.error.code, 'VALUE_NOT_ALLOWED');
  assert.equal((await command(request, desktop, 'addons.savePlace', { addonId: 'discover', placeId: id, name: 'Moved', point: { x: 10, y: 10 } })).body.error.code, 'VALUE_NOT_ALLOWED');
  const rename = await command(request, desktop, 'addons.savePlace', { addonId: 'discover', placeId: id, name: 'Mount Chiliad', point: { x: 501.25, y: 5604.5 } });
  assert.equal((await waitFor(request, desktop, rename.requestId)).observed.name, 'Mount Chiliad');
  const stop = await command(request, phone, 'addons.setEnabled', { addonId: 'discover', enabled: false });
  assert.equal((await waitFor(request, phone, stop.requestId)).status, 'applied');
  assert.deepEqual(discover(await addons(request, phone)).result.stats.map(s => s.id), ['found', 'missed', 'rounds', 'xp', 'level']);

  const removed = await command(request, desktop, 'addons.removePlace', { addonId: 'discover', placeId: id });
  assert.equal((await waitFor(request, desktop, removed.requestId)).observed.removed, true);
  assert.equal((await command(request, desktop, 'addons.removePlace', { addonId: 'discover', placeId: id })).body.error.code, 'NOT_FOUND');
  assert.equal((await command(request, desktop, 'addons.savePlace', { addonId: 'discover', placeId: null, name: 'Off', point: { x: 1, y: 99999 } })).body.error.code, 'VALUE_OUT_OF_RANGE');
  assert.equal((await command(request, desktop, 'addons.savePlace', { addonId: 'ghost', placeId: null, name: 'No', point: { x: 1, y: 1 } })).body.error.code, 'CAPABILITY_UNAVAILABLE');
});

test('completed routes give XP and are listed as completed in GET /api/v1/progress', async t => {
  const { request, adapter } = await setup(t);
  const phone = await pair(request);
  const check = await schemas();
  const doc = await (await request('/api/v1/progress', { headers: phone })).json();
  check('progress', doc);
  assert.equal(doc.reasonCode, null);
  assert.equal(doc.progress.xp, 1240);
  assert.deepEqual(doc.progress.routes.map(r => [r.routeId, r.completions, r.bestTimeS]), [['35', 2, 3122.4], ['36', 1, 1500.2]], 'most recent first');
  assert.equal(doc.progress.stats.find(s => s.id === 'routesCompleted').value, 2);
  assert.equal(doc.progress.lastRoute.kind, 'route');
  assert.equal(doc.progress.lastRoute.stats.find(s => s.id === 'personalBestBonusXp').value, 100);
  assert.deepEqual(doc.progress.recent.map(r => r.kind), ['route', 'discover']);
  // Route ids match the catalog: a client marks those routes as completed.
  const routes = await (await request('/api/v1/routes', { headers: phone })).json();
  const ids = new Set(doc.progress.routes.map(r => r.routeId));
  assert.deepEqual(routes.items.filter(r => ids.has(r.id)).map(r => r.name).sort(), ['Coastal Loop', 'Hill Climb']);
  // Discover shows the same progress.
  assert.deepEqual(discover(await addons(request, phone)).progress, doc.progress);
  const caps = await (await request('/api/v1/capabilities', { headers: phone })).json();
  assert.equal(caps.capabilities.find(c => c.id === 'progress.read').available, true);

  delete adapter.raw.riderProgress;
  adapter.raw.addons.discover.progress = null;
  await adapter.write();
  assert.deepEqual(await (await request('/api/v1/progress', { headers: phone })).json(), { reasonCode: 'CAPABILITY_UNAVAILABLE', progress: null });
});

test('the adapter mirrors every parameter id, range and default of the registry', async () => {
  const dir = join(root, 'bridge', 'game-adapter-v1');
  const code = (await Promise.all(['GtbikeV1Alleycat.cs', 'GtbikeV1SurvivalSprint.cs', 'GtbikeV1Ghost.cs', 'GtbikeV1Discover.cs'].map(f => readFile(join(dir, f), 'utf8')))).join('\n');
  for (const addon of ADDONS) {
    for (const p of addon.parameters) {
      const fmt = v => (Number.isInteger(v) && p.type === 'number' ? `${v}(\\.0)?` : String(v).replace('.', '\\.'));
      const pattern = p.type === 'boolean' ? `Flag\\("${p.id}", ${p.default}, ${p.liveEdit}\\)`
        : p.type === 'enum' ? `Choice\\("${p.id}", new\\[\\] \\{ ${p.options.map(o => `"${o.id}"`).join(', ')} \\}, "${p.default}", ${p.liveEdit}\\)`
          : `Num\\("${p.id}", "${p.type}", ${fmt(p.min)}, ${fmt(p.max)}, ${fmt(p.step)}, ${fmt(p.default)}, ${p.liveEdit}\\)`;
      assert.match(code, new RegExp(pattern), `${addon.id}.${p.id} differs in GtbikeV1Addons.cs`);
    }
  }
});
