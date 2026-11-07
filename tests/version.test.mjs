// The bridge reports the release version of package.json in GET /api/v1/hello (and the PC window's status file), so the
// installer, the phone's diagnostics and the git tag all name the same version.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { setup, root } from './support/bridge-v1.mjs';
import { BRIDGE_VERSION } from '../bridge/api-v1/server.mjs';

test('hello reports the package.json version', async t => {
  const { version } = JSON.parse(await readFile(join(root, 'package.json'), 'utf8'));
  assert.match(version, /^\d+\.\d+\.\d+$/);
  assert.equal(BRIDGE_VERSION, version);
  const { request } = await setup(t);
  assert.equal((await (await request('/api/v1/hello')).json()).bridgeVersion, version);
});
