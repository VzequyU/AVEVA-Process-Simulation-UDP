"""Python side of the tanques4 bridge, version 2 (TCP, step-synchronous).

BridgeV2      : TCP client for the DLL (commands, acknowledgements, samples).
AVEVAEngineV2 : same role as aveva_engine.AVEVAEngine (snapshot + RunDynamics through
                simcentralconnect), with the data path through BridgeV2.
validate      : integrity checks of a list of samples (sequence gaps, time monotonicity).

Protocol: one JSON object per line. See EquationSet.cs for the message list.
"""
from __future__ import annotations

import json
import queue
import socket
import threading
import time
from typing import Dict, List, Optional


class BridgeError(RuntimeError):
    pass


class BridgeV2:
    def __init__(self, host: str = "127.0.0.1", port: int = 5006, timeout: float = 5.0):
        self.host, self.port, self.timeout = host, port, timeout
        self.sock: Optional[socket.socket] = None
        self.inbox: "queue.Queue[dict]" = queue.Queue()
        self.hello: Optional[dict] = None
        self._next_id = 0
        self._reader: Optional[threading.Thread] = None
        self._closed = threading.Event()
        self.bad_lines = 0
        self._held: List[dict] = []   # messages skipped by wait_for; returned by drain()

    # ------------------------------------------------------------ connection
    def connect(self, retries: int = 50, wait: float = 0.1) -> "BridgeV2":
        last = None
        for _ in range(retries):
            try:
                self.sock = socket.create_connection((self.host, self.port), timeout=self.timeout)
                break
            except OSError as exc:
                last = exc
                time.sleep(wait)
        if self.sock is None:
            raise BridgeError(f"cannot connect to {self.host}:{self.port}: {last}")
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.sock.settimeout(None)
        self._closed.clear()
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()
        self.hello = self.wait_for("hello", timeout=self.timeout)
        return self

    def close(self) -> None:
        self._closed.set()
        if self.sock is not None:
            try:
                self.sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            self.sock.close()
            self.sock = None

    def _read_loop(self) -> None:
        buf = b""
        try:
            while not self._closed.is_set():
                chunk = self.sock.recv(1 << 16)
                if not chunk:
                    break
                buf += chunk
                *lines, buf = buf.split(b"\n")
                for line in lines:
                    if not line.strip():
                        continue
                    try:
                        msg = json.loads(line)
                    except json.JSONDecodeError:
                        self.bad_lines += 1
                        continue
                    msg["_rx"] = time.perf_counter()
                    self.inbox.put(msg)
        except OSError:
            pass
        finally:
            self.inbox.put({"type": "_disconnected"})

    # ------------------------------------------------------------ messaging
    def send(self, obj: dict) -> None:
        if self.sock is None:
            raise BridgeError("not connected")
        self.sock.sendall((json.dumps(obj) + "\n").encode("utf-8"))

    def set_values(self, apply: str = "step", confirm: bool = True,
                   keep: Optional[List[dict]] = None, **values: float) -> int:
        """Queue new actuator values by name (e.g. XV1pos=0.3, RebHeat=0.82).

        apply='step' -> at the next step boundary; 'now' -> at the next evaluation.
        With confirm=True the call returns only after the DLL has the command in its queue, so
        a solver run started afterwards applies it at its first evaluation (no race).
        """
        self._next_id += 1
        cid = self._next_id
        msg = {"cmd": "set", "id": cid, "apply": apply}
        msg.update({k: float(v) for k, v in values.items() if v is not None})
        self.send(msg)
        if confirm:
            self.wait_for("queued", timeout=self.timeout, keep=keep,
                          match=lambda m: str(m.get("id")) == str(cid))
        return cid

    def set_inputs(self, q1: Optional[float] = None, q2: Optional[float] = None,
                   apply: str = "step", confirm: bool = True,
                   keep: Optional[List[dict]] = None) -> int:
        """Quadruple tank: flows q1, q2."""
        return self.set_values(apply=apply, confirm=confirm, keep=keep, q1=q1, q2=q2)

    def drain(self) -> List[dict]:
        out, self._held = self._held, []
        while True:
            try:
                out.append(self.inbox.get_nowait())
            except queue.Empty:
                return out

    def wait_for(self, mtype: str, timeout: float = 5.0, keep: Optional[List[dict]] = None,
                 match=None) -> dict:
        """Return the first message of type mtype. Other messages are appended to keep or, if
        keep is None, held and returned by the next drain(): nothing is ever discarded."""
        end = time.perf_counter() + timeout
        while True:
            left = end - time.perf_counter()
            if left <= 0:
                raise BridgeError(f"timeout waiting for '{mtype}'")
            try:
                msg = self.inbox.get(timeout=left)
            except queue.Empty:
                continue
            if msg.get("type") == "_disconnected":
                raise BridgeError("bridge disconnected")
            if msg.get("type") == mtype and (match is None or match(msg)):
                return msg
            (keep if keep is not None else self._held).append(msg)

    def ping(self) -> float:
        t0 = time.perf_counter()
        self.send({"cmd": "ping", "id": 0})
        self.wait_for("pong")
        return time.perf_counter() - t0

    def status(self) -> dict:
        self.send({"cmd": "status"})
        return self.wait_for("status")

    def flush(self, keep: List[dict], timeout: float = 5.0) -> None:
        """After RunDynamics returns: emit the (converged) last step and wait for confirmation."""
        self.send({"cmd": "flush"})
        self.wait_for("flushed", timeout=timeout, keep=keep)


# ---------------------------------------------------------------- integrity checks
def validate(samples: List[dict], dt_nominal: Optional[float] = None) -> Dict[str, object]:
    seqs = [s["seq"] for s in samples]
    ts = [s["t"] for s in samples]
    gaps = [(a, b) for a, b in zip(seqs, seqs[1:]) if b != a + 1]
    non_increasing = [(a, b) for a, b in zip(ts, ts[1:]) if b <= a]
    evals = [s.get("evals", 0) for s in samples]
    rep = {
        "n_samples": len(samples),
        "seq_gaps": gaps[:20],
        "n_seq_gaps": len(gaps),
        "non_increasing_t": non_increasing[:20],
        "n_non_increasing_t": len(non_increasing),
        "evals_per_step_mean": (sum(evals) / len(evals)) if evals else None,
        "evals_per_step_max": max(evals) if evals else None,
    }
    if dt_nominal and len(ts) > 1:
        dts = [b - a for a, b in zip(ts, ts[1:])]
        rep["dt_min"], rep["dt_max"] = min(dts), max(dts)
        rep["n_dt_off_nominal"] = sum(abs(d - dt_nominal) > 1e-6 for d in dts)
    rep["ok"] = rep["n_seq_gaps"] == 0 and rep["n_non_increasing_t"] == 0
    return rep


# ---------------------------------------------------------------- APS engine
class AVEVAEngineV2:
    """Same experiment cycle as aveva_engine.AVEVAEngine.ejecutar, with the v2 data path.

    Differences in the returned dict: 'trayectoria' holds only accepted-step samples, each
    with the applied flows (q1, q2) and setpoints (sp1, sp2); 'ack' records when the command
    took effect; 'integrity' holds the checks of validate().
    """

    def __init__(self, sim_name: str = "Sim 3", snapshot_name: str = "start dym",
                 port: int = 5006, timeout_ms: int = 500000):
        import simcentralconnect   # only needed with APS

        self.sim_name, self.snapshot_name = sim_name, snapshot_name
        self.sc = simcentralconnect.connect().Result
        self.sc.SetOptions(repr({"Timeout": timeout_ms, "EnableApiLogging": "false"}))
        self.sm = self.sc.GetService("ISimulationManager")
        self.snap = self.sc.GetService("ISnapshotManager")
        if self.sim_name not in list(self.sm.GetOpenSimulations().Result):
            if not self.sm.OpenSimulation(self.sim_name).Result:
                raise RuntimeError(f"OpenSimulation failed: {self.sim_name}")
        mode = str(self.sm.GetSimulationMode(self.sim_name).Result)
        if mode.lower() != "dynamics":
            raise RuntimeError("The simulation is not in Dynamics mode.")
        self.port = port
        self.bridge = BridgeV2(port=port).connect()
        print(f"[APS] {self.sim_name} ready | {self.bridge.hello.get('version')}")

    def reset(self) -> bool:
        ok = bool(self.snap.RevertSnapshot(self.sim_name, self.snapshot_name).Result)
        # In a freshly opened simulation the first revert can reload the DLL and drop the
        # connection: reconnect if the link no longer answers.
        try:
            self.bridge.ping()
        except (BridgeError, OSError):
            self.bridge.close()
            self.bridge = BridgeV2(port=self.port).connect()
            print("[APS] bridge reconnected after revert")
        return ok

    def ejecutar(self, q1: float, q2: float, horizonte: float = 20.0) -> dict:
        if not self.reset():
            raise RuntimeError("RevertSnapshot returned False.")
        self.bridge.drain()
        cid = self.bridge.set_inputs(q1, q2, apply="step")

        t0 = time.perf_counter()
        if not self.sm.RunDynamics(self.sim_name, float(horizonte), "s").Result:
            raise RuntimeError("RunDynamics returned False.")
        t1 = time.perf_counter()

        msgs: List[dict] = []
        self.bridge.flush(keep=msgs)
        msgs += self.bridge.drain()
        samples = [m for m in msgs if m.get("type") == "sample"]
        acks = [m for m in msgs if m.get("type") == "ack" and m.get("id") == cid]
        if not samples:
            raise RuntimeError("No samples received.")
        last = samples[-1]
        return {
            "q1": float(q1), "q2": float(q2), "horizonte": float(horizonte),
            "sim_time": last["t"],
            "h1": last["h1"], "h2": last["h2"], "h3": last["h3"], "h4": last["h4"],
            "q1_aplicado": last["q1"], "q2_aplicado": last["q2"],
            "tiempo_pc": t1 - t0,
            "n_paquetes": len(samples),
            "ack": acks[0] if acks else None,
            "integrity": validate(samples),
            "eventos": [m for m in msgs if m.get("type") not in ("sample", "ack", "flushed")],
            "trayectoria": samples,
        }

    def close(self) -> None:
        self.bridge.close()
