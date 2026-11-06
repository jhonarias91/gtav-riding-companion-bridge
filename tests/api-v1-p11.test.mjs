// Bridge API v1, P11: the phone scans BLE advertisements and asks the PC to lock that sensor by address.
// The phone never connects to the sensor; the PC does. Runs against the simulated v1 adapter.
import test from 'node:test';
import assert from 'node:assert/strict';
import { schemas, setup, pair, command, waitFor } from './support/bridge-v1.mjs';
import { validateCommand, CommandError, ROLE_SENSOR_TYPES, ASSIGN_SENSOR_TYPES, isBleAddress } from '../bridge/api-v1/validate.mjs';

// Made-up example addresses, read as one 48-bit number like Windows' BluetoothAddress.
const KICKR = String(0xC0FFEE000001); // C0:FF:EE:00:00:01, FTMS + CPS -> FEC
const STRAP = String(0xC0FFEE000002); // C0:FF:EE:00:00:02, 0x180D -> HR

test('devices.assign: validation rejects wildcard, oversized and ANT-only values', () => {
  const body = args => ({ requestId: '3b8c9d0e-1f2a-4b3c-9d4e-5f6a7b8c9d0e', adapterInstanceId: 'adapter-1', type: 'devices.assign', expectedRevision: 'devices-1', args });
  assert.deepEqual(validateCommand(body({ role: 'heartRate', sensorType: 'HR', deviceId: STRAP })).args, { role: 'heartRate', sensorType: 'HR', deviceId: STRAP });
  const code = args => { try { validateCommand(body(args)); return null; } catch (e) { assert.ok(e instanceof CommandError); return e.details?.field; } };
  assert.equal(code({ role: 'heartRate', sensorType: 'HR', deviceId: '0' }), 'deviceId', '0 is the receiver wildcard');
  assert.equal(code({ role: 'heartRate', sensorType: 'HR', deviceId: String(2 ** 48) }), 'deviceId');
  assert.equal(code({ role: 'heartRate', sensorType: 'HR', deviceId: 123 }), 'deviceId', 'never a JSON number');
  assert.equal(code({ role: 'steering', sensorType: 'STZ', deviceId: STRAP }), 'sensorType', 'STZ is ANT only');
  assert.equal(code({ role: 'heartRate', sensorType: 'HR', deviceId: STRAP, source: 'BLE' }), 'source', 'the source is always BLE');
  assert.ok(isBleAddress(String(2 ** 48 - 1)));
  for (const types of Object.values(ROLE_SENSOR_TYPES)) for (const t of types) assert.ok(ASSIGN_SENSOR_TYPES.includes(t));
});

test('devices.assign: locks the phone-chosen BLE address; applied before the PC connects, connected after', async t => {
  const check = await schemas();
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  const caps = await (await request('/api/v1/capabilities', { headers })).json();
  check('capabilities', caps);
  const cap = caps.capabilities.find(c => c.id === 'devices.assign');
  assert.deepEqual({ available: cap.available, helpId: cap.helpId, applyMode: cap.applyMode }, { available: true, helpId: 'devices.phoneScan', applyMode: 'async' });

  let roles = await (await request('/api/v1/device-roles', { headers })).json();
  const wrongType = await command(request, headers, 'devices.assign', { role: 'heartRate', sensorType: 'BTS', deviceId: STRAP }, roles.devicesRevision);
  assert.equal(wrongType.body.error.code, 'VALUE_NOT_ALLOWED', 'a steering device cannot serve heart rate');
  const hidden = await command(request, headers, 'devices.assign', { role: 'runningSpeed', sensorType: 'HR', deviceId: STRAP }, roles.devicesRevision);
  assert.equal(hidden.body.error.code, 'VALUE_NOT_ALLOWED', 'running speed is hidden while cycling');
  const stale = await command(request, headers, 'devices.assign', { role: 'heartRate', sensorType: 'HR', deviceId: STRAP }, 'devices-old');
  assert.equal(stale.body.error.code, 'REVISION_CONFLICT');

  const assign = await command(request, headers, 'devices.assign', { role: 'power', sensorType: 'FEC', deviceId: KICKR }, roles.devicesRevision);
  assert.equal(assign.res.status, 202);
  const op = await waitFor(request, headers, assign.requestId);
  check('operation', op);
  assert.equal(op.status, 'applied');
  assert.equal(op.observed.connected, false, 'the rule is written; the PC still uses the ANT trainer until it finds the BLE one');
  roles = await (await request('/api/v1/device-roles', { headers })).json();
  check('devices', roles);
  const power = roles.roles.find(r => r.role === 'power');
  assert.equal(power.policy, 'locked');
  assert.deepEqual(power.entries, [{ deviceId: KICKR, source: 'BLE', sensorType: 'FEC', excluded: false }]);

  adapter.connectAssigned('power');
  await adapter.write();
  roles = await (await request('/api/v1/device-roles', { headers })).json();
  assert.deepEqual(roles.roles.find(r => r.role === 'power').current, { deviceId: KICKR, source: 'BLE', sensorType: 'FEC', name: null });

  // Same requestId never runs twice.
  const again = await request('/api/v1/commands', { method: 'POST', headers, body: JSON.stringify({ requestId: assign.requestId, adapterInstanceId: caps.adapterInstanceId, type: 'devices.assign', expectedRevision: roles.devicesRevision, args: { role: 'power', sensorType: 'FEC', deviceId: KICKR } }) });
  assert.equal((await again.json()).operationId, op.operationId);
});

test('devices.assign: unavailable when the adapter cannot resolve it or the mod is inactive', async t => {
  const { request, adapter } = await setup(t);
  const headers = await pair(request);
  adapter.raw.commands = adapter.raw.commands.filter(c => c !== 'devices.assign');
  await adapter.write();
  let caps = await (await request('/api/v1/capabilities', { headers })).json();
  assert.equal(caps.capabilities.find(c => c.id === 'devices.assign').reasonCode, 'CAPABILITY_UNAVAILABLE');
  adapter.raw.commands.push('devices.assign');
  adapter.raw.isDtOk = false;
  await adapter.write();
  caps = await (await request('/api/v1/capabilities', { headers })).json();
  assert.equal(caps.capabilities.find(c => c.id === 'devices.assign').reasonCode, 'MOD_INACTIVE');
});
