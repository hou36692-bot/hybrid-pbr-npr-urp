#!/usr/bin/env bash
# Helpers for driving the open Unity Editor from Git Bash (needs node on PATH for JSON parsing).
#   Tools/ue.sh eval '<C# body returning a value>' [timeout_s]   run C# in the Editor, print the result
#   Tools/ue.sh recompile                                         recompile scripts, wait, report errors
#   Tools/ue.sh errors                                            console warnings/errors, deduped
set -uo pipefail

PROJECT="${UNITY_PROJECT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
P=(--project-path "$PROJECT" --no-banner --result-only)

# Prints .result (strings raw, everything else as JSON). On failure prints the error and returns 1.
print_result() {
  node -e '
    let raw = "";
    process.stdin.on("data", d => raw += d).on("end", () => {
      let j;
      try { j = JSON.parse(raw); } catch { console.error("unparseable CLI output:\n" + raw); process.exit(1); }
      if (j.success === false || j.error) {
        console.error("ERROR: " + [j.error, j.errorDetails].filter(Boolean).join(": "));
        for (const d of j.diagnostics || []) console.error("  " + (d.message || JSON.stringify(d)));
        process.exit(1);
      }
      const r = j.result;
      console.log(typeof r === "string" ? r : JSON.stringify(r, null, 2));
    });'
}

compile_status() { timeout 30 unity command script recompile_status "${P[@]}" 2>/dev/null || true; }

# A finished compile reports "completed"; once the Editor settles (or when nothing needed
# recompiling) it reports "up_to_date". Both mean "not busy".
compile_done() { grep -qE '"status": *"(completed|up_to_date)"' <<<"$1"; }

case "${1:-}" in
  eval)
    t="${3:-120}"
    timeout $((t + 15)) unity command script eval "$2" "${P[@]}" --timeout "$t" | print_result
    ;;
  recompile)
    timeout 60 unity command script recompile "${P[@]}" >/dev/null 2>&1 || true
    # The status can still read "completed" from the previous run, so first wait (briefly) for the
    # compile to start. If nothing changed it never does, and the second loop returns immediately.
    for _ in $(seq 1 8); do
      compile_done "$(compile_status)" || break
      sleep 1
    done
    for _ in $(seq 1 120); do
      s=$(compile_status)
      if compile_done "$s"; then
        node -e '
          let raw = ""; process.stdin.on("data", d => raw += d).on("end", () => {
            const j = JSON.parse(raw);
            if (j.failed || j.compilationFailed || (j.errors || []).length) {
              console.log("COMPILE FAILED");
              for (const e of j.errors || []) console.log("  " + (typeof e === "string" ? e : JSON.stringify(e)));
              process.exit(1);
            }
            console.log("compile OK");
          });' <<<"$s"
        exit $?
      fi
      sleep 2
    done
    echo "recompile did not finish"; exit 1
    ;;
  errors)
    timeout 60 unity command console get --level warning "${P[@]}" \
      | grep '"message"' | grep -v '_GrabTexture' | sort | uniq -c | sort -rn | head -40 || true
    ;;
  *) echo "usage: $0 eval|recompile|errors"; exit 2 ;;
esac
