using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DependencyNavigator;

internal static class Program
{
	private const string _modelPlaceholder = "/*DEPENDENCY_MODEL*/null";

	private static int Main(string[] args)
	{
		try
		{
			if (args.Contains("--help") || args.Contains("-h"))
			{
				Console.WriteLine(CommandLineOptions.Usage);

				return 0;
			}

			CommandLineOptions options = CommandLineOptions.Parse(args);

			string repositoryRoot = options.RepositoryRoot ??
			                        FindRepositoryRoot(Directory.GetCurrentDirectory());

			string outputDirectory = options.OutputDirectory ??
			                         Path.Combine(repositoryRoot, "dependency-navigator-output");

			TierRules tierRules = TierRules.Load(
				options.TierRulesFile ??
				Path.Combine(AppContext.BaseDirectory, "architecture-tiers.json"));

			DependencyModel model = ProjectFileReader.ReadRepository(repositoryRoot, tierRules);

			SourceAnalysis sourceAnalysis = SourceAnalysis.Create(model, repositoryRoot);

			string modelJson = JsonSerializer.Serialize(
				model,
				new JsonSerializerOptions
				{
					PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
					DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
				});

			string viewerHtml = ReadViewerTemplate().Replace(_modelPlaceholder, modelJson);

			WriteOutputFiles(outputDirectory, model, modelJson, viewerHtml);

			Console.WriteLine(
				$"{model.Projects.Count} projects, {model.References.Count} project " +
				$"references, {model.Types.Count} types and {model.TypeReferences.Count} " +
				$"type references written to {outputDirectory}");

			if (options.Serve)
			{
				string address = $"http://localhost:{options.Port}/";

				Console.WriteLine($"The viewer is served at {address} Press Ctrl+C to stop.");

				if (options.OpenBrowser)
				{
					Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
				}

				new ViewerServer(repositoryRoot, viewerHtml, sourceAnalysis, model.Types.Count)
					.Run(address);
			}

			return 0;
		}
		catch (ArgumentException exception)
		{
			Console.Error.WriteLine(exception.Message);
			Console.Error.WriteLine();
			Console.Error.WriteLine(CommandLineOptions.Usage);

			return 1;
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine(exception);

			return 1;
		}
	}

	private static void WriteOutputFiles(string outputDirectory,
	                                     DependencyModel model,
	                                     string modelJson,
	                                     string viewerHtml)
	{
		Directory.CreateDirectory(outputDirectory);

		File.WriteAllText(Path.Combine(outputDirectory, "dependency-model.json"), modelJson);
		File.WriteAllText(Path.Combine(outputDirectory, "dependency-navigator.html"), viewerHtml);
		File.WriteAllText(Path.Combine(outputDirectory, "architecture-overview.md"),
		                  OverviewWriter.CreateOverview(model));

		string projectsDirectory = Path.Combine(outputDirectory, "projects");
		Directory.CreateDirectory(projectsDirectory);

		for (int projectIndex = 0; projectIndex < model.Projects.Count; projectIndex++)
		{
			File.WriteAllText(
				Path.Combine(projectsDirectory, model.Projects[projectIndex].Name + ".md"),
				OverviewWriter.CreateProjectOverview(model, projectIndex));
		}
	}

	private static string FindRepositoryRoot(string startDirectory)
	{
		for (DirectoryInfo? directory = new DirectoryInfo(startDirectory);
		     directory is not null;
		     directory = directory.Parent)
		{
			if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
			{
				return directory.FullName;
			}
		}

		throw new DirectoryNotFoundException(
			$"{startDirectory} is not inside a git repository. " +
			"Pass the repository root as an argument.");
	}

	private static string ReadViewerTemplate()
	{
		using Stream stream = Assembly.GetExecutingAssembly()
		                              .GetManifestResourceStream("ViewerTemplate.html")!;
		using var reader = new StreamReader(stream);

		return reader.ReadToEnd();
	}
}
