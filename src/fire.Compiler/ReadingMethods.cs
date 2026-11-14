using System.Collections.Generic;
using fire.Ast;

namespace fire.Compiler
{
    /// <summary>A method whose own body only reads (FunctionProto.ReadsOnly, docs/spec/THREADING_DESIGN.md section 7): called by a fire thread on an
    /// object of the main program's globals it runs on the thread, like reads of fields there - not as a job of the main program (rule 10), which would
    /// wait for its next safe point. What the body itself does is enough: every change of the shared domain on the way (a method that writes, called
    /// from it; a field of an object it reaches) is a section or a job of its own again. Its body assigns only its own locals (not a `ref`
    /// parameter), takes nothing (`take`, `&amp;`), deletes nothing, and has no thread statements (sync, sections, `fire`, `process`, probes).</summary>
    internal static class ReadingMethods
    {
        public static bool Of(IReadOnlyList<LambdaParam> parms, Stmt.BlockStmt body)
        {
            var all = new List<object>();
            AstNames.Walk(body, all);
            var locals = AstNames.Declared(body);
            foreach (var p in parms) if (!p.ByRef) locals.Add(p.Name);
            bool Local(Expr target) => target is IdentifierExpr id && locals.Contains(id.Name);
            foreach (var n in all)
                switch (n)
                {
                    case AssignExpr a when !Local(a.Target): return false;
                    case IncDecExpr i when !Local(i.Target): return false;
                    case UnaryExpr { Op: UnaryOp.Take or UnaryOp.TryTake or UnaryOp.AddressOf }: return false;
                    case DeleteStmt or ProbeExpr or SyncExpr or SyncGlobalsExpr or SectionEnterStmt or SectionExitStmt or PostGlobalStmt or FireStmt
                        or ProcessStmt or TryProcessExpr or AwaitOnExpr or LeaveStmt or TerminateStmt or UnsafeStmt:
                        return false;
                }
            return true;
        }
    }
}
