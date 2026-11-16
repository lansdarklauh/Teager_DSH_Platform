namespace Teager.Dsh.Domain;

/// <summary>Immutable project metadata attached to a user's message.</summary>
public record ProjectReference(string Id, string Name, string Directory, string? Description);
