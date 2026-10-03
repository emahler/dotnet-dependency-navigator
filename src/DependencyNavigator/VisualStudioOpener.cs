using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DependencyNavigator;

/// <summary>
/// Opens a source file at a line in a running Visual Studio. Each running Visual Studio
/// registers its automation object in the Windows table of running objects, where it is
/// found by its name.
/// </summary>
public static class VisualStudioOpener
{
	private const string _automationObjectNamePrefix = "!VisualStudio.DTE.";

	// Visual Studio rejects calls while it is busy, for example while a dialog is open.
	private const int _callRejectedByVisualStudio = unchecked((int) 0x80010001);
	private const int _visualStudioBusyRetryLater = unchecked((int) 0x8001010A);

	/// <summary>
	/// Returns false if no Visual Studio is running.
	/// </summary>
	public static bool TryOpen(string filePath, int line)
	{
		List<object> visualStudios = FindRunningVisualStudios();

		if (visualStudios.Count == 0)
		{
			return false;
		}

		// Prefer the Visual Studio whose solution contains the file
		dynamic visualStudio =
			visualStudios.FirstOrDefault(candidate => ContainsFile(candidate, filePath)) ??
			visualStudios.FirstOrDefault(HasVisibleMainWindow) ??
			visualStudios[0];

		RetryWhileBusy(() =>
		{
			visualStudio.ItemOperations.OpenFile(filePath);
			visualStudio.ActiveDocument.Selection.GotoLine(line, true);
			visualStudio.MainWindow.Activate();
		});

		return true;
	}

	private static List<object> FindRunningVisualStudios()
	{
		var result = new List<object>();

		Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out IRunningObjectTable table));
		Marshal.ThrowExceptionForHR(CreateBindCtx(0, out IBindCtx bindContext));

		table.EnumRunning(out IEnumMoniker monikers);

		var moniker = new IMoniker[1];

		while (monikers.Next(1, moniker, IntPtr.Zero) == 0)
		{
			moniker[0].GetDisplayName(bindContext, null, out string name);

			if (name.StartsWith(_automationObjectNamePrefix) &&
			    table.GetObject(moniker[0], out object automationObject) == 0)
			{
				result.Add(automationObject);
			}
		}

		return result;
	}

	private static bool ContainsFile(object automationObject, string filePath)
	{
		dynamic visualStudio = automationObject;
		bool result = false;

		RetryWhileBusy(() => result = visualStudio.Solution.FindProjectItem(filePath) != null);

		return result;
	}

	private static bool HasVisibleMainWindow(object automationObject)
	{
		dynamic visualStudio = automationObject;
		bool result = false;

		RetryWhileBusy(() => result = visualStudio.MainWindow.Visible);

		return result;
	}

	private static void RetryWhileBusy(Action action)
	{
		const int maximumAttempts = 15;

		for (int attempt = 1; attempt <= maximumAttempts; attempt++)
		{
			try
			{
				action();
				return;
			}
			catch (COMException exception) when (
				attempt < maximumAttempts &&
				exception.HResult is _callRejectedByVisualStudio or _visualStudioBusyRetryLater)
			{
				Thread.Sleep(200);
			}
		}
	}

	[DllImport("ole32.dll")]
	private static extern int GetRunningObjectTable(int reserved,
	                                                out IRunningObjectTable runningObjectTable);

	[DllImport("ole32.dll")]
	private static extern int CreateBindCtx(int reserved, out IBindCtx bindContext);
}
