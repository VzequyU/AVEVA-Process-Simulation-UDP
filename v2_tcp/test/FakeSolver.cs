// FakeSolver.cs — offline harness that drives the real tanques4.dll the way the APS solver does:
// several Newton iterations per time step (with derivative and residual-only calls), one rejected
// step and one snapshot revert. It writes the converged values of every accepted step to
// expected.csv so the Python test can check that the DLL emitted exactly those samples.
//
// Build (from tanques4_v2): see test/build_and_test.ps1

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using SimCentral.Examples.tanques4;

public static class FakeSolver
{
    const int Q1 = 0, Q2 = 1, H1 = 2, H2 = 3, H3 = 4, H4 = 5, T = 6;

    static readonly double[] v = new double[7];
    static EquationSet eq;
    static StreamWriter expected;

    // Simple coupled tank dynamics; only needs to be smooth, not physical.
    static void Plant(double dt)
    {
        v[H1] += dt * (0.6 * v[Q1] - 0.02 * Math.Sqrt(Math.Max(v[H1], 0)) + 0.01 * Math.Sqrt(Math.Max(v[H3], 0)));
        v[H2] += dt * (0.6 * v[Q2] - 0.02 * Math.Sqrt(Math.Max(v[H2], 0)) + 0.01 * Math.Sqrt(Math.Max(v[H4], 0)));
        v[H3] += dt * (0.4 * v[Q2] - 0.01 * Math.Sqrt(Math.Max(v[H3], 0)));
        v[H4] += dt * (0.4 * v[Q1] - 0.01 * Math.Sqrt(Math.Max(v[H4], 0)));
    }

    // One time step: 3 Newton iterations; the inputs converge to the DLL targets,
    // the levels approach their final value through perturbed intermediate iterates.
    static void Step(double t, double dt, bool accept)
    {
        double[] start = (double[])v.Clone();
        v[T] = t;
        double r;
        for (int it = 0; it < 3; it++)
        {
            eq.SetAllVariableValues(v);
            eq.GetEquationResidual(0, out r, true);  v[Q1] -= r;   // Newton on Q1
            eq.GetEquationResidual(1, out r, true);  v[Q2] -= r;   // Newton on Q2
            Array.Copy(start, 2, v, 2, 4);
            Plant(dt);
            double wrong = (2 - it) * 0.05;                         // intermediate iterate error
            v[H1] += wrong; v[H2] -= wrong;
            eq.SetAllVariableValues(v);
            eq.GetEquationResidual(0, out r, false);
            eq.GetEquationResidual(1, out r, false);
        }
        if (accept)
            expected.WriteLine(string.Join(",", Array.ConvertAll(
                new[] { v[T], v[Q1], v[Q2], v[H1], v[H2], v[H3], v[H4] }, F)));
        else
            Array.Copy(start, v, v.Length);                          // rejected: restore state
    }

    static string F(double x) { return x.ToString("R", CultureInfo.InvariantCulture); }

    public static int Main(string[] args)
    {
        int steps = args.Length > 0 ? int.Parse(args[0]) : 200;
        double dt = 0.5;
        expected = new StreamWriter("expected.csv");
        expected.WriteLine("t,q1,q2,h1,h2,h3,h4");

        v[Q1] = 0.05; v[Q2] = 0.05; v[H1] = 0.3; v[H2] = 0.3; v[H3] = 0.1; v[H4] = 0.1;
        eq = new EquationSet("fake");
        Console.WriteLine("READY"); Console.Out.Flush();
        Console.ReadLine();                                  // wait until Python is connected

        // Phase 1: normal run, with one rejected step in the middle.
        double t = 0;
        Step(t, dt, true);
        for (int k = 1; k <= steps; k++)
        {
            if (k == steps / 2)
            {
                Step(t + dt, dt, false);                     // rejected attempt
                t += dt / 2;                                 // retry with half step: time goes back
                Step(t, dt / 2, true);
                continue;
            }
            t += dt;
            Step(t, dt, true);
        }
        expected.Flush();
        Console.WriteLine("IDLE1"); Console.Out.Flush();
        Console.ReadLine();                                  // Python flushes and sends the next command

        // Phase 2: snapshot revert (time back to 0), then a shorter run.
        v[H1] = 0.3; v[H2] = 0.3; v[H3] = 0.1; v[H4] = 0.1;
        t = 0;
        Step(t, dt, true);
        for (int k = 1; k <= steps / 4; k++) { t += dt; Step(t, dt, true); }
        expected.Flush();
        Console.WriteLine("IDLE2"); Console.Out.Flush();
        Console.ReadLine();
        expected.Close();
        Console.WriteLine("DONE"); Console.Out.Flush();
        return 0;
    }
}
