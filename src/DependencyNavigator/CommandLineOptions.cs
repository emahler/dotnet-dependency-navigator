namespace DependencyNavigator;

public class CommandLineOptions
{
	public const string Usage =
		"""
		Usage: dotnet dependency-navigator [serve] [<repository root>] [options]

		  serve                 Keep running and serve the viewer after writing the files.
		  <repository root>     The directory to analyse. Default: the git repository that
		                        contains the current directory.

		Options:
		  --output <directory>  Where the files are written.
		                        Default: <repository root>/dependency-navigator-output
		  --tiers <file>        The tier rules file. Default: the file delivered with the tool.
		  --port <number>       The port of the viewer in the serve mode. Default: 5177
		  --no-browser          Do not open the browser in the serve mode.
		""";

	public bool Serve { get; private set; }

	public string? RepositoryRoot { get; private set; }

	public string? OutputDirectory { get; private set; }

	public string? TierRulesFile { get; private set; }

	public int Port { get; private set; } = 5177;

	public bool OpenBrowser { get; private set; } = true;

	public static CommandLineOptions Parse(string[] args)
	{
		var result = new CommandLineOptions();

		for (int index = 0; index < args.Length; index++)
		{
			string argument = args[index];

			switch (argument)
			{
				case "serve" when index == 0:
					result.Serve = true;
					break;
				case "--output":
					result.OutputDirectory = Path.GetFullPath(GetValue());
					break;
				case "--tiers":
					result.TierRulesFile = Path.GetFullPath(GetValue());
					break;
				case "--port":
					result.Port = int.Parse(GetValue());
					break;
				case "--no-browser":
					result.OpenBrowser = false;
					break;
				default:
					if (argument.StartsWith("--") || result.RepositoryRoot is not null)
					{
						throw new ArgumentException($"Unexpected argument: {argument}");
					}

					result.RepositoryRoot = Path.GetFullPath(argument);
					break;
			}

			string GetValue()
			{
				if (index + 1 >= args.Length)
				{
					throw new ArgumentException($"The option {argument} needs a value.");
				}

				return args[++index];
			}
		}

		return result;
	}
}
