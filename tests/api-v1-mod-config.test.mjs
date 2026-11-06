// P15 game options: allowlisted GTBikeVConfig.ini edits, only with GTA V closed, with revision check and backup.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { readOptions, patchIni } from '../bridge/api-v1/mod-config.mjs';
import { schemas, setup, pair } from './support/bridge-v1.mjs';

// Same layout GTBike V 0.8.0.4 writes (lowercase sections, "Key = Value", CRLF).
const INI = [
  '[main]', 'ConfigVersion = 80', 'SelectedBike = Tribike', 'DebugWindow = False', 'LogLevel = 3', 'InitialGPSPointLat = -19.10637',
  'InitialGPSPointLong = -169.871', 'WayPointDefaultColor = FFADFF2F', 'WayPointDefaultRadius = 20', 'FineSteeringValue = 0.7',
  'ScreenshotIntervalKm = 5', 'ScreenshotXOffset = 0', 'MenuAutoOpen = True', 'ChangeCarsForBikes = 50,NORMAL',
  'CheeringCrowds = True, 6, 50', 'DefensiveLevel = 1', 'BotDefinition = 2,4,0,0,3', 'ShowRTC = True', 'LastGTAVCrashTime = 0', '',
  '[user]', 'UserWeightKg = 88', '', '[network]', 'TelemetryReporting = True',
  'CourseLibraryURL = https%3A%2F%2Fwww.gtbikev.com%2Ffiles%2Fcourses%2F', 'MultiplayerServerURL = multiplayer.gtbikev.com:4499', '',
  '[keys]', 'MenuKey = F5, ZP_Left_On, ZR_Left_On, WH_Left_Down(L)', '',
].join('\r\n');

async function withConfig(t, text = INI) {
  const dir = await mkdtemp(join(tmpdir(), 'gtbv-modcfg-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const path = join(dir, 'GTBikeVConfig.ini');
  await writeFile(path, text);
  const game = { running: false };
  const ctx = await setup(t, { modConfig: { path, isGameRunning: async () => game.running } });
  const headers = await pair(ctx.request);
  const get = async () => (await ctx.request('/api/v1/mod-config', { headers })).json();
  const post = async body => {
    const res = await ctx.request('/api/v1/mod-config', { method: 'POST', headers, body: JSON.stringify(body) });
    return { status: res.status, body: await res.json() };
  };
  return { ...ctx, path, game, headers, get, post };
}

test('reads every allowlisted option from the file GTBike V writes', () => {
  assert.deepEqual(readOptions(INI), {
    trafficBikesPercent: 50, trafficStyle: 'NORMAL', crowdsEnabled: true, crowdsSlopePercent: 6, crowdsAmount: 50, bots: [2, 4, 0, 0, 3],
    defensiveLevel: 1, waypointColor: 'FFADFF2F', waypointRadius: 20, fineSteering: 0.7, screenshotIntervalKm: 5, screenshotXOffset: 0,
    menuAutoOpen: true, showClock: true, debugWindow: false, logLevel: 3, gpsOriginLat: -19.10637, gpsOriginLon: -169.871, telemetry: true, multiplayer: true,
  });
  assert.equal(readOptions('[network]\nMultiplayerServerURL = NONE\n').multiplayer, false);
  assert.deepEqual(readOptions('[main]\nBotDefinition = \n').bots, []);
  assert.equal(readOptions('[main]\nLogLevel = lots\n').logLevel, null);
  assert.equal(readOptions('').telemetry, null);
});

test('patching keeps every other line, the line endings and the key spelling', () => {
  const out = patchIni(INI, [
    { section: 'main', key: 'changecarsforbikes', text: '75,AGRESSIVE' },
    { section: 'main', key: 'NewKey', text: '1' },
    { section: 'extra', key: 'A', text: 'b' },
  ]);
  assert.match(out, /\r\nChangeCarsForBikes = 75,AGRESSIVE\r\n/);
  assert.match(out, /LastGTAVCrashTime = 0\r\nNewKey = 1\r\n\r\n\[user\]/);
  assert.match(out, /\[extra\]\r\nA = b/);
  const untouched = l => !/ChangeCarsForBikes|NewKey|\[extra\]|^A = b$/.test(l);
  assert.deepEqual(out.split('\r\n').filter(untouched).filter(Boolean), INI.split('\r\n').filter(untouched).filter(Boolean));
});

test('GET returns the options with a revision; the document matches the contract', async t => {
  const { get } = await withConfig(t);
  const doc = await get();
  (await schemas())('mod-config', doc);
  assert.equal(doc.editable, true);
  assert.deepEqual(doc.options.bots, [2, 4, 0, 0, 3]);
  assert.match(doc.revision, /^modcfg-[a-f0-9]{24}$/);
});

test('POST writes only the changed keys, keeps a backup and returns the new revision', async t => {
  const { get, post, path, dir } = await withConfig(t);
  const before = await get();
  const { status, body } = await post({ expectedRevision: before.revision, options: { trafficBikesPercent: 75, bots: [1, 3], multiplayer: false, fineSteering: 0.456, debugWindow: true } });
  assert.equal(status, 200, JSON.stringify(body));
  (await schemas())('mod-config', body);
  assert.notEqual(body.revision, before.revision);
  assert.deepEqual([...body.saved].sort(), ['bots', 'debugWindow', 'fineSteering', 'multiplayer', 'trafficBikesPercent']);
  const text = await readFile(path, 'utf8');
  assert.match(text, /\r\nChangeCarsForBikes = 75,NORMAL\r\n/); // style kept
  assert.match(text, /\r\nBotDefinition = 1,3\r\n/);
  assert.match(text, /\r\nMultiplayerServerURL = NONE\r\n/);
  assert.match(text, /\r\nFineSteeringValue = 0\.46\r\n/);
  assert.match(text, /\r\nDebugWindow = True\r\n/);
  assert.match(text, /\r\nMenuKey = F5, ZP_Left_On, ZR_Left_On, WH_Left_Down\(L\)\r\n/);
  assert.match(text, /\r\nCourseLibraryURL = https%3A%2F%2Fwww\.gtbikev\.com%2Ffiles%2Fcourses%2F\r\n/);
  const backups = await readdir(join(dir, 'bridge-v1', 'mod-config-backups'));
  assert.equal(backups.length, 1);
  assert.equal(await readFile(join(dir, 'bridge-v1', 'mod-config-backups', backups[0]), 'utf8'), INI);
  // Turning multiplayer back on restores the official server, never a client-chosen one.
  const again = await post({ expectedRevision: body.revision, options: { multiplayer: true } });
  assert.equal(again.status, 200);
  assert.match(await readFile(path, 'utf8'), /\r\nMultiplayerServerURL = multiplayer\.gtbikev\.com:4499\r\n/);
});

test('refused while GTA V runs, on a stale revision, for unknown keys and out-of-range values', async t => {
  const { get, post, game, request, headers } = await withConfig(t);
  const doc = await get();
  game.running = true;
  let r = await post({ expectedRevision: doc.revision, options: { logLevel: 4 } });
  assert.equal(r.status, 409);
  assert.equal(r.body.error.code, 'GAME_RUNNING');
  const running = await get();
  assert.equal(running.editable, false);
  assert.equal(running.reasonCode, 'GAME_RUNNING');
  const caps = await (await request('/api/v1/capabilities', { headers })).json();
  assert.equal(caps.capabilities.find(c => c.id === 'modConfig.update').reasonCode, 'GAME_RUNNING');
  assert.equal(caps.capabilities.find(c => c.id === 'modConfig.read').available, true);
  game.running = false;
  r = await post({ expectedRevision: 'modcfg-000000000000000000000000', options: { logLevel: 4 } });
  assert.equal(r.body.error.code, 'REVISION_CONFLICT');
  for (const [options, code, field] of [
    [{ serverUrl: 'evil.example:4499' }, 'INVALID_REQUEST', 'serverUrl'], [{ MenuKey: 'F6' }, 'INVALID_REQUEST', 'MenuKey'],
    [{ logLevel: 9 }, 'VALUE_OUT_OF_RANGE', 'logLevel'], [{ bots: [1, 2, 3, 4, 0, 1] }, 'VALUE_OUT_OF_RANGE', 'bots'],
    [{ trafficStyle: 'CRAZY' }, 'VALUE_OUT_OF_RANGE', 'trafficStyle'], [{ waypointColor: 'red' }, 'VALUE_OUT_OF_RANGE', 'waypointColor'],
    [{ menuAutoOpen: 'yes' }, 'INVALID_REQUEST', 'menuAutoOpen'],
  ]) {
    r = await post({ expectedRevision: doc.revision, options });
    assert.equal(r.body.error.code, code, JSON.stringify(options));
    assert.equal(r.body.error.details.field, field);
  }
});

test('a missing file is reported as unavailable, and the endpoint needs pairing', async t => {
  const { request, path, headers } = await withConfig(t);
  assert.equal((await request('/api/v1/mod-config')).status, 401);
  await rm(path);
  const doc = await (await request('/api/v1/mod-config', { headers })).json();
  (await schemas())('mod-config', doc);
  assert.equal(doc.available, false);
  assert.equal(doc.reasonCode, 'NOT_FOUND');
});
