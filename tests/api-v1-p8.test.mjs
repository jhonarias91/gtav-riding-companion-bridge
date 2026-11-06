import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { crc32, deflateSync } from 'node:zlib';
import Ajv2020 from 'ajv/dist/2020.js';
import { createBridgeV1 } from '../bridge/api-v1/server.mjs';
import { loadWorldMap } from '../bridge/api-v1/world.mjs';

const json = async path => JSON.parse(await readFile(new URL(path, import.meta.url), 'utf8'));

async function testDirectory(t) {
  await mkdir('outputs', { recursive: true });
  const dir = await mkdtemp(join('outputs', 'p8-test-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  return dir;
}

function chunk(type, data) {
  const nameAndData = Buffer.concat([Buffer.from(type), data]);
  const chunk = Buffer.alloc(nameAndData.length + 8);
  chunk.writeUInt32BE(data.length);
  nameAndData.copy(chunk, 4);
  chunk.writeUInt32BE(crc32(nameAndData), chunk.length - 4);
  return chunk;
}

async function makeMap(dir) {
  const header = Buffer.from('00000064000000640800000000', 'hex'); // 100x100 8-bit grayscale.
  const png = Buffer.concat([
    Buffer.from('89504e470d0a1a0a', 'hex'),
    chunk('IHDR', header), chunk('IDAT', deflateSync(Buffer.alloc(100 * 101))), chunk('IEND', Buffer.alloc(0)),
  ]);
  const point = (x, y) => ({ x, y, px: x, py: 100 - y });
  const calibration = {
    version: 1, source: 'Test image', rights: 'Synthetic test data',
    sha256: createHash('sha256').update(png).digest('hex'),
    anchors: [point(10, 10), point(90, 10), point(10, 90)],
    checkpoints: [point(50, 50), point(80, 80)],
  };
  await writeFile(join(dir, 'map.png'), png);
  await writeFile(join(dir, 'calibration.json'), JSON.stringify(calibration));
  return { png, calibration };
}

test('P8 map calibration rejects mismatch, bad checkpoints and corrupt assets', async t => {
  const dir = await testDirectory(t);
  const { png, calibration } = await makeMap(dir);
  const file = join(dir, 'calibration.json');
  const map = await loadWorldMap(dir);
  assert.deepEqual(map.project({ x: 20, y: 70 }), { u: 0.2, v: 0.3 });
  assert.equal(map.project({ x: 20, y: -70 }), null);
  await writeFile(file, JSON.stringify({ ...calibration, sha256: '0'.repeat(64) }));
  await assert.rejects(loadWorldMap(dir), /does not match/);
  const checkpoints = [...calibration.checkpoints, { x: 20, y: 30, px: 20, py: 30 }];
  await writeFile(file, JSON.stringify({ ...calibration, checkpoints }));
  await assert.rejects(loadWorldMap(dir), /checkpoint differs/);
  await writeFile(file, JSON.stringify({ ...calibration, anchors: [calibration.anchors[0], calibration.checkpoints[0], calibration.checkpoints[1]] }));
  await assert.rejects(loadWorldMap(dir), /landmarks must be distinct|non-collinear/);
  png[png.length - 1] ^= 1;
  await writeFile(join(dir, 'map.png'), png);
  await writeFile(file, JSON.stringify({ ...calibration, sha256: createHash('sha256').update(png).digest('hex') }));
  await assert.rejects(loadWorldMap(dir), /incomplete or corrupt/);
});

test('P8 routes expose bounded authenticated data and keep optional writes unavailable', async t => {
  const dir = await testDirectory(t);
  const { png } = await makeMap(dir);
  const map = await loadWorldMap(dir);
  const world = { map, riders: true };
  const now = Date.now();
  const adapterDirectory = join(dir, 'adapter');
  await mkdir(adapterDirectory);
  const fixture = await json('../bridge/fixtures/v1/p8/adapter-world.json');
  const save = extra => writeFile(join(adapterDirectory, 'telemetry.json'), JSON.stringify({
    ...fixture, capturedAtUtc: new Date(now).toISOString(), ...extra,
  }));
  await save();
  const bridge = await createBridgeV1({ adapterDirectory, stateDirectory: join(dir, 'state'), world, pin: '123456', now: () => now });
  t.after(() => bridge.close());
  await new Promise(resolve => bridge.server.listen(0, '127.0.0.1', resolve));
  const url = `http://127.0.0.1:${bridge.server.address().port}/api/v1`;
  const request = (path, options) => fetch(url + path, options);
  const assetPath = `/assets/${map.id}`;
  for (const path of ['/map', '/riders', assetPath]) {
    const response = await request(path);
    assert.equal(response.status, 401);
    await response.json();
  }
  const pair = await request('/pair', { method: 'POST', body: JSON.stringify({ pin: '123456' }) });
  const { token } = await pair.json();
  const headers = { authorization: `Bearer ${token}` };
  const get = async path => (await request(path, { headers })).json();
  const capabilities = async () => Object.fromEntries((await get('/capabilities')).capabilities.map(item => [item.id, item]));
  const ajv = new Ajv2020({ strict: true, strictTypes: false });

  const mapDoc = await get('/map');
  assert.ok(ajv.validate(await json('../bridge/contracts/v1/map.schema.json'), mapDoc), JSON.stringify(ajv.errors));
  assert.deepEqual(mapDoc.position, { u: 0.4, v: 0.4 });
  assert.equal(mapDoc.route.id, '35');
  assert.equal(mapDoc.route.reverse, true);
  assert.deepEqual(mapDoc.route.waypoints, [{ u: 0.7, v: 0.6 }, null, { u: 0.1, v: 0.8 }]);
  assert.equal(ajv.getSchema('https://contracts.gtav-riding-companion.invalid/v1/map.schema.json')({ ...mapDoc, position: { u: 1.1, v: 0 } }), false);
  const asset = await request(assetPath, { headers });
  assert.equal(asset.headers.get('content-type'), 'image/png');
  assert.deepEqual(Buffer.from(await asset.arrayBuffer()), png);
  const nonexistent = await request(`/assets/map-${'0'.repeat(64)}`, { headers });
  assert.equal(nonexistent.status, 404);
  assert.equal((await nonexistent.json()).error.code, 'ASSET_UNAVAILABLE');

  const riders = await get('/riders');
  assert.ok(ajv.validate(await json('../bridge/contracts/v1/riders.schema.json'), riders), JSON.stringify(ajv.errors));
  assert.deepEqual(riders.riders.map(r => [r.kind, r.name, r.wattsPerKg]), [
    ['human', 'Rider', 3.2], ['local', 'You', 0], ['bot', 'Bot', null],
  ]);
  await save({ p8Riders: { ...fixture.p8Riders, connected: false, joinCourseKey: 32803 } });
  assert.equal((await get('/riders')).busiestRoute, null);
  assert.equal((await get('/riders')).connected, false);
  await save({ p8Riders: { ...fixture.p8Riders, joinCourseKey: 32803 } });
  assert.deepEqual((await get('/riders')).busiestRoute, { id: '35', reverse: true });
  const caps = await capabilities();
  assert.equal(caps['map.position'].available, true);
  assert.equal(caps['riders.read'].available, true);
  for (const type of ['multiplayer.joinBusiestRoute', 'advancedSettings.update']) {
    assert.equal(caps[type].available, false);
    const rejected = await request('/commands', {
      method: 'POST', headers,
      body: JSON.stringify({ type, requestId: randomUUID(), adapterInstanceId: fixture.adapterInstanceId, expectedRevision: null, args: {} }),
    });
    assert.equal((await rejected.json()).error.code, 'CAPABILITY_UNAVAILABLE');
  }
  await save({ capturedAtUtc: new Date(now - 6000).toISOString() });
  assert.equal((await get('/map')).error.code, 'ADAPTER_STALE');
  assert.equal((await get('/riders')).error.code, 'ADAPTER_STALE');
  delete world.map;
  world.riders = false;
  const disabled = await capabilities();
  assert.equal(disabled['map.position'].available, false);
  assert.equal(disabled['riders.read'].available, false);
});
