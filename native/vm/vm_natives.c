/* fire VM in C: the natives every program has (native/tables/natives.def - NativeRegistry.CreateDefault) on the functions of the runtime the generator
 * calls for them (CppGenerator, OpCode.CallNative): print, the methods of the base types by number (__StringCall -> stringCall, __CharCall -> charCall,
 * __MathCall -> mathCall, __BitsCall -> bitsCall, __EnvCall -> envCall, __SbCall -> sbCall, __ResourceCall -> resourceCall), __array_copy, __type_arg.
 * The natives of packages and of the bridges (phase B step 4) and those of threads, tasks and channels (step 3) are not here yet. */

/* The text of a type argument (__type_arg): the display text, undefined without one. */
static Value vmTypeArgText(VmFrame* fr, Value ix, OwnList* list) {
    TArgs ta = fr->ctargs;
    if (!ta || ix.kind != K_Int || ix.i < 0 || !ta[ix.i] || !ta[ix.i]->display) return Undef();
    return newStrFromUtf8((const uint8_t*)ta[ix.i]->display, strlen(ta[ix.i]->display), list);
}

/* CallNative `idx` with the top `argc` values of the stack as its arguments: the result replaces them. False: the native is not one of the VM's. */
static bool vmCallNative(Vm* vm, VmFrame* fr, uint32_t idx, uint32_t argc, Value* a, Value* result) {
    OwnList* list = &fr->scope->temp;
    Value u = Undef();
    (void)vm;
    switch (idx) {
        case 0: if (argc != 1) return false; *result = print(a[0], list); return true;
        case 1: if (argc < 2 || argc > 4) return false; *result = stringCall(a[0].i, a[1], (int)argc - 2, argc > 2 ? a[2] : u, argc > 3 ? a[3] : u, list); return true;
        case 2: if (argc != 2) return false; *result = charCall(a[0].i, a[1], list); return true;
        case 3: if (argc != 2) return false; *result = resourceCall(a[0].i, a[1], list); return true;
        case 4: if (argc < 2 || argc > 3) return false; *result = mathCall(a[0].i, a[1], argc > 2 ? a[2] : u); return true;
        case 6: if (argc < 1 || argc > 3) return false; *result = envCall(a[0].i, argc > 1 ? a[1] : u, argc > 2 ? a[2] : u, list); return true;
        case 8: if (argc < 2 || argc > 4) return false; *result = bitsCall(a[0].i, a[1], argc > 2 ? a[2] : u, argc > 3 ? a[3] : u); return true;
        case 11: if (argc != 3) return false; *result = fire_arrayCopy(a[0], a[1], a[2]); return true;
        case 21: if (argc != 1) return false; *result = vmTypeArgText(fr, a[0], list); return true;
        case 37: if (argc < 2 || argc > 3) return false; *result = sbCall(a[0].i, a[1], argc > 2 ? a[2] : u, list); return true;
        default: return false;
    }
}
