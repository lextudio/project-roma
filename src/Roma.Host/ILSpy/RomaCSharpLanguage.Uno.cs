using System;

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Metadata;

namespace ICSharpCode.ILSpy;

public partial class CSharpLanguage
{
    void AddWarningMessage(
        MetadataFile module,
        ITextOutput output,
        string line1,
        string line2 = null,
        string buttonText = null,
        object buttonImage = null,
        Action buttonClickHandler = null)
    {
        WriteCommentLine(output, line1);
        if (!string.IsNullOrEmpty(line2))
        {
            WriteCommentLine(output, line2);
        }
    }
}
