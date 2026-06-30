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
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

using TomsToolbox.Composition;

// Roma's cross-platform "Open command line here" — a partial of ILSpy's GlobalUtils that replaces the
// Windows-only OpenTerminalAt (cmd.exe) from the ILSpy submodule's GlobalUtils.wpf.cs (which Roma.Host
// no longer compiles). Kept in Roma.Host so the ILSpy submodule stays unmodified. The preferred
// terminal is read from RomaHostSettings (empty = per-OS default). Adapted from ProjectRover.
namespace ICSharpCode.ILSpy.Util
{
	static partial class GlobalUtils
	{
		public static void OpenTerminalAt(string path)
		{
			try
			{
				if (string.IsNullOrEmpty(path))
					return;
				path = Path.GetFullPath(path);

				var (preferred, custom) = ResolveTerminalPreference();

				if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				{
					switch (preferred)
					{
						case "PowerShell":
							Process.Start(new ProcessStartInfo { FileName = "powershell.exe", Arguments = $"-NoExit -Command Set-Location -LiteralPath \"{path}\"", UseShellExecute = false });
							break;
						case "PowerShell Core":
							Process.Start(new ProcessStartInfo { FileName = "pwsh.exe", Arguments = $"-NoExit -Command Set-Location -LiteralPath \"{path}\"", UseShellExecute = false });
							break;
						case "Windows Terminal":
							Process.Start(new ProcessStartInfo { FileName = "wt.exe", Arguments = $"-d \"{path}\"", UseShellExecute = false });
							break;
						case "Custom" when !string.IsNullOrWhiteSpace(custom):
							Process.Start(new ProcessStartInfo { FileName = custom, Arguments = path, UseShellExecute = false });
							break;
						default:
							ExecuteCommand("cmd.exe", $"/k \"cd /d {path}\"");
							break;
					}
					return;
				}

				if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				{
					switch (preferred)
					{
						case "System Default":
							{
								var openInfo = new ProcessStartInfo { FileName = "open", UseShellExecute = false };
								openInfo.ArgumentList.Add("-a");
								openInfo.ArgumentList.Add("Terminal");
								openInfo.ArgumentList.Add(path);
								Process.Start(openInfo);
								break;
							}
						case "iTerm2":
							RunAppleScript(ItermScript, path);
							break;
						case "Custom" when !string.IsNullOrWhiteSpace(custom):
							Process.Start(new ProcessStartInfo { FileName = custom, Arguments = $"-c \"cd '{path}'; exec $SHELL\"", UseShellExecute = false });
							break;
						default:
							// "Terminal.app" or unset: launch Terminal.app at the folder.
							RunAppleScript(TerminalScript, path);
							break;
					}
					return;
				}

				switch (preferred)
				{
					case "GNOME Terminal":
						Process.Start(new ProcessStartInfo { FileName = "gnome-terminal", Arguments = $"--working-directory={path}", UseShellExecute = false });
						break;
					case "Konsole":
						Process.Start(new ProcessStartInfo { FileName = "konsole", Arguments = $"--workdir {path}", UseShellExecute = false });
						break;
					case "Xfce Terminal":
						Process.Start(new ProcessStartInfo { FileName = "xfce4-terminal", Arguments = $"--working-directory={path}", UseShellExecute = false });
						break;
					case "XTerm":
						Process.Start(new ProcessStartInfo { FileName = "xterm", Arguments = $"-e bash -lc \"cd '{path}'; exec bash\"", UseShellExecute = false });
						break;
					case "Custom" when !string.IsNullOrWhiteSpace(custom):
						Process.Start(new ProcessStartInfo { FileName = custom, Arguments = path, UseShellExecute = false });
						break;
					default:
						TryLaunchLinuxTerminal(path);
						break;
				}
			}
			catch
			{
				// Process.Start can throw various (often undocumented) errors; ignore.
			}
		}

		// Reads the user's terminal preference from RomaHostSettings (best-effort; returns
		// empty for the per-OS default when settings aren't available).
		static (string preferred, string custom) ResolveTerminalPreference()
		{
			try
			{
				var exportProvider = ICSharpCode.ILSpy.App.ExportProvider;
				var settingsService = exportProvider?.GetExportedValueOrDefault<SettingsService>();
				var settings = settingsService?.GetSettings<global::Roma.Host.RomaHostSettings>();
				if (settings is not null)
					return (settings.PreferredTerminalApp ?? string.Empty, settings.CustomTerminalPath ?? string.Empty);
			}
			catch
			{
				// settings not available yet (early init / headless) — fall back to OS default
			}

			return (string.Empty, string.Empty);
		}

		static void TryLaunchLinuxTerminal(string path)
		{
			var candidates = new[]
			{
				new[] { "gnome-terminal", $"--working-directory={path}" },
				new[] { "konsole", $"--workdir {path}" },
				new[] { "xfce4-terminal", $"--working-directory={path}" },
				new[] { "x-terminal-emulator", $"-e bash -lc \"cd '{path}'; exec bash\"" },
				new[] { "xterm", $"-e bash -lc \"cd '{path}'; exec bash\"" },
			};

			foreach (var candidate in candidates)
			{
				try
				{
					Process.Start(new ProcessStartInfo { FileName = candidate[0], Arguments = candidate[1], UseShellExecute = false });
					return;
				}
				catch
				{
					// try the next emulator
				}
			}
		}

		static void RunAppleScript(string script, params string[] args)
		{
			var psi = new ProcessStartInfo { FileName = "osascript", UseShellExecute = false };
			psi.ArgumentList.Add("-e");
			psi.ArgumentList.Add(script);
			if (args.Length > 0)
			{
				psi.ArgumentList.Add("--");
				foreach (var arg in args)
					psi.ArgumentList.Add(arg);
			}

			Process.Start(psi);
		}

		const string TerminalScript = "on run argv\n"
			+ "set targetPath to item 1 of argv\n"
			+ "tell application \"Terminal\"\n"
			+ "do script \"cd \" & quoted form of targetPath & \"; clear\"\n"
			+ "activate\n"
			+ "end tell\n"
			+ "end run";

		const string ItermScript = "on run argv\n"
			+ "set targetPath to item 1 of argv\n"
			+ "tell application \"iTerm2\"\n"
			+ "create window with default profile\n"
			+ "tell current session of current window\n"
			+ "write text \"cd \" & quoted form of targetPath & \"; clear\"\n"
			+ "end tell\n"
			+ "end tell\n"
			+ "end run";
	}
}
