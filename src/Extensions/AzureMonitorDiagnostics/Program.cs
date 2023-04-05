// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.Monitoring.Extension.Common;
using System.CommandLine;

namespace Microsoft.Diagnostics.Monitoring.AzureMonitorDiagnostics;

internal sealed class Program
{
    public static async Task<int> Main(string[] args)
    {
        AzureMonitorDiagnosticsEgressProvider provider = new();

        // Expected command line format is: dotnet-monitor-egress-azuremonitordiagnostics.exe Egress
        RootCommand rootCommand = new("Uploads an artifact to Azure Monitor Diagnostic Services.");
        Command egressCmd = EgressHelper.CreateEgressCommand(provider);
        rootCommand.Add(egressCmd);
        return await rootCommand.Parse(args).InvokeAsync();
    }
}
