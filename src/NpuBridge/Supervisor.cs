using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace NpuBridge;

/// <summary>
/// The two halves of the self-relaunch contract (DECISIONS D37): the by-path parent waits on the
/// activated child and forwards its exit code, and the child stops when the parent disappears. Together
/// they make Ctrl+C, <c>schtasks /End</c> and a crashed child behave like a normal single process.
/// </summary>
internal static class Supervisor
{
    /// <summary>How long a freshly activated child gets before we conclude it started.</summary>
    private static readonly TimeSpan StartupProbe = TimeSpan.FromSeconds(3);

    /// <summary>Parent side: block until the child exits; kill it on Ctrl+C or our own exit.</summary>
    public static async Task<int> WaitForChildAsync(uint pid)
    {
        Process child;
        try
        {
            child = Process.GetProcessById((int)pid);
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine($"error: the activated instance (pid {pid}) exited before it could be observed. Run with --self-relaunch off to see the failure in this console.");
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

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Kill();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Kill();

            await child.WaitForExitAsync().ConfigureAwait(false);
            Console.WriteLine($"npu-bridge: activated instance exited with code {child.ExitCode}.");
            return child.ExitCode;
        }
    }

    /// <summary>Child side: stop the host when the supervising process is gone (covers TerminateProcess, /End, closed window).</summary>
    public static void WatchParent(int supervisorPid, IHostApplicationLifetime lifetime, Action<string> log)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(supervisorPid);
        }
        catch (ArgumentException)
        {
            log($"supervisor pid {supervisorPid} is already gone; stopping.");
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
}
