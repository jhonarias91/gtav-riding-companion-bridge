// P17: "Route here" / "Clear destination" on the PC window map (player.waypoint / player.clearWaypoint). GTA's own
// map waypoint; the game draws the GPS route. Desktop token only; evidence is the adapter's waypoint report.
import test from 'node:test';
import assert from 'node:assert/strict';
import { setup, pair, command, waitFor, schemas } from './support/bridge-v1.mjs';

const desktopToken = 'd'.repeat(43);
const desktop = { Authorization: `Bearer ${desktopToken}`, 'Content-Type': 'application/json' };
const world = async request => (await request('/api/v1/desktop/world', { headers: desktop })).json();

test('the PC window sets a destination, sees its GPS distance, then clears it', async t => {
  const { request } = await setup(t, { desktopToken });
  const check = await schemas();
  assert.deepEqual((await world(request)).destination, { reasonCode: null, point: null });

  const set = await command(request, desktop, 'player.waypoint', { x: 215.5, y: -810.25 });
  assert.equal(set.res.status, 202);
  check('operation', set.body);
  const op = await waitFor(request, desktop, set.requestId);
  check('operation', op);
  assert.equal(op.status, 'applied');
  assert.deepEqual(op.observed, { x: 215.9, y: -810.55, routeFound: true, routeLengthM: 2450 });
  assert.deepEqual((await world(request)).destination.point, { x: 215.9, y: -810.55, routeFound: true, routeLengthM: 2450 });

  const clear = await command(request, desktop, 'player.clearWaypoint', {});
  assert.equal((await waitFor(request, desktop, clear.requestId)).status, 'applied');
  assert.equal((await world(request)).destination.point, null);
});

test('a waypoint chosen in the game itself shows on the PC map; bad values are refused', async t => {
  const { request, adapter } = await setup(t, { desktopToken });
  adapter.raw.waypoint = { x: 1000, y: 2000, routeFound: false, routeLengthM: null };
  await adapter.write();
  assert.deepEqual((await world(request)).destination.point, { x: 1000, y: 2000, routeFound: false, routeLengthM: null });
  adapter.raw.waypoint = { x: 'far', y: 2000 };
  await adapter.write();
  assert.equal((await world(request)).destination.point, null);
  assert.equal((await command(request, desktop, 'player.waypoint', { x: 1, y: 20000 })).body.error.code, 'VALUE_OUT_OF_RANGE');
  assert.equal((await command(request, desktop, 'player.clearWaypoint', { x: 1 })).body.error.code, 'INVALID_REQUEST');
});

test('phones cannot set or clear the destination', async t => {
  const { request } = await setup(t, { desktopToken });
  const phone = await pair(request);
  const caps = await (await request('/api/v1/capabilities', { headers: phone })).json();
  for (const id of ['player.waypoint', 'player.clearWaypoint']) assert.equal(caps.capabilities.find(c => c.id === id).reasonCode, 'FORBIDDEN_SCOPE');
  assert.equal((await command(request, phone, 'player.waypoint', { x: 1, y: 2 })).res.status, 403);
  assert.equal((await command(request, phone, 'player.clearWaypoint', {})).res.status, 403);
});

test('refused during a course or with the game not in control', async t => {
  const { request, adapter } = await setup(t, { desktopToken });
  adapter.raw.activeRoute = { id: '35', loop: false, reverse: false };
  await adapter.write();
  assert.equal((await world(request)).destination.reasonCode, 'VALUE_NOT_ALLOWED');
  assert.equal((await command(request, desktop, 'player.waypoint', { x: 1, y: 2 })).body.error.code, 'VALUE_NOT_ALLOWED');
  assert.equal((await command(request, desktop, 'player.clearWaypoint', {})).body.error.code, 'VALUE_NOT_ALLOWED');
  adapter.raw.activeRoute = null;
  adapter.raw.gameControl = 'loading';
  await adapter.write();
  assert.equal((await command(request, desktop, 'player.waypoint', { x: 1, y: 2 })).body.error.code, 'GAME_NOT_IN_CONTROL');
});
