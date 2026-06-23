// Stub MEF attributes used by ILSpy option-page viewmodels.
// In Roma these attributes are inert — no MEF container processes them.
// They exist only so the linked ext/ilspy source files compile without #if guards.

namespace System.Composition
{
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class NonSharedAttribute : Attribute { }
}
