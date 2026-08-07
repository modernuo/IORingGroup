// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Diagnostics;
using System.Network;

namespace IORingGroup.Tests;

/// <summary>
/// Wake() contract. The load-bearing guarantee is stickiness: a caller decides it is idle and then
/// blocks, so a Wake() arriving in that window must make the wait return rather than be lost.
/// Without that, a sleeping event loop can miss cross-thread work until its timeout expires.
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

        // A second wait with no pending wake should actually wait rather than return instantly,
        // which is what proves the signal is edge-like and not a counter that keeps firing.
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

    // SkippableFact, not Fact: Skip.IfNot throws a SkipException, which xunit only understands as
    // a skip on a skippable test. Under a plain Fact it surfaces as a failure on every
    // non-Windows platform, which is exactly where this is meant to be quietly ignored.
    [SkippableFact]
    public void ShortWaitBeatsDefaultTimerResolution()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Timer resolution quantisation is a Windows concern.");

        using var ring = CreateRing();

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

        // The Windows default timer resolution is 15.625ms, so without the high-resolution
        // waitable timer a 2ms request routinely sleeps ~15ms. Threshold is deliberately loose
        // so a loaded CI agent does not make this flaky.
        Assert.True(
            average < 10,
            $"A 2ms wait averaged {average:F2}ms; the high-resolution timer is not in effect."
        );
    }
}
