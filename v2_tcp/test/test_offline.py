"""Offline test of the v2 bridge: real tanques4.dll driven by FakeSolver.exe (no APS needed).

Checks:
  1. every accepted step is emitted exactly once, with the CONVERGED values (no Newton iterates);
  2. the rejected step is not emitted and produces a 'rewind' event;
  3. commands are acknowledged with the time and reason of application, and the applied flows
     appear in the samples;
  4. sequence numbers have no gaps and time increases within each run;
  5. throughput (samples per second of wall time).

Usage (from tanques4_v2/test):  python test_offline.py [steps]
"""
import csv
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "python"))
from aveva_bridge_v2 import BridgeV2, validate  # noqa: E402

STEPS = int(sys.argv[1]) if len(sys.argv) > 1 else 200
TOL = 1e-12


def wait_line(proc, token):
    while True:
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError(f"harness ended before {token}")
        if line.strip() == token:
            return


def main():
    proc = subprocess.Popen([str(HERE / "FakeSolver.exe"), str(STEPS)], cwd=HERE,
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
    wait_line(proc, "READY")
    br = BridgeV2().connect()
    print("hello:", br.hello.get("version"))

    msgs = []
    id1 = br.set_inputs(0.08, 0.06)                 # applied at the first evaluation ("start")
    t0 = time.perf_counter()
    proc.stdin.write("\n"); proc.stdin.flush()
    wait_line(proc, "IDLE1")
    t1 = time.perf_counter()
    br.flush(keep=msgs)
    msgs += br.drain()
    n1 = sum(m.get("type") == "sample" for m in msgs)

    id2 = br.set_inputs(q1=0.03)                    # applied at the revert ("rewind")
    proc.stdin.write("\n"); proc.stdin.flush()
    wait_line(proc, "IDLE2")
    br.flush(keep=msgs)
    msgs += br.drain()
    status = br.status()
    proc.stdin.write("\n"); proc.stdin.flush()
    wait_line(proc, "DONE")
    proc.wait(timeout=10)
    br.close()

    samples = [m for m in msgs if m.get("type") == "sample"]
    acks = {m["id"]: m for m in msgs if m.get("type") == "ack"}
    rewinds = [m for m in msgs if m.get("type") == "rewind"]
    with open(HERE / "expected.csv", newline="") as f:
        exp = [{k: float(v) for k, v in r.items()} for r in csv.DictReader(f)]

    fails = []
    # 1. one sample per accepted step, converged values
    if len(samples) != len(exp):
        fails.append(f"{len(samples)} samples vs {len(exp)} accepted steps")
    for s, e in zip(samples, exp):
        for k in ("t", "q1", "q2", "h1", "h2", "h3", "h4"):
            if abs(s[k] - e[k]) > TOL:
                fails.append(f"seq {s['seq']}: {k} = {s[k]} != {e[k]}")
                break
    # 2. rejected step -> rewind; revert -> rewind
    if len(rewinds) != 2:
        fails.append(f"expected 2 rewinds, got {len(rewinds)}: {rewinds}")
    # 3. acknowledgements and applied flows
    a1, a2 = acks.get(id1), acks.get(id2)
    if not a1 or a1["reason"] != "start" or a1["t"] != 0:
        fails.append(f"ack 1 unexpected: {a1}")
    if not a2 or a2["reason"] != "rewind" or a2["t"] != 0:
        fails.append(f"ack 2 unexpected: {a2}")
    if abs(samples[0]["q1"] - 0.08) > TOL or abs(samples[0]["q2"] - 0.06) > TOL:
        fails.append(f"first sample flows {samples[0]['q1']}, {samples[0]['q2']}")
    if abs(samples[n1]["q1"] - 0.03) > TOL or abs(samples[n1]["q2"] - 0.06) > TOL:
        fails.append(f"first sample after revert flows {samples[n1]['q1']}, {samples[n1]['q2']}")
    # 4. integrity per run
    for name, part in (("run 1", samples[:n1]), ("run 2", samples[n1:])):
        rep = validate(part)
        if rep["n_non_increasing_t"]:
            fails.append(f"{name}: time not increasing {rep['non_increasing_t']}")
    seqs = [s["seq"] for s in samples]
    if seqs != list(range(seqs[0], seqs[0] + len(seqs))):
        fails.append("sequence gaps")

    evals = [s["evals"] for s in samples]
    print(f"samples run 1 / run 2 : {n1} / {len(samples) - n1}  (accepted steps: {len(exp)})")
    print(f"residual evals/step    : mean {sum(evals) / len(evals):.1f}  (v1 would send each of them)")
    print(f"rewinds                : {[(round(r['from'], 3), round(r['to'], 3)) for r in rewinds]}")
    print(f"acks                   : {a1['reason']}@t={a1['t']}, {a2['reason']}@t={a2['t']}")
    print(f"throughput run 1       : {n1 / (t1 - t0):.0f} samples/s")
    print(f"bridge status          : sent={status['sent']} dropped={status['dropped']} "
          f"last_error='{status['last_error']}'")
    print("RESULT:", "PASS" if not fails else "FAIL")
    for f_ in fails[:15]:
        print("  -", f_)
    return 0 if not fails else 1


if __name__ == "__main__":
    raise SystemExit(main())
