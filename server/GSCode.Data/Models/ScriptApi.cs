using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Data.Models;

public class ScriptApiResult
{
    public string Status { get; set; } = default!;
    public ScriptApi Result { get; set; } = default!;
}

public class ScriptApi
{
    public float Version { get; set; }
    public string LanguageId { get; set; } = default!;
    public List<ScrFunction> Library { get; set; } = default!;
}
