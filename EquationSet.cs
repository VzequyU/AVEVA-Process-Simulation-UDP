using SimSci.SimCentral.Interfaces;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Net;

namespace SimCentral.Examples.tanques4
{
    public sealed class EquationSet : IExternalEquationSet
    {
        // 2 Ecuaciones para inyectar los caudales Q1 y Q2
        private static IReadOnlyList<string> Equations = new[] { "ControlQ1", "ControlQ2" };
        private static IReadOnlyList<string> EqnText = new[] { "Control Q1 desde Python", "Control Q2 desde Python" };

        // 6 Variables mapeadas con AVEVA
        private static IReadOnlyList<string> Variables = new[] { "Q1", "Q2", "H1", "H2", "H3", "H4" };
        private const int _Q1 = 0, _Q2 = 1, _H1 = 2, _H2 = 3, _H3 = 4, _H4 = 5;

        // Matriz de incidencia: Declaramos todas las variables en ambas ecuaciones para evitar singularidades
        private static readonly int[][] Incidence = {
            new[] { _Q1, _Q2, _H1, _H2, _H3, _H4 },
            new[] { _Q1, _Q2, _H1, _H2, _H3, _H4 }
        };

        private readonly double[][] derivatives;
        private string lastMessage;
        private Severity lastSeverity;
        private readonly double[] v;

        // Red UDP
        private static UdpClient udpClient;
        private static IPEndPoint pythonEndPoint;

        // Valores por defecto al iniciar (0.075 kg/s)
        private double targetQ1 = 0.075;
        private double targetQ2 = 0.075;

        public EquationSet(string nameIn)
        {
            this.v = new double[Variables.Count];
            this.derivatives = new double[Equations.Count][];
            this.derivatives[0] = new double[Incidence[0].Length];
            this.derivatives[1] = new double[Incidence[1].Length];
            this.ClearErrors();

            if (udpClient == null)
            {
                try
                {
                    udpClient = new UdpClient(5006);
                    pythonEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 5005);
                }
                catch { }
            }
        }

        public Severity GetEquationResidual(int equationNumber, out double residual, bool calculateDerivative)
        {
            // 1. ESCUCHAR A PYTHON (Solo procesa en la primera ecuación para no leer doble)
            if (equationNumber == 0 && udpClient != null && udpClient.Available > 0)
            {
                try
                {
                    IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                    byte[] rcvBytes = udpClient.Receive(ref remoteEP);
                    string jsonIn = Encoding.UTF8.GetString(rcvBytes);

                    jsonIn = jsonIn.Replace("{", "").Replace("}", "").Replace("\"", "").Replace(" ", "");
                    string[] parts = jsonIn.Split(',');
                    foreach (var part in parts)
                    {
                        string[] kv = part.Split(':');
                        if (kv.Length == 2)
                        {
                            if (double.TryParse(kv[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
                            {
                                if (kv[0].ToLower() == "q1") targetQ1 = Math.Max(0.0, val);
                                if (kv[0].ToLower() == "q2") targetQ2 = Math.Max(0.0, val);
                            }
                        }
                    }
                }
                catch { }
            }

            // 2. APLICAR AL SOLVER
            if (equationNumber == 0)
            {
                residual = v[_Q1] - targetQ1;
                if (calculateDerivative)
                {
                    Array.Clear(this.derivatives[0], 0, this.derivatives[0].Length);
                    this.derivatives[0][0] = 1.0;
                }
            }
            else // equationNumber == 1
            {
                residual = v[_Q2] - targetQ2;
                if (calculateDerivative)
                {
                    Array.Clear(this.derivatives[1], 0, this.derivatives[1].Length);
                    this.derivatives[1][1] = 1.0;
                }
            }

            // 3. ENVIAR ESTADOS A PYTHON (De forma segura, variable por variable)
            if (equationNumber == 1 && !calculateDerivative && udpClient != null)
            {
                try
                {
                    string h1_str = v[_H1].ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                    string h2_str = v[_H2].ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                    string h3_str = v[_H3].ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                    string h4_str = v[_H4].ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

                    string jsonOut = $"{{\"h1\": {h1_str}, \"h2\": {h2_str}, \"h3\": {h3_str}, \"h4\": {h4_str}}}";

                    byte[] bytes = Encoding.UTF8.GetBytes(jsonOut);
                    udpClient.Send(bytes, bytes.Length, pythonEndPoint);
                }
                catch { }
            }

            return Severity.Ok;
        }

        // --- MÉTODOS OBLIGATORIOS INTERFAZ ---
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