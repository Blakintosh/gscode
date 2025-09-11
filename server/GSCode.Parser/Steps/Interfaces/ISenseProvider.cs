using GSCode.Parser.Data;

namespace GSCode.Parser.Steps.Interfaces
{
    internal interface ISenseProvider
    {
        /// <summary>
        /// A container that stores all the IntelliSense data for the given script. This is provided from
        /// the parser in the constructor, there is one instance per parser.
        /// </summary>
        public ParserIntelliSense Sense { get; }
    }
}
