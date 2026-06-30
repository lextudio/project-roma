// Copyright (c) 2025-2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;

// Roma override of ILSpy's AppUpdateService. Lives in Roma.Host (same namespace/assembly) so the
// ILSpy submodule stays unmodified; this is excluded from the linked ILSpy compile in Roma.Host.csproj.
// Unlike ILSpy (which reports the engine version), Roma reports its OWN app version so update checks
// compare against project-roma releases.
namespace ICSharpCode.ILSpy.Updates
{
	internal enum UpdateStrategy
	{
		NotifyOfUpdates,
		// AutoUpdate
	}

	internal static class AppUpdateService
	{
		public static readonly UpdateStrategy updateStrategy = UpdateStrategy.NotifyOfUpdates;

		// This assembly is Roma.Host (versioned by GitVersion); normalize to Major.Minor.Build.0
		// so it compares cleanly with versions parsed from release tags (e.g. "v1.2.3").
		public static readonly Version CurrentVersion = ResolveCurrentVersion();

		static Version ResolveCurrentVersion()
		{
			var v = typeof(AppUpdateService).Assembly.GetName().Version;
			return v is null
				? new Version(0, 0, 0, 0)
				: new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), 0);
		}
	}
}
