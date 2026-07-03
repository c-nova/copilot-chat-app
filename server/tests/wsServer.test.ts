import { timingSafeEqualString } from '../src/wsServer';

describe('timingSafeEqualString', () => {
  it('returns true for identical strings', () => {
    expect(timingSafeEqualString('super-secret-token', 'super-secret-token')).toBe(true);
  });

  it('returns false for different strings of the same length', () => {
    expect(timingSafeEqualString('super-secret-token', 'super-secret-tokeX')).toBe(false);
  });

  it('returns false for strings of different lengths', () => {
    expect(timingSafeEqualString('short', 'much-longer-token')).toBe(false);
  });

  it('returns false when compared against an empty string', () => {
    expect(timingSafeEqualString('non-empty', '')).toBe(false);
  });

  it('returns true for two empty strings', () => {
    expect(timingSafeEqualString('', '')).toBe(true);
  });
});
