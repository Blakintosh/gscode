using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Data.Models.Interfaces;

public interface IExportedSymbol
{
    public string Name { get; set; }
    public string? Description { get; set; }
    public ExportedSymbolType Type { get; set; }
}

public enum ExportedSymbolType
{
    Function,
    Class
}
