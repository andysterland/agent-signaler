using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Xml;
using AgentSignaler.Contracts;

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
            FormatException or KeyNotFoundException or DiscoveryException => true,
        _ => false
    };

    public static string DescribeLocalFailure(Exception exception)
    {
        if (exception is DiscoveryException)
            return "DNS TXT discovery validation failed. Check the record name and its single HTTPS tunnel root URL. No direct fallback was used.";
        var message = exception.GetBaseException().Message;
        return string.IsNullOrWhiteSpace(message)
            ? "Operation failed. Check file access, the endpoint, startup registration, and legacy Task Scheduler access."
            : $"Operation failed: {message} Check file access, the endpoint, startup registration, and legacy Task Scheduler access.";
    }
}
