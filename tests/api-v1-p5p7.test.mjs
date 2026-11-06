// Bridge API v1, P5 devices, P6 workouts (read only) and P7 activities, against the simulated v1 adapter.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, writeFile, readdir } from 'node:fs/promises';
import { join } from 'node:path';
import { Encoder, Profile } from '@garmin/fitsdk';
import { schemas, setup, pair, command, waitFor } from './support/bridge-v1.mjs';

/** Small real FIT with a session and power records (one gap) for summaries and downloads. */
function fit({ power = true } = {}) {
  const encoder = new Encoder();
  const start = new Date('2026-11-05T10:00:00Z');
  encoder.onMesg(Profile.MesgNum.FILE_ID, { manufacturer: 'development', product: 1, timeCreated: start, type: 'activity' });
  for (let i = 0; i <= 20; i++) {
    if (i === 10) continue; // dropout: must stay a gap, not a zero
    encoder.onMesg(Profile.MesgNum.RECORD, { timestamp: new Date(+start + i * 1000), ...(power ? { power: 200 + i } : {}), heartRate: 140, distance: i * 8 });
  }
  encoder.onMesg(Profile.MesgNum.SESSION, {
    timestamp: new Date(+start + 20000), startTime: start, sport: 'cycling', totalElapsedTime: 20, totalTimerTime: 19, totalDistance: 160,
    totalAscent: 2, ...(power ? { avgPower: 210, maxPower: 220 } : {}), avgHeartRate: 140, maxHeartRate: 141, totalCalories: 5,
  });
  return Buffer.from(encoder.close());
}

test('devices: roles mirror native buttons; Lock/Skip name the device seen; Auto clears the rule', async t => {
  const check = await schemas();
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  let roles = await (await request('/api/v1/device-roles', { headers })).json();
  check('devices', roles);
  const hr = roles.roles.find(r => r.role === 'heartRate');
  assert.deepEqual(hr.actions, { auto: false, lock: true, skip: true });
  assert.equal(roles.roles.find(r => r.role === 'steering').actions.skip, false);

  const changed = await command(request, headers, 'devices.configure', { role: 'heartRate', action: 'lock', expectedDeviceId: '999', expectedSource: 'BLE' }, roles.devicesRevision);
  assert.equal(changed.body.error.code, 'DEVICE_CHANGED');
  const hidden = await command(request, headers, 'devices.configure', { role: 'runningSpeed', action: 'auto', expectedDeviceId: null, expectedSource: null }, roles.devicesRevision);
  assert.equal(hidden.body.error.code, 'VALUE_NOT_ALLOWED');

  const lock = await command(request, headers, 'devices.configure', { role: 'heartRate', action: 'lock', expectedDeviceId: '777', expectedSource: 'BLE' }, roles.devicesRevision);
  const locked = await waitFor(request, headers, lock.requestId);
  check('operation', locked);
  assert.equal(locked.status, 'applied');
  roles = await (await request('/api/v1/device-roles', { headers })).json();
  const after = roles.roles.find(r => r.role === 'heartRate');
  assert.equal(after.policy, 'locked');
  assert.deepEqual(after.actions, { auto: true, lock: false, skip: false });
  const skipLocked = await command(request, headers, 'devices.configure', { role: 'heartRate', action: 'skip', expectedDeviceId: '777', expectedSource: 'BLE' }, roles.devicesRevision);
  assert.equal(skipLocked.body.error.code, 'VALUE_NOT_ALLOWED', 'Skip is disabled while locked, like the native view');

  const auto = await command(request, headers, 'devices.configure', { role: 'heartRate', action: 'auto', expectedDeviceId: null, expectedSource: null }, roles.devicesRevision);
  assert.equal((await waitFor(request, headers, auto.requestId)).status, 'applied');

  adapter.raw.isDtOk = false;
  await adapter.write();
  roles = await (await request('/api/v1/device-roles', { headers })).json();
  check('devices', roles);
  assert.equal(roles.reasonCode, 'MOD_INACTIVE');
});

test('workouts.select (P13): quoted library revision; replaces a running workout like the mod menu; applied on a new selection', async t => {
  const check = await schemas();
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  const plans = await (await request('/api/v1/workout-plans', { headers })).json();
  let caps = await (await request('/api/v1/capabilities', { headers })).json();
  check('capabilities', caps);
  assert.equal(caps.capabilities.find(c => c.id === 'workouts.select').available, true);

  assert.equal((await command(request, headers, 'workouts.select', { workoutId: 'w-00000000000000bb' }, 'workouts-old')).body.error.code, 'REVISION_CONFLICT');
  assert.equal((await command(request, headers, 'workouts.select', { workoutId: 'w-ffffffffffffffff' }, plans.catalogRevision)).body.error.code, 'NOT_FOUND');
  const noRevision = await request('/api/v1/commands', { method: 'POST', headers, body: JSON.stringify({ requestId: randomUUID(), adapterInstanceId: 'adapter-test01', type: 'workouts.select', expectedRevision: null, args: { workoutId: 'w-00000000000000bb' } }) });
  assert.equal(noRevision.status, 422);

  adapter.raw.isDtOk = false;
  await adapter.write();
  assert.equal((await command(request, headers, 'workouts.select', { workoutId: 'w-00000000000000bb' }, plans.catalogRevision)).body.error.code, 'MOD_INACTIVE');
  // The same workout is running (unfinished, mid-way): the mod menu stays enabled, so the phone may replace it.
  adapter.raw.isDtOk = true;
  adapter.raw.activity.workoutPresent = true;
  adapter.raw.activity.workoutFinished = false;
  adapter.raw.workoutProgress = { name: 'Sweet Spot', started: true, inCountdown: false, finished: false, durationType: 'time', elapsed: 600, total: 1200, segmentCount: 2, segment: null, generation: 4 };
  await adapter.write();
  caps = await (await request('/api/v1/capabilities', { headers })).json();
  assert.equal(caps.capabilities.find(c => c.id === 'workouts.select').available, true, 'a running workout does not block a new selection');
  assert.equal(caps.capabilities.find(c => c.id === 'activities.finish').reasonCode, 'WORKOUT_PC_CONTROL_REQUIRED', 'saving stays blocked during a workout');

  // While the adapter has not run it yet, the running workout of the same name is NOT evidence.
  adapter.behaviour['workouts.select'] = 'ignore';
  const pending = await command(request, headers, 'workouts.select', { workoutId: 'w-00000000000000bb' }, plans.catalogRevision);
  assert.notEqual(pending.body.status, 'applied');
  delete adapter.behaviour['workouts.select'];

  const select = await command(request, headers, 'workouts.select', { workoutId: 'w-00000000000000bb' }, plans.catalogRevision);
  const op = await waitFor(request, headers, select.requestId);
  check('operation', op);
  assert.equal(op.status, 'applied');
  assert.equal(op.observed.workoutName, 'Sweet Spot');
  assert.equal(op.observed.inCountdown, true, 'restarted from its countdown');
  assert.equal(adapter.raw.workoutProgress.generation, 5);
});

test('workouts: plans, list, detail and progress; only workouts.select exists, no start/skip/end', async t => {
  const check = await schemas();
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  const plans = await (await request('/api/v1/workout-plans', { headers })).json();
  check('workouts', plans);
  assert.equal(plans.plans[0].workoutCount, 1);
  const list = await (await request('/api/v1/workouts?planId=p-00000000000000aa', { headers })).json();
  check('workouts', list);
  const detail = await (await request('/api/v1/workouts/w-00000000000000bb', { headers })).json();
  check('workouts', detail);
  assert.deepEqual(detail.workout.segments.map(s => s.type), ['warmup', 'steady']);
  assert.equal((await request('/api/v1/workouts?planId=p-ffffffffffffffff', { headers })).status, 404);

  let progress = await (await request('/api/v1/workout-progress', { headers })).json();
  check('workouts', progress);
  assert.equal(progress.present, false);
  adapter.raw.workoutProgress = { name: 'Sweet Spot', started: true, inCountdown: false, finished: false, durationType: 'time', elapsed: 420, total: 1200, segmentCount: 2, segment: { index: 1, type: 'Steady', start: 300, end: 1200, startPowerW: 207, endPowerW: 207, cadenceRpm: 90 } };
  await adapter.write();
  progress = await (await request('/api/v1/workout-progress', { headers })).json();
  check('workouts', progress);
  assert.equal(progress.segment.type, 'steady');

  for (const type of ['workouts.start', 'workouts.pause', 'workouts.skip', 'workouts.end', 'workouts.bias']) {
    const res = await request('/api/v1/commands', { method: 'POST', headers, body: JSON.stringify({ requestId: randomUUID(), adapterInstanceId: 'adapter-test01', type, expectedRevision: null, args: {} }) });
    assert.equal(res.status, 422, type);
  }
});

test('activities: only the Activities folder, real FIT summary with gaps, exact download', async t => {
  const check = await schemas();
  const { request, dir } = await setup(t);
  const headers = await pair(request);
  const folder = join(dir, 'Activities');
  await mkdir(join(folder, 'nested'), { recursive: true });
  const bytes = fit();
  await writeFile(join(folder, 'GTBikeV_20261105_100000.fit'), bytes);
  await writeFile(join(folder, 'nested', 'hidden.fit'), bytes); // subfolders are never listed
  await writeFile(join(folder, 'notes.txt'), 'x');
  await writeFile(join(folder, 'broken.fit'), Buffer.from('not a fit file'));

  const page = await (await request('/api/v1/activities', { headers })).json();
  check('activities', page);
  assert.deepEqual(page.items.map(i => i.fileName).sort(), ['GTBikeV_20261105_100000.fit', 'broken.fit']);
  const good = page.items.find(i => i.fileName.startsWith('GTBikeV'));
  const detail = await (await request(`/api/v1/activities/${good.id}`, { headers })).json();
  check('activities', detail);
  assert.equal(detail.activity.avgPowerW, 210);
  assert.equal(detail.activity.distanceM, 160);
  assert.ok(detail.activity.power.values.includes(null), 'dropout stays a gap');
  const download = await request(`/api/v1/activities/${good.id}/fit`, { headers });
  assert.equal(download.status, 200);
  const body = Buffer.from(await download.arrayBuffer());
  assert.ok(body.equals(bytes));
  assert.equal(download.headers.get('x-content-sha256'), createHash('sha256').update(bytes).digest('hex'));

  const broken = page.items.find(i => i.fileName === 'broken.fit');
  const invalid = await request(`/api/v1/activities/${broken.id}`, { headers });
  assert.equal(invalid.status, 422);
  check('error', await invalid.json());
  assert.equal((await request('/api/v1/activities/a-0000000000000000', { headers })).status, 404);
  assert.equal((await request('/api/v1/activities/..%2F..%2Fsecret/fit', { headers })).status, 404);
});

test('activities.finish: >100 m, stopped, no workout, quoted session; applied only when the new FIT exists', async t => {
  const check = await schemas();
  const { request, adapter, dir } = await setup(t);
  const headers = await pair(request);
  adapter.savedFit = fit({ power: false });

  adapter.raw.activities.activityDistanceM = 80;
  await adapter.write();
  let state = await (await request('/api/v1/state', { headers })).json();
  assert.equal((await command(request, headers, 'activities.finish', {}, state.sessionGeneration)).body.error.code, 'SAVE_TOO_SHORT');

  adapter.raw.activities.activityDistanceM = 2500;
  adapter.raw.metrics.speedMps = 7;
  await adapter.write();
  assert.equal((await command(request, headers, 'activities.finish', {}, state.sessionGeneration)).body.error.code, 'RIDER_MOVING');

  adapter.raw.metrics.speedMps = 0;
  adapter.raw.activity.workoutPresent = true;
  adapter.raw.activity.workoutFinished = false;
  await adapter.write();
  assert.equal((await command(request, headers, 'activities.finish', {}, state.sessionGeneration)).body.error.code, 'WORKOUT_PC_CONTROL_REQUIRED');

  adapter.raw.activity.workoutPresent = false;
  await adapter.write();
  assert.equal((await command(request, headers, 'activities.finish', {}, 'session-99')).body.error.code, 'REVISION_CONFLICT');

  state = await (await request('/api/v1/state', { headers })).json();
  const save = await command(request, headers, 'activities.finish', {}, state.sessionGeneration);
  const op = await waitFor(request, headers, save.requestId);
  check('operation', op);
  assert.equal(op.status, 'applied');
  assert.equal(op.observed.fileName, 'GTBikeV_20261105_101500.fit');
  assert.notEqual(op.observed.sessionGeneration, state.sessionGeneration);
  const detail = await (await request(`/api/v1/activities/${op.observed.activityId}`, { headers })).json();
  check('activities', detail);
  assert.equal(detail.activity.power, null, 'no power in file -> no chart, not zeros');
  const caps = await (await request('/api/v1/capabilities', { headers })).json();
  check('capabilities', caps);
  assert.equal(caps.capabilities.find(c => c.id === 'activities.finish').reasonCode, 'SAVE_TOO_SHORT', 'new activity starts at 0 m');
  assert.equal(caps.capabilities.find(c => c.id === 'activities.list').available, true);
  assert.ok((await readdir(join(dir, 'Activities'))).includes('GTBikeV_20261105_101500.fit'));
});
