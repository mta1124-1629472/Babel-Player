using System.Collections.Generic;

namespace Babel.Player.Models;

public sealed record ExportVideoValidationResult(
    bool CanExport,
    IReadOnlyList<string> Issues);
