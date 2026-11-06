// Pairing lifetime: a phone pairs once and keeps working for 30 days, across bridge restarts. After that the token
// never authorizes anything again, but the bridge still recognises it so the console can show a new code.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
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

test('re-pairing the same client ID replaces its token, while another phone of the same model stays paired', async t => {
  const dir = await tempDir(t);
  const bridge = await createBridgeV1({ adapterDirectory: join(dir, 'v1'), stateDirectory: join(dir, 'bridge-v1') });
  await new Promise(r => bridge.server.listen(0, '127.0.0.1', r));
  t.after(() => bridge.close());
  const base = `http://127.0.0.1:${bridge.server.address().port}`;
  const scan = async (clientId, clientName = 'Pixel 8') => {
    const res = await fetch(`${base}/api/v1/pair`, { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pairingKey: bridge.pairing.currentPairingKey(), clientName, clientId }) });
    assert.equal(res.status, 200);
    return (await res.json()).token;
  };
  const id1 = '1'.repeat(32);
  const id2 = '2'.repeat(32);
  const first = await scan(id1);
  const other = await scan(id2);
  assert.equal(bridge.pairing.activeClients().length, 2);
  const replacement = await scan(id1, 'Renamed Pixel');
  assert.equal(bridge.pairing.activeClients().length, 2);
  assert.equal(bridge.pairing.verify(`Bearer ${first}`), null, 'old token is revoked');
  assert.ok(bridge.pairing.verify(`Bearer ${other}`), 'other phone of the same model stays paired');
  assert.ok(bridge.pairing.verify(`Bearer ${replacement}`));
  const invalid = await fetch(`${base}/api/v1/pair`, { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pairingKey: bridge.pairing.currentPairingKey(), clientName: 'Pixel 8', clientId: 'short' }) });
  assert.equal(invalid.status, 422);
  const restarted = new NativePairing({ stateDirectory: join(dir, 'bridge-v1') });
  await restarted.load();
  assert.equal(restarted.activeClients().length, 2, 'IDs survive a restart');
});

test('legacy model-only duplicates collapse, and a new identified pairing replaces their last token', async t => {
  const dir = await tempDir(t);
  const base = Date.UTC(2026, 10, 5);
  const tokens = ['a', 'b', 'c'].map((value, index) => ({
    hash: createHash('sha256').update(value.repeat(43)).digest('hex'),
    clientName: 'SM-S908E', expiresAt: base + (index + 1) * DAY,
  }));
  await writeFile(join(dir, 'tokens.json'), JSON.stringify({ tokens }));
  const pairing = new NativePairing({ stateDirectory: dir, now: () => base, pin: '135790' });
  await pairing.load();
  assert.equal(pairing.activeClients().length, 1);
  assert.equal(pairing.verify(`Bearer ${'a'.repeat(43)}`), null);
  assert.ok(pairing.verify(`Bearer ${'c'.repeat(43)}`), 'the most recent legacy token survives migration');
  const newPair = await pairing.pair('192.168.1.20', '135790', 'SM-S908E', null, 'd'.repeat(32));
  assert.equal(pairing.activeClients().length, 1);
  assert.equal(pairing.verify(`Bearer ${'c'.repeat(43)}`), null);
  assert.ok(pairing.verify(`Bearer ${newPair.token}`));
});

test('repeated QR scans from an older app do not increase the phone count', async t => {
  const dir = await tempDir(t);
  const pairing = new NativePairing({ stateDirectory: dir });
  for (let scan = 0; scan < 8; scan++) {
    const previous = pairing.activeClients().length;
    const token = await pairing.pair('192.168.1.20', null, 'SM-S908E', pairing.currentPairingKey());
    assert.ok(token.token);
    assert.equal(pairing.activeClients().length, 1);
    if (scan > 0) assert.equal(previous, 1);
  }
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
