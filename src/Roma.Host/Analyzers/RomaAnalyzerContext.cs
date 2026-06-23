using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

using ICSharpCode.ILSpy;
using ICSharpCode.ILSpyX;
using ICSharpCode.ILSpyX.Analyzers;

using TomsToolbox.Composition;

namespace Roma.Host.Analyzers;

internal static class RomaAnalyzerContext
{
    public static Language Language { get; set; } = null!;
    public static AssemblyList AssemblyList { get; set; } = null!;
    public static ICollection<IExport<IAnalyzer, IAnalyzerMetadata>> Analyzers { get; set; } = [];

    // Populates the statics that the single export provider (ICSharpCode.ILSpy.App's RomaExportProvider)
    // exposes to the ILSpy analyzer tree: GetExportedValue<AssemblyList>() and
    // GetExports<IAnalyzer, IAnalyzerMetadata>(). Does NOT assign any App.ExportProvider — there is one
    // provider (installed by BuildILSpyTree). (Previously this assigned a second provider, and because
    // the unqualified `App` here binds to Roma.Host.App — not ICSharpCode.ILSpy.App — it set the wrong
    // static, so the analyzer exports never reached the ILSpy infrastructure and the Analyzer pane was
    // empty.)
    public static void Initialize(LanguageService languageService, AssemblyList assemblyList)
    {
        Language = languageService.Language;
        AssemblyList = assemblyList;
        Analyzers = DiscoverAnalyzers();
    }

    static ICollection<IExport<IAnalyzer, IAnalyzerMetadata>> DiscoverAnalyzers()
        => typeof(IAnalyzer).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract
                && typeof(IAnalyzer).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => {
                var attr = t.GetCustomAttribute<ExportAnalyzerAttribute>();
                if (attr is null || Activator.CreateInstance(t) is not IAnalyzer instance)
                    return null;
                return (IExport<IAnalyzer, IAnalyzerMetadata>)
                    new SimpleExport<IAnalyzer, IAnalyzerMetadata>(instance, attr);
            })
            .Where(x => x is not null)
            .OrderBy(x => x!.Metadata?.Order)
            .ToArray()!;
}

internal sealed record SimpleExport<TValue, TMetadata>(TValue Value, TMetadata Metadata)
    : IExport<TValue, TMetadata>
    where TValue : class
    where TMetadata : class;
