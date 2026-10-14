// ------------------------------------------------------------------------------------------------------------
// <copyright company="AVEVA Software, LLC" file="Main.cs">
//   Copyright 2020 AVEVA Group plc and its subsidiaries. All rights reserved.
// 
// THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
// KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
// </copyright>
// ------------------------------------------------------------------------------------------------------------

using SimSci.SimCentral.Interfaces;
using System.Collections.Generic;
using System.Diagnostics;

//---------------------------------------------------------------------
// Instructions: 
//   Change the namespace for each new external equation DLL so that it is unique.
//   Do not change anything else in this file.
//---------------------------------------------------------------------

// uncomment define TRACE below to enable tracing in Release builds as well as Debug builds
// see below for example of Trace usage

//#define TRACE 

namespace SimCentral.Examples.tanques4 // make this different for each external equation DLL project
{
    // This is the main entry point for the DLL

    public sealed class Main : IExternalClass
    {
        private readonly List<IExternalEquationSetType> children = new List<IExternalEquationSetType>();
        private int sequence;

        #region Public Methods and Operators

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "This is a factory method that returns the object")]
        public static IExternalClass InitializeClass(string dumpFile)
        {
            return new Main();
        }

        #endregion

        #region IExternalSimulation Members

        public string Version
        {
            get
            {
                return "Version 1.0.0.0";
            }
        }

        public Severity CreateEquationSetType(string externalName, string[] parameters, out IExternalEquationSetType equationSetType)
        {
            var newName = "Type" + (++this.sequence);
            var newType = new EquationSetType(newName);
            this.children.Add(newType);
            equationSetType = newType;

            // Use this method to append trace output to the Server.log file if desired
            // (output will normally be produced only in the Debug configuration)
            Trace.WriteLine("Constructed instance of " + newName, "tanques4");

            return Severity.Ok;
        }

        public Severity GetErrorInformation(out string errorDescription)
        {
            errorDescription = string.Empty;
            return Severity.Ok;
        }

        // Temporary, will be removed in a future version
        public Severity CreateFluidType(string externalName, string[] parameters, out IExternalFluidType fluidType)
        {
            fluidType = null;
            return Severity.Ok;
        }

        public void Dispose()
        {
        }

        #endregion
    }
}
