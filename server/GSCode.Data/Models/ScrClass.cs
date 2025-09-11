using GSCode.Data.Models.Interfaces;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Data.Models;

public record class ScrClass : IExportedSymbol
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; } = default!;
    public string? InheritsFrom { get; set; } = default!;

    // TODO: need to account for that members are only scoped to functions that occur after their definition.
    public List<ScrFunction> Methods { get; set; } = [];
    public List<ScrMember> Members { get; set; } = [];
}

public record class ScrMember
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; } = default!;
}