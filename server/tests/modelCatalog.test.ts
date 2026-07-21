import { toModelCatalogEntry } from '../src/modelCatalog';

describe('toModelCatalogEntry', () => {
  it('preserves SDK ids and picker metadata without deriving another catalog', () => {
    expect(toModelCatalogEntry({
      id: 'mai-code-1-flash-picker',
      name: 'MAI-Code-1-Flash',
      policy: { state: 'enabled' },
      capabilities: { supports: { vision: true, reasoningEffort: false } },
      billing: { multiplier: 0.25 },
    })).toEqual({
      id: 'mai-code-1-flash-picker',
      name: 'MAI-Code-1-Flash',
      policy: 'enabled',
      supportsVision: true,
      supportsReasoningEffort: false,
      billingMultiplier: 0.25,
    });
  });
});