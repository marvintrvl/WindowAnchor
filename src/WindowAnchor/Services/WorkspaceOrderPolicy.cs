using System;
using System.Collections.Generic;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Pure ordering policy for the Settings workspace list.</summary>
internal static class WorkspaceOrderPolicy
{
    internal static List<WorkspaceSnapshot> Order(
        IEnumerable<WorkspaceSnapshot> workspaces,
        IReadOnlyList<string>? preferredIds)
    {
        List<WorkspaceSnapshot> all = workspaces.ToList();
        if (preferredIds is null || preferredIds.Count == 0)
            return all.OrderByDescending(workspace => workspace.SavedAt).ToList();

        var result = new List<WorkspaceSnapshot>(all.Count);
        foreach (string workspaceId in preferredIds)
        {
            WorkspaceSnapshot? workspace = all.FirstOrDefault(item =>
                item.WorkspaceId.Equals(workspaceId, StringComparison.OrdinalIgnoreCase));
            if (workspace is not null && !result.Any(item =>
                    item.WorkspaceId.Equals(workspace.WorkspaceId, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(workspace);
            }
        }

        foreach (WorkspaceSnapshot workspace in all.OrderByDescending(item => item.SavedAt))
        {
            if (!result.Any(item =>
                    item.WorkspaceId.Equals(workspace.WorkspaceId, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(workspace);
            }
        }
        return result;
    }
}
