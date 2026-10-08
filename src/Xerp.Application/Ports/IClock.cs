namespace Xerp.Application.Ports;

public interface IClock
{
    /// <summary>Current UTC time, at the precision the database stores (microseconds).</summary>
    DateTime UtcNow { get; }
}
