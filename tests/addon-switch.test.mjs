import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

test('native add-on switches cancel and clean the previous mission before creating the next route', { skip: process.platform !== 'win32' }, () => {
  const script = fileURLToPath(new URL('../bridge/Test-AddonSwitch.ps1', import.meta.url));
  const run = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script], { encoding: 'utf8', timeout: 60000 });
  assert.equal(run.status, 0, `${run.stdout}\n${run.stderr}`);
  assert.match(run.stdout, /Add-on switch harness passed/);
});
