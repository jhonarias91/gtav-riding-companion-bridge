// Runs the real .NET Framework persistence/editor code on Windows, with no game and no user Documents writes.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { execFile } from 'node:child_process';

const exec = promisify(execFile);
const root = fileURLToPath(new URL('../', import.meta.url));

test('Windows routes: select/drag waypoints, create routes, preserve copies and publish', { skip: process.platform !== 'win32', timeout: 60000 }, async t => {
  const dir = await mkdtemp(join(tmpdir(), 'companion-desktop-routes-'));
  t.after(async () => {
    assert.ok(resolve(dir).startsWith(resolve(tmpdir()) + '\\companion-desktop-routes-'));
    await rm(dir, { recursive: true, force: true });
  });
  const buildInfo = join(dir, 'BuildInfo.cs');
  await writeFile(buildInfo, 'namespace GTAVRidingCompanion { static class BuildInfo { internal const string Version = "test"; } }');
  const sources = (await readdir(join(root, 'installer/src'))).filter(f => f.endsWith('.cs')).map(f => join(root, 'installer/src', f));
  const exe = join(dir, 'DesktopRoutesCheck.exe');
  const compiler = join(process.env.WINDIR, 'Microsoft.NET/Framework64/v4.0.30319/csc.exe');
  const refs = ['System.Core', 'System.Windows.Forms', 'System.Drawing', 'System.Web.Extensions', 'System.IO.Compression', 'System.IO.Compression.FileSystem'];
  const compiled = await exec(compiler, ['/nologo', '/warnaserror+', '/target:exe', '/platform:x64', '/main:DesktopRoutesCheck', `/out:${exe}`,
    ...refs.map(r => `/reference:${r}.dll`), ...sources, buildInfo, join(root, 'tests/DesktopRoutesCheck.cs')], { timeout: 30000 });
  assert.equal(compiled.stderr, '');
  const result = await exec(exe, [join(dir, 'ModSettings')], { timeout: 30000 });
  assert.match(result.stdout, /checks passed/);
  assert.equal(result.stderr, '');
  t.diagnostic(result.stdout.trim());
});
