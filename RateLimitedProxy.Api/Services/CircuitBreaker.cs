using Microsoft.Extensions.Options;

namespace RateLimitedProxy.Api.Services;

public enum AdmitOutCome
{
    Allowed,
    Trial,
    Rejected,
}

public readonly record struct Admission(AdmitOutCome Outcome, int RetryAfterSeconds);

public class CircuitBreaker
{
    private enum State
    {
        Closed,
        Open,
        HalfOpen,
    }

    private readonly object _lock = new();
    private readonly int _threshold;
    private readonly TimeSpan _breakDuration;
    private readonly TimeProvider _timeProvider;
    private State _state = State.Closed;
    private int _failures;
    private long _openedAt;
    private bool _trialClaimed;

    public CircuitBreaker(IOptions<CircuitBreakerOptions> options, TimeProvider timeProvider)
    {
        _threshold = options.Value.FailureThreshold;
        _breakDuration = TimeSpan.FromSeconds(options.Value.BreakDurationSeconds);
        _timeProvider = timeProvider;
    }

    public Admission Admit()
    {
        lock (_lock)
        {
            if (_state == State.Open)
            {
                var elapsed = _timeProvider.GetElapsedTime(_openedAt);
                if (elapsed < _breakDuration)
                {
                    var left = (int)
                        Math.Max(1, Math.Ceiling((_breakDuration - elapsed).TotalSeconds));
                    return new Admission(AdmitOutCome.Rejected, left);
                }
                _state = State.HalfOpen;
                _trialClaimed = false;
            }
            if (_state == State.HalfOpen)
            {
                if (_trialClaimed)
                    return new Admission(AdmitOutCome.Rejected, 1);
                _trialClaimed = true;
                return new Admission(AdmitOutCome.Trial, 0);
            }
            return new Admission(AdmitOutCome.Allowed, 0);
        }
    }

    public void RecordSuccess(bool wasTrial)
    {
        lock (_lock)
        {
            if (wasTrial)
            {
                if (_state == State.HalfOpen)
                    Close();
                return;
            }
            if (_state == State.Closed)
                _failures = 0;
        }
    }

    public void RecordFailure(bool wasTrial)
    {
        lock (_lock)
        {
            if (wasTrial)
            {
                if (_state == State.HalfOpen)
                    Open();
                return;
            }
            if (_state != State.Closed)
            {
                return;
            }
            if (++_failures >= _threshold)
                Open();
        }
    }

    public void RecordNeutral(bool wasTrial)
    {
        lock (_lock)
        {
            if (wasTrial && _state == State.HalfOpen)
                _trialClaimed = false;
        }
    }

    private void Open()
    {
        _state = State.Open;
        _openedAt = _timeProvider.GetTimestamp();
        _failures = 0;
        _trialClaimed = false;
    }

    private void Close()
    {
        _state = State.Closed;
        _failures = 0;
        _trialClaimed = false;
    }
}
