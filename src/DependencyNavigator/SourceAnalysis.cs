using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyNavigator;

/// <summary>
/// Reads the types and the references between them from the source files, without building
/// the projects. Each project is compiled in memory from its source files, the compilations
/// of the projects it references and the .NET runtime assemblies. Other assemblies (for example
/// NuGet packages, generated code in obj directories) are not loaded, so their types are
/// unknown and references that can only be resolved through them are missed.
/// The compilations are kept, so that the members behind one type reference can be found
/// later on request.
/// </summary>
public class SourceAnalysis
{
	private static readonly string[] _ignoredDirectoryNames =
		{ "bin", "obj", ".vs", ".git", "node_modules" };

	private static readonly SymbolDisplayFormat _typeNameFormat = new(
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

	private readonly string _repositoryRoot;
	private readonly Dictionary<string, int> _typeIndexByKey = new();
	private readonly List<List<TypeDeclaration>> _declarationsByTypeIndex = new();

	private SourceAnalysis(string repositoryRoot)
	{
		_repositoryRoot = repositoryRoot;
	}

	/// <summary>
	/// Analyses the source files of all projects and adds the types and the type references
	/// to the model.
	/// </summary>
	public static SourceAnalysis Create(DependencyModel model, string repositoryRoot)
	{
		var result = new SourceAnalysis(repositoryRoot);

		CSharpCompilation[] compilations = CreateCompilations(model, repositoryRoot);

		var types = new List<TypeNode>();

		for (int projectIndex = 0; projectIndex < compilations.Length; projectIndex++)
		{
			CSharpCompilation compilation = compilations[projectIndex];

			foreach (SyntaxTree syntaxTree in compilation.SyntaxTrees)
			{
				SemanticModel semanticModel = compilation.GetSemanticModel(syntaxTree);

				foreach (MemberDeclarationSyntax declarationSyntax in
				         GetTopLevelTypeDeclarations(syntaxTree))
				{
					if (semanticModel.GetDeclaredSymbol(declarationSyntax) is not INamedTypeSymbol
					    typeSymbol)
					{
						continue;
					}

					string key = GetKey(typeSymbol);

					if (! result._typeIndexByKey.TryGetValue(key, out int typeIndex))
					{
						typeIndex = types.Count;
						result._typeIndexByKey.Add(key, typeIndex);
						result._declarationsByTypeIndex.Add(new List<TypeDeclaration>());
						types.Add(result.CreateTypeNode(typeSymbol, projectIndex,
						                                declarationSyntax));
					}

					result._declarationsByTypeIndex[typeIndex].Add(
						new TypeDeclaration(compilation, declarationSyntax, typeIndex));
				}
			}
		}

		var referenceKinds = new ConcurrentDictionary<(int From, int To), TypeReferenceKind>();

		Parallel.ForEach(
			result._declarationsByTypeIndex.SelectMany(declarations => declarations)
			      .GroupBy(declaration => declaration.Syntax.SyntaxTree),
			declarationsOfTree =>
			{
				SemanticModel semanticModel =
					declarationsOfTree.First().Compilation.GetSemanticModel(declarationsOfTree.Key);

				var typeIndexBySymbol =
					new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);

				foreach (TypeDeclaration declaration in declarationsOfTree)
				{
					result.CollectReferences(declaration, semanticModel, typeIndexBySymbol,
					                         referenceKinds);
				}
			});

		model.Types = types;
		model.TypeReferences =
			referenceKinds.OrderBy(pair => pair.Key.From)
			              .ThenBy(pair => pair.Key.To)
			              .Select(pair => new[] { pair.Key.From, pair.Key.To, (int) pair.Value })
			              .ToList();

		return result;
	}

	/// <summary>
	/// Returns the members of the referencing type that mention the referenced type or one of
	/// its members, with the line of each mention.
	/// </summary>
	public List<ReferencingMember> FindReferencingMembers(int referencingTypeIndex,
	                                                      int referencedTypeIndex)
	{
		var result = new List<ReferencingMember>();
		var memberBySyntax = new Dictionary<SyntaxNode, ReferencingMember>();

		foreach (TypeDeclaration declaration in _declarationsByTypeIndex[referencingTypeIndex])
		{
			SemanticModel semanticModel =
				declaration.Compilation.GetSemanticModel(declaration.Syntax.SyntaxTree);

			var typeIndexBySymbol = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);

			foreach (SimpleNameSyntax name in
			         declaration.Syntax.DescendantNodes().OfType<SimpleNameSyntax>())
			{
				ISymbol? referencedSymbol = GetReferencedSymbol(name, semanticModel);

				if (GetTypeIndex(GetTypeOf(referencedSymbol), typeIndexBySymbol) !=
				    referencedTypeIndex)
				{
					continue;
				}

				// A mention outside of all members is in the declaration of the type itself,
				// for example in the list of base types.
				SyntaxNode memberSyntax =
					name.Ancestors().FirstOrDefault(
						ancestor => ancestor is BaseMethodDeclarationSyntax
							            or BasePropertyDeclarationSyntax
							            or BaseFieldDeclarationSyntax
							            or EnumMemberDeclarationSyntax) ?? declaration.Syntax;

				if (! memberBySyntax.TryGetValue(memberSyntax, out ReferencingMember? member))
				{
					member = CreateReferencingMember(memberSyntax, semanticModel);
					memberBySyntax.Add(memberSyntax, member);
					result.Add(member);
				}

				var mention = new Mention(
					name.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
					referencedSymbol!.ToDisplayString(
						SymbolDisplayFormat.CSharpShortErrorMessageFormat));

				if (! member.Mentions.Contains(mention))
				{
					member.Mentions.Add(mention);
				}
			}
		}

		return result.OrderBy(member => member.File).ThenBy(member => member.Line).ToList();
	}

	private ReferencingMember CreateReferencingMember(SyntaxNode memberSyntax,
	                                                  SemanticModel semanticModel)
	{
		// The symbol of a field declaration belongs to its first variable
		SyntaxNode symbolSyntax = memberSyntax is BaseFieldDeclarationSyntax fieldSyntax
			                          ? fieldSyntax.Declaration.Variables[0]
			                          : memberSyntax;

		ISymbol? symbol = semanticModel.GetDeclaredSymbol(symbolSyntax);

		FileLinePositionSpan position = memberSyntax.GetLocation().GetLineSpan();

		return new ReferencingMember(
			symbol is null or INamedTypeSymbol
				? "(declaration of the type)"
				: symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
			GetRelativePath(position.Path),
			position.StartLinePosition.Line + 1,
			new List<Mention>());
	}

	private static CSharpCompilation[] CreateCompilations(DependencyModel model,
	                                                      string repositoryRoot)
	{
		int projectCount = model.Projects.Count;

		List<string> projectDirectories =
			model.Projects
			     .Select(project => Path.GetDirectoryName(
				             Path.GetFullPath(Path.Combine(repositoryRoot, project.Path)))!)
			     .ToList();

		var parseOptions = new CSharpParseOptions(
			LanguageVersion.Latest,
			preprocessorSymbols: new[]
			                     {
				                     "DEBUG", "TRACE", "NET", "NETCOREAPP", "NET6_0_OR_GREATER",
				                     "NET8_0", "NET8_0_OR_GREATER"
			                     });

		var syntaxTrees = new List<SyntaxTree>[projectCount];

		Parallel.For(0, projectCount, projectIndex =>
		{
			syntaxTrees[projectIndex] =
				FindSourceFiles(projectDirectories[projectIndex], projectDirectories)
					.Select(filePath => CSharpSyntaxTree.ParseText(
						        File.ReadAllText(filePath), parseOptions, filePath))
					.ToList();
		});

		List<MetadataReference> runtimeReferences =
			((string) AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
			.Split(Path.PathSeparator)
			.Select(filePath => (MetadataReference) MetadataReference.CreateFromFile(filePath))
			.ToList();

		ILookup<int, int> directlyReferenced =
			model.References.ToLookup(reference => reference[0], reference => reference[1]);

		var compilations = new CSharpCompilation[projectCount];

		for (int projectIndex = 0; projectIndex < projectCount; projectIndex++)
		{
			EnsureCompilation(projectIndex);
		}

		return compilations;

		// A project can use the types of all projects it references directly or indirectly.
		void EnsureCompilation(int projectIndex)
		{
			if (compilations[projectIndex] is not null)
			{
				return;
			}

			var allReferenced = new HashSet<int>();
			var pending = new Stack<int>(directlyReferenced[projectIndex]);

			while (pending.Count > 0)
			{
				int referencedIndex = pending.Pop();

				if (! allReferenced.Add(referencedIndex))
				{
					continue;
				}

				foreach (int next in directlyReferenced[referencedIndex])
				{
					pending.Push(next);
				}
			}

			foreach (int referencedIndex in allReferenced)
			{
				EnsureCompilation(referencedIndex);
			}

			compilations[projectIndex] = CSharpCompilation.Create(
				model.Projects[projectIndex].Name,
				syntaxTrees[projectIndex],
				runtimeReferences.Concat(
					allReferenced.Select(
						referencedIndex => compilations[referencedIndex].ToMetadataReference())),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		}
	}

	/// <summary>
	/// Returns the C# files below the project directory, without the files that belong to
	/// another project in a subdirectory.
	/// </summary>
	private static IEnumerable<string> FindSourceFiles(string directory,
	                                                   List<string> projectDirectories)
	{
		foreach (string filePath in Directory.EnumerateFiles(directory, "*.cs"))
		{
			yield return filePath;
		}

		foreach (string subdirectory in Directory.EnumerateDirectories(directory))
		{
			if (_ignoredDirectoryNames.Contains(Path.GetFileName(subdirectory),
			                                    StringComparer.OrdinalIgnoreCase) ||
			    projectDirectories.Contains(subdirectory, StringComparer.OrdinalIgnoreCase))
			{
				continue;
			}

			foreach (string filePath in FindSourceFiles(subdirectory, projectDirectories))
			{
				yield return filePath;
			}
		}
	}

	private static IEnumerable<MemberDeclarationSyntax> GetTopLevelTypeDeclarations(
		SyntaxTree syntaxTree)
	{
		return syntaxTree.GetRoot()
		                 .DescendantNodes(node => node is CompilationUnitSyntax ||
		                                          node is BaseNamespaceDeclarationSyntax)
		                 .OfType<MemberDeclarationSyntax>()
		                 .Where(node => node is BaseTypeDeclarationSyntax ||
		                                node is DelegateDeclarationSyntax);
	}

	private TypeNode CreateTypeNode(INamedTypeSymbol typeSymbol,
	                                int projectIndex,
	                                SyntaxNode declarationSyntax)
	{
		FileLinePositionSpan position = declarationSyntax.GetLocation().GetLineSpan();

		INamedTypeSymbol? baseType = typeSymbol.BaseType;

		bool hasExternalBaseType = typeSymbol.TypeKind == TypeKind.Class &&
		                           baseType is not null &&
		                           baseType.SpecialType == SpecialType.None &&
		                           ! baseType.Locations.Any(location => location.IsInSource);

		return new TypeNode
		       {
			       Name = typeSymbol.ToDisplayString(_typeNameFormat),
			       Namespace = typeSymbol.ContainingNamespace.IsGlobalNamespace
				                   ? string.Empty
				                   : typeSymbol.ContainingNamespace.ToDisplayString(),
			       Project = projectIndex,
			       Kind = typeSymbol.IsRecord
				              ? "record"
				              : typeSymbol.TypeKind.ToString().ToLowerInvariant(),
			       File = GetRelativePath(position.Path),
			       Line = position.StartLinePosition.Line + 1,
			       ExternalBaseType = hasExternalBaseType
				                          ? baseType!.ToDisplayString(_typeNameFormat)
				                          : null
		       };
	}

	private string GetRelativePath(string filePath)
	{
		return Path.GetRelativePath(_repositoryRoot, filePath).Replace('\\', '/');
	}

	private void CollectReferences(
		TypeDeclaration declaration,
		SemanticModel semanticModel,
		Dictionary<ISymbol, int> typeIndexBySymbol,
		ConcurrentDictionary<(int From, int To), TypeReferenceKind> referenceKinds)
	{
		if (semanticModel.GetDeclaredSymbol(declaration.Syntax) is INamedTypeSymbol declaredType)
		{
			AddReference(declaredType.BaseType, TypeReferenceKind.Inherits);

			foreach (INamedTypeSymbol implementedInterface in declaredType.Interfaces)
			{
				AddReference(implementedInterface,
				             declaredType.TypeKind == TypeKind.Interface
					             ? TypeReferenceKind.Inherits
					             : TypeReferenceKind.Implements);
			}
		}

		foreach (SimpleNameSyntax name in
		         declaration.Syntax.DescendantNodes().OfType<SimpleNameSyntax>())
		{
			AddReference(GetTypeOf(GetReferencedSymbol(name, semanticModel)),
			             TypeReferenceKind.Uses);
		}

		void AddReference(INamedTypeSymbol? referencedType, TypeReferenceKind kind)
		{
			int referencedIndex = GetTypeIndex(referencedType, typeIndexBySymbol);

			if (referencedIndex < 0 || referencedIndex == declaration.TypeIndex)
			{
				return;
			}

			referenceKinds.AddOrUpdate((declaration.TypeIndex, referencedIndex), kind,
			                           (_, existingKind) =>
				                           kind > existingKind ? kind : existingKind);
		}
	}

	private static ISymbol? GetReferencedSymbol(SimpleNameSyntax name,
	                                            SemanticModel semanticModel)
	{
		SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(name);

		// An inaccessible or ambiguous symbol is still a reference to its type
		return symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
	}

	/// <summary>
	/// Returns the symbol itself if it is a type, or the type that declares the member.
	/// </summary>
	private static INamedTypeSymbol? GetTypeOf(ISymbol? symbol)
	{
		return symbol as INamedTypeSymbol ??
		       (symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol
			        ? symbol.ContainingType
			        : null);
	}

	/// <summary>
	/// Returns the index of the type in the model, or -1 if the type is not declared in the
	/// source files. A nested type counts as the type that contains it.
	/// </summary>
	private int GetTypeIndex(INamedTypeSymbol? type, Dictionary<ISymbol, int> typeIndexBySymbol)
	{
		if (type is null)
		{
			return -1;
		}

		while (type.ContainingType is not null)
		{
			type = type.ContainingType;
		}

		type = type.OriginalDefinition;

		if (! typeIndexBySymbol.TryGetValue(type, out int typeIndex))
		{
			typeIndex = type.ContainingAssembly is not null &&
			            _typeIndexByKey.TryGetValue(GetKey(type), out int knownIndex)
				            ? knownIndex
				            : -1;
			typeIndexBySymbol.Add(type, typeIndex);
		}

		return typeIndex;
	}

	private static string GetKey(INamedTypeSymbol typeSymbol)
	{
		return typeSymbol.ContainingAssembly.Name + "|" +
		       typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
	}

	private record TypeDeclaration(CSharpCompilation Compilation,
	                               MemberDeclarationSyntax Syntax,
	                               int TypeIndex);
}

/// <summary>
/// A member of a type that mentions another type, with the lines of the mentions.
/// </summary>
public record ReferencingMember(string Name, string File, int Line, List<Mention> Mentions);

public record Mention(int Line, string Target);
