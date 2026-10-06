namespace UniTechDesk.TestSupport;

/// <summary>Reloj controlable para pruebas (avanza solo cuando se le ordena).</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public ManualTimeProvider(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public void Set(DateTimeOffset value) => _now = value;
}
