using Teager.Dsh.Domain;

namespace Teager.Dsh.Infrastructure.Permissions;

/// <summary>Pool 树节点（内存快照）。</summary>
/// <param name="Id">Pool ID。</param>
/// <param name="ParentId">父 Pool ID。</param>
/// <param name="Name">名称。</param>
/// <param name="Depth">层级深度（根为 1）。</param>
/// <param name="Status">状态。</param>
public record PoolNode(string Id, string? ParentId, string Name, int Depth, PoolStatus Status);

/// <summary>Pool 树的内存图：由闭包表构建，供权限计算使用。</summary>
public sealed class PoolGraph
{
    /// <summary>所有 Pool 节点。</summary>
    public Dictionary<string, PoolNode> Nodes { get; } = new();
    /// <summary>祖先列表（由近到远，不含自身）。</summary>
    public Dictionary<string, List<string>> Ancestors { get; } = new();
    /// <summary>后代列表（不含自身）。</summary>
    public Dictionary<string, List<string>> Descendants { get; } = new();

    /// <summary>获取祖先列表，不存在返回空。</summary>
    /// <param name="poolId">Pool ID。</param>
    public IReadOnlyList<string> AncestorsOf(string poolId) => Ancestors.TryGetValue(poolId, out var l) ? l : [];

    /// <summary>获取后代列表，不存在返回空。</summary>
    /// <param name="poolId">Pool ID。</param>
    public IReadOnlyList<string> DescendantsOf(string poolId) => Descendants.TryGetValue(poolId, out var l) ? l : [];

    /// <summary>取 Pool 名称（未知返回 ID）。</summary>
    /// <param name="poolId">Pool ID。</param>
    public string NameOf(string poolId) => Nodes.TryGetValue(poolId, out var n) ? n.Name : poolId;

    /// <summary>从 ancestorId 向下到 poolId 的名称路径（含两端），用于“继承路径”展示。</summary>
    /// <param name="ancestorId">路径起点（祖先或自身）。</param>
    /// <param name="poolId">路径终点。</param>
    public List<string> PathNames(string ancestorId, string poolId)
    {
        var chain = new List<string> { poolId };
        foreach (var a in AncestorsOf(poolId)) { chain.Add(a); if (a == ancestorId) break; }
        if (ancestorId == poolId) chain = [poolId];
        chain.Reverse();
        return chain.Select(NameOf).ToList();
    }
}

/// <summary>用户对某个 Pool 的访问结果。</summary>
/// <param name="CanView">是否可见（含通过子 Pool 成员关系看到祖先 Pool）。</param>
/// <param name="CanManage">是否可管理（Owner/Admin，含祖先 Pool 管理员的向下委托管理）。</param>
/// <param name="DirectRole">直属成员角色。</param>
/// <param name="Source">来源：platform-admin / direct / ancestor-admin / descendant-member / none。</param>
/// <param name="ViaPoolId">来源所经过的 Pool。</param>
public record PoolAccess(bool CanView, bool CanManage, PoolRole? DirectRole, string Source, string? ViaPoolId);

/// <summary>用户对某个 Workspace 的访问结果。</summary>
/// <param name="Role">有效角色，null 表示无数据访问权。</param>
/// <param name="Denied">是否被显式拒绝。</param>
/// <param name="Source">来源：platform-admin / explicit-deny / workspace-member / pool-direct / pool-inherited / none。</param>
/// <param name="ViaPoolId">来源 Pool（继承时为子 Pool）。</param>
/// <param name="Path">继承路径（名称列表）。</param>
public record WorkspaceAccess(WorkspaceRole? Role, bool Denied, string Source, string? ViaPoolId, IReadOnlyList<string> Path);

/// <summary>某 Pool 通过（直接 / 继承）授权可用的共享空间。</summary>
/// <param name="SharedSpaceId">共享空间 ID。</param>
/// <param name="Permission">权限级别，被拒绝时为 null。</param>
/// <param name="Denied">是否被显式拒绝。</param>
/// <param name="Source">direct / inherited。</param>
/// <param name="ViaPoolId">授权所在 Pool。</param>
/// <param name="GrantId">授权记录 ID。</param>
/// <param name="Path">继承路径。</param>
public record PoolSpaceAccess(string SharedSpaceId, SharePermission? Permission, bool Denied, string Source, string ViaPoolId, string GrantId, IReadOnlyList<string> Path);

/// <summary>共享空间访问结论（用户级或 Workspace 级）。</summary>
/// <param name="Permission">有效权限，null 表示无权。</param>
/// <param name="Source">来源说明标识。</param>
/// <param name="ViaPoolId">来源 Pool。</param>
/// <param name="GrantId">来源授权记录。</param>
/// <param name="Path">继承路径。</param>
/// <param name="Notes">说明步骤（供可解释性展示）。</param>
public record SpaceAccess(SharePermission? Permission, string Source, string? ViaPoolId, string? GrantId, IReadOnlyList<string> Path, IReadOnlyList<string> Notes);
