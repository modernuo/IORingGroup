// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace TestClient;

public class Program
{
    private const int DefaultPort = 5000;
    private const string DefaultHost = "127.0.0.1";

    public static async Task Main(string[] args)
    {
        var host = DefaultHost;
        var port = DefaultPort;
        var benchmarkCount = 0;
        var benchmarkConnections = 1;
        var maxConcurrent = 1000; // Default max concurrent connections
        nint cpuAffinity = 0;

        for (var i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--host" || args[i] == "-h") && i + 1 < args.Length)
            {
                host = args[++i];
            }
            else if ((args[i] == "--port" || args[i] == "-P") && i + 1 < args.Length)
            {
                port = int.Parse(args[++i]);
            }
            else if ((args[i] == "--benchmark" || args[i] == "-b") && i + 1 < args.Length)
            {
                benchmarkCount = int.Parse(args[++i]);
            }
            else if ((args[i] == "--connections" || args[i] == "-c") && i + 1 < args.Length)
            {
                benchmarkConnections = int.Parse(args[++i]);
            }
            else if ((args[i] == "--concurrent" || args[i] == "-C") && i + 1 < args.Length)
            {
                maxConcurrent = int.Parse(args[++i]);
            }
            else if ((args[i] == "--affinity" || args[i] == "-a") && i + 1 < args.Length)
            {
                var affinityStr = args[++i];
                if (affinityStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    cpuAffinity = (nint)Convert.ToInt64(affinityStr[2..], 16);
                else
                    cpuAffinity = (nint)long.Parse(affinityStr);
            }
        }

        // Set CPU affinity if specified
        if (cpuAffinity != 0)
        {
            try
            {
                Process.GetCurrentProcess().ProcessorAffinity = cpuAffinity;
                Console.WriteLine($"CPU affinity set to: 0x{cpuAffinity:X}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to set CPU affinity: {ex.Message}");
            }
        }

        if (benchmarkCount > 0)
        {
            await RunBenchmark(host, port, benchmarkCount, benchmarkConnections, maxConcurrent);
            return;
        }

        await RunInteractive(host, port);
    }

    private static async Task RunInteractive(string host, int port)
    {
        Console.WriteLine("IORingGroup Test Client");
        Console.WriteLine($"Connecting to {host}:{port}...");
        Console.WriteLine("Commands:");
        Console.WriteLine("  Type a message and press Enter to send");
        Console.WriteLine("  Type 'quit' or 'exit' to disconnect");
        Console.WriteLine("  Type 'stress N' to send N messages rapidly");
        Console.WriteLine();

        try
        {
            using var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync(host, port);
            Console.WriteLine("Connected!\n");

            using var stream = client.GetStream();
            var buffer = new byte[4096];

            while (true)
            {
                Console.Write("> ");
                var input = Console.ReadLine();

                if (string.IsNullOrEmpty(input))
                    continue;

                if (input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                    input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Disconnecting...");
                    break;
                }

                if (input.StartsWith("stress ", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(input.AsSpan(7), out var count))
                    {
                        await StressTest(stream, buffer, count);
                    }
                    else
                    {
                        Console.WriteLine("Usage: stress <count>");
                    }
                    continue;
                }

                // Send the message
                var data = Encoding.UTF8.GetBytes(input);
                await stream.WriteAsync(data);

                // Wait for response
                var bytesRead = await stream.ReadAsync(buffer);
                var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"Response: {response}");
            }
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }

        Console.WriteLine("Goodbye!");
    }

    private static async Task StressTest(NetworkStream stream, byte[] buffer, int count)
    {
        Console.WriteLine($"Starting stress test with {count} messages...");

        var sw = Stopwatch.StartNew();
        var totalSent = 0;
        var totalReceived = 0;

        for (var i = 0; i < count; i++)
        {
            var message = $"Stress message {i + 1:D6}";
            var data = Encoding.UTF8.GetBytes(message);

            await stream.WriteAsync(data);
            totalSent += data.Length;

            var bytesRead = await stream.ReadAsync(buffer);
            totalReceived += bytesRead;
        }

        sw.Stop();

        Console.WriteLine("Stress test complete!");
        Console.WriteLine($"  Messages: {count:N0}");
        Console.WriteLine($"  Sent: {totalSent:N0} bytes");
        Console.WriteLine($"  Received: {totalReceived:N0} bytes");
        Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0} ms");
        if (sw.ElapsedMilliseconds > 0)
        {
            Console.WriteLine($"  Rate: {count * 1000.0 / sw.ElapsedMilliseconds:N0} msg/s");
            Console.WriteLine($"  Throughput: {(totalSent + totalReceived) * 1000.0 / sw.ElapsedMilliseconds / 1024:N2} KB/s");
        }
    }

    private static async Task RunBenchmark(string host, int port, int messagesPerConnection, int connectionCount, int maxConcurrent)
    {
        Console.WriteLine("IORingGroup Benchmark Client");
        Console.WriteLine($"Host: {host}:{port}");
        Console.WriteLine($"Connections: {connectionCount}");
        Console.WriteLine($"Messages per connection: {messagesPerConnection:N0}");
        Console.WriteLine($"Max concurrent: {maxConcurrent}");
        Console.WriteLine();

        // Use semaphore to limit concurrent connection attempts
        using var semaphore = new SemaphoreSlim(maxConcurrent);

        var tasks = new Task<BenchmarkResult>[connectionCount];
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < connectionCount; i++)
        {
            var connId = i;
            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    return await RunBenchmarkConnection(host, port, messagesPerConnection, connId);
                }
                finally
                {
                    semaphore.Release();
                }
            });
        }

        var results = await Task.WhenAll(tasks);
        sw.Stop();

        var totalMessages = results.Sum(r => r.Messages);
        var totalBytes = results.Sum(r => r.Bytes);
        var successfulConnections = results.Count(r => r.Success);

        Console.WriteLine();
        Console.WriteLine("=== Benchmark Results ===");
        Console.WriteLine($"Successful connections: {successfulConnections}/{connectionCount}");
        Console.WriteLine($"Total messages: {totalMessages:N0}");
        Console.WriteLine($"Total bytes: {totalBytes:N0}");
        Console.WriteLine($"Total time: {sw.ElapsedMilliseconds:N0} ms");
        if (sw.ElapsedMilliseconds > 0)
        {
            Console.WriteLine($"Rate: {totalMessages * 1000.0 / sw.ElapsedMilliseconds:N0} msg/s");
            Console.WriteLine($"Throughput: {totalBytes * 1000.0 / sw.ElapsedMilliseconds / 1024 / 1024:N2} MB/s");
        }
    }

    private static async Task<BenchmarkResult> RunBenchmarkConnection(string host, int port, int messageCount, int connectionId)
    {
        var result = new BenchmarkResult();
        const int maxRetries = 5;

        for (var retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                using var client = new TcpClient();
                client.NoDelay = true;
                client.SendBufferSize = 65536;
                client.ReceiveBufferSize = 65536;
                // Set SO_LINGER to 0 to avoid TIME_WAIT accumulation
                client.LingerState = new LingerOption(true, 0);
                await client.ConnectAsync(host, port);

                using var stream = client.GetStream();
                var message = $"Benchmark message from connection {connectionId:D4}";
                var data = Encoding.UTF8.GetBytes(message);

                // Pipelined I/O: send and receive concurrently
                long bytesSent = 0;
                long bytesReceived = 0;
                long messagesReceived = 0;

                // Sender task - sends all messages as fast as possible
                var sendTask = Task.Run(async () =>
                {
                    for (var i = 0; i < messageCount; i++)
                    {
                        await stream.WriteAsync(data);
                        Interlocked.Add(ref bytesSent, data.Length);
                    }
                    // Signal end of sending by shutting down the send side
                    // (Don't shutdown - server needs to keep receiving)
                });

                // Receiver task - receives all responses as fast as possible
                var recvTask = Task.Run(async () =>
                {
                    var buffer = new byte[65536];  // Larger buffer for batched reads
                    var totalExpected = messageCount * data.Length;
                    long totalReceived = 0;

                    while (totalReceived < totalExpected)
                    {
                        var bytesRead = await stream.ReadAsync(buffer);
                        if (bytesRead == 0) break;  // Connection closed
                        totalReceived += bytesRead;
                        Interlocked.Add(ref bytesReceived, bytesRead);
                        // Count complete messages (approximate - assumes no partial messages)
                        Interlocked.Add(ref messagesReceived, bytesRead / data.Length);
                    }
                });

                await Task.WhenAll(sendTask, recvTask);

                result.Bytes = bytesSent + bytesReceived;
                result.Messages = messagesReceived;
                result.Success = true;
                return result;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused && retry < maxRetries - 1)
            {
                // Retry with exponential backoff
                await Task.Delay(10 * (1 << retry));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection {connectionId} error: {ex.Message}");
                return result;
            }
        }

        return result;
    }

    private struct BenchmarkResult
    {
        public bool Success;
        public long Messages;
        public long Bytes;
    }
}
