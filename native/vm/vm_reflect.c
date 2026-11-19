/* fire VM in C: reflection (phase B step 5). */
static Value vmReflNative(Vm* vm, VmFrame* fr, uint32_t which, const Value* a, OwnList* list) {
    (void)vm; (void)fr; (void)a; (void)list;
    vmUnsupported("the native", g_vmBridgeReflection[which]);
}
