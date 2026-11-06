// Bridge API v1, P12: sensor names and disconnecting a BLE sensor the PC uses, chosen on the phone.
// Runs against the simulated v1 adapter.
import test from 'node:test';
import assert from 'node:assert/strict';
import { schemas, setup, pair, command, waitFor } from './support/bridge-v1.mjs';
import { validateCommand, CommandError } from '../bridge/api-v1/validate.mjs';

const STRAP = '777';

test('devices.disconnect: validation accepts only a BLE type and address, without a role', () => {
  const body = args => ({ requestId: '5d6e7f80-9a1b-4c2d-8e3f-4a5b6c7d8e9f', adapterInstanceId: 'adapter-1', type: 'devices.disconnect', expectedRevision: 'devices-1', args });
  assert.deepEqual(validateCommand(body({ sensorType: 'HR', deviceId: STRAP })).args, { sensorType: 'HR', deviceId: STRAP });
  const field = args => { try { validateCommand(body(args)); return null; } catch (e) { assert.ok(e instanceof CommandError); return e.details?.field; } };
  assert.equal(field({ sensorType: 'HR', deviceId: '0' }), 'deviceId');
  assert.equal(field({ sensorType: 'STZ', deviceId: STRAP }), 'sensorType', 'ANT-only type');
  assert.equal(field({ sensorType: 'HR', deviceId: STRAP, role: 'heartRate' }), 'role');
  assert.throws(() => validateCommand({ ...body({ sensorType: 'HR', deviceId: STRAP }), expectedRevision: null }));
});

test('device-roles carries the BLE name; ANT has none; control characters are dropped', async t => {
  const check = await schemas();
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  adapter.raw.devices.roles.find(r => r.role === 'heartRate').current.name = '  Polar\u0007 H10\n';
  adapter.raw.devices.roles.find(r => r.role === 'power').current.name = 'ignored for ANT';
  await adapter.write();
  const roles = await (await request('/api/v1/device-roles', { headers })).json();
  check('devices', roles);
  assert.equal(roles.roles.find(r => r.role === 'heartRate').current.name, 'Polar H10');
  assert.equal(roles.roles.find(r => r.role === 'power').current.name, null);
});

test('devices.disconnect: only a BLE sensor in use; applied once no role uses it', async t => {
  const check = await schemas();
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  const caps = await (await request('/api/v1/capabilities', { headers })).json();
  check('capabilities', caps);
  const cap = caps.capabilities.find(c => c.id === 'devices.disconnect');
  assert.deepEqual({ available: cap.available, helpId: cap.helpId, applyMode: cap.applyMode }, { available: true, helpId: 'devices.phoneScan', applyMode: 'async' });

  let roles = await (await request('/api/v1/device-roles', { headers })).json();
  const notUsed = await command(request, headers, 'devices.disconnect', { sensorType: 'HR', deviceId: '999' }, roles.devicesRevision);
  assert.equal(notUsed.body.error.code, 'DEVICE_CHANGED', 'the PC does not use that sensor');
  const wrongType = await command(request, headers, 'devices.disconnect', { sensorType: 'FEC', deviceId: STRAP }, roles.devicesRevision);
  assert.equal(wrongType.body.error.code, 'DEVICE_CHANGED', 'the type must match what the PC uses');
  const stale = await command(request, headers, 'devices.disconnect', { sensorType: 'HR', deviceId: STRAP }, 'devices-old');
  assert.equal(stale.body.error.code, 'REVISION_CONFLICT');

  const sent = await command(request, headers, 'devices.disconnect', { sensorType: 'HR', deviceId: STRAP }, roles.devicesRevision);
  assert.equal(sent.res.status, 202);
  const op = await waitFor(request, headers, sent.requestId);
  check('operation', op);
  assert.equal(op.status, 'applied');
  assert.equal(op.observed.connected, false);
  roles = await (await request('/api/v1/device-roles', { headers })).json();
  assert.equal(roles.roles.find(r => r.role === 'heartRate').current, null);
});

test('devices.disconnect: unavailable when the adapter cannot resolve it or the mod is inactive', async t => {
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  adapter.raw.commands = adapter.raw.commands.filter(c => c !== 'devices.disconnect');
  await adapter.write();
  let caps = await (await request('/api/v1/capabilities', { headers })).json();
  assert.equal(caps.capabilities.find(c => c.id === 'devices.disconnect').reasonCode, 'CAPABILITY_UNAVAILABLE');
  adapter.raw.commands.push('devices.disconnect');
  adapter.raw.isDtOk = false;
  await adapter.write();
  caps = await (await request('/api/v1/capabilities', { headers })).json();
  assert.equal(caps.capabilities.find(c => c.id === 'devices.disconnect').reasonCode, 'MOD_INACTIVE');
});
