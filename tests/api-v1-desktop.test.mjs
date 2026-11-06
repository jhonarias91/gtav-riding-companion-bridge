// PC window: loopback desktop token and the raw world position for its own map.
import test from 'node:test';
import assert from 'node:assert/strict';
import { setup, pair, command, waitFor } from './support/bridge-v1.mjs';

const desktopToken = 'd'.repeat(43);
const desktop = { Authorization: `Bearer ${desktopToken}`, 'Content-Type': 'application/json' };

test('the desktop token reads the raw world position; phones cannot reach /desktop', async t => {
  const { request, adapter } = await setup(t, { desktopToken });
  adapter.raw.p8Map = { position: { x: 100.25, y: -200.5, z: 30, heading: 45 }, route: { courseKey: 35, waypoints: [{ x: 1, y: 2 }, { x: 3, y: 4 }] } };
  await adapter.write();
  const world = await (await request('/api/v1/desktop/world', { headers: desktop })).json();
  assert.deepEqual(world.position, { x: 100.25, y: -200.5, z: 30, heading: 45 });
  assert.deepEqual(world.route, { reverse: false, waypoints: [{ x: 1, y: 2 }, { x: 3, y: 4 }] });
  assert.equal(world.mod, 'dataReady');
  assert.equal(world.adapter, 'fresh');

  const phone = await pair(request);
  assert.equal((await request('/api/v1/desktop/world', { headers: phone })).status, 404);
  assert.equal((await request('/api/v1/desktop/world', { headers: { Authorization: `Bearer ${'e'.repeat(43)}` } })).status, 401);
});

test('the desktop token sends the same commands a phone can (mod settings)', async t => {
  const { request } = await setup(t, { desktopToken });
  const settings = await (await request('/api/v1/settings', { headers: desktop })).json();
  const { res, requestId } = await command(request, desktop, 'profile.update', { ftpW: 250 }, settings.settingsRevision);
  assert.equal(res.status, 202);
  assert.equal((await waitFor(request, desktop, requestId)).status, 'applied');
});

test('without a desktop token the bridge has no desktop surface', async t => {
  const { request } = await setup(t);
  assert.equal((await request('/api/v1/desktop/world', { headers: desktop })).status, 401);
});

test('no position while the adapter is stale or the mod has not reported one', async t => {
  const { request, adapter } = await setup(t, { desktopToken });
  delete adapter.raw.p8Map;
  await adapter.write();
  const world = await (await request('/api/v1/desktop/world', { headers: desktop })).json();
  assert.equal(world.position, null);
  assert.equal(world.route, null);
});
