using GSCode.Parser.SPA.Logic.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser;

public interface IScriptParser
{
    public DefinitionsTable? DefinitionsTable { get; protected set; }

    public Task ParseAsync();
}