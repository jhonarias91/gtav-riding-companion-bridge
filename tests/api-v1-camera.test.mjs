import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { setup, pair, command, waitFor, schemas } from './support/bridge-v1.mjs';
import { CAMERA_MODES, toCamera, cameraEvidence } from '../bridge/api-v1/camera.mjs';
import { validateCommand } from '../bridge/api-v1/validate.mjs';

async function cameraSetup(t) {
  const env = await setup(t);
  env.adapter.raw.commands.push('camera.next', 'camera.setMode', 'camera.look');
  env.adapter.raw.camera = { reasonCode: null, currentMode: 'near', modes: CAMERA_MODES, headingDeg: 0, pitchDeg: 0, lookReasonCode: null, busy: false };
  await env.adapter.write();
  return { ...env, headers: await pair(env.request) };
}

test('camera commands agree with strict schemas, reject injected keys, invalid modes, angles and revisions', async () => {
  const check = await schemas();
  const body = (type, args, expectedRevision = null) => ({ requestId: randomUUID(), adapterInstanceId: 'adapter-test01', type, args, expectedRevision });
  for (const doc of [body('camera.next', {}), ...CAMERA_MODES.map(mode => body('camera.setMode', { mode })), body('camera.look', { headingDeg: -180, pitchDeg: 45 })]) {
    check('command', doc); assert.deepEqual(validateCommand(doc), doc);
  }
  for (const doc of [body('camera.next', { key: 'F5' }), body('camera.setMode', { mode: 'drone' }), body('camera.look', { headingDeg: 181, pitchDeg: 0 }),
    body('camera.look', { headingDeg: 0, pitchDeg: NaN }), body('camera.look', { headingDeg: '0', pitchDeg: 0 }), body('camera.next', {}, 'state-1')]) assert.throws(() => validateCommand(doc));
});

test('camera document and next view use the shared phone API with read-back and exactly one pulse', async t => {
  const check = await schemas();
  const { request, adapter, headers } = await cameraSetup(t);
  const doc = await (await request('/api/v1/camera', { headers })).json();
  check('camera', doc); assert.deepEqual(doc.modes, CAMERA_MODES); assert.equal(doc.currentMode, 'near');
  const sent = await command(request, headers, 'camera.next', {});
  assert.equal(sent.res.status, 202);
  const done = await waitFor(request, headers, sent.requestId);
  check('operation', done); assert.equal(done.status, 'applied'); assert.equal(done.observed.mode, 'medium');
  await request('/api/v1/commands', { method: 'POST', headers, body: JSON.stringify({ requestId: sent.requestId, adapterInstanceId: adapter.instance, type: 'camera.next', args: {}, expectedRevision: null }) });
  assert.equal(adapter.cameraPulses, 1);
});

test('all native bike views can be selected and yaw/pitch are confirmed before applied', async t => {
  const { request, headers } = await cameraSetup(t);
  for (const mode of CAMERA_MODES) {
    const sent = await command(request, headers, 'camera.setMode', { mode });
    assert.equal(sent.res.status, 202);
    assert.equal((await waitFor(request, headers, sent.requestId)).observed.mode, mode);
  }
  const near = await command(request, headers, 'camera.setMode', { mode: 'near' });
  await waitFor(request, headers, near.requestId);
  const sent = await command(request, headers, 'camera.look', { headingDeg: 60, pitchDeg: -20 });
  const done = await waitFor(request, headers, sent.requestId);
  assert.equal(done.status, 'applied'); assert.equal(done.observed.headingDeg, 60); assert.equal(done.observed.pitchDeg, -20);
});

test('mod look-behind, inactive mod, stale data and older adapters disable the relevant controls', async t => {
  const { request, adapter, headers } = await cameraSetup(t);
  adapter.raw.camera.lookReasonCode = 'CAMERA_CONTROL_IN_USE'; await adapter.write();
  assert.equal((await command(request, headers, 'camera.look', { headingDeg: 0, pitchDeg: 0 })).body.error.code, 'CAMERA_CONTROL_IN_USE');
  adapter.raw.isDtOk = false; await adapter.write();
  assert.equal((await command(request, headers, 'camera.next', {})).body.error.code, 'MOD_INACTIVE');
  const stale = toCamera({ fresh: false, raw: adapter.raw });
  assert.equal(stale.available, false); assert.deepEqual(stale.modes, []); assert.equal(stale.headingDeg, null);
  assert.equal(toCamera({ fresh: true, raw: { isDtOk: true } }).reasonCode, 'CAPABILITY_UNAVAILABLE');
});

test('another request, unobserved completion or clamped target never count as applied', () => {
  const args = { headingDeg: 90, pitchDeg: 0 }, base = { requestId: 'mine' };
  const documents = result => ({ snap: { fresh: true, raw: { camera: { command: result } } } });
  assert.equal(cameraEvidence(args, base, documents({ requestId: 'other', status: 'done', mode: 'near', headingDeg: 90, pitchDeg: 0 })), null);
  assert.equal(cameraEvidence(args, base, documents({ requestId: 'mine', status: 'running', mode: 'near', headingDeg: 90, pitchDeg: 0 })), null);
  assert.equal(cameraEvidence(args, base, documents({ requestId: 'mine', status: 'done', mode: 'near', headingDeg: 0, pitchDeg: 0 })), null);
});
