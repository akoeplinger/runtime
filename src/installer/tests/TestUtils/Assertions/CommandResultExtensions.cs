// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.Cli.Build.Framework;
using System;
using Xunit;

namespace Microsoft.DotNet.CoreSetup.Test
{
    public static class CommandResultExtensions
    {
        public static CommandResultAssertions Should(this CommandResult commandResult)
        {
            return new CommandResultAssertions(commandResult);
        }

        public static CommandResult StdErrAfter(this CommandResult commandResult, string pattern)
        {
            int i = commandResult.StdErr.IndexOf(pattern);
            Xunit.Assert.True(i >= 0,
                $"Trying to filter StdErr after '{pattern}', but such string can't be found in the StdErr.{Environment.NewLine}{commandResult.StdErr}");
            string filteredStdErr = commandResult.StdErr.Substring(i);

            return new CommandResult(commandResult.StartInfo, commandResult.ExitCode, commandResult.StdOut, filteredStdErr);
        }
    }
}
