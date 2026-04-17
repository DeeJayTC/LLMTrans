import { spawn } from 'node:child_process';
import { ArgsError, helpText, parseArgs } from './args.js';
import { runBridge } from './bridge.js';
import { formatDoctorResult, runDoctor } from './doctor.js';
import { TranslateClient } from './translate.js';
import type { BridgeArgs } from './types.js';

const VERSION = '0.1.0';

async function main(argv: readonly string[]): Promise<number> {
  let args: BridgeArgs;
  try {
    args = parseArgs(argv);
  } catch (err) {
    if (err instanceof ArgsError) {
      process.stderr.write(`llmtrans-mcp-bridge: ${err.message}\n\n${helpText}\n`);
      return 2;
    }
    throw err;
  }

  if (args.mode === 'help') {
    process.stdout.write(helpText + '\n');
    return 0;
  }
  if (args.mode === 'version') {
    process.stdout.write(`${VERSION}\n`);
    return 0;
  }

  if (args.mode === 'doctor') {
    const result = await runDoctor(args);
    process.stdout.write(formatDoctorResult(result) + '\n');
    return result.ok ? 0 : 1;
  }

  const translator = new TranslateClient({
    endpoint: args.endpoint,
    route: args.route,
    timeoutMs: args.requestTimeoutMs,
  });

  const exit = await runBridge(args, {
    clientIn: process.stdin,
    clientOut: process.stdout,
    stderr: process.stderr,
    translator,
    spawnUpstream: (cmd, spawnedArgs) =>
      spawn(cmd, spawnedArgs, {
        stdio: ['pipe', 'pipe', 'pipe'],
        env: process.env,
      }),
  });

  return exit;
}

main(process.argv.slice(2)).then(
  (code) => process.exit(code),
  (err) => {
    process.stderr.write(`llmtrans-mcp-bridge fatal: ${err instanceof Error ? err.stack ?? err.message : String(err)}\n`);
    process.exit(70);
  },
);
