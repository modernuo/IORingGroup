using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TestClient;

/// <summary>
/// Simple TCP client for testing the IORingGroup echo server.
/// </summary>
public class Program
{
    private const int DefaultPort = 5000;
    private const string DefaultHost = "127.0.0.1";

    // Flag to pause background receiver during stress test
    private static volatile bool _inStressTest = false;

    public static async Task Main(string[] args)
    {
        var host = args.Length > 0 ? args[0] : DefaultHost;
        var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : DefaultPort;

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
            using var cts = new CancellationTokenSource();

            // Start receive task for interactive mode
            var receiveTask = ReceiveAsync(stream, buffer, cts.Token);

            // Read user input and send
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
                    cts.Cancel();
                    break;
                }

                if (input.StartsWith("stress ", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(input.AsSpan(7), out var count))
                    {
                        // Pause background receiver and run stress test
                        _inStressTest = true;
                        await Task.Delay(50); // Give receiver time to pause
                        await StressTest(stream, count);
                        _inStressTest = false;
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
                Console.WriteLine($"Sent: {input} ({data.Length} bytes)");
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

    private static async Task ReceiveAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Skip reading when stress test is running
                if (_inStressTest)
                {
                    await Task.Delay(10, ct);
                    continue;
                }

                // Use a short read timeout so we can check the stress test flag
                if (!stream.DataAvailable)
                {
                    await Task.Delay(10, ct);
                    continue;
                }

                var bytesRead = await stream.ReadAsync(buffer, ct);
                if (bytesRead == 0)
                {
                    Console.WriteLine("\nServer disconnected.");
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"\nReceived: {message} ({bytesRead} bytes)");
                Console.Write("> ");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (IOException)
        {
            // Stream closed
        }
    }

    private static async Task StressTest(NetworkStream stream, int count)
    {
        Console.WriteLine($"Starting stress test with {count} messages...");
        Console.WriteLine($"[DEBUG] Stream.DataAvailable before test: {stream.DataAvailable}");

        // Drain any pending data first
        var drainBuffer = new byte[4096];
        while (stream.DataAvailable)
        {
            var drained = await stream.ReadAsync(drainBuffer);
            Console.WriteLine($"[DEBUG] Drained {drained} bytes of stale data");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var buffer = new byte[4096];
        var totalSent = 0;
        var totalReceived = 0;

        for (var i = 0; i < count; i++)
        {
            var message = $"Stress test message {i + 1}/{count}";
            var data = Encoding.UTF8.GetBytes(message);

            Console.WriteLine($"[DEBUG] Sending message {i + 1}/{count} ({data.Length} bytes)");
            await stream.WriteAsync(data);
            await stream.FlushAsync();
            totalSent += data.Length;

            Console.WriteLine($"[DEBUG] Awaiting response {i + 1}/{count}");

            // Read with timeout
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var bytesRead = await stream.ReadAsync(buffer, readCts.Token);
                if (bytesRead == 0)
                {
                    Console.WriteLine($"[DEBUG] Server closed connection!");
                    break;
                }
                totalReceived += bytesRead;
                var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"[DEBUG] Received response {i + 1}/{count}: {response} ({bytesRead} bytes)");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[DEBUG] Timeout waiting for response {i + 1}/{count}!");
                break;
            }
        }

        sw.Stop();

        Console.WriteLine("Stress test complete!");
        Console.WriteLine($"  Messages: {count}");
        Console.WriteLine($"  Sent: {totalSent:N0} bytes");
        Console.WriteLine($"  Received: {totalReceived:N0} bytes");
        if (sw.ElapsedMilliseconds > 0)
        {
            Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0} ms");
            Console.WriteLine($"  Rate: {count * 1000.0 / sw.ElapsedMilliseconds:N0} messages/sec");
            Console.WriteLine($"  Throughput: {(totalSent + totalReceived) * 1000.0 / sw.ElapsedMilliseconds / 1024:N2} KB/sec");
        }
    }
}
