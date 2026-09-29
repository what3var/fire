using fire.Ast;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Parsing
{
    public class StatementBlock
    {
        public StatementBlock(string source, List<Stmt>? statements, IReadOnlyList<string>? usings)
        {
            Source = source;
            Statements = statements;
            Usings = usings;
        }

        public string Source { get; set; }

        public List<Stmt>? Statements { get; set; }

        public IReadOnlyList<string>? Usings { get; set; }
    }
}
