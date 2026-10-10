// Keeps Remotion's temp files off C:. Load it FIRST in every script that bundles or renders
// (import "./remotion-tmp.cjs" or require("./remotion-tmp.cjs")); remotion.config.ts imports it
// for the CLI and Studio.
//
// Remotion has no temp setting of its own: webpack bundles (a full copy of public/), asset copies
// of every video a render reads, and Chrome profiles all go to os.tmpdir(), and on Windows it
// leaves them behind. On 2026-10-10 they filled C: (over 250 GB in %TEMP%). This points TEMP at a
// per-run folder under B:\Temp\remotion (outside the Windows Search index) and removes that folder
// when the process ends. Folders of runs that died without cleaning up are removed on the next run.
// REMOTION_TMP_ROOT overrides the root. No effect where that drive does not exist (Mac, servers).
// Same helper as B:\Coding\Lyrics\tools\remotion-tmp.mjs.
const { existsSync, mkdirSync, readdirSync, rmSync, statSync } = require("node:fs");
const path = require("node:path");

const ROOT = process.env.REMOTION_TMP_ROOT || "B:/Temp/remotion";

if (process.platform === "win32" && existsSync(path.parse(path.resolve(ROOT)).root) && !process.env.REMOTION_TMP_RUN) {
  mkdirSync(ROOT, { recursive: true });
  const alive = (pid) => {
    try {
      process.kill(pid, 0);
      return true;
    } catch (e) {
      return e.code === "EPERM";
    }
  };
  for (const name of readdirSync(ROOT)) {
    const m = /^run-(\d+)$/.exec(name);
    const dir = path.join(ROOT, name);
    if (m && !alive(Number(m[1])) && Date.now() - statSync(dir).mtimeMs > 3600e3) rmSync(dir, { recursive: true, force: true });
  }
  const run = path.join(ROOT, `run-${process.pid}`);
  mkdirSync(run, { recursive: true });
  process.env.TEMP = process.env.TMP = process.env.TMPDIR = process.env.REMOTION_TMP_RUN = run;
  process.on("exit", () => {
    try {
      rmSync(run, { recursive: true, force: true, maxRetries: 5, retryDelay: 300 });
    } catch {}
  });
  for (const sig of ["SIGINT", "SIGTERM", "SIGBREAK"]) process.once(sig, () => process.exit(130));
}
module.exports = { RUN_TMP: process.env.REMOTION_TMP_RUN || null };
