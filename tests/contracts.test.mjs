// P0 contract checks for the native Android bridge API v1 (proposal).
// Requires the dev dependency `ajv` (JSON Schema 2020-12). Synthetic fixtures only.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import Ajv2020Module from 'ajv/dist/2020.js';

const Ajv2020 = Ajv2020Module.default ?? Ajv2020Module;
const root = dirname(dirname(fileURLToPath(import.meta.url)));
const contracts = join(root, 'bridge', 'contracts', 'v1');
const fixtures = join(root, 'bridge', 'fixtures', 'v1');
const helpCatalog = process.env.GTBIKEV_HELP_CATALOG ?? join(root, '..', 'planning', 'android', 'AYUDA_CONTEXTUAL.md');
const readJson = async path => JSON.parse(await readFile(path, 'utf8'));
const schemaNames = ['common', 'hello', 'capabilities', 'state', 'command', 'operation', 'error', 'routes', 'route-preview', 'settings', 'devices', 'workouts', 'activities', 'map', 'riders', 'mod-config', 'addons', 'progress'];

async function loadAjv() {
  const ajv = new Ajv2020({ strict: true, strictTypes: false, allErrors: true });
  const byName = {};
  for (const name of schemaNames) {
    const schema = await readJson(join(contracts, `${name}.schema.json`));
    ajv.addSchema(schema);
    byName[name] = schema.$id;
  }
  return { ajv, validator: name => ajv.getSchema(byName[name]) };
}

function pointer(path) {
  return path.split('/').slice(1).map(p => p.replace(/~1/g, '/').replace(/~0/g, '~'));
}
function setAt(doc, path, value) {
  const keys = pointer(path); const last = keys.pop();
  const parent = keys.reduce((node, key) => { assert.ok(node && key in node, `missing ${path}`); return node[key]; }, doc);
  parent[last] = value;
}
function removeAt(doc, path) {
  const keys = pointer(path); const last = keys.pop();
  const parent = keys.reduce((node, key) => node[key], doc);
  assert.ok(parent && last in parent, `missing ${path}`);
  if (Array.isArray(parent)) parent.splice(Number(last), 1); else delete parent[last];
}
async function caseDocument(entry) {
  if (entry.file) return readJson(join(fixtures, entry.file));
  const doc = structuredClone(await readJson(join(fixtures, entry.base)));
  for (const [path, value] of Object.entries(entry.set ?? {})) {
    const keys = pointer(path); const last = keys.pop();
    const parent = keys.reduce((node, key) => node[key], doc);
    if (parent && !(last in parent)) parent[last] = value; else setAt(doc, path, value);
  }
  for (const path of entry.remove ?? []) removeAt(doc, path);
  return doc;
}

test('every schema compiles under strict JSON Schema 2020-12', async () => {
  const { validator } = await loadAjv();
  for (const name of schemaNames.filter(n => n !== 'common')) assert.equal(typeof validator(name), 'function', name);
});

test('fixtures match their expected verdict', async () => {
  const { validator } = await loadAjv();
  const index = await readJson(join(fixtures, 'index.json'));
  const listed = new Set(index.cases.filter(c => c.file).map(c => c.file));
  for (const dir of ['valid', 'invalid']) {
    for (const name of await readdir(join(fixtures, dir))) assert.ok(listed.has(`${dir}/${name}`), `fixture not in index: ${dir}/${name}`);
  }
  for (const entry of index.cases) {
    const label = entry.file ?? entry.derived;
    const validate = validator(entry.schema);
    const ok = validate(await caseDocument(entry));
    assert.equal(ok, entry.valid, `${label}: expected ${entry.valid ? 'valid' : 'invalid'}${ok ? '' : ' -> ' + JSON.stringify(validate.errors?.slice(0, 3))}`);
  }
});

test('capability enum is exactly the policy catalog and excludes ERG/workout control', async () => {
  const policy = await readJson(join(contracts, 'policy.json'));
  const capabilities = await readJson(join(contracts, 'capabilities.schema.json'));
  const command = await readJson(join(contracts, 'command.schema.json'));
  const catalogIds = policy.capabilityCatalog.map(c => c.id);
  assert.deepEqual([...capabilities.$defs.capabilityId.enum].sort(), [...catalogIds].sort());
  assert.equal(new Set(catalogIds).size, catalogIds.length, 'duplicate capability id');
  const patterns = policy.forbiddenCommandPatterns.map(p => new RegExp(p, 'i'));
  const commandTypes = command.$defs.commandType.enum;
  for (const id of [...catalogIds, ...commandTypes]) {
    assert.ok(!policy.forbiddenCapabilities.includes(id), `forbidden id allowed: ${id}`);
    for (const p of patterns) assert.ok(!p.test(id), `${id} matches forbidden pattern ${p}`);
  }
  const commandCapabilities = new Set(policy.capabilityCatalog.filter(c => c.commandType).map(c => c.commandType));
  for (const type of commandTypes) assert.ok(commandCapabilities.has(type), `command type without catalog entry: ${type}`);
});

test('helpIds used by the contract exist in the contextual help catalog', async () => {
  const catalog = await readFile(helpCatalog, 'utf8');
  const known = new Set([...catalog.matchAll(/`([a-z][A-Za-z0-9]*(?:\.[a-z][A-Za-z0-9]*)+)`/g)].map(m => m[1]));
  const policy = await readJson(join(contracts, 'policy.json'));
  const used = new Set(policy.capabilityCatalog.map(c => c.helpId));
  for (const name of await readdir(join(fixtures, 'valid'))) {
    JSON.stringify(await readJson(join(fixtures, 'valid', name)), (key, value) => { if (key === 'helpId' && value) used.add(value); return value; });
  }
  for (const id of used) assert.ok(known.has(id), `helpId missing from AYUDA_CONTEXTUAL.md: ${id}`);
});

test('valid state fixtures list each sensor role at most once', async () => {
  for (const name of (await readdir(join(fixtures, 'valid'))).filter(n => n.startsWith('state_'))) {
    const roles = (await readJson(join(fixtures, 'valid', name))).sensors.map(s => s.role);
    assert.equal(new Set(roles).size, roles.length, name);
  }
});
