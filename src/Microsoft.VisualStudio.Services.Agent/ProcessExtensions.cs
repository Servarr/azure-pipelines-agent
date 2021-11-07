// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Microsoft.VisualStudio.Services.Agent
{
  public static class ProcessExtensions
    {
        public static string GetEnvironmentVariable(this Process process, IHostContext hostContext, string variable)
        {
            ArgUtil.NotNull(process, nameof(process));
            ArgUtil.NotNull(hostContext, nameof(hostContext));
            ArgUtil.NotNull(variable, nameof(variable));

            switch (PlatformUtil.HostOS)
            {
                case PlatformUtil.OS.Linux:
                    return GetEnvironmentVariableLinux(process, hostContext, variable);
                case PlatformUtil.OS.OSX:
                    return GetEnvironmentVariableUsingPs(process, hostContext, variable);
                case PlatformUtil.OS.Windows:
                    return WindowsEnvVarHelper.GetEnvironmentVariable(process, hostContext, variable);
            }

            throw new NotImplementedException($"Cannot look up environment variables on {PlatformUtil.HostOS}");
        }

        private static string GetEnvironmentVariableLinux(Process process, IHostContext hostContext, string variable)
        {
            // On FreeBSD we use procstat and JSON formatted output provided by libxo
            var trace = hostContext.GetTrace(nameof(ProcessExtensions));
            trace.Info($"Read env from output of `procstat -e --libxo json {process.Id}`");

            Dictionary<string, string> env = new Dictionary<string, string>();
            List<string> procstatOut = new List<string>();
            object outputLock = new object();
            using (var p = hostContext.CreateService<IProcessInvoker>())
            {
                p.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs stdout)
                {
                    if (!string.IsNullOrEmpty(stdout.Data))
                    {
                        lock (outputLock)
                        {
                            procstatOut.Add(stdout.Data);
                        }
                    }
                };

                p.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs stderr)
                {
                    if (!string.IsNullOrEmpty(stderr.Data))
                    {
                        lock (outputLock)
                        {
                            trace.Error(stderr.Data);
                        }
                    }
                };

                int exitCode = p.ExecuteAsync(workingDirectory: hostContext.GetDirectory(WellKnownDirectory.Root),
                                                fileName: "procstat",
                                                arguments: $"-e --libxo json {process.Id}",
                                                environment: null,
                                                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                if (exitCode == 0)
                {
                    trace.Info($"Successfully dump environment variables for {process.Id}");
                    if (procstatOut.Count > 0 && procstatOut[0].StartsWith("{"))
                    {
                        string procstatStr = String.Join(' ', procstatOut);
                        trace.Verbose($"procstat output: '{procstatStr}'");

                        try
                        {
                            var procstatDoc = JsonDocument.Parse(procstatStr);

                            env = procstatDoc.RootElement
                                .GetProperty("procstat")
                                .GetProperty("environment")
                                .GetProperty($"{process.Id}")
                                .GetProperty("environment")
                                .EnumerateArray()
                                .ToDictionary(s => s.GetString().Split('=', 2)[0], s => s.GetString().Split('=', 2)[1]);

                            foreach (var pair in env)
                            {
                                trace.Verbose($"PID:{process.Id} ({pair.Key}={pair.Value})");
                            }
                        }
                        catch(Exception e)
                        {
                            trace.Error(e);
                        }
                    }
                }
            }

            if (env.TryGetValue(variable, out string envVariable))
            {
                return envVariable;
            }
            else
            {
                return null;
            }
        }

        private static string GetEnvironmentVariableUsingPs(Process process, IHostContext hostContext, string variable)
        {
            // On OSX, there is no /proc folder for us to read environment for given process,
            // So we have call `ps e -p <pid> -o command` to print out env to STDOUT,
            // However, the output env are not format in a parseable way, it's just a string that concatenate all envs with space,
            // It doesn't escape '=' or ' ', so we can't parse the output into a dictionary of all envs.
            // So we only look for the env you request, in the format of variable=value. (it won't work if you variable contains = or space)
            var trace = hostContext.GetTrace(nameof(ProcessExtensions));
            trace.Info($"Read env from output of `ps e -p {process.Id} -o command`");

            Dictionary<string, string> env = new Dictionary<string, string>();
            List<string> psOut = new List<string>();
            object outputLock = new object();
            using (var p = hostContext.CreateService<IProcessInvoker>())
            {
                p.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs stdout)
                {
                    if (!string.IsNullOrEmpty(stdout.Data))
                    {
                        lock (outputLock)
                        {
                            psOut.Add(stdout.Data);
                        }
                    }
                };

                p.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs stderr)
                {
                    if (!string.IsNullOrEmpty(stderr.Data))
                    {
                        lock (outputLock)
                        {
                            trace.Error(stderr.Data);
                        }
                    }
                };

                int exitCode = p.ExecuteAsync(workingDirectory: hostContext.GetDirectory(WellKnownDirectory.Root),
                                                fileName: "ps",
                                                arguments: $"e -p {process.Id} -o command",
                                                environment: null,
                                                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                if (exitCode == 0)
                {
                    trace.Info($"Successfully dump environment variables for {process.Id}");
                    if (psOut.Count > 0)
                    {
                        string psOutputString = string.Join(" ", psOut);
                        trace.Verbose($"ps output: '{psOutputString}'");

                        int varStartIndex = psOutputString.IndexOf(variable, StringComparison.Ordinal);
                        if (varStartIndex >= 0)
                        {
                            string rightPart = psOutputString.Substring(varStartIndex + variable.Length + 1);
                            if (rightPart.IndexOf(' ') > 0)
                            {
                                string value = rightPart.Substring(0, rightPart.IndexOf(' '));
                                env[variable] = value;
                            }
                            else
                            {
                                env[variable] = rightPart;
                            }

                            trace.Verbose($"PID:{process.Id} ({variable}={env[variable]})");
                        }
                    }
                }
            }

            if (env.TryGetValue(variable, out string envVariable))
            {
                return envVariable;
            }
            else
            {
                return null;
            }
        }
    }
}
