/* fire VM in C: JSON (phase B step 5). */
static Value vmJsonCall(Vm* vm, VmFrame* fr, uint32_t argc, const Value* a, OwnList* list) {
    (void)vm; (void)fr; (void)argc; (void)a; (void)list;
    vmUnsupported("the native", "__JsonCall");
}
