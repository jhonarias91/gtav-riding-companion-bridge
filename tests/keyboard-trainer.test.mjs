import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { toState } from '../bridge/api-v1/normalize.mjs';
import { schemas } from './support/bridge-v1.mjs';

const input = { mode: 'keyboard', targetPowerW: 200, cadenceRpm: 85, pedalling: true };
function sample(testInput = input) {
  return { fresh: true, ageMs: 0, raw: {
    adapterInstanceId: 'adapter-test01', sequence: 1, capturedAtUtc: '2026-11-08T12:00:00Z', modLoaded: true, isDtOk: true,
    binding: 'compatible', bindingProfile: 'gtbikev-0804', modVersion: '0.8.0.4', modLayer: 'dataReady', gameControl: 'inControl',
    receivers: { ant: 'uninitialized', ble: 'uninitialized' }, sport: 'cycling', sessionGeneration: 1,
    metrics: { powerW: 200, speedMps: 5, speedVirtual: true, cadenceRpm: 85, heartRateBpm: null }, sensors: [], testInput,
  } };
}

test('keyboard input is explicit, virtual, and creates no physical sensor or heart rate', async () => {
  const state = toState(sample());
  (await schemas())('state', state);
  assert.deepEqual(state.testInput, input);
  for (const key of ['powerW', 'speedMps', 'cadenceRpm']) assert.equal(state.metrics[key].source, 'virtual');
  assert.deepEqual(state.sensors, []);
  assert.equal(state.layers.bleReceiver, 'uninitialized');
  assert.equal(state.metrics.heartRateBpm.reasonCode, 'SENSOR_MISSING');
  assert.equal(toState(sample(null)).testInput, null);
  const older = sample(); delete older.raw.testInput;
  assert.equal(toState(older).testInput, null);
});

test('stale simulated values keep their label; invalid metadata cannot expose live synthetic power', async () => {
  const current = toState(sample());
  const late = sample(null); late.fresh = false; late.ageMs = 10000;
  const stale = toState(late, current);
  (await schemas())('state', stale);
  assert.deepEqual(stale.testInput, input);
  assert.equal(stale.metrics.powerW.quality, 'stale');
  assert.equal(stale.metrics.powerW.source, 'virtual');
  assert.equal(toState(null, current).testInput, null);
  for (const bad of [{ ...input, targetPowerW: 0 }, { ...input, mode: 'mystery' }, { ...input, pedalling: 1 }]) {
    const doc = toState(sample(bad));
    assert.equal(doc.metrics.powerW.value, null);
    assert.equal(doc.metrics.cadenceRpm.quality, 'unavailable');
  }
});

const nativePhysics = new URL('../../reference/GTBikeV-0.8.0.4-decompiled/GTBikeV/VirtualTrainer.cs', import.meta.url);
test('native keyboard trainer harness restores state and survives native polling', { skip: process.platform !== 'win32' || !existsSync(nativePhysics) }, () => {
  const script = fileURLToPath(new URL('../bridge/Test-KeyboardTrainer.ps1', import.meta.url));
  const run = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script], { encoding: 'utf8', timeout: 60000 });
  assert.equal(run.status, 0, `${run.stdout}\n${run.stderr}`);
  assert.match(run.stdout, /Keyboard trainer harness passed/);
});
