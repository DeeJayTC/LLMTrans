import { describe, expect, it } from 'vitest';
import { ArgsError, parseArgs } from '../src/args.js';

describe('parseArgs', () => {
  it('parses bridge invocation with flags before the separator', () => {
    const a = parseArgs([
      '--route', 'rt_abc',
      '--endpoint', 'https://llmtrans.example/',
      '--',
      'npx', '-y', '@modelcontextprotocol/server-github',
    ]);
    expect(a.mode).toBe('bridge');
    expect(a.route).toBe('rt_abc');
    expect(a.endpoint).toBe('https://llmtrans.example');
    expect(a.upstreamCmd).toBe('npx');
    expect(a.upstreamArgs).toEqual(['-y', '@modelcontextprotocol/server-github']);
    expect(a.passthrough).toBe(false);
  });

  it('rejects missing route', () => {
    expect(() => parseArgs(['--', 'npx'])).toThrow(ArgsError);
  });

  it('rejects missing upstream command in bridge mode', () => {
    expect(() => parseArgs(['--route', 'rt_x'])).toThrow(ArgsError);
  });

  it('rejects unknown flag', () => {
    expect(() => parseArgs(['--rout', 'x', '--', 'y'])).toThrow(ArgsError);
  });

  it('parses doctor subcommand without an upstream', () => {
    const a = parseArgs(['doctor', '--route', 'rt_abc', '--endpoint', 'https://x/']);
    expect(a.mode).toBe('doctor');
    expect(a.route).toBe('rt_abc');
    expect(a.endpoint).toBe('https://x');
  });

  it('passthrough flag is captured', () => {
    const a = parseArgs(['--route', 'rt', '--passthrough', '--', 'cmd']);
    expect(a.passthrough).toBe(true);
  });

  it('help and version shortcuts return blank args with mode set', () => {
    expect(parseArgs(['--help']).mode).toBe('help');
    expect(parseArgs(['-h']).mode).toBe('help');
    expect(parseArgs(['--version']).mode).toBe('version');
    expect(parseArgs(['-v']).mode).toBe('version');
  });

  it('timeout below 100ms is rejected', () => {
    expect(() => parseArgs(['--route', 'r', '--timeout', '50', '--', 'cmd'])).toThrow(ArgsError);
  });
});
