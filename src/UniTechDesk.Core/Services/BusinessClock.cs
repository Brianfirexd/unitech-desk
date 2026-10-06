using UniTechDesk.Core.Options;

namespace UniTechDesk.Core.Services;

/// <summary>Reloj del negocio: UTC sin fracciones de segundo (la BD usa DATETIME2(0)) y "hoy" según la zona horaria local.</summary>
public sealed class BusinessClock
{
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;

    public BusinessClock(TimeProvider time, AppOptions options)
    {
        _time = time;
        try { _zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Nicaragua no usa horario de verano: UTC-6 fijo. Sirve de respaldo si el sistema no tiene la zona instalada.
            _zone = TimeZoneInfo.CreateCustomTimeZone("UTC-06", TimeSpan.FromHours(-6), "UTC-06", "UTC-06");
        }
    }

    public DateTime UtcNow
    {
        get
        {
            var n = _time.GetUtcNow().UtcDateTime;
            return new DateTime(n.Ticks - n.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }
    }

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, _zone));
}
