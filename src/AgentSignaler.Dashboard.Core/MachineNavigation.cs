using AgentSignaler.Service;

namespace AgentSignaler.Dashboard;

internal static class MachineNavigation
{
    public static bool IsLocal(MachineView machine) =>
        string.Equals(machine.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    public static string Name(MachineView machine) => IsLocal(machine) ? "local" : machine.Name;

    public static IOrderedEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, MachineView> machine) =>
        items.OrderBy(item => !IsLocal(machine(item)))
            .ThenBy(item => Name(machine(item)), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => machine(item).MachineId);
}
