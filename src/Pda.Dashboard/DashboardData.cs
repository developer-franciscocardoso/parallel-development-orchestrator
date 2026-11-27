using Microsoft.AspNetCore.Http;
using Pda.Core;
using Pda.Hosting;

namespace Pda.Dashboard;

public sealed record DashboardData(string Section, IReadOnlyList<ProjectInfo> Projects,
    IReadOnlyList<PlanInfo> Plans, PlanReport? Selected, DoctorReport? Doctor,
    long Cursor)
{
    public static readonly string[] Sections =
    [
        "Overview", "Projects", "Plans", "Executions", "Tasks", "Workers",
        "Dependency graphs", "Quality", "Integration", "Analytics",
        "Configuration", "Diagnostics"
    ];

    public static DashboardData Load(OrchestratorHost host, IQueryCollection query)
    {
        string name = query["section"].ToString();
        var section = Sections.FirstOrDefault(s => string.Equals(s, name,
            StringComparison.OrdinalIgnoreCase)) ??
            (name.Length == 0 ? "Overview" :
                throw new KeyNotFoundException("Unknown dashboard section."));
        // Capture cursor first so events committed during a read are replayed.
        var cursor = host.EventCursor();
        var projects = host.Store.Projects();
        var plans = host.Store.Plans();
        var id = query["plan"].ToString();
        if (id.Length > 0 && !plans.Any(p => p.Id == id))
            throw new KeyNotFoundException("Unknown plan.");
        var current = id.Length == 0 ? plans.FirstOrDefault()?.Id : id;
        var selected = current is null ? null : host.Report(current);
        var doctor = section == "Diagnostics" ? host.Doctor() : null;
        return new(section, projects, plans, selected, doctor, cursor);
    }

    public static string Link(string section, string? plan) =>
        "/?section=" + Uri.EscapeDataString(section) + (plan is null ? "" :
            "&plan=" + Uri.EscapeDataString(plan));
}
