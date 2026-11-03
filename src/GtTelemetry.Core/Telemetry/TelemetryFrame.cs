namespace GtTelemetry.Core.Telemetry;

/// <summary>
/// One game-agnostic telemetry sample. Adapters fill what their game exposes and leave the rest at zero; output formats
/// read only this, so they never see game addresses or units.
/// </summary>
/// <remarks>
/// Axes follow the car, Forza style: X right, Y up, Z forward. Angular rates are about those axes: pitch about X
/// (positive nose down), yaw about Y (positive turning right), roll about Z (positive rolling left). Per-wheel arrays
/// are FL, FR, RL, RR.
/// </remarks>
public sealed record TelemetryFrame
{
    /// <summary>In a car and the values are live (false in menus, loading and when the emulator is paused).</summary>
    public bool Driving { get; init; }

    public float Rpm { get; init; }

    public float MaxRpm { get; init; }

    public float IdleRpm { get; init; }

    /// <summary>Vehicle speed, m/s.</summary>
    public float SpeedMps { get; init; }

    /// <summary>Velocity along the car's forward axis, m/s (negative when reversing).</summary>
    public float VelocityForwardMps { get; init; }

    public float VelocityRightMps { get; init; }

    public float VelocityUpMps { get; init; }

    /// <summary>Acceleration in the car's frame, m/s² (gravity excluded).</summary>
    public float AccelRight { get; init; }

    public float AccelUp { get; init; }

    public float AccelForward { get; init; }

    /// <summary>rad/s.</summary>
    public float PitchRate { get; init; }

    public float YawRate { get; init; }

    public float RollRate { get; init; }

    /// <summary>Heading, pitch and roll angles, radians.</summary>
    public float Yaw { get; init; }

    public float Pitch { get; init; }

    public float Roll { get; init; }

    /// <summary>World position, m.</summary>
    public float PositionX { get; init; }

    public float PositionY { get; init; }

    public float PositionZ { get; init; }

    /// <summary>0–1.</summary>
    public float Throttle { get; init; }

    /// <summary>0–1.</summary>
    public float Brake { get; init; }

    /// <summary>0–1.</summary>
    public float Clutch { get; init; }

    /// <summary>0–1.</summary>
    public float Handbrake { get; init; }

    /// <summary>−1 full left to 1 full right.</summary>
    public float Steer { get; init; }

    /// <summary>−1 reverse, 0 neutral, 1+ forward gears.</summary>
    public int Gear { get; init; }

    /// <summary>Per wheel (FL, FR, RL, RR), or null when the game has no wheel data.</summary>
    public WheelState[]? Wheels { get; init; }

    public static readonly TelemetryFrame NotDriving = new();
}

public readonly record struct WheelState
{
    /// <summary>0 fully extended, 1 fully compressed.</summary>
    public float SuspensionNormalized { get; init; }

    /// <summary>Compression from full extension, m.</summary>
    public float SuspensionMeters { get; init; }

    /// <summary>Normalised to the tyre's peak: |1| is where it lets go. Positive under power.</summary>
    public float SlipRatio { get; init; }

    /// <summary>Normalised to the tyre's peak, like <see cref="SlipRatio"/>.</summary>
    public float SlipAngle { get; init; }

    /// <summary>rad/s.</summary>
    public float RotationSpeed { get; init; }

    public bool OnRumbleStrip { get; init; }

    /// <summary>0–1 vibration from the surface (grass, gravel, sand).</summary>
    public float SurfaceRumble { get; init; }
}

/// <summary>Produces frames for one game. Called once per output tick.</summary>
public interface ITelemetrySource
{
    TelemetryFrame Read();
}
