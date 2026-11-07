// P16: "Teleport here" on the PC window map (player.teleport). Desktop token only; evidence comes from the adapter's
// own teleport report under the same requestId.
import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { setup, pair, command, waitFor, schemas } from './support/bridge-v1.mjs';

const desktopToken = 'd'.repeat(43);
const desktop = { Authorization: `Bearer ${desktopToken}`, 'Content-Type': 'application/json' };

test('the PC window teleports the rider; the operation is applied with the adapter\'s final point', async t => {
  const { request } = await setup(t, { desktopToken });
  const check = await schemas();
  const world = await (await request('/api/v1/desktop/world', { headers: desktop })).json();
  assert.deepEqual(world.teleport, { reasonCode: null });
  const caps = await (await request('/api/v1/capabilities', { headers: desktop })).json();
  check('capabilities', caps);
  assert.equal(caps.capabilities.find(c => c.id === 'player.teleport').available, true);

  const { res, body, requestId } = await command(request, desktop, 'player.teleport', { x: -1234.567, y: 2750.25 });
  assert.equal(res.status, 202);
  check('operation', body);
  const op = await waitFor(request, desktop, requestId);
  check('operation', op);
  assert.equal(op.status, 'applied');
  assert.deepEqual(op.observed, { x: -1231.57, y: 2746.25, z: 31.5, snappedToRoad: true });
  const after = await (await request('/api/v1/desktop/world', { headers: desktop })).json();
  assert.equal(after.position.x, -1231.57);
});

test('a paired phone can neither teleport nor see the capability as available', async t => {
  const { request } = await setup(t, { desktopToken });
  const phone = await pair(request);
  const caps = await (await request('/api/v1/capabilities', { headers: phone })).json();
  const teleport = caps.capabilities.find(c => c.id === 'player.teleport');
  assert.deepEqual([teleport.available, teleport.reasonCode], [false, 'FORBIDDEN_SCOPE']);
  const { res, body } = await command(request, phone, 'player.teleport', { x: 1, y: 2 });
  assert.equal(res.status, 403);
  assert.equal(body.error.code, 'FORBIDDEN_SCOPE');
});

test('arguments are strict: numbers inside the map, no revision, no extra fields', async t => {
  const { request } = await setup(t, { desktopToken });
  const state = await (await request('/api/v1/state', { headers: desktop })).json();
  const send = (args, expectedRevision = null) => request('/api/v1/commands', { method: 'POST', headers: desktop,
    body: JSON.stringify({ requestId: randomUUID(), adapterInstanceId: state.adapterInstanceId, type: 'player.teleport', expectedRevision, args }) }).then(r => r.json());
  assert.equal((await send({ x: 20000, y: 0 })).error.code, 'VALUE_OUT_OF_RANGE');
  assert.equal((await send({ x: '1', y: 0 })).error.code, 'INVALID_REQUEST');
  assert.equal((await send({ x: 1, y: 0, z: 3 })).error.code, 'INVALID_REQUEST');
  assert.equal((await send({ x: 1, y: 0 }, 'state-abc')).error.code, 'INVALID_REQUEST');
});

test('refused during a course, with the mod inactive or the game not in control', async t => {
  const { request, adapter } = await setup(t, { desktopToken });
  adapter.raw.activeRoute = { id: '35', loop: false, reverse: false };
  await adapter.write();
  assert.equal((await (await request('/api/v1/desktop/world', { headers: desktop })).json()).teleport.reasonCode, 'VALUE_NOT_ALLOWED');
  assert.equal((await command(request, desktop, 'player.teleport', { x: 1, y: 2 })).body.error.code, 'VALUE_NOT_ALLOWED');
  adapter.raw.activeRoute = null;
  adapter.raw.gameControl = 'paused';
  await adapter.write();
  assert.equal((await command(request, desktop, 'player.teleport', { x: 1, y: 2 })).body.error.code, 'GAME_NOT_IN_CONTROL');
  adapter.raw.gameControl = 'inControl';
  adapter.raw.isDtOk = false;
  adapter.raw.modLayer = 'inactive';
  await adapter.write();
  assert.equal((await command(request, desktop, 'player.teleport', { x: 1, y: 2 })).body.error.code, 'MOD_INACTIVE');
});

test('open water fails the operation and the rider stays put; a running job blocks a second one', async t => {
  const { request, adapter } = await setup(t, { desktopToken });
  adapter.teleportOutcome = 'water';
  const water = await command(request, desktop, 'player.teleport', { x: -3000, y: -3000 });
  const op = await waitFor(request, desktop, water.requestId);
  assert.equal(op.status, 'failed');
  assert.equal(op.error.code, 'VALUE_NOT_ALLOWED');
  assert.deepEqual((await (await request('/api/v1/desktop/world', { headers: desktop })).json()).position, { x: 100, y: -200, z: 30, heading: 0 });

  adapter.teleportOutcome = 'running';
  const first = await command(request, desktop, 'player.teleport', { x: 5, y: 5 });
  await waitFor(request, desktop, first.requestId, ['pending']);
  for (let i = 0; i < 40 && (await (await request('/api/v1/desktop/world', { headers: desktop })).json()).teleport.reasonCode !== 'BRIDGE_BUSY'; i++) await new Promise(r => setTimeout(r, 50));
  assert.equal((await command(request, desktop, 'player.teleport', { x: 6, y: 6 })).body.error.code, 'BRIDGE_BUSY');
});
