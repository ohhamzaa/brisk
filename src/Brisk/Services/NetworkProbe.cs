using System.Net;
using System.Net.NetworkInformation;

namespace Brisk.Services;

public readonly record struct NetSnapshot(
    double RouterMs, double RouterLoss, double InternetMs, double InternetLoss, double InternetJitter,
    double DownBytesPerSec, double UpBytesPerSec, bool Probing)
{
    public static readonly NetSnapshot Empty = new(double.NaN, 0, double.NaN, 0, double.NaN, 0, 0, false);
}

/// <summary>
/// Measures lag the way a gamer feels it: pings your router (Wi-Fi / local problems) and
/// 1.1.1.1 (internet / ISP problems) every 2 s, plus total download/upload speed.
/// These two pings are the only network traffic Brisk ever sends, and they can be turned off.
/// </summary>
public sealed class NetworkProbe : IDisposable
{
    private const int Keep = 10; // last 20 s
    private static readonly IPAddress Internet = IPAddress.Parse("1.1.1.1");
    private readonly Queue<double> _router = new(), _inet = new();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private IPAddress? _gateway;
    private DateTime _gatewayCheckedAt;
    private int _busy;

    public bool Enabled { get; set; } = true;

    public NetworkProbe()
    {
        _timer = new Timer(_ => _ = Probe(), null, 500, 2000);
    }

    private async Task Probe()
    {
        if (!Enabled || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (_gateway == null || DateTime.UtcNow - _gatewayCheckedAt > TimeSpan.FromSeconds(30))
            {
                _gateway = FindGateway();
                _gatewayCheckedAt = DateTime.UtcNow;
            }
            var router = _gateway != null ? PingOnce(_gateway) : Task.FromResult(double.NaN);
            var inet = PingOnce(Internet);
            double r = await router, i = await inet;
            lock (_gate)
            {
                if (_gateway != null) Push(_router, r);
                Push(_inet, i);
            }
        }
        catch { }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static async Task<double> PingOnce(IPAddress ip)
    {
        try
        {
            using var p = new Ping();
            var reply = await p.SendPingAsync(ip, 1000);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1; // -1 = lost
        }
        catch { return -1; }
    }

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > Keep) q.Dequeue();
    }

    /// <summary>Called by the monitor once a second with throughput it read from PDH.</summary>
    public NetSnapshot Sample(double down, double up)
    {
        if (!Enabled) return NetSnapshot.Empty with { DownBytesPerSec = down, UpBytesPerSec = up };
        lock (_gate)
        {
            var (rMs, rLoss, _) = Stats(_router);
            var (iMs, iLoss, iJit) = Stats(_inet);
            return new NetSnapshot(rMs, rLoss, iMs, iLoss, iJit, down, up, _inet.Count > 0);
        }
    }

    private static (double Avg, double Loss, double Jitter) Stats(Queue<double> q)
    {
        if (q.Count == 0) return (double.NaN, 0, double.NaN);
        var ok = q.Where(v => v >= 0).ToList();
        double loss = 1 - ok.Count / (double)q.Count;
        if (ok.Count == 0) return (double.NaN, loss, double.NaN);
        double avg = ok.Average();
        double jitter = ok.Count < 2 ? 0 : ok.Zip(ok.Skip(1), (a, b) => Math.Abs(a - b)).Average();
        return (avg, loss, jitter);
    }

    private static IPAddress? FindGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch { return null; }
    }

    public void Dispose() => _timer.Dispose();
}
