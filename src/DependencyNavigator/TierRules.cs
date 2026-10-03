using System.IO.Enumeration;
using System.Text.Json;

namespace DependencyNavigator;

/// <summary>
/// Assigns each project to a tier by its name. The first tier with a matching pattern wins.
/// </summary>
public class TierRules
{
	private readonly List<Tier> _tiers;

	private TierRules(List<Tier> tiers)
	{
		_tiers = tiers;
	}

	public IReadOnlyList<string> TierNames => _tiers.Select(tier => tier.Name).ToList();

	public static TierRules Load(string rulesFilePath)
	{
		var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

		RulesFile? rulesFile =
			JsonSerializer.Deserialize<RulesFile>(File.ReadAllText(rulesFilePath), options);

		if (rulesFile?.Tiers is null || rulesFile.Tiers.Count == 0)
		{
			throw new InvalidDataException($"No tiers are defined in {rulesFilePath}");
		}

		return new TierRules(rulesFile.Tiers);
	}

	public string GetTierName(string projectName)
	{
		foreach (Tier tier in _tiers)
		{
			if (tier.Patterns.Any(
				    pattern => FileSystemName.MatchesSimpleExpression(pattern, projectName)))
			{
				return tier.Name;
			}
		}

		throw new InvalidDataException(
			$"No tier pattern matches the project {projectName}. " +
			"Add a last tier with the pattern \"*\" to the tier rules file.");
	}

	private class RulesFile
	{
		public List<Tier>? Tiers { get; set; }
	}

	private class Tier
	{
		public string Name { get; set; } = string.Empty;

		public List<string> Patterns { get; set; } = new();
	}
}
