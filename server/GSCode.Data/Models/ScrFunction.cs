using GSCode.Data.Models.Interfaces;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Data.Models;

public record class ScrFunction : IExportedSymbol
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; } = default!;
    public List<ScrFunctionArg>? Args { get; set; } = default!;
    public ScrFunctionArg? CalledOn { get; set; }
    public ScrFunctionArg? Returns { get; set; }
    public List<string> Tags { get; set; } = default!;
    public string? Namespace { get; set; }
    public string? IntelliSense { get; set; }
}

public record class ScrFunctionArg
{   
    public string Name { get; set; } = default!;

    public string? Description { get; set; } = default!;

    public string? Type { get; set; } = default!;

    public bool? Required { get; set; }

    public ScriptValue? Default { get; set; }
}
