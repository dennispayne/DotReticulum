namespace DotReticulum.Transport;

/// <summary>Upstream Interface.py burst controls. All sample windows are bounded to 48 timestamps.</summary>
public sealed record InterfaceIngressOptions
{
    public bool Enabled { get; init; } = true;
    public bool EgressEnabled { get; init; }
    public TimeSpan BurstHold { get; init; } = TimeSpan.FromSeconds(15);
    public double BurstFrequencyNew { get; init; } = 3;
    public double BurstFrequency { get; init; } = 10;
    public double PathRequestBurstFrequencyNew { get; init; } = 3;
    public double PathRequestBurstFrequency { get; init; } = 8;
    public TimeSpan NewTime { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan BurstPenalty { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HeldReleaseInterval { get; init; } = TimeSpan.FromSeconds(5);
    public double EgressPathRequestFrequency { get; init; } = 5;
}

public sealed record InterfaceTrafficControlInfo(long ReceivedAnnounces, long ReceivedAnnounceBytes,
    long SentAnnounces, long SentAnnounceBytes, long ReceivedPathRequests, long ReceivedPathRequestBytes,
    long SentPathRequests, long SentPathRequestBytes, bool AnnounceBurstActive, bool PathRequestBurstActive,
    double IncomingAnnounceFrequency, double IncomingPathRequestFrequency, double OutgoingPathRequestFrequency,
    int AnnounceSamples, int PathRequestSamples, int OutgoingPathRequestSamples)
{
    public double OutgoingAnnounceFrequency { get; init; }
    public double? AnnounceBurstActivated { get; init; }
    public double? PathRequestBurstActivated { get; init; }
}

internal sealed class InterfaceTrafficController(TimeProvider time, InterfaceIngressOptions options)
{
    private readonly object _gate = new();
    private readonly double _created = Seconds(time);
    private InterfaceIngressOptions _options = options;
    private readonly Queue<double> _incomingAnnounces = new();
    private readonly Queue<double> _outgoingAnnounces = new();
    private readonly Queue<double> _incomingRequests = new();
    private readonly Queue<double> _outgoingRequests = new();
    private long _receivedAnnounces, _receivedAnnounceBytes, _sentAnnounces, _sentAnnounceBytes;
    private long _receivedRequests, _receivedRequestBytes, _sentRequests, _sentRequestBytes;
    private bool _announceBurst, _requestBurst;
    private double _announceActivated, _announceSustained, _requestActivated, _requestSustained, _heldRelease;
    private int _requestCooldown;
    private double Now => Seconds(time);
    private static double Seconds(TimeProvider time) => time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    public void Configure(InterfaceIngressOptions options) { lock (_gate) _options = options; }
    private static void Sample(Queue<double> samples, double now)
    {
        if (samples.Count == 48) samples.Dequeue();
        samples.Enqueue(now);
    }
    public void ReceivedAnnounce(int size)
    {
        lock (_gate) { _receivedAnnounces++; _receivedAnnounceBytes += size; Sample(_incomingAnnounces, Now); }
    }
    public void SentAnnounce(int size)
    {
        lock (_gate) { _sentAnnounces++; _sentAnnounceBytes += size; Sample(_outgoingAnnounces, Now); }
    }
    public void ReceivedPathRequest(int size)
    {
        lock (_gate) { _receivedRequests++; _receivedRequestBytes += size; Sample(_incomingRequests, Now); }
    }
    public void SentPathRequest(int size)
    {
        lock (_gate) { _sentRequests++; _sentRequestBytes += size; Sample(_outgoingRequests, Now); }
    }

    private static double Frequency(Queue<double> samples, double now, bool incoming, bool preemptive = false)
    {
        var count = samples.Count;
        if (count <= (incoming ? 2 : 1)) return 0;
        var span = now - samples.Peek();
        if (span > 10) samples.Dequeue();
        return span <= 0 ? 0 : (count + (preemptive ? 1 : 0)) / span;
    }

    public bool ShouldIngressLimitAnnounces()
    {
        lock (_gate)
        {
            if (!_options.Enabled) return false;
            var now = Now;
            var threshold = now - _created < _options.NewTime.TotalSeconds ? _options.BurstFrequencyNew : _options.BurstFrequency;
            var frequency = Frequency(_incomingAnnounces, now, incoming: true);
            if (_announceBurst)
            {
                if (frequency < threshold && now > _announceActivated + _options.BurstHold.TotalSeconds
                    && now > _announceSustained + _options.BurstHold.TotalSeconds)
                {
                    if (_incomingAnnounces.Count >= 2) _announceBurst = false;
                }
                else if (frequency >= threshold) _announceSustained = now;
                return true;
            }
            if (frequency <= threshold) return false;
            _announceBurst = true;
            _announceActivated = _announceSustained = now;
            _heldRelease = now + _options.BurstPenalty.TotalSeconds;
            return true;
        }
    }

    public bool ShouldIngressLimitPathRequests()
    {
        lock (_gate)
        {
            if (!_options.Enabled) return false;
            var now = Now;
            var threshold = now - _created < _options.NewTime.TotalSeconds
                ? _options.PathRequestBurstFrequencyNew : _options.PathRequestBurstFrequency;
            var frequency = Frequency(_incomingRequests, now, incoming: true);
            if (_requestBurst)
            {
                if (frequency < threshold && now > _requestActivated + _options.BurstHold.TotalSeconds
                    && now > _requestSustained + _options.BurstHold.TotalSeconds)
                {
                    if (_requestCooldown <= 0) _requestBurst = false;
                    else _requestCooldown--;
                }
                else
                {
                    _requestCooldown = 3;
                    if (frequency >= threshold) _requestSustained = now;
                }
                return true;
            }
            if (frequency <= threshold) return false;
            _requestBurst = true;
            _requestActivated = _requestSustained = now;
            _requestCooldown = 3;
            return true;
        }
    }

    public bool ShouldEgressLimitPathRequests()
    {
        lock (_gate) return _options.EgressEnabled
            && Frequency(_outgoingRequests, Now, incoming: false, preemptive: true) > _options.EgressPathRequestFrequency
            && _outgoingRequests.Count >= 2;
    }

    public bool ReserveHeldRelease()
    {
        lock (_gate)
        {
            var now = Now;
            if (now <= _heldRelease) return false;
            var threshold = now - _created < _options.NewTime.TotalSeconds ? _options.BurstFrequencyNew : _options.BurstFrequency;
            if (Frequency(_incomingAnnounces, now, incoming: true) >= threshold) return false;
            _heldRelease = now + _options.HeldReleaseInterval.TotalSeconds;
            return true;
        }
    }

    public InterfaceTrafficControlInfo Snapshot()
    {
        lock (_gate) return new InterfaceTrafficControlInfo(_receivedAnnounces, _receivedAnnounceBytes, _sentAnnounces,
            _sentAnnounceBytes, _receivedRequests, _receivedRequestBytes, _sentRequests, _sentRequestBytes,
            _announceBurst, _requestBurst, Frequency(_incomingAnnounces, Now, incoming: true),
            Frequency(_incomingRequests, Now, incoming: true), Frequency(_outgoingRequests, Now, incoming: false),
            _incomingAnnounces.Count, _incomingRequests.Count, _outgoingRequests.Count)
        {
            OutgoingAnnounceFrequency = Frequency(_outgoingAnnounces, Now, incoming: false),
            AnnounceBurstActivated = _announceActivated > 0 ? _announceActivated : null,
            PathRequestBurstActivated = _requestActivated > 0 ? _requestActivated : null,
        };
    }
}
