using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Xml;

namespace AgentSignaler.Remote;

public static class RemoteFailure
{
    public static bool IsExpected(Exception exception) => exception switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 &&
            aggregate.InnerExceptions.All(IsExpected),
        IOException or UnauthorizedAccessException or JsonException or XmlException or
            COMException or Win32Exception or SecurityException or HttpRequestException or
            OperationCanceledException or ArgumentException or InvalidDataException or InvalidOperationException or
            FormatException or KeyNotFoundException => true,
        _ => false
    };
}
