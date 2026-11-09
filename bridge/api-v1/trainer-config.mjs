// GTBikeV 0.8.0.4 [devices] VirtualGears: one native mode token followed by ratios.
// Persistent edits share the mod-config revision, backup, closed-game guard and write lock.
import { ModConfigStore, parseIni } from './mod-config.mjs';

export const DEFAULT_GEAR_RATIOS = Object.freeze([0.75, 0.87, 0.99, 1.11, 1.23, 1.38, 1.53, 1.68, 1.86, 2.04, 2.22, 2.4, 2.61, 2.82, 3.03, 3.24, 3.49, 3.74, 3.99, 4.24, 4.54, 4.84, 5.14, 5.49]);
export const TRAINER_DEFAULTS = Object.freeze({ enabled: false, ratios: DEFAULT_GEAR_RATIOS });

export function validGearRatios(value) {
  return Array.isArray(value) && value.length >= 1 && value.length <= 64
    && value.every((n, i) => typeof n === 'number' && Number.isFinite(n) && n >= 0.1 && n <= 20
      && Math.abs(n * 1000 - Math.round(n * 1000)) < 1e-7 && (i === 0 || n > value[i - 1]));
}
const MODE = { true: 'manual', manual: 'manual', false: 'none', none: 'none', auto: 'auto', native: 'native' };
export function parseVirtualGears(value) {
  if (typeof value !== 'string') return { mode: null, enabled: null, ratios: null };
  const [token, ...parts] = value.split(',').map(s => s.trim());
  const mode = Object.hasOwn(MODE, token.toLowerCase()) ? MODE[token.toLowerCase()] : null;
  const ratios = parts.every(s => /^\d+(?:\.\d+)?$/.test(s)) ? parts.map(Number).sort((a, b) => a - b) : [];
  return { mode, enabled: mode === null ? null : mode !== 'none', ratios: validGearRatios(ratios) ? ratios : null };
}
const rawValue = text => text === null ? null : parseIni(text).sections.get('devices')?.keys.get('virtualgears')?.value;
const definitions = {
  enabled: { section: 'devices', key: 'VirtualGears', check: v => typeof v === 'boolean' ? v : undefined, read: s => parseVirtualGears(s).enabled ?? undefined },
  ratios: { section: 'devices', key: 'VirtualGears', check: v => validGearRatios(v) ? [...v] : undefined, read: s => parseVirtualGears(s).ratios ?? undefined },
};

export class TrainerConfigStore extends ModConfigStore {
  constructor(options) {
    super({ ...options, definitions, defaults: TRAINER_DEFAULTS, resource: 'trainerConfig',
      extra: text => ({ nativeMode: parseVirtualGears(rawValue(text)).mode }),
      serialize(values, touched, text) {
        const previous = parseVirtualGears(rawValue(text));
        // Editing ratios preserves Auto/Native. Explicit on/off uses the documented True/False alias.
        const mode = touched.includes('enabled') ? (values.enabled ? 'True' : 'False')
          : ({ none: 'False', manual: 'True', auto: 'Auto', native: 'Native' }[previous.mode] ?? (values.enabled ? 'True' : 'False'));
        return [{ section: 'devices', key: 'VirtualGears', text: `${mode}, ${values.ratios.join(', ')}` }];
      },
    });
  }
}
