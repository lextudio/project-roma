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
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

// Roma override of ILSpy's UpdateService. Lives in Roma.Host (same namespace/assembly) so the ILSpy
// submodule stays unmodified; the linked ILSpy UpdateService.cs is excluded in Roma.Host.csproj.
// Checks Roma's own releases on GitHub (lextudio/project-roma) instead of ILSpy's updates.xml feed,
// via the GitHub REST "latest release" endpoint + System.Text.Json (no Octokit dependency).
// Adapted from ProjectRover's GitHub-based UpdateService.
namespace ICSharpCode.ILSpy.Updates
{
	internal static class UpdateService
	{
		const string LatestReleaseApiUrl = "https://api.github.com/repos/lextudio/project-roma/releases/latest";
		const string ReleasesPageUrl = "https://github.com/lextudio/project-roma/releases";

		public static AvailableVersionInfo LatestAvailableVersion { get; private set; }

		public static async Task<AvailableVersionInfo> GetLatestVersionAsync()
		{
			using var client = new HttpClient();
			// GitHub requires a User-Agent; ask for the v3 JSON media type.
			client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ProjectRoma", "1.0"));
			client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

			var json = await client.GetStringAsync(LatestReleaseApiUrl).ConfigureAwait(false);
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;

			var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
			var htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;
			var draft = root.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean();
			var prerelease = root.TryGetProperty("prerelease", out var preEl) && preEl.GetBoolean();

			var version = (draft || prerelease) ? null : ParseTag(tag);

			LatestAvailableVersion = new AvailableVersionInfo {
				Version = version ?? AppUpdateService.CurrentVersion,
				DownloadUrl = version is null ? null : (IsHttpUrl(htmlUrl) ? htmlUrl : ReleasesPageUrl)
			};
			return LatestAvailableVersion;
		}

		// Parse a release tag (e.g. "v1.2.3", "1.2.3", "1.2.3-beta") to a normalized
		// Major.Minor.Build.0 version, or null if it isn't a version tag.
		static Version ParseTag(string tag)
		{
			if (string.IsNullOrWhiteSpace(tag))
				return null;

			var s = tag.Trim();
			if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
				s = s.Substring(1);

			var suffix = s.IndexOfAny(new[] { '-', '+' });
			if (suffix >= 0)
				s = s.Substring(0, suffix);

			return Version.TryParse(s, out var v)
				? new Version(v.Major, v.Minor < 0 ? 0 : v.Minor, v.Build < 0 ? 0 : v.Build, 0)
				: null;
		}

		static bool IsHttpUrl(string url)
			=> !string.IsNullOrWhiteSpace(url)
				&& (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
					|| url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

		/// <summary>
		/// If automatic update checking is enabled, checks if there are any updates available.
		/// Returns the download URL if an update is available; null otherwise / if no check ran.
		/// </summary>
		public static async Task<string> CheckForUpdatesIfEnabledAsync(UpdateSettings settings)
		{
			if (!settings.AutomaticUpdateCheckEnabled)
				return null;

			// perform update check if we never did one before, or it's been > 7 days
			if (settings.LastSuccessfulUpdateCheck == null
				|| settings.LastSuccessfulUpdateCheck < DateTime.UtcNow.AddDays(-7)
				|| settings.LastSuccessfulUpdateCheck > DateTime.UtcNow)
			{
				return await CheckForUpdateInternal(settings).ConfigureAwait(false);
			}

			return null;
		}

		public static Task<string> CheckForUpdatesAsync(UpdateSettings settings)
		{
			return CheckForUpdateInternal(settings);
		}

		static async Task<string> CheckForUpdateInternal(UpdateSettings settings)
		{
			try
			{
				var v = await GetLatestVersionAsync().ConfigureAwait(false);
				settings.LastSuccessfulUpdateCheck = DateTime.UtcNow;
				return v.Version > AppUpdateService.CurrentVersion ? v.DownloadUrl : null;
			}
			catch (Exception)
			{
				// ignore errors getting the version info
				return null;
			}
		}
	}
}
