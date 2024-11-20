using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.Data
{
    public interface ISenseToken : ISemanticToken, IHoverable 
    {
        public bool IsFromPreprocessor { get; }
    }

    public interface ISemanticToken
    {
        public Range Range { get; }

        public string SemanticTokenType { get; }
        public string[] SemanticTokenModifiers { get; }
    }

    public interface IHoverable
    {
        /// <summary>
        /// The range for this token. The range must not span over multiple lines.
        /// </summary>
        public Range Range { get; }

        public Hover GetHover();

    }
}
