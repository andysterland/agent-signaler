using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Xml.Linq;

namespace AgentSignaler.Remote;

public interface IIntegrationTaskScheduler
{
    string? ReadXml(string name);
    void Write(string name, string xml);
    void Delete(string name);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsTaskScheduler : IIntegrationTaskScheduler
{
    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler unavailable.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    public string? ReadXml(string name)
    {
        dynamic service = Connect();
        dynamic folder = service.GetFolder("\\");
        try
        {
            dynamic task = folder.GetTask(name);
            try { return (string)task.Xml; }
            finally { Marshal.FinalReleaseComObject(task); }
        }
        // COM interop can map HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) to FileNotFoundException.
        catch (Exception ex) when (ex is COMException or FileNotFoundException &&
            ex.HResult == unchecked((int)0x80070002)) { return null; }
        finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }

    public void Write(string name, string xml)
    {
        dynamic service = Connect();
        dynamic folder = service.GetFolder("\\");
        try
        {
            var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No user identity.");
            dynamic task = folder.RegisterTask(name, xml, 6, user, null, 3, null);
            Marshal.FinalReleaseComObject(task);
        }
        finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }

    public void Delete(string name)
    {
        dynamic service = Connect();
        dynamic folder = service.GetFolder("\\");
        try { folder.DeleteTask(name, 0); }
        catch (Exception ex) when (ex is COMException or FileNotFoundException &&
            ex.HResult == unchecked((int)0x80070002)) { }
        finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
}

public static class ScheduledTaskDefinition
{
    public static string Name(Guid id) => $"AgentSignaler-Heartbeat-{id:D}";
    public static string Owner(Guid id) => $"AgentSignaler integration {id:D}";
    public static bool IsOwned(string xml, Guid id, string relayPath, string configPath)
    {
        try
        {
            if (xml is null || xml.Length > 65536) return false;
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            if (doc.Root?.Name != ns + "Task" || id == Guid.Empty ||
                !Path.IsPathFullyQualified(relayPath) || !Path.IsPathFullyQualified(configPath))
                return false;
            var registration = doc.Root.Elements(ns + "RegistrationInfo").SingleOrDefault();
            if (registration?.Elements(ns + "Description").SingleOrDefault()?.Value != Owner(id)) return false;
            var actions = doc.Root.Elements(ns + "Actions").SingleOrDefault();
            var action = actions?.Elements().SingleOrDefault();
            if (action?.Name != ns + "Exec" || action.Elements().Count() != 2) return false;
            var command = action.Elements(ns + "Command").SingleOrDefault()?.Value;
            var arguments = action.Elements(ns + "Arguments").SingleOrDefault()?.Value;
            const string prefix = "heartbeat --config ";
            return string.Equals(command, Path.GetFullPath(relayPath), StringComparison.OrdinalIgnoreCase) &&
                arguments is not null && arguments.StartsWith(prefix, StringComparison.Ordinal) &&
                string.Equals(arguments[prefix.Length..], QuoteArgument(Path.GetFullPath(configPath)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or ArgumentException) { return false; }
    }

    public static string QuoteArgument(string argument)
    {
        // Windows CommandLineToArgvW escaping, including trailing backslashes.
        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
