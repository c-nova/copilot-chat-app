import { formatToolDetail, summarizeToolArguments, truncate } from '../src/copilotRunner';

describe('summarizeToolArguments', () => {
  it('returns undefined for non-object arguments', () => {
    expect(summarizeToolArguments(undefined)).toBeUndefined();
    expect(summarizeToolArguments(null)).toBeUndefined();
    expect(summarizeToolArguments('not an object')).toBeUndefined();
  });

  it('prefers well-known keys like "command" over other string fields', () => {
    const result = summarizeToolArguments({ command: 'ls -la', other: 'ignored' });
    expect(result).toBe('ls -la');
  });

  it('falls back to any string value when no preferred key matches', () => {
    const result = summarizeToolArguments({ someField: 'fallback value' });
    expect(result).toBe('fallback value');
  });

  it('returns undefined when there is no usable string value', () => {
    expect(summarizeToolArguments({ count: 42, flag: true })).toBeUndefined();
  });

  it('collapses whitespace and truncates long values', () => {
    const longValue = 'a'.repeat(200);
    const result = summarizeToolArguments({ command: longValue });
    expect(result?.length).toBeLessThanOrEqual(140);
    expect(result?.endsWith('…')).toBe(true);
  });

  it('never surfaces values from keys that look like secrets', () => {
    expect(summarizeToolArguments({ authorization: 'Bearer abc123' })).toBeUndefined();
    expect(summarizeToolArguments({ apiKey: 'abc123', other: 'safe value' })).toBe('safe value');
  });
});

describe('truncate', () => {
  it('leaves short text untouched', () => {
    expect(truncate('hello world')).toBe('hello world');
  });

  it('collapses internal whitespace/newlines to single spaces', () => {
    expect(truncate('hello\n\n  world')).toBe('hello world');
  });

  it('truncates and appends an ellipsis when text exceeds max length', () => {
    const result = truncate('0123456789', 5);
    expect(result).toBe('0123…');
    expect(result.length).toBe(5);
  });
});

describe('formatToolDetail', () => {
  it('returns undefined for empty or non-object arguments', () => {
    expect(formatToolDetail(undefined)).toBeUndefined();
    expect(formatToolDetail({})).toBeUndefined();
  });

  it('pretty-prints the arguments as JSON', () => {
    const result = formatToolDetail({ path: 'foo.txt' });
    expect(result).toContain('"path": "foo.txt"');
  });

  it('truncates very large payloads with a marker', () => {
    const big = { data: 'x'.repeat(5000) };
    const result = formatToolDetail(big, 100);
    expect(result?.length).toBeLessThanOrEqual(100 + '\n…(truncated)'.length);
    expect(result?.endsWith('…(truncated)')).toBe(true);
  });

  it('redacts values whose key looks like a secret, at any nesting depth', () => {
    const result = formatToolDetail({
      url: 'https://example.com',
      headers: { Authorization: 'Bearer super-secret-token', 'X-Api-Key': 'abc123' },
      password: 'hunter2',
    });
    expect(result).toContain('"url": "https://example.com"');
    expect(result).not.toContain('super-secret-token');
    expect(result).not.toContain('abc123');
    expect(result).not.toContain('hunter2');
    expect(result).toContain('***REDACTED***');
  });
});
