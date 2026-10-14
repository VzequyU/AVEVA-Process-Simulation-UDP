// ------------------------------------------------------------------------------------------------------------
// <copyright company="AVEVA Software, LLC" file="EquationSetType.cs">
//   Copyright 2020 AVEVA Group plc and its subsidiaries. All rights reserved.
// 
// THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
// KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
// </copyright>
// ------------------------------------------------------------------------------------------------------------
//
//---------------------------------------------------------------------
// Instructions: 
//   Change the namespace for each new external equation DLL so that it is unique.
//   Do not change anything else in this file.
//---------------------------------------------------------------------

using SimSci.SimCentral.Interfaces;
using System;
using System.Collections.Generic;

namespace SimCentral.Examples.tanques4
{
    public sealed class EquationSetType : IExternalEquationSetType
    {
        private readonly string name;
        private readonly List<IExternalEquationSet> children = new List<IExternalEquationSet>();
        private int sequence;

        #region Constructors and Destructors

        public EquationSetType(string nameIn)
        {
            this.name = nameIn;
        }
        #endregion


        #region IExternalEquationSetType Members

        public Severity CreateEquationSet(string externalName, out IExternalEquationSet equationSet)
        {
            var newName = this.name + "_EqSet" + (++this.sequence);
            var newESet = new EquationSet(newName);
            this.children.Add(newESet);
            equationSet = newESet;
            return Severity.Ok;
        }

        public Severity GetErrorInformation(out string errorDescription)
        {
            errorDescription = string.Empty;
            return Severity.Ok;
        }
        public Severity GetPropertyValues(string name, double[] values)
        {
            throw new NotImplementedException();
        }


        public void Dispose()
        {
        }

        #endregion
    }
}
