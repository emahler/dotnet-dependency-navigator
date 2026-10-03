namespace DependencyNavigator;

public class DependencyModel
{
	public required string Generated { get; init; }

	/// <summary>
	/// The tier names in the order of the tier rules file.
	/// </summary>
	public required IReadOnlyList<string> Tiers { get; init; }

	public required IReadOnlyList<ProjectNode> Projects { get; init; }

	/// <summary>
	/// Each entry is a pair of indexes into <see cref="Projects" />: the referencing project
	/// and the referenced project.
	/// </summary>
	public required IReadOnlyList<int[]> References { get; init; }

	/// <summary>
	/// The types declared directly in a namespace. Nested types count as part of the type
	/// that contains them.
	/// </summary>
	public IReadOnlyList<TypeNode> Types { get; set; } = new List<TypeNode>();

	/// <summary>
	/// Each entry holds two indexes into <see cref="Types" /> (the referencing type and the
	/// referenced type) and the <see cref="TypeReferenceKind" /> as a number.
	/// </summary>
	public IReadOnlyList<int[]> TypeReferences { get; set; } = new List<int[]>();
}

public enum TypeReferenceKind
{
	Uses = 0,
	Implements = 1,
	Inherits = 2
}

public class TypeNode
{
	public required string Name { get; init; }

	public required string Namespace { get; init; }

	/// <summary>
	/// The index of the declaring project in <see cref="DependencyModel.Projects" />.
	/// </summary>
	public required int Project { get; init; }

	/// <summary>
	/// One of: class, record, interface, struct, enum, delegate.
	/// </summary>
	public required string Kind { get; init; }

	/// <summary>
	/// The path of the source file relative to the repository root, with forward slashes.
	/// </summary>
	public required string File { get; init; }

	public required int Line { get; init; }

	/// <summary>
	/// The name of the base class if it is not declared in the source files of the
	/// repository (for example a class of a NuGet package), otherwise null.
	/// </summary>
	public string? ExternalBaseType { get; init; }
}

public class ProjectNode
{
	public required string Name { get; init; }

	/// <summary>
	/// The path of the project file relative to the repository root, with forward slashes.
	/// </summary>
	public required string Path { get; init; }

	/// <summary>
	/// The top-level directory of the repository that contains the project.
	/// </summary>
	public required string TopLevelDirectory { get; init; }

	public required string Tier { get; init; }
}
