using BenchmarkDotNet.Attributes;
using GSCode.Lexer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.CLI
{
    public class Benchmarks
    {
        private static string documnentText;

        [Benchmark]
        public void SceneShared()
        {
            if(string.IsNullOrEmpty(documnentText))
            {
                documnentText = File.ReadAllText(@"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Black Ops III\share\raw\scripts\shared\scene_shared.gsc");
            }
            ScriptLexer.TokenizeScriptContent(documnentText, null, out _);
        }
    }
}
