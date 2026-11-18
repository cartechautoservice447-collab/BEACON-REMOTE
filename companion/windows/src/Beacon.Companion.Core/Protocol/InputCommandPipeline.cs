using Beacon.Companion.Core.Security;

namespace Beacon.Companion.Core.Protocol;

public sealed record InputFloodOptions
{
    public int MaxMessageBytes { get; init; } = RemoteProtocolSerializer.DefaultMaxMessageBytes;
    public int MaxPointerMovesPerSecond { get; init; } = 240;
    public int PointerMoveBurstCapacity { get; init; } = 30;
    public int MaxDiscreteEventsPerSecond { get; init; } = 120;
    public int DiscreteBurstCapacity { get; init; } = 40;
    public int MaxMalformedCommandsPerSecond { get; init; } = 20;
    public int MalformedBurstCapacity { get; init; } = 20;
    public TimeSpan MalformedCooldownDuration { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxQueuedDiscreteEvents { get; init; } = 128;
    public int MaxQueuedCriticalReleases { get; init; } = 128;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPointerMovesPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PointerMoveBurstCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDiscreteEventsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(DiscreteBurstCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMalformedCommandsPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MalformedBurstCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MalformedCooldownDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxQueuedDiscreteEvents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxQueuedCriticalReleases);
    }
}

public enum InputCommandDisposition
{
    Accepted = 0,
    AcceptedCoalesced = 1,
    RejectedProtocol = 2,
    RejectedInvalidCommand = 3,
    RejectedUnauthorized = 4,
    RejectedRateLimited = 5,
    RejectedMalformedFlood = 6,
}

public sealed record InputPipelineProcessResult(
    bool Accepted,
    InputCommandDisposition Disposition,
    RemoteInputEvent? Command = null,
    ProtocolParseFailureReason ParseFailure = ProtocolParseFailureReason.None,
    InputValidationFailureReason ValidationFailure = InputValidationFailureReason.None,
    InputRejectionReason AuthorizationFailure = InputRejectionReason.None,
    string? Detail = null);

public sealed record InputPipelineMetrics(
    long TotalMessages,
    long AcceptedDiscreteCount,
    long AcceptedCriticalReleaseCount,
    long AcceptedResetCount,
    long CoalescedMoveCount,
    long DroppedRateLimitedCount,
    long RejectedProtocolCount,
    long RejectedValidationCount,
    long RejectedAuthorizationCount,
    long MalformedFloodBlockCount,
    bool HasPendingMove,
    int PendingDiscreteCount,
    int PendingReleaseCount);

/// <summary>
/// Combines bounded wire deserialization, syntactic/domain command validation,
/// separated session authorization, token-bucket flood protection, safe pointer-move coalescing,
/// and non-droppable critical release/reset prioritization using strictly bounded O(1) structures.
/// </summary>
public sealed class InputCommandPipeline
{
    private readonly InputFloodOptions _options;
    private readonly IInputCommandValidator _validator;
    private readonly IInputPermissionEvaluator _permissionEvaluator;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _sync = new();

    private readonly TokenBucket _moveBucket;
    private readonly TokenBucket _discreteBucket;
    private readonly TokenBucket _malformedBucket;

    private readonly Queue<RemoteInputEvent> _discreteQueue = new();
    private readonly Queue<RemoteInputEvent> _criticalReleaseQueue = new();
    private PointerMoveAbsEvent? _pendingMove;
    private bool _pendingReset;
    private DateTimeOffset _malformedCooldownUntilUtc = DateTimeOffset.MinValue;

    private long _totalMessages;
    private long _acceptedDiscreteCount;
    private long _acceptedCriticalReleaseCount;
    private long _acceptedResetCount;
    private long _coalescedMoveCount;
    private long _droppedRateLimitedCount;
    private long _rejectedProtocolCount;
    private long _rejectedValidationCount;
    private long _rejectedAuthorizationCount;
    private long _malformedFloodBlockCount;

    public InputCommandPipeline(
        InputFloodOptions? options = null,
        IInputCommandValidator? validator = null,
        IInputPermissionEvaluator? permissionEvaluator = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new InputFloodOptions();
        _options.Validate();
        _validator = validator ?? new InputCommandValidator();
        _permissionEvaluator = permissionEvaluator ?? new InputPermissionEvaluator(_validator);
        _timeProvider = timeProvider ?? TimeProvider.System;

        _moveBucket = new TokenBucket(_options.PointerMoveBurstCapacity, _options.MaxPointerMovesPerSecond, _timeProvider);
        _discreteBucket = new TokenBucket(_options.DiscreteBurstCapacity, _options.MaxDiscreteEventsPerSecond, _timeProvider);
        _malformedBucket = new TokenBucket(_options.MalformedBurstCapacity, _options.MaxMalformedCommandsPerSecond, _timeProvider);
    }

    public InputPipelineProcessResult ProcessWireMessage(string? rawJson, SessionAuthorizationContext authContext)
    {
        ArgumentNullException.ThrowIfNull(authContext);

        if (IsInMalformedCooldown())
        {
            return RecordMalformedFloodBlock();
        }

        var parsed = RemoteProtocolSerializer.TryDeserialize(rawJson, _options.MaxMessageBytes);
        if (!parsed.Succeeded || parsed.Command is null)
        {
            return RecordProtocolFailure(parsed);
        }

        return ProcessParsedCommand(parsed.Command, authContext);
    }

    public InputPipelineProcessResult ProcessWireMessage(
        ReadOnlySpan<byte> utf8Json,
        SessionAuthorizationContext authContext)
    {
        ArgumentNullException.ThrowIfNull(authContext);

        if (IsInMalformedCooldown())
        {
            return RecordMalformedFloodBlock();
        }

        var parsed = RemoteProtocolSerializer.TryDeserialize(utf8Json, _options.MaxMessageBytes);
        if (!parsed.Succeeded || parsed.Command is null)
        {
            return RecordProtocolFailure(parsed);
        }

        return ProcessParsedCommand(parsed.Command, authContext);
    }

    public InputPipelineProcessResult ProcessParsedCommand(
        RemoteInputEvent command,
        SessionAuthorizationContext authContext)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(authContext);

        // Step 1: Pure syntactic & domain validation (independent of session auth).
        var validation = _validator.Validate(command);
        if (!validation.IsValid)
        {
            lock (_sync)
            {
                _totalMessages++;
                _rejectedValidationCount++;
            }

            return new InputPipelineProcessResult(
                Accepted: false,
                Disposition: InputCommandDisposition.RejectedInvalidCommand,
                Command: command,
                ValidationFailure: validation.Reason,
                Detail: validation.Detail);
        }

        // Step 2: Separated session authorization.
        var authDecision = _permissionEvaluator.Authorize(authContext, command);
        if (!authDecision.Allowed)
        {
            lock (_sync)
            {
                _totalMessages++;
                _rejectedAuthorizationCount++;
            }

            return new InputPipelineProcessResult(
                Accepted: false,
                Disposition: InputCommandDisposition.RejectedUnauthorized,
                Command: command,
                AuthorizationFailure: authDecision.Reason,
                Detail: authDecision.Detail);
        }

        // Step 3: Flood control, critical release preservation, and O(1) move coalescing.
        lock (_sync)
        {
            _totalMessages++;

            if (command is InputResetEvent)
            {
                _pendingMove = null;
                _discreteQueue.Clear();
                _criticalReleaseQueue.Clear();
                _pendingReset = true;
                _acceptedResetCount++;

                return new InputPipelineProcessResult(
                    Accepted: true,
                    Disposition: InputCommandDisposition.Accepted,
                    Command: command);
            }

            if (command is PointerMoveAbsEvent move)
            {
                var wasOverwrite = _pendingMove is not null;
                _pendingMove = move;
                if (wasOverwrite)
                {
                    _coalescedMoveCount++;
                    return new InputPipelineProcessResult(
                        Accepted: true,
                        Disposition: InputCommandDisposition.AcceptedCoalesced,
                        Command: move);
                }

                return new InputPipelineProcessResult(
                    Accepted: true,
                    Disposition: InputCommandDisposition.Accepted,
                    Command: move);
            }

            if (IsCriticalRelease(command))
            {
                if (_criticalReleaseQueue.Count >= _options.MaxQueuedCriticalReleases)
                {
                    _droppedRateLimitedCount++;
                    return new InputPipelineProcessResult(
                        Accepted: false,
                        Disposition: InputCommandDisposition.RejectedRateLimited,
                        Command: command,
                        Detail: "Critical release queue capacity reached.");
                }

                // Preserve preceding pointer position before button/key release when capacity allows,
                // without ever blocking or displacing the critical release itself.
                if (_pendingMove is not null &&
                    _discreteQueue.Count < _options.MaxQueuedDiscreteEvents &&
                    _moveBucket.TryConsume())
                {
                    _discreteQueue.Enqueue(_pendingMove);
                    _pendingMove = null;
                }

                _criticalReleaseQueue.Enqueue(command);
                _acceptedCriticalReleaseCount++;

                return new InputPipelineProcessResult(
                    Accepted: true,
                    Disposition: InputCommandDisposition.Accepted,
                    Command: command);
            }

            if (_discreteQueue.Count >= _options.MaxQueuedDiscreteEvents || !_discreteBucket.TryConsume())
            {
                _droppedRateLimitedCount++;
                return new InputPipelineProcessResult(
                    Accepted: false,
                    Disposition: InputCommandDisposition.RejectedRateLimited,
                    Command: command,
                    Detail: "Discrete input rate limit exceeded.");
            }

            if (_pendingMove is not null &&
                _discreteQueue.Count + 1 < _options.MaxQueuedDiscreteEvents &&
                _moveBucket.TryConsume())
            {
                _discreteQueue.Enqueue(_pendingMove);
                _pendingMove = null;
            }

            _discreteQueue.Enqueue(command);
            _acceptedDiscreteCount++;

            return new InputPipelineProcessResult(
                Accepted: true,
                Disposition: InputCommandDisposition.Accepted,
                Command: command);
        }
    }

    public IReadOnlyList<RemoteInputEvent> DrainReadyEvents()
    {
        lock (_sync)
        {
            if (_pendingReset)
            {
                _pendingReset = false;
                return [new InputResetEvent()];
            }

            if (_discreteQueue.Count == 0 && _criticalReleaseQueue.Count == 0 && _pendingMove is null)
            {
                return [];
            }

            var output = new List<RemoteInputEvent>(_discreteQueue.Count + _criticalReleaseQueue.Count + 1);
            while (_discreteQueue.Count > 0)
            {
                output.Add(_discreteQueue.Dequeue());
            }

            while (_criticalReleaseQueue.Count > 0)
            {
                output.Add(_criticalReleaseQueue.Dequeue());
            }

            if (_pendingMove is not null && _moveBucket.TryConsume())
            {
                output.Add(_pendingMove);
                _pendingMove = null;
            }

            return output;
        }
    }

    public InputPipelineMetrics GetMetrics()
    {
        lock (_sync)
        {
            return new InputPipelineMetrics(
                TotalMessages: _totalMessages,
                AcceptedDiscreteCount: _acceptedDiscreteCount,
                AcceptedCriticalReleaseCount: _acceptedCriticalReleaseCount,
                AcceptedResetCount: _acceptedResetCount,
                CoalescedMoveCount: _coalescedMoveCount,
                DroppedRateLimitedCount: _droppedRateLimitedCount,
                RejectedProtocolCount: _rejectedProtocolCount,
                RejectedValidationCount: _rejectedValidationCount,
                RejectedAuthorizationCount: _rejectedAuthorizationCount,
                MalformedFloodBlockCount: _malformedFloodBlockCount,
                HasPendingMove: _pendingMove is not null,
                PendingDiscreteCount: _discreteQueue.Count,
                PendingReleaseCount: _criticalReleaseQueue.Count);
        }
    }

    private bool IsInMalformedCooldown()
    {
        lock (_sync)
        {
            return _timeProvider.GetUtcNow() < _malformedCooldownUntilUtc;
        }
    }

    private InputPipelineProcessResult RecordMalformedFloodBlock()
    {
        lock (_sync)
        {
            _totalMessages++;
            _malformedFloodBlockCount++;
        }

        return new InputPipelineProcessResult(
            Accepted: false,
            Disposition: InputCommandDisposition.RejectedMalformedFlood,
            ParseFailure: ProtocolParseFailureReason.MalformedJson,
            Detail: "Malformed message flood protection active.");
    }

    private InputPipelineProcessResult RecordProtocolFailure(ProtocolParseResult parsed)
    {
        lock (_sync)
        {
            _totalMessages++;
            if (!_malformedBucket.TryConsume())
            {
                _malformedCooldownUntilUtc = _timeProvider.GetUtcNow() + _options.MalformedCooldownDuration;
                _malformedFloodBlockCount++;
                return new InputPipelineProcessResult(
                    Accepted: false,
                    Disposition: InputCommandDisposition.RejectedMalformedFlood,
                    ParseFailure: parsed.FailureReason,
                    Detail: "Malformed message flood threshold exceeded.");
            }

            _rejectedProtocolCount++;
            return new InputPipelineProcessResult(
                Accepted: false,
                Disposition: InputCommandDisposition.RejectedProtocol,
                ParseFailure: parsed.FailureReason,
                Detail: parsed.ErrorDetail);
        }
    }

    private static bool IsCriticalRelease(RemoteInputEvent command) =>
        command switch
        {
            PointerButtonEvent { Action: InputAction.Up } => true,
            KeyInputEvent { Action: InputAction.Up } => true,
            _ => false,
        };

    private sealed class TokenBucket
    {
        private readonly double _capacity;
        private readonly double _refillPerSecond;
        private readonly TimeProvider _timeProvider;
        private double _tokens;
        private long _lastTimestamp;

        public TokenBucket(int capacity, int refillPerSecond, TimeProvider timeProvider)
        {
            _capacity = capacity;
            _refillPerSecond = refillPerSecond;
            _timeProvider = timeProvider;
            _tokens = capacity;
            _lastTimestamp = _timeProvider.GetTimestamp();
        }

        public bool TryConsume()
        {
            var now = _timeProvider.GetTimestamp();
            var elapsedSeconds = _timeProvider.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
            if (elapsedSeconds > 0.0)
            {
                _tokens = Math.Min(_capacity, _tokens + (elapsedSeconds * _refillPerSecond));
                _lastTimestamp = now;
            }

            if (_tokens < 1.0)
            {
                return false;
            }

            _tokens -= 1.0;
            return true;
        }
    }
}
