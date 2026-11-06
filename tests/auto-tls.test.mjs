// Automatic HTTPS: the key is created once and reused, so the fingerprint the phone pins never changes; the
// certificate names the PC's addresses, so the app's address check passes; the QR carries what the app needs.
import test from 'node:test';
import assert from 'node:assert/strict';
import https from 'node:https';
import tls from 'node:tls';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { X509Certificate, createHash } from 'node:crypto';
import { keyFingerprint, loadOrCreateBridgeKey, selfSignedCertificate } from '../bridge/api-v1/auto-tls.mjs';
import { pairingUri, terminalQr } from '../bridge/pairing-qr.mjs';

test('the bridge key is created once; every certificate built from it has the same fingerprint', async t => {
  const dir = await mkdtemp(join(tmpdir(), 'gtbv-tls-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const first = await loadOrCreateBridgeKey(dir);
  const again = await loadOrCreateBridgeKey(dir);
  assert.equal(first.created, true);
  assert.equal(again.created, false);
  assert.equal(again.keyPem, first.keyPem);

  const home = selfSignedCertificate(first.keyPem, ['192.168.1.50', '127.0.0.1']);
  const movedIp = selfSignedCertificate(again.keyPem, ['192.168.1.77', '127.0.0.1']);
  assert.equal(keyFingerprint(home), keyFingerprint(movedIp), 'a new IP keeps the pinned key');
  const x = new X509Certificate(home);
  assert.equal(x.subjectAltName, 'IP Address:192.168.1.50, IP Address:127.0.0.1');
  assert.ok(x.verify(x.publicKey), 'self-signed');
  const spki = x.publicKey.export({ type: 'spki', format: 'der' });
  assert.equal(keyFingerprint(home), createHash('sha256').update(spki).digest('hex'), 'SHA-256 of the SPKI, as the app pins it');
});

test('a TLS client that checks the address accepts the listed IP and refuses any other', async t => {
  const dir = await mkdtemp(join(tmpdir(), 'gtbv-tls-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const { keyPem } = await loadOrCreateBridgeKey(dir);
  const cert = selfSignedCertificate(keyPem, ['127.0.0.1']);
  const server = https.createServer({ key: keyPem, cert, minVersion: 'TLSv1.2' }, (req, res) => res.end('ok'));
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  t.after(() => server.close());
  const get = checkServerIdentity => new Promise(resolve => {
    https.get({ host: '127.0.0.1', port: server.address().port, ca: cert, checkServerIdentity }, res => {
      let body = '';
      res.on('data', c => { body += c; });
      res.on('end', () => resolve(body));
    }).on('error', e => resolve(e.code));
  });
  assert.equal(await get(tls.checkServerIdentity), 'ok');
  assert.equal(await get((host, c) => tls.checkServerIdentity('10.0.0.9', c)), 'ERR_TLS_CERT_ALTNAME_INVALID');
});

test('the pairing QR carries address, port, identity, fingerprint and the one-time pairing key', () => {
  const fingerprint = 'a'.repeat(64);
  const pairingKey = 'Q'.repeat(22);
  const uri = pairingUri({ bridgeId: 'bridge-c016498b8324', hosts: ['192.168.1.27', '10.0.0.5'], port: 8788, fingerprint, pairingKey });
  assert.equal(uri, `gtav-riding-companion://pair?v=1&id=bridge-c016498b8324&h=192.168.1.27,10.0.0.5&p=8788&k=${fingerprint}&s=${pairingKey}`);
  assert.equal(pairingUri({ bridgeId: 'bridge-1', hosts: ['127.0.0.1'], port: 8788, fingerprint: null, pairingKey }), `gtav-riding-companion://pair?v=1&id=bridge-1&h=127.0.0.1&p=8788&s=${pairingKey}`);
  assert.throws(() => pairingUri({ bridgeId: 'bridge-1', hosts: ['pc.example'], port: 8788, fingerprint, pairingKey }));
  assert.throws(() => pairingUri({ bridgeId: 'bridge-1', hosts: ['127.0.0.1'], port: 8788, fingerprint, pairingKey: '123456' }));
  const lines = terminalQr(uri).split('\n');
  assert.ok(lines.length > 20);
  assert.ok(lines.every(l => l.startsWith('\x1b[30;47m') && l.endsWith('\x1b[0m')), 'black on white in every terminal theme');
});
