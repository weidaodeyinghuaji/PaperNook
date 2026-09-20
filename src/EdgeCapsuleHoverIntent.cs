using System.Diagnostics;

namespace PaperTodo;

internal enum EdgeCapsuleHoverIntentMode
{
    Initial,
    Transfer
}

internal enum EdgeCapsuleHoverIntentDecision
{
    NoExtraDelay,
    Delay,
    Veto
}

internal enum EdgeCapsuleCorridorExitDecision
{
    KeepAlive,
    ConfirmNoTargetIntent,
    CloseForNoTargetIntent
}

/// <summary>
/// A negative-only hover-intent gate. The live physical hit test always chooses the candidate;
/// this policy may only delay/veto an activation or release an already empty queue corridor. It
/// never chooses a destination and never opens a capsule on its own.
/// </summary>
internal sealed class EdgeCapsuleHoverIntentPredictor
{
    private const int SampleCapacity = 20;
    private const double HistoryWindowMilliseconds = 64;
    private const double StaleHistoryMilliseconds = 180;
    private const double DuplicateSampleMilliseconds = 1.5;
    private const double BrakingRatio = 0.72;
    private const double BrakingDeltaDipPerMillisecond = 0.05;
    private const double AccelerationRatio = 1.15;
    private const double AccelerationDeltaDipPerMillisecond = 0.04;

    // Empty-region browse intent deliberately fuses two independent signals. Geometry carries more
    // weight because it remains reliable during slow diagonal travel; motion can independently save
    // a fast transfer but cannot keep resetting the close clock on weak residual direction alone.
    private const double CorridorGeometryWeight = 0.60;
    private const double CorridorMotionWeight = 0.40;
    private const double CorridorStrongEvidenceConfidence = 0.84;
    private const double CorridorKeepEnterConfidence = 0.58;
    private const double CorridorKeepExitConfidence = 0.42;
    private const double CorridorMeaningfulMovementDip = 0.5;
    private const double CorridorGeometryStationaryGraceMilliseconds = 160;
    private const double CorridorMotionFullConfidenceHorizonMilliseconds = 250;
    private const double CorridorMotionFadeHorizonMilliseconds = 900;

    private static readonly IntentSensitivityProfile VeryHighProfile = new(
        Initial: new IntentProfile(6, 4, 22, 55, 135),
        Transfer: new IntentProfile(10, 8, 38, 90, 190),
        StableFallbackMilliseconds: 38,
        MinimumDirectionalSpeedDipPerMillisecond: 0.13,
        MinimumDirectionConsistency: 0.76,
        MinimumVerticalDominance: 0.60,
        CorridorExit: new CorridorExitProfile(
            0.060, 0.62, 12, 200));

    private static readonly IntentSensitivityProfile HighProfile = new(
        Initial: new IntentProfile(8, 8, 32, 80, 180),
        Transfer: new IntentProfile(12, 12, 50, 120, 240),
        StableFallbackMilliseconds: 50,
        MinimumDirectionalSpeedDipPerMillisecond: 0.10,
        MinimumDirectionConsistency: 0.72,
        MinimumVerticalDominance: 0.55,
        CorridorExit: new CorridorExitProfile(
            0.075, 0.68, 16, 350));

    private static readonly IntentSensitivityProfile MediumProfile = new(
        Initial: new IntentProfile(8, 10, 36, 90, 200),
        Transfer: new IntentProfile(14, 20, 66, 155, 310),
        StableFallbackMilliseconds: 60,
        MinimumDirectionalSpeedDipPerMillisecond: 0.075,
        MinimumDirectionConsistency: 0.68,
        MinimumVerticalDominance: 0.50,
        CorridorExit: new CorridorExitProfile(
            0.090, 0.74, 20, 500));

    // "Low" describes activation sensitivity: it applies longer waits and recognizes less
    // pronounced residual motion as pass-through risk, so stopping must be more deliberate.
    private static readonly IntentSensitivityProfile LowProfile = new(
        Initial: new IntentProfile(10, 14, 44, 110, 240),
        Transfer: new IntentProfile(18, 34, 90, 205, 410),
        StableFallbackMilliseconds: 85,
        MinimumDirectionalSpeedDipPerMillisecond: 0.055,
        MinimumDirectionConsistency: 0.64,
        MinimumVerticalDominance: 0.45,
        CorridorExit: new CorridorExitProfile(
            0.110, 0.79, 24, 650));

    private static readonly IntentSensitivityProfile VeryLowProfile = new(
        Initial: new IntentProfile(12, 18, 54, 135, 300),
        Transfer: new IntentProfile(22, 48, 115, 255, 500),
        StableFallbackMilliseconds: 110,
        MinimumDirectionalSpeedDipPerMillisecond: 0.040,
        MinimumDirectionConsistency: 0.60,
        MinimumVerticalDominance: 0.40,
        CorridorExit: new CorridorExitProfile(
            0.140, 0.84, 30, 800));

    private readonly PointerSample[] _samples =
        new PointerSample[SampleCapacity];
    private int _sampleStart;
    private int _sampleCount;
    private double _dpiScaleX = 1;
    private double _dpiScaleY = 1;
    private PointerSample? _corridorGeometryOrigin;
    private bool _corridorKeepAliveLatched;
    private long _lastMeaningfulPointerMovementTimestamp;
    private DeviceScreenPoint? _lastMeaningfulPointerAnchor;

    private readonly record struct PointerSample(
        DeviceScreenPoint Point,
        long Timestamp);

    private readonly record struct IntentProfile(
        double MinimumObservationMilliseconds,
        double MinimumDelayMilliseconds,
        double MaximumDelayMilliseconds,
        double PassThroughVetoHorizonMilliseconds,
        double DynamicDelayHorizonMilliseconds);

    private readonly record struct IntentSensitivityProfile(
        IntentProfile Initial,
        IntentProfile Transfer,
        double StableFallbackMilliseconds,
        double MinimumDirectionalSpeedDipPerMillisecond,
        double MinimumDirectionConsistency,
        double MinimumVerticalDominance,
        CorridorExitProfile CorridorExit);

    private readonly record struct CorridorExitProfile(
        double MinimumSpeedDipPerMillisecond,
        double MinimumPathConsistency,
        double TargetPaddingDip,
        double NoTargetIntentCloseMilliseconds);

    private readonly record struct MotionEstimate(
        bool HasMotion,
        double SignedHorizontalSpeedDipPerMillisecond,
        double SignedVerticalSpeedDipPerMillisecond,
        double RecentSpeedDipPerMillisecond,
        double RecentVerticalSpeedDipPerMillisecond,
        double PriorVerticalSpeedDipPerMillisecond,
        double PathConsistency,
        double DirectionConsistency,
        double VerticalDominance,
        bool HasSpeedTrend);

    private readonly record struct CorridorMotionEvidence(
        double Confidence,
        bool StronglyOpposed);

    public void Reset()
    {
        _sampleStart = 0;
        _sampleCount = 0;
        _dpiScaleX = 1;
        _dpiScaleY = 1;
        _corridorGeometryOrigin = null;
        _corridorKeepAliveLatched = false;
        _lastMeaningfulPointerMovementTimestamp = 0;
        _lastMeaningfulPointerAnchor = null;
    }

    public void Reset(
        DeviceScreenPoint pointer,
        long timestamp,
        double dpiScaleX,
        double dpiScaleY)
    {
        Reset();
        _dpiScaleX = NormalizeDpiScale(dpiScaleX);
        _dpiScaleY = NormalizeDpiScale(dpiScaleY);
        _lastMeaningfulPointerMovementTimestamp = timestamp;
        _lastMeaningfulPointerAnchor = pointer;
        AddSample(new PointerSample(pointer, timestamp));
    }

    public void Observe(
        DeviceScreenPoint pointer,
        long timestamp,
        double dpiScaleX,
        double dpiScaleY)
    {
        var nextScaleX = NormalizeDpiScale(dpiScaleX);
        var nextScaleY = NormalizeDpiScale(dpiScaleY);
        if (_sampleCount > 0 &&
            (Math.Abs(_dpiScaleX - nextScaleX) > 0.001 ||
             Math.Abs(_dpiScaleY - nextScaleY) > 0.001))
        {
            Reset();
        }
        _dpiScaleX = nextScaleX;
        _dpiScaleY = nextScaleY;

        if (_sampleCount == 0)
        {
            _lastMeaningfulPointerMovementTimestamp = timestamp;
            _lastMeaningfulPointerAnchor = pointer;
            AddSample(new PointerSample(pointer, timestamp));
            return;
        }

        var latest = SampleAt(_sampleCount - 1);
        var elapsed = ElapsedMilliseconds(latest.Timestamp, timestamp);
        if (elapsed < 0 || elapsed > StaleHistoryMilliseconds)
        {
            Reset(pointer, timestamp, nextScaleX, nextScaleY);
            return;
        }
        if (elapsed < DuplicateSampleMilliseconds)
        {
            return;
        }

        // Accumulate slow travel against the last meaningful anchor instead of requiring one sample
        // to exceed the threshold. A deliberate crawl through the safe corridor therefore keeps
        // geometry fresh, while sub-threshold stationary jitter still expires normally.
        var movementAnchor = _lastMeaningfulPointerAnchor ?? latest.Point;
        var deltaX = (pointer.X - movementAnchor.X) / nextScaleX;
        var deltaY = (pointer.Y - movementAnchor.Y) / nextScaleY;
        if (Math.Sqrt(deltaX * deltaX + deltaY * deltaY) >=
            CorridorMeaningfulMovementDip)
        {
            _lastMeaningfulPointerMovementTimestamp = timestamp;
            _lastMeaningfulPointerAnchor = pointer;
        }
        AddSample(new PointerSample(pointer, timestamp));
    }

    public EdgeCapsuleHoverIntentDecision Evaluate(
        EdgeCapsuleHoverIntentMode mode,
        string sensitivity,
        DeviceScreenRect targetBounds,
        DeviceScreenPoint pointer,
        double candidateElapsedMilliseconds,
        double stableElapsedMilliseconds)
    {
        var sensitivityProfile = ResolveSensitivityProfile(sensitivity);
        var profile = mode == EdgeCapsuleHoverIntentMode.Initial
            ? sensitivityProfile.Initial
            : sensitivityProfile.Transfer;

        // This is a deterministic escape hatch, not a positive prediction. Even a noisy motion
        // estimate cannot keep a genuinely settled pointer pending forever.
        if (stableElapsedMilliseconds >=
            sensitivityProfile.StableFallbackMilliseconds)
        {
            return EdgeCapsuleHoverIntentDecision.NoExtraDelay;
        }

        if (candidateElapsedMilliseconds <
            profile.MinimumObservationMilliseconds)
        {
            return EdgeCapsuleHoverIntentDecision.Delay;
        }

        var motion = EstimateMotion();
        if (!motion.HasMotion ||
            motion.RecentVerticalSpeedDipPerMillisecond <
                sensitivityProfile.MinimumDirectionalSpeedDipPerMillisecond ||
            motion.DirectionConsistency <
                sensitivityProfile.MinimumDirectionConsistency ||
            motion.VerticalDominance <
                sensitivityProfile.MinimumVerticalDominance)
        {
            return stableElapsedMilliseconds >=
                profile.MinimumDelayMilliseconds
                ? EdgeCapsuleHoverIntentDecision.NoExtraDelay
                : EdgeCapsuleHoverIntentDecision.Delay;
        }

        var braking = motion.HasSpeedTrend &&
            motion.RecentVerticalSpeedDipPerMillisecond <=
                motion.PriorVerticalSpeedDipPerMillisecond * BrakingRatio &&
            motion.PriorVerticalSpeedDipPerMillisecond -
                motion.RecentVerticalSpeedDipPerMillisecond >=
                BrakingDeltaDipPerMillisecond;
        var accelerating = motion.HasSpeedTrend &&
            motion.RecentVerticalSpeedDipPerMillisecond >=
                motion.PriorVerticalSpeedDipPerMillisecond *
                    AccelerationRatio &&
            motion.RecentVerticalSpeedDipPerMillisecond -
                motion.PriorVerticalSpeedDipPerMillisecond >=
                AccelerationDeltaDipPerMillisecond;

        var distanceToExitDevice =
            motion.SignedVerticalSpeedDipPerMillisecond < 0
            ? pointer.Y - targetBounds.Top
            : targetBounds.Bottom - pointer.Y;
        var distanceToExitDip = distanceToExitDevice / _dpiScaleY;
        var timeToExit = Math.Max(0, distanceToExitDip) /
            motion.RecentVerticalSpeedDipPerMillisecond;

        // menu-aim style negative protection: a coherent, non-braking trajectory that will leave
        // this physical target soon is a pass-through, so it cannot activate this target. Strong
        // acceleration broadens the protection slightly; braking removes it immediately.
        var vetoHorizon = profile.PassThroughVetoHorizonMilliseconds *
            (accelerating ? 1.20 : 1.0);
        if (!braking && timeToExit <= vetoHorizon)
        {
            return EdgeCapsuleHoverIntentDecision.Veto;
        }

        // hoverIntent style adaptive dwell: faster, persistent motion consumes more of the bounded
        // delay budget. A clear braking trend selects the short end of that budget, while the
        // stable clock prevents time accumulated during earlier movement from authorizing the
        // target immediately after the pointer shifts again.
        var risk = Math.Clamp(
            1 - timeToExit / profile.DynamicDelayHorizonMilliseconds,
            0,
            1);
        if (accelerating)
        {
            risk = Math.Min(1, risk + 0.20);
        }
        else if (braking)
        {
            risk *= 0.25;
        }
        else if (!motion.HasSpeedTrend)
        {
            risk *= 0.85;
        }

        var requiredDelay = profile.MinimumDelayMilliseconds +
            (profile.MaximumDelayMilliseconds -
                profile.MinimumDelayMilliseconds) * risk;
        return stableElapsedMilliseconds >= requiredDelay
            ? EdgeCapsuleHoverIntentDecision.NoExtraDelay
            : EdgeCapsuleHoverIntentDecision.Delay;
    }

    /// <summary>
    /// Evaluates only empty pixels inside the queue transfer rectangle. Real applied capsule/card
    /// bounds remain absolute authority. In the blank region, a menu-aim-style geometric corridor
    /// and recent motion trend independently produce confidence; only strong or mutually supporting
    /// evidence clears the close clock. Weak historical direction therefore cannot make browse mode
    /// sticky, while a slow safe-polygon path or a fast coherent trajectory can each preserve it.
    /// </summary>
    public EdgeCapsuleCorridorExitDecision EvaluateCorridorExit(
        string sensitivity,
        ReadOnlySpan<DeviceScreenRect> keepAliveBounds,
        DeviceScreenPoint pointer,
        double noTargetIntentElapsedMilliseconds)
    {
        var profile = ResolveSensitivityProfile(sensitivity).CorridorExit;
        RefreshCorridorGeometryOrigin(keepAliveBounds);

        var horizontalPadding = profile.TargetPaddingDip * _dpiScaleX;
        var verticalPadding = profile.TargetPaddingDip * _dpiScaleY;
        var geometryConfidence = 0.0;
        if (_corridorGeometryOrigin is { } origin)
        {
            foreach (var bounds in keepAliveBounds)
            {
                geometryConfidence = Math.Max(
                    geometryConfidence,
                    GeometryCorridorConfidence(
                        origin.Point,
                        pointer,
                        bounds,
                        horizontalPadding,
                        verticalPadding));
            }
        }
        if (!HasRecentMeaningfulPointerMovement())
        {
            // A safe triangle is a path, not a parking zone. Once the pointer has actually stopped,
            // geometry remains useful as supporting evidence but may no longer clear the close clock
            // on its own. This removes the classic safe-polygon "stuck in the triangle" failure.
            geometryConfidence = Math.Min(geometryConfidence, 0.55);
        }

        var motionEvidence = EvaluateCorridorMotionEvidence(
            profile,
            keepAliveBounds,
            pointer,
            horizontalPadding,
            verticalPadding);
        var strongestEvidence = Math.Max(
            geometryConfidence,
            motionEvidence.Confidence);
        var fusedConfidence =
            geometryConfidence * CorridorGeometryWeight +
            motionEvidence.Confidence * CorridorMotionWeight;

        // One genuinely strong signal may rescue a transfer. The exception is a safe polygon that
        // is contradicted by coherent motion away from every real target: that becomes ambiguous
        // instead of staying latched forever. Two medium signals can also enter keep-alive.
        var strongEvidenceKeepsAlive =
            strongestEvidence >= CorridorStrongEvidenceConfidence &&
            !(geometryConfidence >= CorridorStrongEvidenceConfidence &&
              motionEvidence.StronglyOpposed);
        var enterKeepAlive =
            strongEvidenceKeepsAlive ||
            fusedConfidence >= CorridorKeepEnterConfidence;
        var retainKeepAlive =
            _corridorKeepAliveLatched &&
            !motionEvidence.StronglyOpposed &&
            fusedConfidence >= CorridorKeepExitConfidence;
        if (enterKeepAlive || retainKeepAlive)
        {
            _corridorKeepAliveLatched = true;
            return EdgeCapsuleCorridorExitDecision.KeepAlive;
        }

        _corridorKeepAliveLatched = false;
        return noTargetIntentElapsedMilliseconds >=
            profile.NoTargetIntentCloseMilliseconds
            ? EdgeCapsuleCorridorExitDecision.CloseForNoTargetIntent
            : EdgeCapsuleCorridorExitDecision.ConfirmNoTargetIntent;
    }

    public double CorridorNoTargetIntentCloseMilliseconds(string sensitivity) =>
        ResolveSensitivityProfile(sensitivity)
            .CorridorExit
            .NoTargetIntentCloseMilliseconds;

    private bool HasRecentMeaningfulPointerMovement()
    {
        if (_sampleCount == 0 ||
            _lastMeaningfulPointerMovementTimestamp <= 0)
        {
            return false;
        }

        var latest = SampleAt(_sampleCount - 1);
        var elapsed = ElapsedMilliseconds(
            _lastMeaningfulPointerMovementTimestamp,
            latest.Timestamp);
        return elapsed >= 0 &&
            elapsed <= CorridorGeometryStationaryGraceMilliseconds;
    }

    private void RefreshCorridorGeometryOrigin(
        ReadOnlySpan<DeviceScreenRect> keepAliveBounds)
    {
        // Observe() runs before the physical queue resolver. Search backward for the newest sample
        // that was actually inside a committed target. This gives the safe polygon a real departure
        // point without teaching the predictor about paper IDs or queue ownership.
        for (var sampleIndex = _sampleCount - 1;
            sampleIndex >= 0;
            sampleIndex--)
        {
            var sample = SampleAt(sampleIndex);
            foreach (var bounds in keepAliveBounds)
            {
                if (!Contains(bounds, sample.Point))
                {
                    continue;
                }

                if (!_corridorGeometryOrigin.HasValue ||
                    sample.Timestamp > _corridorGeometryOrigin.Value.Timestamp)
                {
                    _corridorGeometryOrigin = sample;
                    _corridorKeepAliveLatched = false;
                }
                return;
            }
        }
    }

    private CorridorMotionEvidence EvaluateCorridorMotionEvidence(
        CorridorExitProfile profile,
        ReadOnlySpan<DeviceScreenRect> keepAliveBounds,
        DeviceScreenPoint pointer,
        double horizontalPadding,
        double verticalPadding)
    {
        var motion = EstimateMotion();
        if (!motion.HasMotion ||
            motion.RecentSpeedDipPerMillisecond <
                profile.MinimumSpeedDipPerMillisecond ||
            motion.PathConsistency < profile.MinimumPathConsistency)
        {
            return default;
        }

        var directionX =
            motion.SignedHorizontalSpeedDipPerMillisecond * _dpiScaleX;
        var directionY =
            motion.SignedVerticalSpeedDipPerMillisecond * _dpiScaleY;
        var directionLength = Math.Sqrt(
            directionX * directionX + directionY * directionY);
        if (directionLength <= double.Epsilon)
        {
            return default;
        }

        var bestConfidence = 0.0;
        var bestAlignment = -1.0;
        foreach (var bounds in keepAliveBounds)
        {
            if (bounds.IsEmpty)
            {
                continue;
            }

            var minimumX = bounds.Left - Math.Max(0, horizontalPadding);
            var maximumX = bounds.Right + Math.Max(0, horizontalPadding);
            var minimumY = bounds.Top - Math.Max(0, verticalPadding);
            var maximumY = bounds.Bottom + Math.Max(0, verticalPadding);
            var targetX = Math.Clamp(pointer.X, minimumX, maximumX);
            var targetY = Math.Clamp(pointer.Y, minimumY, maximumY);
            var targetDeltaX = targetX - pointer.X;
            var targetDeltaY = targetY - pointer.Y;
            var targetDistance = Math.Sqrt(
                targetDeltaX * targetDeltaX +
                targetDeltaY * targetDeltaY);

            if (targetDistance <= double.Epsilon)
            {
                bestConfidence = Math.Max(bestConfidence, 0.76);
                bestAlignment = Math.Max(bestAlignment, 1.0);
                continue;
            }

            var alignment =
                (directionX * targetDeltaX + directionY * targetDeltaY) /
                (directionLength * targetDistance);
            bestAlignment = Math.Max(bestAlignment, alignment);
            if (alignment <= 0.30)
            {
                continue;
            }

            var headingConfidence = Math.Clamp(
                (alignment - 0.30) / 0.70,
                0,
                1);
            var pathFloor = profile.MinimumPathConsistency * 0.70;
            var pathConfidence = Math.Clamp(
                (motion.PathConsistency - pathFloor) /
                Math.Max(0.001, 1 - pathFloor),
                0,
                1);
            var speedConfidence = Math.Clamp(
                (motion.RecentSpeedDipPerMillisecond -
                    profile.MinimumSpeedDipPerMillisecond) /
                Math.Max(
                    0.001,
                    profile.MinimumSpeedDipPerMillisecond * 2),
                0,
                1);

            var directionLengthSquared =
                directionX * directionX + directionY * directionY;
            var projectedMilliseconds =
                (directionX * targetDeltaX + directionY * targetDeltaY) /
                directionLengthSquared;
            var horizonConfidence = projectedMilliseconds <=
                CorridorMotionFullConfidenceHorizonMilliseconds
                ? 1.0
                : Math.Clamp(
                    1 -
                    (projectedMilliseconds -
                        CorridorMotionFullConfidenceHorizonMilliseconds) /
                    (CorridorMotionFadeHorizonMilliseconds -
                        CorridorMotionFullConfidenceHorizonMilliseconds),
                    0,
                    1);

            var confidence = headingConfidence *
                (0.50 + 0.30 * pathConfidence + 0.20 * speedConfidence) *
                (0.72 + 0.28 * horizonConfidence);
            bestConfidence = Math.Max(bestConfidence, confidence);
        }

        var stronglyOpposed =
            motion.RecentSpeedDipPerMillisecond >=
                profile.MinimumSpeedDipPerMillisecond * 1.15 &&
            motion.PathConsistency >= profile.MinimumPathConsistency &&
            bestAlignment <= 0.05;
        return new CorridorMotionEvidence(
            Math.Clamp(bestConfidence, 0, 1),
            stronglyOpposed);
    }

    private static double GeometryCorridorConfidence(
        DeviceScreenPoint origin,
        DeviceScreenPoint pointer,
        DeviceScreenRect bounds,
        double horizontalPadding,
        double verticalPadding)
    {
        if (bounds.IsEmpty || Contains(bounds, origin))
        {
            return 0;
        }

        var minimumX = bounds.Left - Math.Max(0, horizontalPadding);
        var maximumX = bounds.Right + Math.Max(0, horizontalPadding);
        var minimumY = bounds.Top - Math.Max(0, verticalPadding);
        var maximumY = bounds.Bottom + Math.Max(0, verticalPadding);
        var centerX = (bounds.Left + bounds.Right) / 2.0;
        var centerY = (bounds.Top + bounds.Bottom) / 2.0;
        var targetDeltaX = centerX - origin.X;
        var targetDeltaY = centerY - origin.Y;

        double edgeAX;
        double edgeAY;
        double edgeBX;
        double edgeBY;
        if (Math.Abs(targetDeltaY) >= Math.Abs(targetDeltaX))
        {
            // Pad across the travel direction, but keep the triangle's target face on the real
            // capsule edge. Padding larger than a physical inter-capsule gap must never move the
            // face behind the departure point and invert the safe polygon.
            var edgeY = targetDeltaY >= 0
                ? bounds.Top
                : bounds.Bottom - 1.0;
            edgeAX = minimumX;
            edgeAY = edgeY;
            edgeBX = maximumX;
            edgeBY = edgeY;
        }
        else
        {
            var edgeX = targetDeltaX >= 0
                ? bounds.Left
                : bounds.Right - 1.0;
            edgeAX = edgeX;
            edgeAY = minimumY;
            edgeBX = edgeX;
            edgeBY = maximumY;
        }

        if (PointInTriangle(
                pointer.X,
                pointer.Y,
                origin.X,
                origin.Y,
                edgeAX,
                edgeAY,
                edgeBX,
                edgeBY))
        {
            return 0.94;
        }

        // Outside the strict safe triangle, direction toward the target is only supporting evidence.
        // It can combine with real motion evidence, but cannot keep the preview alive by itself.
        var travelX = pointer.X - origin.X;
        var travelY = pointer.Y - origin.Y;
        var travelLength = Math.Sqrt(travelX * travelX + travelY * travelY);
        var nearestX = Math.Clamp(origin.X, minimumX, maximumX);
        var nearestY = Math.Clamp(origin.Y, minimumY, maximumY);
        var desiredX = nearestX - origin.X;
        var desiredY = nearestY - origin.Y;
        var desiredLength = Math.Sqrt(desiredX * desiredX + desiredY * desiredY);
        if (travelLength <= double.Epsilon ||
            desiredLength <= double.Epsilon)
        {
            return 0;
        }

        var alignment = Math.Clamp(
            (travelX * desiredX + travelY * desiredY) /
            (travelLength * desiredLength),
            -1,
            1);
        if (alignment <= 0)
        {
            return 0;
        }

        var originDistance = DistanceToBounds(
            origin.X,
            origin.Y,
            minimumX,
            maximumX,
            minimumY,
            maximumY);
        var pointerDistance = DistanceToBounds(
            pointer.X,
            pointer.Y,
            minimumX,
            maximumX,
            minimumY,
            maximumY);
        var progress = originDistance > double.Epsilon
            ? Math.Clamp(1 - pointerDistance / originDistance, 0, 1)
            : 0;
        return Math.Min(
            0.58,
            0.18 + 0.28 * alignment + 0.12 * progress);
    }

    private MotionEstimate EstimateMotion()
    {
        if (_sampleCount < 2)
        {
            return default;
        }

        var latest = SampleAt(_sampleCount - 1);
        var firstIndex = _sampleCount - 2;
        while (firstIndex > 0 &&
            ElapsedMilliseconds(
                SampleAt(firstIndex - 1).Timestamp,
                latest.Timestamp) <= HistoryWindowMilliseconds)
        {
            firstIndex--;
        }

        var first = SampleAt(firstIndex);
        var duration = ElapsedMilliseconds(first.Timestamp, latest.Timestamp);
        if (duration <= 0)
        {
            return default;
        }

        var totalDistance = 0.0;
        var totalVerticalDistance = 0.0;
        for (var index = firstIndex + 1;
            index < _sampleCount;
            index++)
        {
            var previous = SampleAt(index - 1).Point;
            var current = SampleAt(index).Point;
            var deltaX = (current.X - previous.X) / _dpiScaleX;
            var deltaY = (current.Y - previous.Y) / _dpiScaleY;
            totalDistance += Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            totalVerticalDistance += Math.Abs(deltaY);
        }

        var netHorizontalDistance =
            (latest.Point.X - first.Point.X) / _dpiScaleX;
        var netVerticalDistance =
            (latest.Point.Y - first.Point.Y) / _dpiScaleY;
        var absoluteNetVerticalDistance = Math.Abs(netVerticalDistance);
        if (totalDistance <= double.Epsilon)
        {
            return default;
        }

        var midpointTimestamp = first.Timestamp +
            (latest.Timestamp - first.Timestamp) / 2;
        var midpointIndex = firstIndex;
        for (var index = firstIndex + 1;
            index < _sampleCount - 1;
            index++)
        {
            if (SampleAt(index).Timestamp <= midpointTimestamp)
            {
                midpointIndex = index;
                continue;
            }
            break;
        }

        var midpoint = SampleAt(midpointIndex);
        var recentDuration = ElapsedMilliseconds(
            midpoint.Timestamp,
            latest.Timestamp);
        if (recentDuration <= 0)
        {
            midpoint = first;
            recentDuration = duration;
        }

        var recentHorizontalDelta =
            (latest.Point.X - midpoint.Point.X) / _dpiScaleX;
        var recentVerticalDelta =
            (latest.Point.Y - midpoint.Point.Y) / _dpiScaleY;
        var recentSignedHorizontalSpeed =
            recentHorizontalDelta / recentDuration;
        var recentSignedVerticalSpeed =
            recentVerticalDelta / recentDuration;
        var recentVerticalSpeed = Math.Abs(recentSignedVerticalSpeed);
        var recentSpeed = Math.Sqrt(
            recentSignedHorizontalSpeed * recentSignedHorizontalSpeed +
            recentSignedVerticalSpeed * recentSignedVerticalSpeed);
        var priorDuration = ElapsedMilliseconds(
            first.Timestamp,
            midpoint.Timestamp);
        var priorVerticalSpeed = priorDuration > 0
            ? Math.Abs(midpoint.Point.Y - first.Point.Y) /
                _dpiScaleY /
                priorDuration
            : recentVerticalSpeed;

        return new MotionEstimate(
            HasMotion: true,
            SignedHorizontalSpeedDipPerMillisecond:
                recentSignedHorizontalSpeed,
            SignedVerticalSpeedDipPerMillisecond:
                recentSignedVerticalSpeed,
            RecentSpeedDipPerMillisecond: recentSpeed,
            RecentVerticalSpeedDipPerMillisecond:
                recentVerticalSpeed,
            PriorVerticalSpeedDipPerMillisecond:
                priorVerticalSpeed,
            PathConsistency: Math.Sqrt(
                netHorizontalDistance * netHorizontalDistance +
                netVerticalDistance * netVerticalDistance) /
                totalDistance,
            DirectionConsistency:
                totalVerticalDistance > double.Epsilon
                    ? absoluteNetVerticalDistance / totalVerticalDistance
                    : 0,
            VerticalDominance:
                absoluteNetVerticalDistance / totalDistance,
            HasSpeedTrend: priorDuration > 0);
    }

    private static bool Contains(
        DeviceScreenRect bounds,
        DeviceScreenPoint point) =>
        !bounds.IsEmpty &&
        point.X >= bounds.Left &&
        point.X < bounds.Right &&
        point.Y >= bounds.Top &&
        point.Y < bounds.Bottom;

    private static bool PointInTriangle(
        double pointX,
        double pointY,
        double firstX,
        double firstY,
        double secondX,
        double secondY,
        double thirdX,
        double thirdY)
    {
        static double Cross(
            double ax,
            double ay,
            double bx,
            double by,
            double cx,
            double cy) =>
            (bx - ax) * (cy - ay) -
            (by - ay) * (cx - ax);

        var first = Cross(
            firstX, firstY, secondX, secondY, pointX, pointY);
        var second = Cross(
            secondX, secondY, thirdX, thirdY, pointX, pointY);
        var third = Cross(
            thirdX, thirdY, firstX, firstY, pointX, pointY);
        var hasNegative = first < 0 || second < 0 || third < 0;
        var hasPositive = first > 0 || second > 0 || third > 0;
        return !(hasNegative && hasPositive);
    }

    private static double DistanceToBounds(
        double x,
        double y,
        double minimumX,
        double maximumX,
        double minimumY,
        double maximumY)
    {
        var deltaX = x < minimumX
            ? minimumX - x
            : x > maximumX
                ? x - maximumX
                : 0;
        var deltaY = y < minimumY
            ? minimumY - y
            : y > maximumY
                ? y - maximumY
                : 0;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private static IntentSensitivityProfile ResolveSensitivityProfile(
        string sensitivity)
    {
        return EdgeCapsuleHoverIntentSensitivities.Normalize(sensitivity) switch
        {
            EdgeCapsuleHoverIntentSensitivities.VeryLow => VeryLowProfile,
            EdgeCapsuleHoverIntentSensitivities.Low => LowProfile,
            EdgeCapsuleHoverIntentSensitivities.High => HighProfile,
            EdgeCapsuleHoverIntentSensitivities.VeryHigh => VeryHighProfile,
            _ => MediumProfile
        };
    }

    private static double NormalizeDpiScale(double scale) =>
        double.IsFinite(scale) ? Math.Max(1, scale) : 1;

    private void AddSample(PointerSample sample)
    {
        if (_sampleCount < SampleCapacity)
        {
            var destination = (_sampleStart + _sampleCount) %
                SampleCapacity;
            _samples[destination] = sample;
            _sampleCount++;
            return;
        }

        _samples[_sampleStart] = sample;
        _sampleStart = (_sampleStart + 1) % SampleCapacity;
    }

    private PointerSample SampleAt(int index) =>
        _samples[(_sampleStart + index) % SampleCapacity];

    private static double ElapsedMilliseconds(long start, long end) =>
        Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
}
