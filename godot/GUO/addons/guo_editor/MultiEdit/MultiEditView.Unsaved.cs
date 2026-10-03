#if TOOLS
namespace GUO.Editor;

using System;

public partial class MultiEditView
{
    /// <summary>Runs <paramref name="proceed"/> now, or after the user decides what to do with unsaved changes.</summary>
    internal void GuardUnsaved(Action proceed) => proceed();
}
#endif
