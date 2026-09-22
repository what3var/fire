using fire.Bytecode;
using fire.Terminal;
using fire.Terminal.Bridge;
using fire.Terminal.Windows;
using fire.Values;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Compiler
{
    public class Linker
    {
        public LinkedProgram CompileAndLink(IReadOnlyList<string> sources, Func<Value[], Value>? debugWriter = null)
        {
            var natives = new NativeRegistry();
            var nativeImports = new HashSet<string>();
            var alreadyIncluded = new HashSet<string>();
            var firstUserSource = 1;

            natives.Register("print", args => Value.MakeUndefined());

            nativeImports.Add(NativeImports.Print);

            var processedSources = new List<ProcessedSource>();
            var inputSources = new List<string>() { fire.Standard.Prelude.Source };

            inputSources.AddRange(sources);

            var registry = new DirectiveRegistry(); // komplett leer, NICHT CreateDefault()
            registry.Register("import", 1, (ctx, args, line) =>
            {
                if (args[0].Kind == ValueKind.String)
                {
                    switch (args[0].AsString().ToLower())
                    {
                        case "graphics":
                            nativeImports.Add(NativeImports.Graphics);
                            firstUserSource = 2;
                            return null;
                        default:
                            throw new Exception($"'{args[0].AsString()}' ist keine bekannte Erweiterung.");
                    }
                }
                throw new Exception($"Falsche Argumente für 'import'-Direktive.");
            });

            foreach (var source in inputSources)
            {
                var processed = Preprocessor.Process(source, Directory.GetCurrentDirectory(), alreadyIncluded, registry);
                processedSources.Add(processed);
            }

            if (nativeImports.Contains(NativeImports.Graphics))
            {
                var processed = Preprocessor.Process(GraphicsBridge.PreludeSource, Directory.GetCurrentDirectory(), alreadyIncluded, registry);
                processedSources.Insert(1, processed);

                GraphicsBridge.RegisterStubs(natives);
            }


            // Kein activeUsings/usingsByStmt mehr nötig (SPEC "Namespaces") -
            // jede Typ-Referenz im AST trägt ihren eigenen Namespace-Kontext
            // direkt an sich selbst (siehe Ast.TypeRef.Namespaces), vom
            // Parser beim Parsen jeder einzelnen ProcessedSource gesetzt.
            var program = Parser.ParseMultiple(processedSources);
            var resolveResult = Resolver.Resolve(program, natives.Names);
            var compiled = Compiler.Compile(program, resolveResult, natives);

            var linkedProgram = new LinkedProgram(compiled, nativeImports, firstUserSource);

            Packer.PackProgram(linkedProgram);

            return linkedProgram;
        }
    }
}
