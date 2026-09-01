using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace NpuBridge;

/// <summary>
/// The two halves of the self-relaunch contract (DECISIONS D37): the by-path parent waits on the
/// activated child and forwards its exit code, and the child stops when the parent disappears. Together
/// they make Ctrl+C, <c>schtasks /End</c> and a crashed child behave like a normal single process.
/// Processes are identified by pid <em>and</em> checked by image name and start time, because a pid can be
/// reused by an unrelated process within milliseconds of the original exiting.
/// </summary>
internal static class Supervisor
{
    /// <summary>How long a freshly activated child gets before we conclude it started.</summary>
    private static readonly TimeSpan StartupProbe = TimeSpan.FromSeconds(3);

    /// <summary>A related process must have started within this window of the activation / of the child.</summary>
    private static readonly TimeSpan StartSkew = TimeSpan.FromSeconds(30);

    private static readonly string ExpectedProcessName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "NpuBridge");

    /// <summary>Parent side: block until the child exits; kill it on Ctrl+C or our own exit.</summary>
    public static async Task<int> WaitForChildAsync(uint pid, DateTime activatedAtUtc)
    {
        Process? child = TryOpen((int)pid);
        if (child is null || !LooksLikeOurs(child, earliestStartUtc: activatedAtUtc - StartSkew, latestStartUtc: DateTime.UtcNow + StartSkew))
        {
            child?.Dispose();
            Console.Error.WriteLine($"error: the activated instance (pid {pid}) exited before it could be observed, or the pid now belongs to another process. " +
                                    "Run with --self-relaunch off to see the failure in this console.");
            return 3;
        }

        using (child)
        {
            if (child.WaitForExit((int)StartupProbe.TotalMilliseconds))
            {
                Console.Error.WriteLine($"error: the activated instance exited immediately with code {child.ExitCode}. " +
                                        "Typical causes: the listen port is in use, or the backend failed to initialize. " +
                                        "Run with --self-relaunch off to see the failure in this console (Phi Silica itself then reports 'no identity' in /healthz).");
                return child.ExitCode == 0 ? 3 : child.ExitCode;
            }

            Console.WriteLine($"npu-bridge: supervising activated instance pid {pid}; Ctrl+C or closing this window stops it.");

            void Kill()
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill();
                    }
                }
                catch (Exception)
                {
                    // Already gone.
                }
            }

            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                e.Cancel = true;
                Kill();
            };
            EventHandler onExit = (_, _) => Kill();

            Console.CancelKeyPress += onCancel;
            AppDomain.CurrentDomain.ProcessExit += onExit;
            try
            {
                await child.WaitForExitAsync().ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
                AppDomain.CurrentDomain.ProcessExit -= onExit;
            }

            Console.WriteLine($"npu-bridge: activated instance exited with code {child.ExitCode}.");
            return child.ExitCode;
        }
    }

    /// <summary>Child side: stop the host when the supervising process is gone (covers TerminateProcess, /End, closed window).</summary>
    public static void WatchParent(int supervisorPid, IHostApplicationLifetime lifetime, Action<string> log)
    {
        var parent = TryOpen(supervisorPid);
        DateTime ownStartUtc;
        try
        {
            using var self = Process.GetCurrentProcess();
            ownStartUtc = self.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            ownStartUtc = DateTime.UtcNow;
        }

        // The supervisor is our own image and must have started before us; anything else is a reused pid.
        if (parent is null || !LooksLikeOurs(parent, earliestStartUtc: ownStartUtc - TimeSpan.FromHours(24), latestStartUtc: ownStartUtc + TimeSpan.FromSeconds(1)))
        {
            parent?.Dispose();
            log($"supervisor pid {supervisorPid} is gone or is not npu-bridge; stopping.");
            lifetime.StopApplication();
            return;
        }

        var thread = new Thread(() =>
        {
            using (parent)
            {
                parent.WaitForExit();
            }

            log($"supervisor pid {supervisorPid} exited; stopping.");
            lifetime.StopApplication();
        })
        {
            IsBackground = true,
            Name = "npu-bridge supervisor watch",
        };
        thread.Start();
    }

    private static Process? TryOpen(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Same image name and a plausible start time; guards against pid reuse by a stranger.</summary>
    private static bool LooksLikeOurs(Process process, DateTime earliestStartUtc, DateTime latestStartUtc)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            if (!string.Equals(process.ProcessName, ExpectedProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var started = process.StartTime.ToUniversalTime();
            return started >= earliestStartUtc && started <= latestStartUtc;
        }
        catch (Exception)
        {
            // Access denied or the process vanished mid-check: treat as not ours.
            return false;
        }
    }
}
