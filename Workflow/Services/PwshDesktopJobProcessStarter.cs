using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Workflow.Services.Native;

namespace Workflow.Services;

/// <summary>
/// Runs a step script as <c>pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File</c> through
/// <see cref="Process"/> - not ConPTY: nobody reads a terminal here, only the log. The process lives
/// in a job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so neither an app crash nor a script that
/// left an application under test behind can leave anything running.
/// </summary>
public sealed class PwshDesktopJobProcessStarter : IDesktopJobProcessStarter
{
    private const int UncontainedKillWaitMilliseconds = 3000;

    /// <inheritdoc />
    public IDesktopJobProcess Start(DesktopJobLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);

        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", launch.ScriptPath })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var log = new StreamWriter(
            new FileStream(launch.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        SafeJobObjectHandle? job = null;
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            job = CreateKillOnCloseJob();
            var running = new PwshDesktopJobProcess(process, job, log);

            process.Start();

            // Residual window: the process is not created suspended (CreateProcess with
            // CREATE_SUSPENDED is out of scope), so a child it starts before this call escapes the
            // job. pwsh needs well over the few microseconds in between to load before the script can
            // start anything, and Kill(entireProcessTree) in Kill/TerminateAsync still reaches such a
            // child through the process tree.
            if (!NativeMethods.AssignProcessToJobObject(job, process.Handle))
            {
                var error = Marshal.GetLastPInvokeError();
                KillUncontained(process);
                throw new DesktopJobStartException(
                    string.Create(CultureInfo.InvariantCulture, $"Job-Objekt-Zuordnung fehlgeschlagen (Win32 {error})"));
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return running;
        }
        catch
        {
            job?.Dispose();
            process.Dispose();
            log.Dispose();
            throw;
        }
    }

    /// <summary>Kills a process that is not in the job, with everything it may already have started.</summary>
    private static void KillUncontained(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(UncontainedKillWaitMilliseconds);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (Win32Exception)
        {
            // Already exiting.
        }
        catch (AggregateException)
        {
            // Part of the tree could not be killed; nothing more can be done from here.
        }
    }

    private static SafeJobObjectHandle CreateKillOnCloseJob()
    {
        var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        var limits = new JobObjectExtendedLimitInfo
        {
            BasicLimitInformation = new JobObjectBasicLimitInfo
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose,
            },
        };

        if (!NativeMethods.SetInformationJobObject(
                job,
                NativeMethods.JobObjectExtendedLimitInformation,
                ref limits,
                Marshal.SizeOf<JobObjectExtendedLimitInfo>()))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        return job;
    }

    /// <summary>One running pwsh, its job object and its log.</summary>
    private sealed class PwshDesktopJobProcess : IDesktopJobProcess
    {
        // After an exit or a kill, how long the output may still take to drain into the log.
        private const int OutputDrainMilliseconds = 2000;

        // How often TerminateAsync re-kills and re-checks the job.
        private const int TerminatePollMilliseconds = 50;

        private readonly Process _process;
        private readonly SafeJobObjectHandle _job;
        private readonly StreamWriter _log;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        private readonly object _logGate = new();
        private bool _logClosed;

        public PwshDesktopJobProcess(Process process, SafeJobObjectHandle job, StreamWriter log)
        {
            _process = process;
            _job = job;
            _log = log;

            // The Exited event, not WaitForExitAsync: the latter also waits for the redirected streams
            // to close, which a grandchild that inherited them can delay indefinitely.
            _process.Exited += (_, _) => _exited.TrySetResult(_process.ExitCode);
            _process.OutputDataReceived += (_, e) => Append(e.Data);
            _process.ErrorDataReceived += (_, e) => Append(e.Data);
        }

        public Task<int> Exited => _exited.Task;

        public void Kill()
        {
            NativeMethods.TerminateJobObject(_job, 1);

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            catch (Win32Exception)
            {
                // Already exiting; the job object took it down.
            }
            catch (AggregateException)
            {
                // A descendant outside the job could not be killed; TerminateAsync retries.
            }
        }

        public async Task<bool> TerminateAsync(TimeSpan timeout)
        {
            var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

            while (true)
            {
                Kill();

                if (JobIsEmpty() && _process.HasExited)
                {
                    return true;
                }

                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    return false;
                }

                await Task.Delay(TerminatePollMilliseconds);
            }
        }

        /// <summary>True when no process is left in the job object.</summary>
        private bool JobIsEmpty() =>
            NativeMethods.QueryInformationJobObject(
                _job,
                NativeMethods.JobObjectBasicAccountingInformation,
                out var accounting,
                Marshal.SizeOf<JobObjectBasicAccountingInfo>(),
                IntPtr.Zero)
            && accounting.ActiveProcesses == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // Closing the job kills anything the script left behind, which also closes the pipes
            // and lets the last output lines reach the log.
            _job.Dispose();
            _process.WaitForExit(OutputDrainMilliseconds);

            lock (_logGate)
            {
                _logClosed = true;
                _log.Dispose();
            }

            _process.Dispose();
        }

        private void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_logGate)
            {
                if (!_logClosed)
                {
                    _log.WriteLine(line);
                    _log.Flush();
                }
            }
        }
    }
}
