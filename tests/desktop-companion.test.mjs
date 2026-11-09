import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { execFile } from 'node:child_process';

const exec = promisify(execFile);
const root = fileURLToPath(new URL('../', import.meta.url));
test('Windows shows observed camera controls and labels simulated, stale and missing ride data', { skip: process.platform !== 'win32', timeout: 60000 }, async t => {
  const dir = await mkdtemp(join(tmpdir(), 'companion-desktop-ui-'));
  t.after(async () => {
    assert.ok(resolve(dir).startsWith(resolve(tmpdir()) + '\\companion-desktop-ui-'));
    await rm(dir, { recursive: true, force: true });
  });
  const info = join(dir, 'BuildInfo.cs');
  await writeFile(info, 'namespace GTAVRidingCompanion { static class BuildInfo { internal const string Version = "test"; } }');
  const sources = (await readdir(join(root, 'installer/src'))).filter(f => f.endsWith('.cs')).map(f => join(root, 'installer/src', f));
  const exe = join(dir, 'DesktopCompanionCheck.exe');
  const refs = ['System.Core', 'System.Windows.Forms', 'System.Drawing', 'System.Web.Extensions', 'System.IO.Compression', 'System.IO.Compression.FileSystem'];
  await exec(join(process.env.WINDIR, 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'), ['/nologo', '/warnaserror+', '/target:exe', '/platform:x64', '/main:DesktopCompanionCheck', `/out:${exe}`,
    ...refs.map(r => `/reference:${r}.dll`), ...sources, info, join(root, 'tests/DesktopCompanionCheck.cs')], { timeout: 30000 });
  const images = join(root, 'outputs/ui-keyboard'); await mkdir(images, { recursive: true });
  const result = await exec(exe, [images], { timeout: 30000 });
  assert.match(result.stdout, /Desktop companion checks passed/);
  assert.equal(result.stderr, '');
  t.diagnostic(result.stdout.trim());
});
