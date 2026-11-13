using System.Collections.Generic;

namespace Zep.Core.Application
{
    // WHY: Unity's DragAndDrop.paths is sometimes empty for drags that
    // originate outside the editor (e.g. macOS Finder), but the original
    // file paths are still on the platform pasteboard under
    // platform-specific keys. The shape of that lookup differs per OS,
    // so the policy code (the import pipeline) talks to this contract
    // and the platform adapter owns the probing. Returning null means
    // "no platform-specific paths recovered" — distinct from an empty
    // list, which would mean "probed and got zero entries".
    public interface IDragSourcePathResolver
    {
        IReadOnlyList<string> TryResolveOriginalPaths();
    }
}
