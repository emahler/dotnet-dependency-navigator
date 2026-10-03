using System.Xml.Linq;

namespace DependencyNavigator;

public static class ProjectFileReader
{
	private static readonly string[] _ignoredDirectoryNames =
		{ "bin", "obj", ".vs", ".git", "node_modules" };

	public static DependencyModel ReadRepository(string repositoryRoot, TierRules tierRules)
	{
		List<string> projectFilePaths = FindProjectFiles(repositoryRoot);

		var projects = new List<ProjectNode>();
		var indexByFilePath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		foreach (string projectFilePath in projectFilePaths)
		{
			string relativePath = Path.GetRelativePath(repositoryRoot, projectFilePath)
			                          .Replace('\\', '/');
			string name = Path.GetFileNameWithoutExtension(projectFilePath);

			indexByFilePath.Add(projectFilePath, projects.Count);
			projects.Add(new ProjectNode
			             {
				             Name = name,
				             Path = relativePath,
				             TopLevelDirectory = relativePath.Contains('/')
				                                 ? relativePath.Split('/')[0]
				                                 : "(repository root)",
				             Tier = tierRules.GetTierName(name)
			             });
		}

		var references = new List<int[]>();

		foreach (string projectFilePath in projectFilePaths)
		{
			int referencingIndex = indexByFilePath[projectFilePath];

			foreach (string referencedFilePath in ReadReferencedProjectFiles(projectFilePath))
			{
				if (! indexByFilePath.TryGetValue(referencedFilePath, out int referencedIndex))
				{
					Console.WriteLine(
						$"Warning: {projects[referencingIndex].Path} references a project " +
						$"file that was not found: {referencedFilePath}");
					continue;
				}

				references.Add(new[] { referencingIndex, referencedIndex });
			}
		}

		return new DependencyModel
		       {
			       Generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
			       Tiers = tierRules.TierNames,
			       Projects = projects,
			       References = references
		       };
	}

	private static List<string> FindProjectFiles(string repositoryRoot)
	{
		var result = new List<string>();

		foreach (string filePath in Directory.EnumerateFiles(
			         repositoryRoot, "*.csproj", SearchOption.AllDirectories))
		{
			string[] directoryNames =
				Path.GetRelativePath(repositoryRoot, Path.GetDirectoryName(filePath)!)
				    .Split(Path.DirectorySeparatorChar);

			if (directoryNames.Intersect(_ignoredDirectoryNames, StringComparer.OrdinalIgnoreCase)
			                  .Any())
			{
				continue;
			}

			// Temporary project files that the WPF build leaves behind
			if (Path.GetFileNameWithoutExtension(filePath).EndsWith("_wpftmp"))
			{
				continue;
			}

			result.Add(Path.GetFullPath(filePath));
		}

		result.Sort(StringComparer.OrdinalIgnoreCase);

		return result;
	}

	/// <summary>
	/// Returns all project references of the project file, including the references that
	/// apply only under a build condition.
	/// </summary>
	private static IEnumerable<string> ReadReferencedProjectFiles(string projectFilePath)
	{
		string projectDirectory = Path.GetDirectoryName(projectFilePath)!;

		return XDocument.Load(projectFilePath)
		                .Descendants()
		                .Where(element => element.Name.LocalName == "ProjectReference")
		                .Select(element => element.Attribute("Include")?.Value)
		                .Where(include => ! string.IsNullOrWhiteSpace(include))
		                .Select(include => Path.GetFullPath(
			                        Path.Combine(projectDirectory, include!)))
		                .Distinct(StringComparer.OrdinalIgnoreCase);
	}
}
