using WindowAnchor.Models;

namespace WindowAnchor.Services;

internal static class AppAdapterEntryFactory
{
    internal static WorkspaceEntry CreateBaseEntry(WindowRecord window) => new()
    {
        ExecutablePath = window.ExecutablePath,
        ProcessName = window.ProcessName,
        WindowClassName = window.ClassName,
        AppUserModelId = window.AppUserModelId,
        Position = window,
        MonitorId = window.MonitorId,
        MonitorIndex = window.MonitorIndex,
        MonitorName = window.MonitorName
    };
}
