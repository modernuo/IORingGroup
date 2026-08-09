// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Diagnostics;
using System.Network;

namespace IORingGroup.Tests;

/// <summary>
/// Wake() contract. The load-bearing guarantee is stickiness: a Wake() arriving between an idle
/// check and the subsequent wait must make that wait return rather than be lost.
/// </summary>
public class WakeTests
{
    private static IIORingGroup CreateRing() =>
        System.Network.IORingGroup.Create(queueSize: 256, maxConnections: 64, maxOutstandingSends: 8);

    [Fact]
    public void WakeBeforeWaitIsNotLost()
    {
        using var ring = CreateRing();

        ring.Wake();

        var sw = Stopwatch.StartNew();
        ring.WaitForCompletion(2000);
        sw.Stop();

        Assert.True(
            sw.ElapsedMilliseconds < 500,
            $"WaitForCompletion blocked for {sw.ElapsedMilliseconds}ms after a preceding Wake(); the wake was lost."
        );
    }

    [Fact]
    public void WakeFromAnotherThreadUnblocksWait()
    {
        using var ring = CreateRing();

        using var ready = new ManualResetEventSlim(false);
        var waker = new Thread(
            () =>
            {
                ready.Wait();
                Thread.Sleep(100);
                ring.Wake();
            }
        );

        waker.Start();
        ready.Set();

        var sw = Stopwatch.StartNew();
        ring.WaitForCompletion(5000);
        sw.Stop();
        waker.Join();

        Assert.True(
            sw.ElapsedMilliseconds < 2000,
            $"WaitForCompletion blocked for {sw.ElapsedMilliseconds}ms; expected to be woken after ~100ms."
        );
    }

    [Fact]
    public void RepeatedWakesDoNotAccumulate()
    {
        using var ring = CreateRing();

        for (var i = 0; i < 100; i++)
        {
            ring.Wake();
        }

        // Drain whatever the wakes queued.
        ring.WaitForCompletion(1);

        // A second wait with no pending wake must actually wait, proving the signal does not
        // accumulate.
        var sw = Stopwatch.StartNew();
        ring.WaitForCompletion(60);
        sw.Stop();

        Assert.True(
            sw.ElapsedMilliseconds >= 25,
            $"WaitForCompletion returned after {sw.ElapsedMilliseconds}ms; stale wakes are still pending."
        );
    }

    [Fact]
    public void WakeAfterDisposeIsSafe()
    {
        var ring = CreateRing();
        ring.Dispose();

        ring.Wake();
    }

    // SkippableFact: under a plain Fact, Skip.IfNot surfaces as a failure on non-Windows.
    [SkippableFact]
    public void ShortWaitBeatsDefaultTimerResolution()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Timer resolution quantisation is a Windows concern.");

        using var ring = CreateRing();

        // A host without high-resolution waits quantises to 15.625ms by design.
        Skip.IfNot(ring.SupportsHighResolutionWait, "This host cannot honour short waits, by design.");

        // Warm up so first-call costs stay out of the measurement.
        ring.WaitForCompletion(2);

        const int iterations = 20;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            ring.WaitForCompletion(2);
        }

        sw.Stop();

        var average = sw.Elapsed.TotalMilliseconds / iterations;

        // Loose threshold (well under the 15.625ms default resolution) to stay CI-stable.
        Assert.True(
            average < 10,
            $"A 2ms wait averaged {average:F2}ms; the high-resolution timer is not in effect."
        );
    }
}
