// Static guard: the v1 adapter sources must never reference ERG, target power, workout control or legacy key paths.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const dir = join(root, 'bridge', 'game-adapter-v1');
const forbidden = [
  'ERGMode', 'SetTargetPower', '_ERGtargetPower', 'OnWorkoutSelected', 'WorkoutSelectedHandler',
  'CreateNewActivity', 'PowerAdjust', 'StravaAPI', 'KeyInputManager',
  // P7 saves through saveActivityMenuItem; the summary view it opens (accept/discard -> upload/delete) stays on the PC.
  'activitySummaryView', 'ActivitySummaryView', 'SelectWorkout', 'workoutSelectionView', 'ERG', 'SetTargetPower',
];
// P13 (workouts.select, requested 2026-11-06): only GtbikeV1More.cs may raise the workout selector's own
// OnWorkoutSelected event, the native Select button path. Everything else above stays forbidden there too.
const allowedIn = { 'GtbikeV1More.cs': ['OnWorkoutSelected', 'workoutSelectionView'] };

test('v1 adapter sources do not touch forbidden members', async () => {
  const files = (await readdir(dir)).filter(f => f.endsWith('.cs'));
  assert.ok(files.length >= 2, 'v1 sources found');
  for (const file of files) {
    const code = (await readFile(join(dir, file), 'utf8')).split(/\r?\n/).filter(l => !l.trim().startsWith('//')).join('\n');
    for (const name of forbidden) {
      if (allowedIn[file]?.includes(name)) continue;
      assert.ok(!code.includes(name), `${file} references ${name}`);
    }
  }
});

test('workout selection only raises the selector event, never the mod handler or ERG', async () => {
  const code = (await readFile(join(dir, 'GtbikeV1More.cs'), 'utf8')).split(/\r?\n/).filter(l => !l.trim().startsWith('//')).join('\n');
  assert.match(code, /viewType\.GetField\("OnWorkoutSelected", I\)/);
  assert.match(code, /GtbikeV1Extensions\.RunOnTick\(script,/);
  for (const name of ['WorkoutSelectedHandler', 'ERGMode', 'SetTargetPower', 'PowerAdjust', '"End"', 'CreateNewActivity']) {
    assert.ok(!code.includes(name), `GtbikeV1More.cs references ${name}`);
  }
});

test('v1 adapter dispatches exactly the contract allowlist', async () => {
  const code = await readFile(join(dir, 'GTBikeVCompanionV1.cs'), 'utf8');
  const types = [...code.matchAll(/type == "([a-z][A-Za-z]*\.[A-Za-z]+)"/g)].map(m => m[1]);
  const command = JSON.parse(await readFile(join(root, 'bridge', 'contracts', 'v1', 'command.schema.json'), 'utf8'));
  assert.deepEqual([...new Set(types)].sort(), [...command.$defs.commandType.enum].sort());
});

test('v1 extensions only open the route selector panel', async () => {
  const code = await readFile(join(dir, 'GTBikeVCompanionV1.cs'), 'utf8');
  assert.match(code, /\(Get\(args, "panel"\) as string\) != "routes"/);
  for (const field of ['workoutSelectionView', 'workoutSelectionSubmenu', 'activitySummaryView', 'deviceSelectionView']) {
    const ext = await readFile(join(dir, 'GtbikeV1Extensions.cs'), 'utf8');
    assert.ok(!ext.includes(field), `extensions reference ${field}`);
  }
});
