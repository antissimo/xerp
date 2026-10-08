using Xerp.Application.Ports;

namespace Xerp.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow
    {
        get
        {
            // PostgreSQL keeps microseconds; truncating here makes a value read back equal to the one written.
            var now = DateTime.UtcNow;
            return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
        }
    }
}
