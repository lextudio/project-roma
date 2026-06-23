// Roma icon catalogue for ICSharpCode.ILSpy.Images.
//
// The real Images.cs loads icons via the WPF DrawingGroup/pack:// pipeline. Roma instead
// loads the ILSpy SVGs shipped under ILSpyIcons/ (Content, copied to output) directly as
// WinUI SvgImageSource handles — the global ImageSource alias points at the WinUI type, so
// these render anywhere a linked node binds its Icon. Member names mirror upstream Images.*
// so linked tree nodes resolve unchanged; Roma's own icon code (RomaTreeIconProvider,
// RomaSearchResultFactory) sources its glyphs from here too instead of duplicating URIs.
//
// ILSpy composes a base glyph with an access/static/extension overlay into a single image
// via DrawingGroup. WinUI can't composite SvgImageSource, so Roma renders the overlay as a
// second layer (see RomaTreeIconProvider); GetIcon(...) here returns the base glyph only and
// OverlayFor(...) exposes the overlay separately.

using System;

using ICSharpCode.Decompiler.TypeSystem;

using Microsoft.UI.Xaml.Media.Imaging;

namespace ICSharpCode.ILSpy
{
    internal static class Images
    {
        // When true, icons are dark-adapted on the fly. There are no dark SVG assets — the light SVG
        // is the only source. ILSpy recolors its WPF vector (DrawingGroup) icons at runtime; Uno's
        // SvgImageSource can't load data: URIs or in-memory streams, but it loads ms-appdata URIs the
        // same way as ms-appx. So on first use we recolor the light SVG (ILSpy's ThemeManager
        // dark-adaptation math, see RomaDarkSvg) and write it to the app's LocalFolder, then load it
        // via ms-appdata. The members below are properties so they re-resolve on theme change.
        internal static bool IsDark;

        private const string DarkFolder = "roma-dark-icons";
        private static readonly System.Collections.Generic.Dictionary<string, ImageSource> _cache = new();

        // Load an SVG (theme-aware) as a renderable image handle.
        private static ImageSource Svg(string file)
        {
            var key = (IsDark ? "dark:" : "") + file;
            if (_cache.TryGetValue(key, out var img))
                return img;

            var uri = (IsDark ? EnsureDarkVariant(file) : null)
                ?? LightSvgUri(file);
            img = new SvgImageSource(uri);
            _cache[key] = img;
            return img;
        }

        // Construct a URI for the light SVG using the real filesystem path.
        // ms-appx:/// resolution on macOS .app bundles may use Contents/Resources/
        // (NSBundle convention) rather than Contents/MacOS/ where the SVGs live.
        private static Uri LightSvgUri(string file) => Roma.Host.ILSpyIconHelper.GetUri(file);

        // Recolor the light SVG and cache it in LocalFolder/roma-dark-icons/, returning an ms-appdata
        // URI for it. Returns null (caller falls back to the light icon) if anything fails.
        private static Uri? EnsureDarkVariant(string file)
        {
            try
            {
                var localRoot = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                var darkDir = System.IO.Path.Combine(localRoot, DarkFolder);
                var darkPath = System.IO.Path.Combine(darkDir, file);

                if (!System.IO.File.Exists(darkPath))
                {
                    var lightPath = System.IO.Path.Combine(AppContext.BaseDirectory, "ILSpyIcons", file);
                    if (!System.IO.File.Exists(lightPath))
                        return null;
                    System.IO.Directory.CreateDirectory(darkDir);
                    System.IO.File.WriteAllText(darkPath, Roma.Host.RomaDarkSvg.Recolor(System.IO.File.ReadAllText(lightPath)));
                }

                return new Uri($"ms-appdata:///local/{DarkFolder}/{file}");
            }
            catch
            {
                return null;
            }
        }

        // No glyph (no SVG shipped yet for this icon); binding to null renders nothing.
        private static readonly ImageSource None = null!;

        // The DrawingGroup loader has no SVG equivalent; callers tolerate a null result.
        public static ImageSource Load(string icon) => None;

        public static ImageSource Load(object? part, string icon) => None;

        // ── Commands / toolbar ───────────────────────────────────────
        public static ImageSource ViewCode = None;            // no SVG shipped
        public static ImageSource Save => Svg("save.svg");
        public static ImageSource OK = None;                  // no SVG shipped
        public static ImageSource Delete = None;              // no SVG shipped
        public static ImageSource Search => Svg("search.svg");

        // ── Assemblies / references ─────────────────────────────────
        public static ImageSource Assembly => Svg("assembly.svg");
        public static ImageSource AssemblyWarning => Svg("assembly_warning.svg");
        public static ImageSource AssemblyLoading => Svg("assembly_loading.svg");
        public static ImageSource FindAssembly => Svg("assembly_list.svg");
        public static ImageSource Library => Svg("library.svg");
        public static ImageSource Namespace => Svg("namespace.svg");
        public static ImageSource ReferenceFolder => Svg("reference_folder.svg");
        public static ImageSource NuGet = None;               // no SVG shipped
        public static ImageSource MetadataFile => Svg("metadata.svg");
        public static ImageSource WebAssemblyFile = None;     // no SVG shipped
        public static ImageSource ProgramDebugDatabase = None; // no SVG shipped

        // ── Metadata view ───────────────────────────────────────────
        public static ImageSource Metadata => Svg("metadata.svg");
        public static ImageSource Heap => Svg("Heap.svg");
        public static ImageSource Header => Svg("Header.svg");
        public static ImageSource MetadataTable => Svg("MetadataTable.svg");
        public static ImageSource MetadataTableGroup => Svg("MetadataTableGroup.svg");
        public static ImageSource ListFolder => Svg("ListFolder.svg");
        public static ImageSource ListFolderOpen => Svg("ListFolder.Open.svg");

        // ── Type hierarchy / folders ────────────────────────────────
        public static ImageSource SubTypes => Svg("sub_types.svg");
        public static ImageSource SuperTypes => Svg("super_types.svg");
        public static ImageSource FolderOpen => Svg("FolderOpen.svg");
        public static ImageSource FolderClosed => Svg("Folder.Closed.svg");

        // ── Resources ───────────────────────────────────────────────
        public static ImageSource Resource => Svg("resource.svg");
        public static ImageSource ResourceImage => Svg("ResourceImage.svg");
        public static ImageSource ResourceResourcesFile => Svg("ResourceResourcesFile.svg");
        public static ImageSource ResourceXml => Svg("ResourceXml.svg");
        public static ImageSource ResourceXsd => Svg("ResourceXsd.svg");
        public static ImageSource ResourceXslt => Svg("ResourceXslt.svg");

        // ── Types ───────────────────────────────────────────────────
        public static ImageSource Class => Svg("class.svg");
        public static ImageSource Struct => Svg("struct.svg");
        public static ImageSource Interface => Svg("interface.svg");
        public static ImageSource Delegate => Svg("delegate.svg");
        public static ImageSource Enum => Svg("enum.svg");
        public static ImageSource Type => Svg("show_public_only.svg");

        // ── Fields ──────────────────────────────────────────────────
        public static ImageSource Field => Svg("field.svg");
        public static ImageSource FieldReadOnly => Svg("field_read_only.svg");
        public static ImageSource Literal => Svg("literal.svg");
        public static ImageSource EnumValue => Svg("enum_value.svg");

        // ── Methods ─────────────────────────────────────────────────
        public static ImageSource Method => Svg("method.svg");
        public static ImageSource Constructor => Svg("constructor.svg");
        public static ImageSource VirtualMethod => Svg("virtual_method.svg");
        public static ImageSource Operator => Svg("operator.svg");
        public static ImageSource ExtensionMethod => Svg("extension_method.svg");
        public static ImageSource PInvokeMethod => Svg("pinvoke_method.svg");

        // ── Properties / events ─────────────────────────────────────
        public static ImageSource Property => Svg("property.svg");
        public static ImageSource Indexer => Svg("indexer.svg");
        public static ImageSource Event => Svg("event.svg");

        // ── Overlays (rendered as a separate layer over the base glyph) ──
        public static ImageSource OverlayProtected => Svg("overlay_protected.svg");
        public static ImageSource OverlayInternal => Svg("overlay_internal.svg");
        public static ImageSource OverlayProtectedInternal => Svg("overlay_protected_internal.svg");
        public static ImageSource OverlayPrivate => Svg("overlay_private.svg");
        public static ImageSource OverlayPrivateProtected => Svg("overlay_private_protected.svg");
        public static ImageSource OverlayCompilerControlled => Svg("overlay_compiler_controlled.svg");
        public static ImageSource OverlayReference => Svg("reference_overlay.svg");
        public static ImageSource OverlayStatic => Svg("overlay_static.svg");
        public static ImageSource OverlayExtension => Svg("overlay_extension.svg");

        // ── Reference glyphs (base only; ILSpy overlays ReferenceOverlay onto these) ──
        public static ImageSource TypeReference => Svg("show_public_only.svg");
        public static ImageSource MethodReference => Svg("method.svg");
        public static ImageSource FieldReference => Svg("field.svg");
        public static ImageSource ExportedType => Svg("show_public_only.svg");

        // GetIcon: ILSpy composites base + access/static/extension overlay into one image.
        // Roma layers the overlay separately (RomaTreeIconProvider), so the catalogue returns
        // the base glyph here; use BaseFor/OverlayFor to obtain the pieces explicitly.
        public static ImageSource GetIcon(TypeIcon icon, AccessOverlayIcon overlay, bool isStatic = false, bool isExtension = false) => BaseFor(icon);

        public static ImageSource GetIcon(MemberIcon icon, AccessOverlayIcon overlay, bool isStatic, bool isExtension) => BaseFor(icon);

        public static ImageSource BaseFor(TypeIcon icon) => icon switch
        {
            TypeIcon.Class     => Class,
            TypeIcon.Enum      => Enum,
            TypeIcon.Struct    => Struct,
            TypeIcon.Interface => Interface,
            TypeIcon.Delegate  => Delegate,
            _                  => Class,
        };

        public static ImageSource BaseFor(MemberIcon icon) => icon switch
        {
            MemberIcon.Field         => Field,
            MemberIcon.FieldReadOnly => FieldReadOnly,
            MemberIcon.Literal       => Literal,
            MemberIcon.EnumValue     => EnumValue,
            MemberIcon.Property      => Property,
            MemberIcon.Indexer       => Indexer,
            MemberIcon.Method        => Method,
            MemberIcon.Constructor   => Constructor,
            MemberIcon.VirtualMethod => VirtualMethod,
            MemberIcon.Operator      => Operator,
            MemberIcon.PInvokeMethod => PInvokeMethod,
            MemberIcon.Event         => Event,
            _                        => Method,
        };

        // The access-modifier overlay for the given accessibility (null = public/none).
        public static ImageSource? OverlayFor(AccessOverlayIcon overlay) => overlay switch
        {
            AccessOverlayIcon.Public             => null,
            AccessOverlayIcon.Protected          => OverlayProtected,
            AccessOverlayIcon.Internal           => OverlayInternal,
            AccessOverlayIcon.ProtectedInternal  => OverlayProtectedInternal,
            AccessOverlayIcon.Private            => OverlayPrivate,
            AccessOverlayIcon.PrivateProtected   => OverlayPrivateProtected,
            AccessOverlayIcon.CompilerControlled => OverlayCompilerControlled,
            _                                    => null,
        };

        public static AccessOverlayIcon GetOverlayIcon(Accessibility accessibility) => accessibility switch
        {
            Accessibility.Public => AccessOverlayIcon.Public,
            Accessibility.Internal => AccessOverlayIcon.Internal,
            Accessibility.ProtectedAndInternal => AccessOverlayIcon.PrivateProtected,
            Accessibility.Protected => AccessOverlayIcon.Protected,
            Accessibility.ProtectedOrInternal => AccessOverlayIcon.ProtectedInternal,
            Accessibility.Private => AccessOverlayIcon.Private,
            _ => AccessOverlayIcon.CompilerControlled,
        };
    }
}
