using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Fserp.FleetOperations.IntegrationTests.Fleet;

/// <summary>
/// A byte-for-byte TCP forwarder that sits between the cache client and the real Redis, so that a Redis
/// outage can be induced on any machine. The cache is configured to connect to this forwarder's loopback
/// port; the forwarder opens one upstream connection per accepted connection and relays both directions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a forwarder and not <c>container.StopAsync()</c>.</b> Stopping the container works only when
/// Docker is present, which would make the outage test unrunnable against a hosted Redis reached through
/// <see cref="RedisFixture.ConnectionStringVariable"/> — exactly the environment the escape hatch exists
/// for. Closing the forwarder's listener and dropping its live sockets is a real transport failure:
/// StackExchange.Redis sees its connection reset and every later connect attempt refused, and raises
/// <c>RedisConnectionException</c> from inside the cache adapter. Nothing is mocked, stubbed or
/// intercepted: the exception the cache sees is produced by the operating system's TCP stack.
/// </para>
/// <para>
/// The forwarder is transparent while it is up, including for TLS, because it relays bytes and never
/// parses RESP. A test proves that transparency rather than assuming it: see
/// <see cref="ConnectionsAccepted"/>, which the outage test asserts is non-zero before the cut.
/// </para>
/// </remarks>
internal sealed class RedisOutageProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly ConcurrentBag<TcpClient> _live = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;
    private int _accepted;

    private RedisOutageProxy(string upstreamHost, int upstreamPort, string upstreamConnectionString)
    {
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        ConnectionString = ReplaceEndpoint(upstreamConnectionString, LocalPort);
        _acceptLoop = Task.Run(() => AcceptAsync(_stopping.Token));
    }

    /// <summary>The configuration string the cache must use: the original one with its endpoint replaced.</summary>
    /// <remarks>
    /// Every other option of the original string — password, ssl, abortConnect, timeouts — is kept
    /// verbatim, so the client is configured exactly as the deployment configured it.
    /// </remarks>
    public string ConnectionString { get; }

    /// <summary>The loopback port the cache connects to.</summary>
    public int LocalPort { get; }

    /// <summary>How many connections the cache has opened through the forwarder.</summary>
    public int ConnectionsAccepted => Volatile.Read(ref _accepted);

    /// <summary>Whether <see cref="Cut"/> has been called.</summary>
    public bool IsCut { get; private set; }

    /// <summary>
    /// Starts a forwarder in front of the Redis named by <paramref name="upstreamConnectionString"/>.
    /// </summary>
    /// <param name="upstreamConnectionString">The real Redis configuration string.</param>
    public static RedisOutageProxy Start(string upstreamConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamConnectionString);
        var (host, port) = ReadEndpoint(upstreamConnectionString);
        return new RedisOutageProxy(host, port, upstreamConnectionString);
    }

    /// <summary>
    /// Induces the outage: the listener stops accepting and every live socket in both directions is
    /// dropped. From this moment the cache's connection is reset and every reconnection attempt is
    /// refused by the loopback stack, which is what a stopped Redis looks like to a client.
    /// </summary>
    public void Cut()
    {
        IsCut = true;
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Already stopped; the outage is the state we want, not the call that produced it.
        }

        while (_live.TryTake(out var client))
        {
            Drop(client);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        Cut();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _stopping.Dispose();
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !IsCut)
        {
            TcpClient inbound;
            try
            {
                inbound = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            Interlocked.Increment(ref _accepted);
            _live.Add(inbound);
            _ = Task.Run(() => RelayAsync(inbound, cancellationToken), CancellationToken.None);
        }
    }

    private async Task RelayAsync(TcpClient inbound, CancellationToken cancellationToken)
    {
        TcpClient? upstream = null;
        try
        {
            upstream = new TcpClient();
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, cancellationToken);
            _live.Add(upstream);
            if (IsCut)
            {
                // Raced with the cut: the connection must not survive it.
                Drop(inbound);
                Drop(upstream);
                return;
            }

            inbound.NoDelay = true;
            upstream.NoDelay = true;
            var clientToServer = inbound.GetStream().CopyToAsync(upstream.GetStream(), cancellationToken);
            var serverToClient = upstream.GetStream().CopyToAsync(inbound.GetStream(), cancellationToken);
            await Task.WhenAny(clientToServer, serverToClient);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Either end going away is the normal end of a relayed connection, and after Cut it is the
            // point of the exercise.
        }
        finally
        {
            Drop(inbound);
            if (upstream is not null)
            {
                Drop(upstream);
            }
        }
    }

    private static void Drop(TcpClient client)
    {
        try
        {
            // A reset rather than a graceful close: a stopped server does not say goodbye.
            var socket = client.Client;
            if (socket is not null)
            {
                socket.Close(0);
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // Already gone.
        }

        try
        {
            client.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// The host and port of the first endpoint in a StackExchange.Redis configuration string. An endpoint
    /// is a comma-separated token that carries no <c>=</c>; everything else is an option.
    /// </summary>
    internal static (string Host, int Port) ReadEndpoint(string connectionString)
    {
        foreach (var token in Tokens(connectionString))
        {
            if (token.Contains('=', StringComparison.Ordinal))
            {
                continue;
            }

            var endpoint = StripScheme(token);
            if (endpoint.StartsWith('['))
            {
                // IPv6 literal: [::1]:6379
                var close = endpoint.IndexOf(']', StringComparison.Ordinal);
                if (close > 0)
                {
                    var address = endpoint[1..close];
                    var remainder = endpoint[(close + 1)..];
                    return (address, remainder.StartsWith(':') ? ParsePort(remainder[1..]) : 6379);
                }
            }

            var separator = endpoint.LastIndexOf(':');
            return separator < 0
                ? (endpoint, 6379)
                : (endpoint[..separator], ParsePort(endpoint[(separator + 1)..]));
        }

        throw new ArgumentException(
            "The Redis configuration string names no endpoint. Expected 'host:port[,option=value...]'.",
            nameof(connectionString));
    }

    /// <summary>
    /// The same configuration string with its endpoints replaced by one loopback endpoint. Options are
    /// preserved verbatim, including a password, which <c>ConfigurationOptions.ToString()</c> would drop.
    /// </summary>
    internal static string ReplaceEndpoint(string connectionString, int localPort)
    {
        var parts = new List<string>();
        var replaced = false;
        foreach (var token in Tokens(connectionString))
        {
            if (token.Contains('=', StringComparison.Ordinal))
            {
                parts.Add(token);
                continue;
            }

            if (replaced)
            {
                // A second endpoint would let the client bypass the forwarder, and with it the outage.
                continue;
            }

            parts.Insert(0, string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{localPort}"));
            replaced = true;
        }

        if (!replaced)
        {
            parts.Insert(0, string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{localPort}"));
        }

        return string.Join(',', parts);
    }

    private static IEnumerable<string> Tokens(string connectionString) =>
        connectionString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string StripScheme(string endpoint)
    {
        var scheme = endpoint.IndexOf("://", StringComparison.Ordinal);
        return scheme < 0 ? endpoint : endpoint[(scheme + 3)..];
    }

    private static int ParsePort(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 6379;
}
