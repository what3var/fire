/* native/forge/gen/cppgenerator6.c - src/fire.Native/CppGenerator.cs (commit 808e9788), part 6: StepCore, the rest of its switch (arithmetic,
 * comparisons, jumps, scopes, calls, objects, lambdas, arrays, ownership, exceptions, threads). An instruction that is not ported yet refuses. */

static FgStep StepCore2(FgSC* x) {
    switch (ins->op) {
        default:
            FG_UNPORTED("the instruction %s", fgOpName(ins->op));
    }
}

#undef g
#undef f
#undef ins
#undef st
#undef sb
#undef locals
#undef d
#undef E
#undef ES
#undef S
#undef SR
#undef T
#undef VR
#undef R
#undef K
#undef U
#undef BK
