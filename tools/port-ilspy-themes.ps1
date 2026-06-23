<#
.SYNOPSIS
  Regenerates Roma's editor theme dictionaries (src/Roma.Host/Themes/Theme.*.uno.xaml) from
  ILSpy's upstream WPF theme dictionaries (ext/ilspy/ILSpy/Themes/Theme.*.xaml).

.WHY
  ILSpy's WPF themes express the editor surface color via WPF idioms that don't exist in
  Uno/WinUI XAML:
    * SystemColors DynamicResource  (e.g. {DynamicResource {x:Static SystemColors.InfoColorKey}})
    * ComponentResourceKey x:Keys   (e.g. {x:Static themes:ResourceKeys.TextBackgroundBrush})
  Hand-porting forces a human to "resolve" these to literals, and a wrong guess silently drops a
  color — that is exactly how the Light theme's signature pale-yellow editor background
  (SystemColors.InfoColor = #FFFFE1) was lost and flattened to #FFFFFF.

  This converter resolves them deterministically:
    * SystemColors.<Name>ColorKey  -> literal hex from the table below
    * ResourceKeys.<Name>Brush     -> string key  "ILSpy.<Name>"   (Brush suffix dropped)
    * themes:SyntaxColor entries   -> copied verbatim
    * Pen entries / MergedDictionaries / Base.* -> skipped (Roma's editor consumes neither)

  Roma's ThemeManager keys dark/light detection off "ILSpy.TextBackground", and the editor
  surface color is sourced from that same key (MainPage.Theming.ApplyTheme), so the brush keys
  must keep the "ILSpy.<Name>" shape.

.NOTES
  One-shot/full-overwrite generator. Re-run after pulling new ILSpy theme changes. If a new
  SystemColors key appears that is not in $SystemColors, the script FAILS LOUDLY rather than
  emitting a wrong/placeholder color — add the mapping and re-run.
#>

[CmdletBinding()]
param(
    [string] $SourceDir = "$PSScriptRoot/../ext/ilspy/ILSpy/Themes",
    [string] $TargetDir = "$PSScriptRoot/../src/Roma.Host/Themes"
)

$ErrorActionPreference = 'Stop'

# WPF SystemColors -> literal RGB, using the classic Windows palette ILSpy's themes assume.
# Keyed WITHOUT the trailing "Key"/"Color" qualifiers stripped below (i.e. "InfoColor").
$SystemColors = @{
    'InfoColor'             = '#FFFFE1'   # tooltip background — the Light theme's editor yellow
    'InfoTextColor'         = '#000000'
    'WindowColor'           = '#FFFFFF'
    'WindowTextColor'       = '#000000'
    'ControlColor'          = '#F0F0F0'
    'ControlTextColor'      = '#000000'
    'ControlLightColor'     = '#E3E3E3'
    'ControlLightLightColor'= '#FFFFFF'
    'ControlDarkColor'      = '#A0A0A0'
    'ControlDarkDarkColor'  = '#696969'
    'GrayTextColor'         = '#6D6D6D'
    'HighlightColor'        = '#3399FF'
    'HighlightTextColor'    = '#FFFFFF'
    'MenuColor'             = '#F0F0F0'
    'MenuTextColor'         = '#000000'
    'MenuBarColor'          = '#F0F0F0'
}

$XmlNs = 'http://schemas.microsoft.com/winfx/2006/xaml'

$Themes = 'Light','Dark','RSharpLight','RSharpDark','VSCodeLightPlus','VSCodeDarkPlus'

function Resolve-Color([string] $raw, [string] $themeFile) {
    $raw = $raw.Trim()
    if ($raw -match 'SystemColors\.(\w+?)Key') {
        $name = $Matches[1]            # e.g. "InfoColor" (from "InfoColorKey")
        if ($SystemColors.ContainsKey($name)) { return $SystemColors[$name] }
        throw "Unmapped SystemColors key '$name' in $themeFile. Add it to `$SystemColors and re-run."
    }
    if ($raw -match '^\{.*\}$') {
        throw "Unhandled markup-extension color '$raw' in $themeFile."
    }
    return $raw                        # literal hex or named WPF color (valid in Uno XAML)
}

function Convert-Theme([string] $theme) {
    $srcPath = Join-Path $SourceDir "Theme.$theme.xaml"
    $dstPath = Join-Path $TargetDir "Theme.$theme.uno.xaml"
    [xml] $doc = Get-Content $srcPath -Raw
    $root = $doc.DocumentElement

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"')
    [void]$sb.AppendLine('                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"')
    [void]$sb.AppendLine('                    xmlns:themes="using:ICSharpCode.ILSpy.Themes">')
    [void]$sb.AppendLine("  <!-- AUTO-GENERATED from ext/ilspy/ILSpy/Themes/Theme.$theme.xaml by tools/port-ilspy-themes.ps1.")
    [void]$sb.AppendLine('       Do not edit by hand; re-run the script after changing the upstream ILSpy theme. -->')

    foreach ($node in $root.ChildNodes) {
        switch ($node.NodeType) {
            'Comment' { [void]$sb.AppendLine("  <!--$($node.Value)-->"); continue }
            'Element' {
                switch ($node.LocalName) {
                    'ResourceDictionary.MergedDictionaries' { } # Base.* chrome — not editor colors
                    'Pen' { }                                   # no Uno equivalent; editor doesn't use it
                    'SolidColorBrush' {
                        $key = $node.GetAttribute('Key', $XmlNs)
                        if ($key -notmatch 'ResourceKeys\.(\w+)') {
                            throw "Unexpected SolidColorBrush x:Key '$key' in Theme.$theme.xaml"
                        }
                        $name = $Matches[1] -replace 'Brush$', ''     # TextBackgroundBrush -> TextBackground
                        $rawColor = $node.GetAttribute('Color')
                        if ([string]::IsNullOrWhiteSpace($rawColor)) {
                            # Upstream leaves the brush color unset (transparent default), e.g. the
                            # VS Code themes' CurrentLineBackgroundBrush. Emit a color-less brush.
                            [void]$sb.AppendLine("  <SolidColorBrush x:Key=`"ILSpy.$name`" />")
                        } else {
                            $color = Resolve-Color $rawColor "Theme.$theme.xaml"
                            [void]$sb.AppendLine("  <SolidColorBrush x:Key=`"ILSpy.$name`" Color=`"$color`" />")
                        }
                    }
                    'Color' {
                        $key = $node.GetAttribute('Key', $XmlNs)
                        if ($key -notmatch 'ResourceKeys\.(\w+)') {
                            throw "Unexpected Color x:Key '$key' in Theme.$theme.xaml"
                        }
                        $name = $Matches[1] -replace 'Color$', ''     # TextMarkerBackgroundColor -> TextMarkerBackground
                        $value = Resolve-Color $node.InnerText "Theme.$theme.xaml"
                        [void]$sb.AppendLine("  <Color x:Key=`"ILSpy.$name`">$value</Color>")
                    }
                    'SyntaxColor' {
                        $key = $node.GetAttribute('Key', $XmlNs)
                        $attrs = "x:Key=`"$key`""
                        foreach ($a in 'Foreground','Background','FontWeight','FontStyle') {
                            $v = $node.GetAttribute($a)
                            if ($v) { $attrs += " $a=`"$v`"" }
                        }
                        [void]$sb.AppendLine("  <themes:SyntaxColor $attrs />")
                    }
                    default { throw "Unhandled element '<$($node.LocalName)>' in Theme.$theme.xaml" }
                }
            }
        }
    }

    [void]$sb.AppendLine('</ResourceDictionary>')
    Set-Content -Path $dstPath -Value $sb.ToString().TrimEnd() -Encoding UTF8
    Write-Host "wrote $dstPath"
}

foreach ($t in $Themes) { Convert-Theme $t }
Write-Host "Done. Regenerated $($Themes.Count) theme dictionaries."
