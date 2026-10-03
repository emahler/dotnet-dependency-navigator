using System.Net;
using System.Text;
using System.Text.Json;

namespace DependencyNavigator;

/// <summary>
/// Serves the viewer on this computer only, and answers the questions that the viewer cannot
/// answer from its embedded model: which members cause a type reference, and what the source
/// file looks like.
/// </summary>
public class ViewerServer
{
	private static readonly JsonSerializerOptions _jsonOptions =
		new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	private readonly string _repositoryRoot;
	private readonly string _viewerHtml;
	private readonly SourceAnalysis _sourceAnalysis;
	private readonly int _typeCount;

	public ViewerServer(string repositoryRoot,
	                    string viewerHtml,
	                    SourceAnalysis sourceAnalysis,
	                    int typeCount)
	{
		_repositoryRoot = repositoryRoot;
		_viewerHtml = viewerHtml;
		_sourceAnalysis = sourceAnalysis;
		_typeCount = typeCount;
	}

	public void Run(string address)
	{
		using var listener = new HttpListener();
		listener.Prefixes.Add(address);
		listener.Start();

		while (true)
		{
			HttpListenerContext context = listener.GetContext();

			try
			{
				Respond(context);
			}
			catch (Exception exception)
			{
				// One failed request must not stop the server
				Console.Error.WriteLine($"Error answering {context.Request.RawUrl}: {exception}");
				TryWrite(context.Response, 500, "text/plain", exception.Message);
			}
		}
	}

	private void Respond(HttpListenerContext context)
	{
		HttpListenerRequest request = context.Request;

		switch (request.Url!.AbsolutePath)
		{
			case "/":
				Write(context.Response, 200, "text/html", _viewerHtml);
				break;

			case "/api/references":
				int referencingTypeIndex = GetTypeIndex(request, "from");
				int referencedTypeIndex = GetTypeIndex(request, "to");

				Write(context.Response, 200, "application/json",
				      JsonSerializer.Serialize(
					      _sourceAnalysis.FindReferencingMembers(
						      referencingTypeIndex, referencedTypeIndex), _jsonOptions));
				break;

			case "/source":
				Write(context.Response, 200, "text/html",
				      CreateSourcePage(request.QueryString["file"] ?? string.Empty,
				                       ParseNumbers(request.QueryString["line"]).FirstOrDefault(),
				                       ParseNumbers(request.QueryString["mentions"])));
				break;

			case "/api/open-in-visual-studio":
				bool opened = VisualStudioOpener.TryOpen(
					ResolveSourceFile(request.QueryString["file"] ?? string.Empty),
					ParseNumbers(request.QueryString["line"]).FirstOrDefault(1));

				Write(context.Response, opened ? 200 : 404, "text/plain",
				      opened ? "Opened" : "No running Visual Studio was found");
				break;

			default:
				Write(context.Response, 404, "text/plain", "Not found");
				break;
		}
	}

	private int GetTypeIndex(HttpListenerRequest request, string parameterName)
	{
		int typeIndex = int.Parse(request.QueryString[parameterName]!);

		if (typeIndex < 0 || typeIndex >= _typeCount)
		{
			throw new ArgumentOutOfRangeException(parameterName);
		}

		return typeIndex;
	}

	private static List<int> ParseNumbers(string? commaSeparatedNumbers)
	{
		return (commaSeparatedNumbers ?? string.Empty)
		       .Split(',', StringSplitOptions.RemoveEmptyEntries)
		       .Select(int.Parse)
		       .ToList();
	}

	/// <summary>
	/// Creates a page that shows the source file with line numbers. The page scrolls to the
	/// given line and marks the lines that mention the referenced type.
	/// </summary>
	private string CreateSourcePage(string relativeFilePath, int line, List<int> mentionLines)
	{
		string filePath = ResolveSourceFile(relativeFilePath);

		var html = new StringBuilder();

		html.Append(
			"""
			<!doctype html>
			<html lang="en">
			<head>
			<meta charset="utf-8">
			<style>
			  :root { --background: #ffffff; --text: #1c2330; --muted: #98a2b3;
			          --header: #f0f2f5; --border: #d5d9e0; --mention: #fff3bf; --target: #ffd43b; }
			  @media (prefers-color-scheme: dark) {
			    :root { --background: #15181d; --text: #e4e7ec; --muted: #667085;
			            --header: #1e2229; --border: #363c47; --mention: #4a4215; --target: #7a6400; }
			  }
			  body { margin: 0; background: var(--background); color: var(--text);
			         font: 13px/1.5 Consolas, monospace; tab-size: 4; }
			  header { position: sticky; top: 0; padding: 6px 12px; background: var(--header);
			           border-bottom: 1px solid var(--border); font-family: "Segoe UI", sans-serif; }
			  .line { display: flex; white-space: pre; min-height: 1.5em; }
			  .number { width: 5em; padding-right: 1em; text-align: right; color: var(--muted);
			            user-select: none; flex: none; }
			  .mention { background: var(--mention); }
			  .target { background: var(--target); }
			</style>
			""");

		html.Append("<title>").Append(WebUtility.HtmlEncode(Path.GetFileName(filePath)))
		    .Append("</title></head><body><header>")
		    .Append(WebUtility.HtmlEncode($"{relativeFilePath}:{line}"))
		    .Append("</header>");

		string[] sourceLines = File.ReadAllLines(filePath);

		for (int index = 0; index < sourceLines.Length; index++)
		{
			int lineNumber = index + 1;

			string cssClass = lineNumber == line
				                  ? "line target"
				                  : mentionLines.Contains(lineNumber)
					                  ? "line mention"
					                  : "line";

			html.Append($"<div class=\"{cssClass}\" id=\"L{lineNumber}\">")
			    .Append($"<span class=\"number\">{lineNumber}</span><span>")
			    .Append(WebUtility.HtmlEncode(sourceLines[index]))
			    .Append("</span></div>");
		}

		html.Append($"<script>document.getElementById('L{line}')")
		    .Append("?.scrollIntoView({ block: 'center' });</script></body></html>");

		return html.ToString();
	}

	/// <summary>
	/// Returns the full path of a C# file inside the repository. Other files are refused.
	/// </summary>
	private string ResolveSourceFile(string relativeFilePath)
	{
		string filePath = Path.GetFullPath(Path.Combine(_repositoryRoot, relativeFilePath));

		if (! filePath.StartsWith(_repositoryRoot + Path.DirectorySeparatorChar,
		                          StringComparison.OrdinalIgnoreCase) ||
		    ! filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
		{
			throw new UnauthorizedAccessException(
				$"Only C# files inside the repository are shown: {relativeFilePath}");
		}

		return filePath;
	}

	private static void Write(HttpListenerResponse response,
	                          int statusCode,
	                          string contentType,
	                          string content)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(content);

		response.StatusCode = statusCode;
		response.ContentType = contentType + "; charset=utf-8";
		response.ContentLength64 = bytes.Length;
		response.OutputStream.Write(bytes);
		response.Close();
	}

	private static void TryWrite(HttpListenerResponse response,
	                             int statusCode,
	                             string contentType,
	                             string content)
	{
		try
		{
			Write(response, statusCode, contentType, content);
		}
		catch (Exception exception)
		{
			// The browser may have closed the connection already
			Console.Error.WriteLine($"The error response could not be sent: {exception.Message}");
		}
	}
}
