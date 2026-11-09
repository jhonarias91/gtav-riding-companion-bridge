import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { schemas, setup, pair, command, waitFor } from './support/bridge-v1.mjs';
import { parseVirtualGears, validGearRatios, DEFAULT_GEAR_RATIOS } from '../bridge/api-v1/trainer-config.mjs';
import { toTrainer, gearEvidence } from '../bridge/api-v1/trainer.mjs';
import { validateCommand } from '../bridge/api-v1/validate.mjs';

async function configSetup(t, value = 'False, 0.75, 1, 2') {
  const iniDir = await mkdtemp(join(tmpdir(), 'gtbv-trainer-'));
  t.after(() => rm(iniDir, { recursive: true, force: true }));
  const path = join(iniDir, 'GTBikeVConfig.ini');
  const original = `[main]\r\nDebugWindow = False\r\n[devices]\r\n; keep me\r\nVirtualGears = ${value}\r\nBrakingDivisor = 6\r\n[keys]\r\nGearUpKey = NumPad9(U)\r\n`;
  await writeFile(path, original);
  const game = { running: false };
  const env = await setup(t, { modConfig: { path, isGameRunning: async () => game.running } });
  const headers = await pair(env.request);
  const get = async () => (await env.request('/api/v1/trainer-config', { headers })).json();
  const post = async body => {
    const res = await env.request('/api/v1/trainer-config', { method: 'POST', headers, body: JSON.stringify(body) });
    return { status: res.status, body: await res.json() };
  };
  return { ...env, iniDir, path, game, original, headers, get, post };
}

test('VirtualGears parser follows native True/False/manual/auto/native semantics and validates ratios', () => {
  for (const [token, mode, enabled] of [['True', 'manual', true], ['False', 'none', false], ['None', 'none', false], ['Manual', 'manual', true], ['Auto', 'auto', true], ['Native', 'native', true]])
    assert.deepEqual(parseVirtualGears(`${token}, 2, 1`), { mode, enabled, ratios: [1, 2] });
  assert.equal(parseVirtualGears('constructor,1').mode, null);
  for (const ratios of [[], [0], [-1], [NaN], [21], [1, 1], [2, 1], [0.1234], Array.from({ length: 65 }, (_, n) => 0.1 + n / 100)]) assert.equal(validGearRatios(ratios), false);
  assert.equal(validGearRatios(DEFAULT_GEAR_RATIOS), true);
});

test('trainer config persists only native VirtualGears with closed-game revision and backup', async t => {
  const env = await configSetup(t), check = await schemas();
  const before = await env.get(); check('trainer-config', before);
  assert.deepEqual(before.options, { enabled: false, ratios: [0.75, 1, 2] });
  assert.equal((await env.request('/api/v1/trainer-config')).status, 401);
  const saved = await env.post({ expectedRevision: before.revision, options: { enabled: true, ratios: [0.8, 1.2, 2.4, 3] } });
  assert.equal(saved.status, 200, JSON.stringify(saved.body)); check('trainer-config', saved.body);
  assert.equal(saved.body.nativeMode, 'manual');
  assert.equal(await readFile(env.path, 'utf8'), env.original.replace('False, 0.75, 1, 2', 'True, 0.8, 1.2, 2.4, 3'));
  const backups = await readdir(join(env.dir, 'bridge-v1', 'mod-config-backups'));
  assert.equal(await readFile(join(env.dir, 'bridge-v1', 'mod-config-backups', backups[0]), 'utf8'), env.original);
  assert.equal((await env.post({ expectedRevision: before.revision, options: { enabled: false } })).body.error.code, 'REVISION_CONFLICT');
  env.game.running = true;
  assert.equal((await env.get()).editable, false);
  assert.equal((await env.post({ expectedRevision: saved.body.revision, options: { enabled: false } })).body.error.code, 'GAME_RUNNING');
});

test('ratio-only edits preserve Auto and arbitrary device/ERG/keyboard writes remain refused', async t => {
  const env = await configSetup(t, 'Auto, 1, 2');
  const before = await env.get();
  const saved = await env.post({ expectedRevision: before.revision, options: { ratios: [0.5, 1, 1.5] } });
  assert.equal(saved.status, 200); assert.match(await readFile(env.path, 'utf8'), /VirtualGears = Auto, 0.5, 1, 1.5/);
  for (const options of [{ targetPower: 250 }, { BrakingDivisor: 2 }, { GearUpKey: 'W' }, { enabled: 'True' }, { ratios: [1, 1] }, { ratios: [2, 1] }])
    assert.equal((await env.post({ expectedRevision: saved.body.revision, options })).status, 422);
  const missing = await configSetup(t); await rm(missing.path);
  const doc = await missing.get(); (await schemas())('trainer-config', doc); assert.equal(doc.available, false);
});

async function liveSetup(t) {
  const env = await setup(t);
  env.adapter.raw.commands.push('gears.shiftUp', 'gears.shiftDown');
  env.adapter.raw.trainer = { enabled: true, mode: 'manual', gear: 2, gearCount: 4, ratio: 2, reasonCode: null, shiftReasonCode: null, busy: false };
  await env.adapter.write();
  return { ...env, headers: await pair(env.request) };
}
test('native gear commands are typed, finite and confirmed once by request-specific read-back', async t => {
  const env = await liveSetup(t), check = await schemas();
  const live = await (await env.request('/api/v1/trainer', { headers: env.headers })).json(); check('trainer', live);
  assert.equal(live.gear, 2); assert.equal(live.canShiftDown, true);
  for (const type of ['gears.shiftUp', 'gears.shiftDown']) {
    const body = { requestId: randomUUID(), adapterInstanceId: env.adapter.instance, type, args: {}, expectedRevision: null };
    check('command', body); assert.deepEqual(validateCommand(body), body);
    assert.throws(() => validateCommand({ ...body, expectedRevision: 'state-x' }));
    assert.throws(() => validateCommand({ ...body, args: { key: 'NumPad9' } }));
  }
  const sent = await command(env.request, env.headers, 'gears.shiftUp', {});
  assert.equal(sent.res.status, 202);
  const done = await waitFor(env.request, env.headers, sent.requestId); check('operation', done);
  assert.equal(done.status, 'applied'); assert.equal(done.observed.gear, 3);
  await env.request('/api/v1/commands', { method: 'POST', headers: env.headers, body: JSON.stringify({ requestId: sent.requestId, adapterInstanceId: env.adapter.instance, type: 'gears.shiftUp', args: {}, expectedRevision: null }) });
  assert.equal(env.adapter.gearShifts, 1);
});
test('disabled, inactive, stale, busy, boundary and older adapters cannot shift', async t => {
  const env = await liveSetup(t);
  const doc = () => ({ fresh: true, raw: env.adapter.raw });
  env.adapter.raw.trainer.enabled = false; await env.adapter.write();
  assert.equal((await command(env.request, env.headers, 'gears.shiftUp', {})).body.error.code, 'GEARS_DISABLED');
  env.adapter.raw.trainer.enabled = true; env.adapter.raw.trainer.gear = 4; await env.adapter.write();
  assert.equal((await command(env.request, env.headers, 'gears.shiftUp', {})).body.error.code, 'GEAR_LIMIT');
  env.adapter.raw.isDtOk = false; assert.equal(toTrainer(doc()).shiftReasonCode, 'MOD_INACTIVE');
  assert.equal(toTrainer({ ...doc(), fresh: false }).gear, null);
  delete env.adapter.raw.trainer; assert.equal(toTrainer(doc()).reasonCode, 'CAPABILITY_UNAVAILABLE');
  assert.equal(gearEvidence({}, { requestId: 'mine' }, { snap: { fresh: true, raw: { trainer: { command: { requestId: 'other', status: 'done', gear: 1, mode: 'manual' } } } } }), null);
});
