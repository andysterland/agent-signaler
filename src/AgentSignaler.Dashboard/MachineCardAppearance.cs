using AgentSignaler.Contracts;

namespace AgentSignaler.Dashboard;

internal readonly record struct MachineCardAppearance(
    string Glyph, uint Background, uint Border, uint Icon, uint Text, uint Connection,
    uint Status, uint Activity, uint Mapping, uint Footer)
{
    // Both views use the state palette from docs\images\dashboard-overview.svg.
    public static MachineCardAppearance For(AgentState state) => state switch
    {
        AgentState.Executing => new("\u25B6", 0x123B68, 0x38BDF8, 0x7DD3FC, 0xE0F2FE, 0x22C55E,
            0xF8FAFC, 0xBAE6FD, 0xDBEAFE, 0x93C5FD),
        AgentState.Waiting => new("\u25CF", 0x5A3C0B, 0xF59E0B, 0xFBBF24, 0xFEF3C7, 0x22C55E,
            0xFFFBEB, 0xFDE68A, 0xFEF3C7, 0xFCD34D),
        AgentState.Succeeded => new("\u2713", 0x12452B, 0x22C55E, 0x4ADE80, 0xDCFCE7, 0x22C55E,
            0xF0FDF4, 0xBBF7D0, 0xDCFCE7, 0x86EFAC),
        AgentState.Failed => new("\u00D7", 0x5A1C22, 0xEF4444, 0xF87171, 0xFEE2E2, 0x22C55E,
            0xFEF2F2, 0xFECACA, 0xFEE2E2, 0xFCA5A5),
        AgentState.Idle => new("\u2014", 0x293548, 0x64748B, 0x94A3B8, 0xF1F5F9, 0x22C55E,
            0xF8FAFC, 0xCBD5E1, 0xE2E8F0, 0x94A3B8),
        _ => new("\u25CB", 0x242B37, 0x475569, 0x64748B, 0xCBD5E1, 0x64748B,
            0xE2E8F0, 0x94A3B8, 0xCBD5E1, 0x64748B)
    };
}
