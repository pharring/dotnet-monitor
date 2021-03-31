﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;

namespace Microsoft.Diagnostics.Monitoring.TestCommon
{
    public partial class DotNetHost
    {
        public static string HostExePath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
            @"..\..\..\..\..\.dotnet\dotnet.exe" :
            "../../../../../.dotnet/dotnet";
    }
}