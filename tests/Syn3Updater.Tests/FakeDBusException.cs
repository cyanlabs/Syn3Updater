namespace Tmds.DBus.Protocol;

/// <summary>Stands in for the real Tmds.DBus exception, which the crash reporter matches by its full name.</summary>
internal sealed class DBusException : Exception
{
    public DBusException(string message) : base(message) { }
}
