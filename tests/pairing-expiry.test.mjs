// Pairing lifetime: a phone pairs once and keeps working for 30 days, across bridge restarts. After that the token
// never authorizes anything again, but the bridge still recognises it so the console can show a new code.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { NativePairing, TOKEN_LIFETIME_DAYS } from '../bridge/api-v1/auth.mjs';
import { createBridgeV1 } from '../bridge/api-v1/server.mjs';

const DAY = 24 * 60 * 60_000;

async function tempDir(t) {
  const dir = await mkdtemp(join(tmpdir(), 'gtbv-pairing-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  return dir;
}

test('a token lasts 30 days, survives a restart, then is refused but still recognised', async t => {
  const dir = await tempDir(t);
  let clock = Date.UTC(2026, 10, 5);
  const pairing = new NativePairing({ stateDirectory: dir, now: () => clock, pin: '135790' });
  const paired = await pairing.pair('192.168.1.20', '135790', 'Pixel 8');
  assert.equal(TOKEN_LIFETIME_DAYS, 30);
  assert.equal(paired.expiresAtUtc, new Date(clock + 30 * DAY).toISOString());
  const header = `Bearer ${paired.token}`;

  clock += 29 * DAY;
  const restarted = new NativePairing({ stateDirectory: dir, now: () => clock });
  await restarted.load();
  assert.ok(restarted.verify(header), 'still valid on day 29 after a restart');
  assert.equal(restarted.expired(header), null);
  assert.deepEqual(restarted.activeClients().map(c => c.clientName), ['Pixel 8']);

  clock += 2 * DAY;
  assert.equal(restarted.verify(header), null, 'refused on day 31');
  assert.deepEqual(restarted.expired(header), { clientName: 'Pixel 8', expiresAtUtc: paired.expiresAtUtc });
  assert.deepEqual(restarted.activeClients(), []);
  assert.equal(restarted.expired('Bearer ' + 'x'.repeat(43)), null, 'an unknown token is not reported as expired');

  clock += 91 * DAY;
  const later = new NativePairing({ stateDirectory: dir, now: () => clock });
  await later.load();
  assert.equal(later.expired(header), null, 'forgotten 90 days after expiry');
});

test('scanning the QR pairs with its one-time key; the key and the PIN both change after any pairing', async t => {
  const dir = await tempDir(t);
  const paired = [];
  const bridge = await createBridgeV1({ adapterDirectory: join(dir, 'v1'), stateDirectory: join(dir, 'bridge-v1'), onPaired: p => paired.push(p) });
  await new Promise(r => bridge.server.listen(0, '127.0.0.1', r));
  t.after(() => bridge.close());
  const pair = body => fetch(`http://127.0.0.1:${bridge.server.address().port}/api/v1/pair`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

  const key = bridge.pairing.currentPairingKey();
  const pin = bridge.pairing.currentPin();
  assert.match(key, /^[A-Za-z0-9_-]{22}$/, '128-bit key, base64url');
  assert.equal((await pair({ pairingKey: key, pin, clientName: 'Pixel 8' })).status, 422, 'never both');
  assert.equal((await pair({ pairingKey: 'x'.repeat(22), clientName: 'Pixel 8' })).status, 401);

  const ok = await pair({ pairingKey: key, clientName: 'Pixel 8' });
  assert.equal(ok.status, 200);
  assert.match((await ok.json()).token, /^[A-Za-z0-9_-]{43}$/);
  assert.deepEqual(paired.map(p => [p.clientName, p.viaQr]), [['Pixel 8', true]]);
  assert.notEqual(bridge.pairing.currentPairingKey(), key, 'one QR, one phone');
  assert.notEqual(bridge.pairing.currentPin(), pin);
  const reused = await pair({ pairingKey: key, clientName: 'Other phone' });
  assert.equal((await reused.json()).error.code, 'PAIRING_PIN_INVALID', 'a used QR no longer pairs');
});

test('the bridge answers AUTH_REQUIRED for an expired token and tells the console which phone it was', async t => {
  const dir = await tempDir(t);
  let clock = Date.now();
  const notices = [];
  const bridge = await createBridgeV1({ adapterDirectory: join(dir, 'v1'), stateDirectory: join(dir, 'bridge-v1'), pin: '246810',
    now: () => clock, onPairingExpired: notice => notices.push(notice) });
  await new Promise(r => bridge.server.listen(0, '127.0.0.1', r));
  t.after(() => bridge.close());
  const base = `http://127.0.0.1:${bridge.server.address().port}`;

  const pairRes = await fetch(`${base}/api/v1/pair`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ pin: '246810', clientName: 'Pixel 8' }) });
  const { token } = await pairRes.json();
  const state = () => fetch(`${base}/api/v1/state`, { headers: { Authorization: `Bearer ${token}` } });

  assert.notEqual((await state()).status, 401);
  assert.deepEqual(notices, []);

  clock += 31 * DAY;
  const refused = await state();
  assert.equal(refused.status, 401);
  assert.equal((await refused.json()).error.code, 'AUTH_REQUIRED', 'same code as before: older apps keep working');
  assert.equal(notices.length, 1);
  assert.equal(notices[0].clientName, 'Pixel 8');

  const stranger = await fetch(`${base}/api/v1/state`, { headers: { Authorization: 'Bearer ' + 'y'.repeat(43) } });
  assert.equal(stranger.status, 401);
  assert.equal(notices.length, 1, 'an unknown token never triggers the console notice');
});
