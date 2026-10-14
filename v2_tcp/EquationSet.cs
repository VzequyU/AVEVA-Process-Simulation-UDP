// ------------------------------------------------------------------------------------------------
// EquationSet.cs — quadruple-tank external equations, bridge version 2.
//
// Drop-in replacement for the v1 DLL: same namespace, same 2 equations and same 7 variables
// (Q1, Q2, H1, H2, H3, H4, SimTime), so the APS Model Type does not change.
// SimTime must keep being bound to the solver clock (M1.SimTime = TG1.TimedValue).
//
// What changes with respect to v1:
//   1. One sample per ACCEPTED time step. The solver evaluates the residuals several times per
//      step (Newton iterations). The DLL keeps the latest evaluation of the current step in a
//      buffer and sends it only when SimTime advances, i.e. after that step was accepted.
//   2. Commands are applied at step boundaries (or immediately with "apply":"now") and are
//      acknowledged with the simulation time at which they took effect.
//   3. Every sample carries the inputs actually applied (Q1, Q2 as solved by APS), the
//      setpoints, the id of the last applied command and the number of residual evaluations.
//   4. Full double precision (round-trip format) instead of 4 decimals.
//   5. TCP with a non-blocking queue (Bridge.cs) instead of UDP.
//
// Messages DLL -> Python (one JSON object per line):
//   {"type":"hello",...}
//   {"type":"sample","seq":n,"t":..,"q1":..,"q2":..,"h1":..,"h2":..,"h3":..,"h4":..,
//    "sp1":..,"sp2":..,"cmd":id,"evals":k,"final":false}
//   {"type":"ack","id":id,"t":..,"reason":"step|now|rewind|start","sp1":..,"sp2":..}
//   {"type":"rewind","from":t_old,"to":t_new}        (snapshot revert or rejected step)
//   {"type":"superseded","id":id}                     (pending command replaced before use)
//   {"type":"pong"|"flushed"|"status"|"error",...}
//
// Messages Python -> DLL:
//   {"cmd":"set","id":7,"q1":0.08,"q2":0.06,"apply":"step"}   ("apply":"now" for steady state)
//   {"cmd":"flush"}   {"cmd":"ping","id":1}   {"cmd":"status"}   {"cmd":"reset_seq"}
// ------------------------------------------------------------------------------------------------

using SimSci.SimCentral.Interfaces;
using System;
using System.Collections.Generic;

namespace SimCentral.Examples.tanques4
{
    public sealed class EquationSet : IExternalEquationSet
    {
        private static IReadOnlyList<string> Equations = new[] { "ControlQ1", "ControlQ2" };
        private static IReadOnlyList<string> EqnText = new[] { "Control Q1 desde Python", "Control Q2 desde Python" };

        private static IReadOnlyList<string> Variables = new[] { "Q1", "Q2", "H1", "H2", "H3", "H4", "SimTime" };
        private const int _Q1 = 0, _Q2 = 1, _H1 = 2, _H2 = 3, _H3 = 4, _H4 = 5, _SIMTIME = 6;

        // Same incidence pattern as v1 (all variables in both equations).
        private static readonly int[][] Incidence = {
            new[] { _Q1, _Q2, _H1, _H2, _H3, _H4, _SIMTIME },
            new[] { _Q1, _Q2, _H1, _H2, _H3, _H4, _SIMTIME }
        };

        private const double TimeEps = 1e-9;

        private readonly double[][] derivatives;
        private readonly double[] v;
        private string lastMessage;
        private Severity lastSeverity;

        // State shared between the solver thread and the bridge reader thread (flush/status).
        private readonly object stateLock = new object();

        private double targetQ1 = 0.05;
        private double targetQ2 = 0.05;
        private long appliedCmd = -1;

        private bool hasPending;
        private bool pendingNow;
        private long pendingId = -1;
        private double pendingQ1, pendingQ2;

        private bool haveTime;
        private double stepTime;          // simulation time of the step being solved
        private long seq;
        private long evalsThisStep;

        private readonly double[] buf;    // latest residual evaluation of the current step
        private bool bufValid;
        private double bufSp1, bufSp2;
        private long bufCmd = -1;
        private double flushedTime = double.NaN;

        public EquationSet(string nameIn)
        {
            this.v = new double[Variables.Count];
            this.buf = new double[Variables.Count];
            this.derivatives = new double[Equations.Count][];
            this.derivatives[0] = new double[Incidence[0].Length];
            this.derivatives[1] = new double[Incidence[1].Length];
            this.ClearErrors();

            Bridge.EnsureStarted();
            Bridge.FlushHandler = this.Flush;
            Bridge.StatusHandler = this.Status;
        }

        public Severity GetEquationResidual(int equationNumber, out double residual, bool calculateDerivative)
        {
            if (equationNumber == 0)
            {
                try { this.OnStepEntry(); }
                catch (Exception ex) { Bridge.LastError = "step: " + ex.Message; }
            }

            if (equationNumber == 0)
            {
                residual = v[_Q1] - targetQ1;
                if (calculateDerivative)
                {
                    Array.Clear(this.derivatives[0], 0, this.derivatives[0].Length);
                    this.derivatives[0][0] = 1.0;   // dR/dQ1
                }
            }
            else
            {
                residual = v[_Q2] - targetQ2;
                if (calculateDerivative)
                {
                    Array.Clear(this.derivatives[1], 0, this.derivatives[1].Length);
                    this.derivatives[1][1] = 1.0;   // dR/dQ2
                }
            }

            // Keep the latest full evaluation of this step; it is sent once the step is accepted.
            if (equationNumber == 1 && !calculateDerivative)
            {
                lock (stateLock)
                {
                    Array.Copy(v, buf, v.Length);
                    bufValid = true;
                    bufSp1 = targetQ1;
                    bufSp2 = targetQ2;
                    bufCmd = appliedCmd;
                    evalsThisStep++;
                }
            }

            return Severity.Ok;
        }

        // ---------------------------------------------------------------- step logic
        private void OnStepEntry()
        {
            Dictionary<string, string> cmd;
            while (Bridge.TryReceive(out cmd)) this.HandleCommand(cmd);

            double t = v[_SIMTIME];
            lock (stateLock)
            {
                if (!haveTime)
                {
                    haveTime = true;
                    stepTime = t;
                    ApplyPending(t, "start");
                }
                else if (t > stepTime + TimeEps)
                {
                    // The previous step was accepted: its last evaluation is converged.
                    EmitBuffered(false);
                    stepTime = t;
                    evalsThisStep = 0;
                    bufValid = false;
                    ApplyPending(t, "step");
                }
                else if (t < stepTime - TimeEps)
                {
                    // Snapshot revert or rejected step: discard the unaccepted evaluations.
                    Bridge.Send(Json.Obj("type", "rewind", "from", stepTime, "to", t));
                    stepTime = t;
                    evalsThisStep = 0;
                    bufValid = false;
                    flushedTime = double.NaN;
                    ApplyPending(t, "rewind");
                }

                if (hasPending && pendingNow) ApplyPending(t, "now");
            }
        }

        private void HandleCommand(Dictionary<string, string> msg)
        {
            string cmd;
            msg.TryGetValue("cmd", out cmd);
            cmd = (cmd ?? "set").ToLowerInvariant();

            if (cmd == "reset_seq")
            {
                lock (stateLock) { seq = 0; }
                Bridge.Send(Json.Obj("type", "ack_reset_seq"));
                return;
            }
            if (cmd != "set")
            {
                Bridge.Send(Json.Obj("type", "error", "msg", "unknown cmd", "cmd", cmd));
                return;
            }

            double q1, q2, idD;
            long id = Json.TryDouble(msg, "id", out idD) ? (long)idD : -1;
            string apply;
            msg.TryGetValue("apply", out apply);

            lock (stateLock)
            {
                if (hasPending && pendingId != id)
                    Bridge.Send(Json.Obj("type", "superseded", "id", pendingId));

                double baseQ1 = hasPending ? pendingQ1 : targetQ1;
                double baseQ2 = hasPending ? pendingQ2 : targetQ2;
                pendingQ1 = Json.TryDouble(msg, "q1", out q1) ? Math.Max(0.0, q1) : baseQ1;
                pendingQ2 = Json.TryDouble(msg, "q2", out q2) ? Math.Max(0.0, q2) : baseQ2;
                pendingId = id;
                pendingNow = string.Equals(apply, "now", StringComparison.OrdinalIgnoreCase);
                hasPending = true;
            }
        }

        // Caller holds stateLock.
        private void ApplyPending(double t, string reason)
        {
            if (!hasPending) return;
            targetQ1 = pendingQ1;
            targetQ2 = pendingQ2;
            appliedCmd = pendingId;
            hasPending = false;
            pendingNow = false;
            Bridge.Send(Json.Obj("type", "ack", "id", appliedCmd, "t", t, "reason", reason,
                                 "sp1", targetQ1, "sp2", targetQ2));
        }

        // Caller holds stateLock.
        private void EmitBuffered(bool final)
        {
            if (!bufValid) return;
            double t = buf[_SIMTIME];
            if (!double.IsNaN(flushedTime) && Math.Abs(flushedTime - t) <= TimeEps) return;  // already sent by flush
            seq++;
            Bridge.Send(Json.Obj(
                "type", "sample", "seq", seq, "t", t,
                "q1", buf[_Q1], "q2", buf[_Q2],
                "h1", buf[_H1], "h2", buf[_H2], "h3", buf[_H3], "h4", buf[_H4],
                "sp1", bufSp1, "sp2", bufSp2, "cmd", bufCmd,
                "evals", evalsThisStep, "final", final));
            if (final) flushedTime = t;
        }

        /// <summary>Called from the bridge reader thread after RunDynamics returns: the solver is idle,
        /// so the buffered evaluation of the last step is converged.</summary>
        private void Flush()
        {
            lock (stateLock) { EmitBuffered(true); }
        }

        private string Status()
        {
            lock (stateLock)
            {
                return Json.Obj("type", "status", "version", Bridge.Version,
                    "t", stepTime, "seq", seq, "sp1", targetQ1, "sp2", targetQ2,
                    "applied_cmd", appliedCmd, "pending", hasPending,
                    "sent", Bridge.Sent, "dropped", Bridge.Dropped, "last_error", Bridge.LastError);
            }
        }

        // ---------------------------------------------------------------- interface members
        public Severity Initialize(int[] knownVariables) => Severity.Ok;
        public bool NeedsInitialization() => false;
        public int EquationCount => Equations.Count;
        public int VariableCount => Variables.Count;
        public int ParameterCount => 0;
        public IReadOnlyList<string> EquationNames => Equations;
        public IReadOnlyList<string> VariableNames => Variables;
        public IReadOnlyList<string> ParameterNames => null;
        public IReadOnlyList<DataType> ParameterDataTypes => null;
        public Severity GetErrorInformation(out string errorDescription) { errorDescription = this.lastMessage; return this.lastSeverity; }
        public Severity GetAllVariableValues(double[] outValues) { v.CopyTo(outValues, 0); return Severity.Ok; }
        public void Dispose() { }
        public Severity GetVariableIncidence(int equationNumber, out int[] incidence) { incidence = Incidence[equationNumber]; return Severity.Ok; }
        public Severity SetAllVariableValues(double[] values) { values.CopyTo(this.v, 0); return Severity.Ok; }
        public Severity SetVariableValue(int index, double value) { v[index] = value; return Severity.Ok; }
        public Severity GetVariableValue(int index, out double value) { value = v[index]; return Severity.Ok; }
        public Severity GetVariableLimits(int index, out double? minValue, out double? maxValue) { minValue = null; maxValue = null; return Severity.Ok; }
        public Severity SetParameterValues(object[] values) => Severity.Error;
        public Severity SetParameterValue(int index, object value) => Severity.Error;
        public Severity GetParameterValues(object[] value) => Severity.Error;
        public Severity GetParameterValue(int index, out object value) { value = null; return Severity.Error; }
        public Severity GetEquationDescription(int equationNumber, out string descr) { descr = EqnText[equationNumber]; return Severity.Ok; }
        public Severity GetAllEquationResiduals(int equationNumber, double[] outResiduals, bool calculateDerivative) => throw new NotImplementedException();
        public Severity GetEquationDerivatives(int equationNumber, double[] outDerivatives) { this.derivatives[equationNumber].CopyTo(outDerivatives, 0); return Severity.Ok; }
        public Severity GetAllEquationDerivatives(int equationNumber, double[][] outDerivatives) => throw new NotImplementedException();
        public bool CanSolve(int[] knownVariableIndexes) => false;
        public void SetKnownVariables(int[] knownVariableIndexes) { }
        public Severity Solve(int[] knownVariableIndexes) => Severity.Error;
        private void ClearErrors() { this.lastMessage = string.Empty; this.lastSeverity = Severity.Ok; }
    }
}
