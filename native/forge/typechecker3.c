/* native/forge/typechecker3.c - TypeChecker.cs, third part: conditional access, members, access modifiers, records, lambdas, joins, calls,
 * `new`, TC031; then TypeChecker.TakeLocal.cs (TC035), SetterClaims.cs and the entry (Check/Run) (included by typechecker2.c). */

typedef enum FcProof { PR_Proven, PR_Unprovable, PR_Absent } FcProof;
static bool HasMethodNamed(FcTC* t, const FcST* receiver, const char* name);
static bool FullyKnown(FcTC* t, const FcST* receiver);
static bool InterfaceFullyKnown(FcTC* t, const FcST* receiver);
static bool IsUniversalMember(const char* name) { return streq0(name, "TakeLocal") || streq0(name, "TakeUpwards") || streq0(name, "TakeGlobal") || streq0(name, "TakeTo"); }

static FcProof ProveMember(FcTC* t, const FcST* ty, const char* name) {
    switch (ty->kind) {
        case STK_Class:
            if (ty->cls) {
                if (FindMember(t, ty, name) != NULL || HasMethodNamed(t, ty, name) || IsUniversalMember(name)) return PR_Proven;
                if (!FullyKnown(t, ty) && !InterfaceFullyKnown(t, ty)) return PR_Unprovable;
                FC_DICT_EACH(t->classes, e) {
                    const char* k = (const char*)e->key;
                    if (!streq0(k, ty->cls) && DerivesFrom(t, k, ty->cls) && (FindMemberOf(t, k, name) != NULL || HasMethodNamed(t, stClassOf(TC_, k, NULL), name))) return PR_Unprovable;
                }
                return PR_Absent;
            }
            return PR_Unprovable;
        case STK_String: return fcBuiltinHas(FVK_String, false, name) ? PR_Proven : PR_Absent;
        case STK_Char: return fcBuiltinHas(FVK_Char, false, name) ? PR_Proven : PR_Absent;
        case STK_Int: case STK_Float: case STK_Bool: case STK_Word:
            return ty->kind == STK_Int && fcBuiltinHas(FVK_Int, false, name) ? PR_Proven : PR_Absent;
        case STK_Enum: return fcBuiltinHas(FVK_Int, true, name) ? PR_Proven : PR_Absent;
        case STK_Array: return fcBuiltinHas(FVK_Array, false, name) ? PR_Proven : fcBuiltinHas(FVK_Buffer, false, name) ? PR_Unprovable : PR_Absent;
        case STK_Lambda: return fcBuiltinHas(FVK_Lambda, false, name) ? PR_Proven : PR_Unprovable;
        default: return PR_Unprovable;
    }
}
static const FcST* InferConditionalAccess(FcTC* t, FcConditionalAccessExpr* access) {
    const FcST* target = Infer(t, access->target);
    const FcST* on = stIsDynamic(target) ? target : stWithNull(TC_, target, NUL_Defined);
    if (access->checkMember) {
        switch (ProveMember(t, target, access->member)) {
            case PR_Proven: fcSetAdd(t->rr->provenMembers, access); break;
            case PR_Absent:
                ReportError(t, TC_NeverMemberCode, TF(t, "'%s' has no member '%s'%s - '??.' would always skip it", S_(t, target), access->member, target->kind == STK_Class ? " (nor a class derived from it)" : ""), access->h.line);
                on = ST_DYNAMIC;
                break;
            default: on = ST_DYNAMIC; break;
        }
    }
    fcDictPut(&t->placeholderTypes, access->placeholder, (void*)on);
    return stWithNull(TC_, Infer(t, access->inner), NUL_MayBeUndefined);
}

static const char* StaticClassRef(FcTC* t, FcExpr* target);
static void CheckAccess(FcTC* t, const FcST* receiver, const char* name, FcMemberUse use, int argc, int line, bool isStatic, bool exact);
static void CheckInterfaceAccessor(FcTC* t, const FcST* receiver, const char* name, FcMemberUse use, int line);
static void CheckRecordWrite(FcTC* t, const FcST* target, const char* name, FcExpr* receiver, int line);
static void CheckReadonlyWrite(FcTC* t, const FcST* target, const char* name, FcExpr* receiver, int line);
static const char* InterfaceMissingText(FcTC* t, const FcST* receiver, const char* name, const char* what) {
    return TF(t, "'%s' has no %s '%s' (an interface type reaches only what the interface and the ones it extends declare - cast to the class to use others: 'x!ClassName')", S_(t, receiver), what, name);
}

static const FcST* InferMember(FcTC* t, FcMemberExpr* m, FcMemberUse use) {
    FcResolvedRef* resolvedRef = (FcResolvedRef*)fcDictGet(t->refs, m);
    const FcST* target;
    bool isStatic = false;
    const char* staticClass;
    const FcST* member;
    if (resolvedRef && resolvedRef->kind == RR_EnumMember && resolvedRef->enumType) return stEnumOf(TC_, resolvedRef->enumType);
    target = Infer(t, m->target);
    if (streq0(m->name, "Bits") && use == MU_Read && stIsValueKind(target) && target->kind != STK_Array) {
        const char* baseName = target->kind == STK_Int ? (target->raw ? "byte" : "int") : target->kind == STK_Float ? "float" : target->kind == STK_Bool ? "bool" : target->kind == STK_Char ? "char" : "word";
        int declaredBits = target->bits != 0 ? target->bits : target->wide != 0 ? target->wide : FC_NULL_INT;
        fcDictPut(t->rr->bitsOf, m, FC_IVAL(BitsOfType(t, baseName, declaredBits) + 1));
        return stWithNull(TC_, stWithUnit(TC_, ST_INT, FC_UNITLESS, false), NUL_Defined);
    }
    if (stIsDynamic(target) && (staticClass = StaticClassRef(t, m->target)) != NULL) { target = stClassOf(TC_, staticClass, NULL); isStatic = true; }
    CheckAccess(t, target, m->name, use, 0, m->h.line, isStatic, false);
    CheckInterfaceAccessor(t, target, m->name, use, m->h.line);
    if (use == MU_Write && !isStatic) { CheckRecordWrite(t, target, m->name, m->target, m->h.line); CheckReadonlyWrite(t, target, m->name, m->target, m->h.line); }
    if (target->kind == STK_Class && FindMember(t, target, m->name) == NULL && !HasMethodNamed(t, target, m->name)) {
        if (FullyKnown(t, target)) Report(t, TC_UnknownMemberCode, TF(t, "'%s' has no field, property or method '%s'", S_(t, target), m->name), m->h.line);
        else if (InterfaceFullyKnown(t, target) && !IsUniversalMember(m->name)) Report(t, TC_UnknownMemberCode, InterfaceMissingText(t, target, m->name, "property or method"), m->h.line);
    }
    if (target->kind == STK_Class && (member = FindMember(t, target, m->name)) != NULL) return member;
    if (target->kind == STK_Array && streq0(m->name, "length")) return ST_INT;
    if (target->kind == STK_String && streq0(m->name, "Length")) return ST_INT;
    return ST_DYNAMIC;
}

static void CheckInterfaceAccessor(FcTC* t, const FcST* receiver, const char* name, FcMemberUse use, int line) {
    FcList* ic;
    int i, k;
    if (use == MU_Call) return;
    ic = InterfaceChain(t, receiver);
    for (i = 0; i < ic->count; i++) {
        FcInterfaceDecl* iface = (FcInterfaceDecl*)((FcChainEntry*)ic->items[i])->decl;
        if (!iface->properties) continue;
        for (k = 0; k < iface->properties->count; k++) {
            FcInterfacePropertySig* sig = (FcInterfacePropertySig*)iface->properties->items[k];
            if (!streq0(sig->name, name)) continue;
            if (use == MU_Write && !sig->hasSet)
                ReportError(t, TC_InterfaceAccessorCode, TF(t, "The property '%s' of interface '%s' has no 'set' - it cannot be assigned through '%s'", name, PlainName(t, iface->name), S_(t, receiver)), line);
            else if (use == MU_Read && !sig->hasGet)
                ReportError(t, TC_InterfaceAccessorCode, TF(t, "The property '%s' of interface '%s' has no 'get' - it cannot be read through '%s'", name, PlainName(t, iface->name), S_(t, receiver)), line);
            return;
        }
    }
}

static const char* ArityText(FcTC* t, FcList* parameters) {
    int min = 0, i;
    for (i = 0; i < parameters->count; i++) if (((FcLambdaParam*)parameters->items[i])->defaultValue == NULL) min++;
    return min == parameters->count ? TF(t, "%d", min) : TF(t, "%d to %d", min, parameters->count);
}
static const char* fcJoinDistinct(FcTC* t, FcList* texts, const char* sep) {
    FcList* d = fcListNew(TC_);
    FcBuf b = {0};
    int i;
    for (i = 0; i < texts->count; i++) if (!fcListHasStr(d, (const char*)texts->items[i])) fcListAdd(TC_, d, texts->items[i]);
    fcBufStr(&b, "");
    for (i = 0; i < d->count; i++) { if (i) fcBufStr(&b, sep); fcBufStr(&b, (const char*)d->items[i]); }
    return fcBufDone(TC_, &b);
}
static void ReportMissingMethod(FcTC* t, FcMemberExpr* callee, const FcST* target, int argc) {
    FcList* chain;
    FcList* named;
    bool anyFits = false;
    int i, k;
    if (target->kind != STK_Class || IsUniversalMember(callee->name) || (streq0(callee->name, "ToString") && argc == 0)) return;
    if (!FullyKnown(t, target)) {
        FcList* ic;
        FcList* sigs = fcListNew(TC_);
        if (!InterfaceFullyKnown(t, target) || FindMember(t, target, callee->name) != NULL) return;
        ic = InterfaceChain(t, target);
        for (i = 0; i < ic->count; i++) {
            FcInterfaceDecl* iface = (FcInterfaceDecl*)((FcChainEntry*)ic->items[i])->decl;
            for (k = 0; k < iface->methods->count; k++) if (streq0(((FcInterfaceMethodSig*)iface->methods->items[k])->name, callee->name)) fcListAdd(TC_, sigs, iface->methods->items[k]);
        }
        if (sigs->count == 0) { Report(t, TC_UnknownMemberCode, InterfaceMissingText(t, target, callee->name, "method"), callee->h.line); return; }
        for (i = 0; i < sigs->count; i++) if (Fits(((FcInterfaceMethodSig*)sigs->items[i])->params, argc)) anyFits = true;
        if (!anyFits) {
            FcList* texts = fcListNew(TC_);
            for (i = 0; i < sigs->count; i++) fcListAdd(TC_, texts, (void*)ArityText(t, ((FcInterfaceMethodSig*)sigs->items[i])->params));
            Report(t, TC_ArgumentCode, TF(t, "'%s' of '%s' takes %s argument(s), not %d", callee->name, S_(t, target), fcJoinDistinct(t, texts, " or "), argc), callee->h.line);
        }
        return;
    }
    if (streq0(callee->name, "resume") && DerivesFrom(t, target->cls, "Exception")) return;
    if (FindMember(t, target, callee->name) != NULL) return;
    chain = Chain(t, target);
    named = fcListNew(TC_);
    for (i = 0; i < chain->count; i++) {
        FcClassDecl* decl = (FcClassDecl*)((FcChainEntry*)chain->items[i])->decl;
        for (k = 0; k < decl->members->count; k++) {
            FcStmt* m = (FcStmt*)decl->members->items[k];
            if (m->kind == SK_MethodDecl && streq0(((FcMethodDecl*)m)->name, callee->name)) fcListAdd(TC_, named, m);
        }
    }
    if (named->count == 0) { Report(t, TC_UnknownMemberCode, TF(t, "'%s' has no method '%s'", S_(t, target), callee->name), callee->h.line); return; }
    for (i = 0; i < named->count; i++) if (Fits(((FcMethodDecl*)named->items[i])->params, argc)) anyFits = true;
    if (!anyFits) {
        FcList* texts = fcListNew(TC_);
        for (i = 0; i < named->count; i++) fcListAdd(TC_, texts, (void*)ArityText(t, ((FcMethodDecl*)named->items[i])->params));
        Report(t, TC_ArgumentCode, TF(t, "'%s' of '%s' takes %s argument(s), not %d", callee->name, S_(t, target), fcJoinDistinct(t, texts, " or "), argc), callee->h.line);
    }
}

/* ---- access modifiers ---- */
static bool AccessAllowedHere(FcTC* t, const char* declaring, FcAccess access) {
    FcList* chain;
    int i;
    if (access == ACC_Public) return true;
    if (t->cls == NULL) return false;
    if (access == ACC_Private) return streq0(t->cls->name, declaring);
    chain = Chain(t, stClassOf(TC_, t->cls->name, NULL));
    for (i = 0; i < chain->count; i++) if (streq0(((FcClassDecl*)((FcChainEntry*)chain->items[i])->decl)->name, declaring)) return true;
    return false;
}
/* AccessOf: 0 nothing found, 1 allowed, 2 denied (with the message) */
static int AccessOf(FcTC* t, const char* className, const char* name, FcMemberUse use, int argc, bool isStatic, const char** message) {
    FcList* chain = Chain(t, stClassOf(TC_, className, NULL));
    int i, k, j;
    if (use == MU_Call) {
        FcList* methods = fcListNew(TC_);
        FcList* declaring = fcListNew(TC_);
        FcList* seen = fcListNew(TC_);
        for (i = 0; i < chain->count; i++) {
            FcClassDecl* decl = (FcClassDecl*)((FcChainEntry*)chain->items[i])->decl;
            FcList* named = fcListNew(TC_);
            for (k = 0; k < decl->members->count; k++) {
                FcStmt* m = (FcStmt*)decl->members->items[k];
                if (m->kind == SK_MethodDecl && streq0(((FcMethodDecl*)m)->name, name) && ((FcMethodDecl*)m)->isStatic == isStatic) fcListAdd(TC_, named, m);
            }
            for (k = 0; k < named->count; k++) {
                FcMethodDecl* m = (FcMethodDecl*)named->items[k];
                bool over = false;
                if (!Fits(m->params, argc)) continue;
                for (j = 0; j < seen->count && !over; j++) if (OverridesStatic((FcMethodDecl*)seen->items[j], m)) over = true;
                if (!over) { fcListAdd(TC_, methods, m); fcListAdd(TC_, declaring, (void*)decl->name); }
            }
            for (k = 0; k < named->count; k++) fcListAdd(TC_, seen, named->items[k]);
        }
        if (methods->count == 0) return 0;
        for (i = 0; i < methods->count; i++) if (AccessAllowedHere(t, (const char*)declaring->items[i], ((FcMethodDecl*)methods->items[i])->access)) return 1;
        *message = TF(t, "%s '%s' of '%s' is %s and cannot be called from here.", isStatic ? "Static method" : "Method", name, (const char*)declaring->items[0], AccessText(((FcMethodDecl*)methods->items[0])->access));
        return 2;
    }
    for (i = 0; i < chain->count; i++) {
        FcClassDecl* decl = (FcClassDecl*)((FcChainEntry*)chain->items[i])->decl;
        for (k = 0; k < decl->members->count; k++) {
            FcStmt* m = (FcStmt*)decl->members->items[k];
            if (m->kind == SK_FieldDecl && streq0(((FcFieldDecl*)m)->name, name) && ((FcFieldDecl*)m)->isStatic == isStatic) {
                if (AccessAllowedHere(t, decl->name, ((FcFieldDecl*)m)->access)) return 1;
                *message = TF(t, "%s '%s' of '%s' is %s and cannot be accessed from here.", isStatic ? "Static field" : "Field", name, decl->name, AccessText(((FcFieldDecl*)m)->access));
                return 2;
            }
        }
    }
    for (i = 0; i < chain->count; i++) {
        FcClassDecl* decl = (FcClassDecl*)((FcChainEntry*)chain->items[i])->decl;
        for (k = 0; k < decl->members->count; k++) {
            FcStmt* m = (FcStmt*)decl->members->items[k];
            FcPropertyDecl* p;
            if (m->kind != SK_PropertyDecl) continue;
            p = (FcPropertyDecl*)m;
            if (streq0(p->name, name) && p->isStatic == isStatic && (use == MU_Read ? p->getter : p->setter) != NULL) {
                if (AccessAllowedHere(t, decl->name, p->access)) return 1;
                *message = TF(t, "'%s%s' of '%s' is %s and cannot be accessed from here.", use == MU_Read ? "get_" : "set_", name, decl->name, AccessText(p->access));
                return 2;
            }
        }
    }
    return 0;
}
static FcList* DerivedClasses(FcTC* t, const char* className) {
    FcList* found = (FcList*)fcDictGet(&t->derivedClasses, className);
    int i;
    if (found) return found;
    found = fcListNew(TC_);
    FC_DICT_EACH(t->classes, e) {
        FcClassDecl* c = (FcClassDecl*)e->value;
        FcList* chain;
        if (streq0(c->name, className)) continue;
        chain = Chain(t, stClassOf(TC_, c->name, NULL));
        for (i = 1; i < chain->count; i++) if (streq0(((FcClassDecl*)((FcChainEntry*)chain->items[i])->decl)->name, className)) { fcListAdd(TC_, found, (void*)c->name); break; }
    }
    fcDictPut(&t->derivedClasses, className, found);
    return found;
}
static void CheckAccess(FcTC* t, const FcST* receiver, const char* name, FcMemberUse use, int argc, int line, bool isStatic, bool exact) {
    const char* message = NULL;
    const char* className;
    int i;
    if (t->inBoundLambda && !isStatic && !exact) return;
    if (receiver->kind != STK_Class || (className = receiver->cls) == NULL || !fcDictHas(t->classes, className) || !FullyKnown(t, receiver)) return;
    if (AccessOf(t, className, name, use, argc, isStatic, &message) != 2) return;
    if (!exact && !isStatic) {
        FcList* derived = DerivedClasses(t, className);
        for (i = 0; i < derived->count; i++) { const char* m2; if (AccessOf(t, (const char*)derived->items[i], name, use, argc, false, &m2) != 2) return; }
    }
    ReportError(t, TC_AccessCode, message, line);
}
static void CheckImplicitThisAccess(FcTC* t, FcIdentifierExpr* id, FcMemberUse use) {
    FcResolvedRef* r;
    if (t->cls != NULL && !t->inBoundLambda && (r = (FcResolvedRef*)fcDictGet(t->refs, id)) != NULL && r->kind == RR_ImplicitThisMember && !IsLocal(t, id->name)) {
        CheckAccess(t, ThisClass(t), id->name, use, 0, id->h.line, false, false);
        if (use == MU_Write) { CheckRecordWrite(t, ThisClass(t), id->name, NULL, id->h.line); CheckReadonlyWrite(t, ThisClass(t), id->name, NULL, id->h.line); }
    }
}

static const FcST* InferWith(FcTC* t, FcWithExpr* with) {
    const FcST* source = Infer(t, with->source);
    int i;
    if (with->copy == UOP__NONE && source->kind == STK_Class && source->cls) {
        FcList* chain = Chain(t, source);
        if (chain->count > 0 && !((FcClassDecl*)((FcChainEntry*)chain->items[0])->decl)->isRecord)
            ReportError(t, TC_WithWordCode, TF(t, "'with' without 'copy' or 'flat' is for records - '%s' is a class: write 'copy x with { ... }' (a deep copy) or 'flat x with { ... }'", ((FcClassDecl*)((FcChainEntry*)chain->items[0])->decl)->name), with->h.line);
    }
    for (i = 0; i < with->sets->count; i++) {
        FcWithSet* set = (FcWithSet*)with->sets->items[i];
        const FcST* target = ST_DYNAMIC;
        const FcST* value;
        if (source->kind == STK_Class) {
            const FcST* member;
            CheckAccess(t, source, set->name, MU_Write, 0, set->line, false, false);
            if ((member = FindMember(t, source, set->name)) != NULL) target = member;
            else if (FullyKnown(t, source)) Report(t, TC_UnknownMemberCode, TF(t, "'%s' has no field or property '%s'", S_(t, source), set->name), set->line);
        }
        value = InferExpected(t, set->value, target);
        CheckAssignable(t, value, target, set->line, TF(t, "'%s'", set->name), TC_AssignCode, set->value);
    }
    return stWithNull(TC_, source, NUL_Defined);
}

static bool fcChainHasClass(FcTC* t, FcList* chain, const char* name) {
    int i;
    (void)t;
    for (i = 0; i < chain->count; i++) if (streq0(((FcClassDecl*)((FcChainEntry*)chain->items[i])->decl)->name, name)) return true;
    return false;
}
static void CheckRecordWrite(FcTC* t, const FcST* target, const char* name, FcExpr* receiver, int line) {
    FcList* chain;
    FcClassDecl* first;
    bool has = false;
    int i, k;
    if (target->kind != STK_Class || target->cls == NULL) return;
    chain = Chain(t, target);
    if (chain->count == 0 || !(first = (FcClassDecl*)((FcChainEntry*)chain->items[0])->decl)->isRecord) return;
    for (i = 0; i < chain->count && !has; i++) {
        FcClassDecl* d = (FcClassDecl*)((FcChainEntry*)chain->items[i])->decl;
        for (k = 0; k < d->members->count; k++) { FcStmt* m = (FcStmt*)d->members->items[k]; if (m->kind == SK_FieldDecl && streq0(((FcFieldDecl*)m)->name, name) && !((FcFieldDecl*)m)->isStatic) has = true; }
    }
    if (!has) return;
    if (t->inConstructor && !t->inBoundLambda && (receiver == NULL || receiver->kind == EK_ThisExpr) && t->cls != NULL && fcChainHasClass(t, chain, t->cls->name)) return;
    ReportError(t, TC_RecordWriteCode, TF(t, "'%s' is a field of the record '%s': a record is written only while it is constructed (in its constructor) - a changed copy is 'copy r with { .%s = ... }'", name, first->name, name), line);
}
static void CheckReadonlyWrite(FcTC* t, const FcST* target, const char* name, FcExpr* receiver, int line) {
    FcList* chain;
    int i, k, j;
    if (target->kind != STK_Class || target->cls == NULL || !FullyKnown(t, target)) return;
    chain = Chain(t, target);
    for (i = 0; i < chain->count; i++) {
        FcClassDecl* decl = (FcClassDecl*)((FcChainEntry*)chain->items[i])->decl;
        FcStmt* member = NULL;
        FcList* derived;
        for (k = 0; k < decl->members->count && !member; k++) {
            FcStmt* m = (FcStmt*)decl->members->items[k];
            if ((m->kind == SK_FieldDecl && !((FcFieldDecl*)m)->isStatic && streq0(((FcFieldDecl*)m)->name, name))
                || (m->kind == SK_PropertyDecl && !((FcPropertyDecl*)m)->isStatic && streq0(((FcPropertyDecl*)m)->name, name))
                || (m->kind == SK_MethodDecl && !((FcMethodDecl*)m)->isStatic && streq0(((FcMethodDecl*)m)->name, name))) member = m;
        }
        if (!member) continue;
        if (!(member->kind == SK_FieldDecl && ((FcFieldDecl*)member)->isReadonly)) return;
        if (t->inConstructor && !t->inBoundLambda && (receiver == NULL || receiver->kind == EK_ThisExpr) && t->cls != NULL && fcChainHasClass(t, Chain(t, ThisClass(t)), decl->name)) return;
        derived = DerivedClasses(t, target->cls);
        for (j = 0; j < derived->count; j++) {
            FcClassDecl* dd = (FcClassDecl*)fcDictGet(t->classes, derived->items[j]);
            if (!dd) continue;
            for (k = 0; k < dd->members->count; k++) {
                FcStmt* m = (FcStmt*)dd->members->items[k];
                if ((m->kind == SK_FieldDecl && !((FcFieldDecl*)m)->isStatic && !((FcFieldDecl*)m)->isReadonly && streq0(((FcFieldDecl*)m)->name, name))
                    || (m->kind == SK_PropertyDecl && !((FcPropertyDecl*)m)->isStatic && streq0(((FcPropertyDecl*)m)->name, name))) return;
            }
        }
        ReportError(t, TC_ReadonlyWriteCode, TF(t, "'%s.%s' is 'readonly' and can only be assigned inside a constructor of the declaring class", PlainName(t, decl->name), name), line);
        return;
    }
}
static void CheckRecordDecl(FcTC* t, FcClassDecl* decl) {
    int i;
    for (i = 0; i < decl->members->count; i++) {
        FcStmt* m = (FcStmt*)decl->members->items[i];
        FcFieldDecl* f;
        const FcST* type;
        bool ok;
        if (m->kind != SK_FieldDecl) continue;
        f = (FcFieldDecl*)m;
        if (f->isStatic) continue;
        type = DeclaredOrDynamic(t, f->type, f->arrayRanks->count, NULL);
        switch (type->kind) {
            case STK_Dynamic: case STK_Bool: case STK_Int: case STK_Float: case STK_Char: case STK_String: case STK_Lambda: case STK_Enum: case STK_Word: case STK_Undefined: ok = true; break;
            case STK_Class: {
                FcList* chain = type->cls ? Chain(t, type) : NULL;
                ok = chain && chain->count > 0 && ((FcClassDecl*)((FcChainEntry*)chain->items[0])->decl)->isRecord;
                break;
            }
            default: ok = false; break;
        }
        if (!ok) ReportError(t, TC_RecordFieldCode, TF(t, "The field '%s' of the record '%s' is a '%s': a record holds only what does not change - numbers, texts, enums, records, lambdas", f->name, decl->name, S_(t, type)), f->h.line);
    }
}
static void CheckConstructorAccess(FcTC* t, const char* className, int argc, int line) {
    FcClassDecl* decl = (FcClassDecl*)fcDictGet(t->classes, className);
    FcList* ctors;
    int i;
    if (!decl) return;
    ctors = fcListNew(TC_);
    for (i = 0; i < decl->members->count; i++) {
        FcStmt* m = (FcStmt*)decl->members->items[i];
        if (m->kind == SK_ConstructorDecl && Fits(((FcConstructorDecl*)m)->params, argc)) fcListAdd(TC_, ctors, m);
    }
    if (ctors->count == 0) return;
    for (i = 0; i < ctors->count; i++) if (AccessAllowedHere(t, decl->name, ((FcConstructorDecl*)ctors->items[i])->access)) return;
    ReportError(t, TC_AccessCode, TF(t, "Constructor of '%s' is %s and cannot be called from here.", decl->name, AccessText(((FcConstructorDecl*)ctors->items[0])->access)), line);
}
static const char* BaseClassName(FcTC* t) {
    int k;
    if (!t->cls || !t->cls->baseRefs) return NULL;
    for (k = 0; k < t->cls->baseRefs->count; k++) { const char* n = NameOf(t, (FcTypeRef*)t->cls->baseRefs->items[k]); if (fcDictHas(t->classes, n)) return n; }
    return NULL;
}
static bool HasMethodNamed(FcTC* t, const FcST* receiver, const char* name) {
    FcList* chain = Chain(t, receiver);
    FcList* ic;
    int i, k;
    for (i = 0; i < chain->count; i++) {
        FcClassDecl* d = (FcClassDecl*)((FcChainEntry*)chain->items[i])->decl;
        for (k = 0; k < d->members->count; k++) { FcStmt* m = (FcStmt*)d->members->items[k]; if (m->kind == SK_MethodDecl && streq0(((FcMethodDecl*)m)->name, name)) return true; }
    }
    ic = InterfaceChain(t, receiver);
    for (i = 0; i < ic->count; i++) {
        FcInterfaceDecl* d = (FcInterfaceDecl*)((FcChainEntry*)ic->items[i])->decl;
        for (k = 0; k < d->methods->count; k++) if (streq0(((FcInterfaceMethodSig*)d->methods->items[k])->name, name)) return true;
    }
    return false;
}
static bool InterfaceFullyKnown(FcTC* t, const FcST* receiver) {
    FcList* closure;
    int i;
    if (receiver->cls == NULL || !IsInterface(t, receiver->cls)) return false;
    closure = InterfaceClosure(t, receiver->cls);
    for (i = 0; i < closure->count; i++) if (!fcDictHas(&t->interfaces, closure->items[i])) return false;
    return true;
}
static bool FullyKnown(FcTC* t, const FcST* receiver) {
    FcList* chain;
    int i, k;
    if (receiver->cls == NULL || !fcDictHas(t->classes, receiver->cls)) return false;
    chain = Chain(t, receiver);
    for (i = 0; i < chain->count; i++) {
        FcClassDecl* d = (FcClassDecl*)((FcChainEntry*)chain->items[i])->decl;
        if (!d->baseRefs) continue;
        for (k = 0; k < d->baseRefs->count; k++) if (!tcKnown(t, NameOf(t, (FcTypeRef*)d->baseRefs->items[k]))) return false;
    }
    return true;
}

/* ---- casts ---- */
static bool MayImplement(FcTC* t, const char* className, const char* iface) {
    const char* name = className;
    int d, k;
    for (d = 0; d < 64 && name != NULL; d++) {
        FcClassDecl* decl = (FcClassDecl*)fcDictGet(t->classes, name);
        FcTypeRef* b;
        if (!decl) return true;
        if (decl->baseRefs)
            for (k = 0; k < decl->baseRefs->count; k++) {
                FcTypeRef* br = (FcTypeRef*)decl->baseRefs->items[k];
                const char* plain = PlainName(t, br->baseName);
                const char* n = NameOf(t, br);
                FcList* closure;
                int j;
                if (streq0(plain, iface) || (!fcDictHas(t->classes, n) && !fcDictHas(&t->interfaces, plain))) return true;
                if (IsInterface(t, n)) {
                    closure = InterfaceClosure(t, n);
                    for (j = 0; j < closure->count; j++) if (streq0(PlainName(t, (const char*)closure->items[j]), iface)) return true;
                }
            }
        b = fcFirstClassBase(t, decl);
        name = b ? NameOf(t, b) : NULL;
    }
    return name != NULL;
}
static bool CastPossible(FcTC* t, const char* from, const char* to) {
    bool fromInterface = fcDictHas(&t->interfaces, PlainName(t, from)) && !fcDictHas(t->classes, from);
    bool toInterface = fcDictHas(&t->interfaces, PlainName(t, to)) && !fcDictHas(t->classes, to);
    const char* cls;
    const char* iface;
    if (fromInterface && toInterface) return true;
    if ((!fromInterface && !fcDictHas(t->classes, from)) || (!toInterface && !fcDictHas(t->classes, to))) return true;
    if (!fromInterface && !toInterface) return DerivesFrom(t, from, to) || DerivesFrom(t, to, from);
    cls = fromInterface ? to : from;
    iface = PlainName(t, fromInterface ? from : to);
    FC_DICT_EACH(t->classes, e) if (DerivesFrom(t, (const char*)e->key, cls) && MayImplement(t, (const char*)e->key, iface)) return true;
    return false;
}
static void CheckCast(FcTC* t, const FcST* operand, const FcST* target, int line) {
    if (operand->kind != STK_Class || target->kind != STK_Class || CastPossible(t, operand->cls, target->cls)) return;
    Report(t, TC_ImpossibleCastCode, TF(t, "A value of type '%s' can never be a '%s': the cast '!%s' always fails at run time (only undefined passes)", S_(t, operand), S_(t, target), S_(t, target)), line);
}

/* ---- expression trees (TC030) ---- */
static void CheckTreeRules(FcTC* t, FcExpr* e, int line) {
    int i;
#define NO(what) ReportError(t, TC_ExpressionTreeCode, TF(t, "An expression tree cannot hold %s", what), line)
    switch (e->kind) {
        case EK_LiteralExpr: case EK_IdentifierExpr: case EK_ThisExpr: case EK_SelfClassExpr: return;
        case EK_MemberExpr: CheckTreeRules(t, ((FcMemberExpr*)e)->target, line); return;
        case EK_IndexExpr: CheckTreeRules(t, ((FcIndexExpr*)e)->target, line); CheckTreeRules(t, ((FcIndexExpr*)e)->index, line); return;
        case EK_CallExpr: {
            FcCallExpr* call = (FcCallExpr*)e;
            if (call->awaited) { NO("an 'await'"); return; }
            CheckTreeRules(t, call->callee, line);
            for (i = 0; i < call->args->count; i++) CheckTreeRules(t, (FcExpr*)call->args->items[i], line);
            return;
        }
        case EK_BinaryExpr: CheckTreeRules(t, ((FcBinaryExpr*)e)->left, line); CheckTreeRules(t, ((FcBinaryExpr*)e)->right, line); return;
        case EK_UnaryExpr: {
            FcUnaryExpr* u = (FcUnaryExpr*)e;
            if (u->op == UOP_Negate || u->op == UOP_LogicalNot || u->op == UOP_BitNot) { CheckTreeRules(t, u->operand, line); return; }
            if (u->op == UOP_Take || u->op == UOP_TryTake) { NO("a 'take'"); return; }
            break;
        }
        case EK_ConditionalExpr: CheckTreeRules(t, ((FcConditionalExpr*)e)->condition, line); CheckTreeRules(t, ((FcConditionalExpr*)e)->then, line); CheckTreeRules(t, ((FcConditionalExpr*)e)->otherwise, line); return;
        case EK_NewExpr: for (i = 0; i < ((FcNewExpr*)e)->args->count; i++) CheckTreeRules(t, (FcExpr*)((FcNewExpr*)e)->args->items[i], line); return;
        case EK_IsOfExpr: CheckTreeRules(t, ((FcIsOfExpr*)e)->operand, line); return;
        case EK_TypeCoerceExpr: CheckTreeRules(t, ((FcTypeCoerceExpr*)e)->operand, line); return;
        case EK_ArrayLiteralExpr: for (i = 0; i < ((FcArrayLiteralExpr*)e)->elements->count; i++) CheckTreeRules(t, (FcExpr*)((FcArrayLiteralExpr*)e)->elements->items[i], line); return;
        case EK_LambdaExpr: {
            FcLambdaExpr* inner = (FcLambdaExpr*)e;
            if (inner->expressionBody && inner->onTarget == NULL) { CheckTreeRules(t, ((FcReturnStmt*)inner->body->statements->items[0])->value, line); return; }
            NO("a lambda with a block or 'on' (a nested lambda is a tree too: 'x => expression')");
            return;
        }
        case EK_AssignExpr: NO("an assignment"); return;
        case EK_IncDecExpr: NO("'++' or '--'"); return;
        default: break;
    }
    {
        const char* name = fcExprKindNames[e->kind];
        FcBuf b = {0};
        const char* p;
        fcBufStr(&b, "this expression (");
        /* GetType().Name.Replace("Expr", "") */
        for (p = name; *p; ) { if (strncmp(p, "Expr", 4) == 0) p += 4; else fcBufChar(&b, *p++); }
        fcBufChar(&b, ')');
        NO(fcBufDone(TC_, &b));
    }
#undef NO
}
static const FcST* InferTreeArg(FcTC* t, FcExpr* arg, FcLambdaParam* param, FcDict* map, int line) {
    FcLambdaSignature shape;
    const FcST* shapeType;
    FcLambdaExpr* lambda;
    if (param->byRef || !param->type || !param->type->lambdaSignature || !param->type->lambdaSignature->isExpression) return NULL;
    shape = *param->type->lambdaSignature;
    shape.isExpression = false;
    shapeType = DeclaredLambda(t, &shape, map);
    if (!(arg->kind == EK_LambdaExpr && ((FcLambdaExpr*)arg)->expressionBody && ((FcLambdaExpr*)arg)->onTarget == NULL)) {
        if (arg->kind != EK_LambdaExpr) {
            const FcST* passedOn = Infer(t, arg);
            if (passedOn->kind == STK_Class && streq0(passedOn->cls, "LambdaExpression")) return passedOn;
        }
        ReportError(t, TC_ExpressionTreeCode, TF(t, "'%s' is an expression tree: it takes a lambda written here with an expression body ('x => ...') or a tree (LambdaExpression), not %s", param->name,
            arg->kind == EK_LambdaExpr ? "a lambda with a block" : "a value"), line);
        return InferExpected(t, arg, shapeType);
    }
    lambda = (FcLambdaExpr*)arg;
    fcSetAdd(t->rr->treeLambdas, lambda);
    CheckTreeRules(t, ((FcReturnStmt*)lambda->body->statements->items[0])->value, line);
    {
        FcDict* saved = t->treeTypes;
        const FcST* r;
        t->treeTypes = t->rr->treeTypes;
        FC_TRY(TC_) {
            r = InferExpected(t, arg, shapeType);
        } FC_CATCH(TC_) {
            t->treeTypes = saved;
            fcRethrow(TC_);
        } FC_END_TRY;
        t->treeTypes = saved;
        return r;
    }
}

/* ---- lambdas ---- */
static const FcST* InferLambdaCore(FcTC* t, FcLambdaExpr* lam, const FcST* expected);
static const FcST* InferLambda(FcTC* t, FcLambdaExpr* lam, const FcST* expected) {
    bool previous = t->inLambda, previousBound = t->inBoundLambda;
    const FcST* previousThis = t->thisType;
    const FcST* r;
    if (lam->onTarget != NULL) t->thisType = Infer(t, lam->onTarget);
    t->inLambda = true;
    t->inBoundLambda |= lam->onTarget != NULL;
    r = InferLambdaCore(t, lam, expected);
    t->inLambda = previous; t->inBoundLambda = previousBound; t->thisType = previousThis;
    return r;
}
static const FcST* InferLambdaCore(FcTC* t, FcLambdaExpr* lam, const FcST* expected) {
    FcList* context = expected && expected->kind == STK_Lambda && expected->params && expected->params->count == lam->params->count ? expected->params : NULL;
    const FcST* expectedReturn = expected && expected->kind == STK_Lambda && expected->ret && expected->ret->kind != STK_Undefined ? expected->ret : NULL;
    FcList* parameterTypes = fcListNew(TC_);
    const FcST* previousReturn;
    FcTypeRef* previousReturnRef;
    FcList* previousCollected;
    FcList* previousNotes;
    FcDict* previousPlain;
    FcList* returns;
    FcDict* outerCopies;
    FcResolvedRef* lamRef;
    FcList* values;
    const FcST* returned;
    bool anyDefault = false;
    int i;
    for (i = 0; i < lam->params->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)lam->params->items[i];
        const FcST* declared = Declared(t, p->type, p->arrayRanks->count, NULL);
        if (declared) {
            if (context && !Assignable(t, (const FcST*)context->items[i], declared))
                Report(t, TC_ArgumentCode, TF(t, "Parameter '%s' of the lambda is of type '%s', but the lambda is called with '%s'", p->name, S_(t, declared), S_(t, (const FcST*)context->items[i])), lam->h.line);
            fcListAdd(TC_, parameterTypes, (void*)declared);
        } else if (context) fcListAdd(TC_, parameterTypes, context->items[i]);
        else {
            if (!(expected && expected->kind == STK_Lambda))
                Report(t, TC_UntypedCode, TF(t, "Parameter '%s' of the lambda has no type and is assumed to be 'any'. Please add a type (use 'any' if every type is allowed).", p->name), lam->h.line);
            fcListAdd(TC_, parameterTypes, (void*)ST_DYNAMIC);
        }
    }
    previousReturn = t->returnType;
    previousReturnRef = t->returnTypeRef;
    t->returnTypeRef = NULL;
    previousCollected = t->collectedReturns;
    previousNotes = t->retNotes;
    previousPlain = t->plainParams;
    t->retNotes = NULL;
    t->plainParams = NULL;
    t->returnType = expectedReturn;
    returns = fcListNew(TC_);
    t->collectedReturns = returns;
    Push(t);
    for (i = 0; i < lam->params->count; i++) Declare(t, ((FcLambdaParam*)lam->params->items[i])->name, (const FcST*)parameterTypes->items[i]);
    outerCopies = t->implicitCopies;
    t->implicitCopies = NULL;
    lamRef = (FcResolvedRef*)fcDictGet(t->refs, lam);
    if (lamRef && lamRef->kind == RR_LambdaCaptures && lamRef->implicitNames && lamRef->implicitNames->count > 0) {
        t->implicitCopies = fcDictNew(TC_, FC_KEY_STR);
        for (i = 0; i < lamRef->implicitNames->count; i++) fcSetAdd(t->implicitCopies, lamRef->implicitNames->items[i]);
        if (fcSetAdd(&t->reportedLambdas, lam))
            for (i = 0; i < lamRef->implicitNames->count; i++) {
                const char* name = (const char*)lamRef->implicitNames->items[i];
                FcSKind k = Lookup(t, name)->kind;
                const char* deep = k == STK_Class || k == STK_Object || k == STK_Array || k == STK_Dynamic
                    ? " If it holds an object or an array, the copy is deep, with everything it owns: what the lambda gives back of it belongs to the copy and dies with it - pass the object as a parameter of the lambda to use it itself." : "";
                Report(t, TC_ImplicitCaptureCode, TF(t, "The lambda uses '%s' of the code around it: it gets a copy of it, made with the lambda - a change does not reach '%s' outside, and a later change outside is not seen.%s Write 'taking %s' where it is used (a copy), 'handover %s' (moved into the lambda), or bind an object with 'on'.",
                    name, name, deep, name, name), lam->h.line);
            }
    }
    for (i = 0; i < lam->body->statements->count; i++) Check(t, (FcStmt*)lam->body->statements->items[i]);
    t->implicitCopies = outerCopies;
    Pop(t);
    t->returnType = previousReturn;
    t->returnTypeRef = previousReturnRef;
    t->collectedReturns = previousCollected;
    t->retNotes = previousNotes;
    t->plainParams = previousPlain;
    values = fcListNew(TC_);
    for (i = 0; i < returns->count; i++) if (((const FcST*)returns->items[i])->kind != STK_Undefined) fcListAdd(TC_, values, returns->items[i]);
    if (expectedReturn) returned = expectedReturn;
    else if (values->count == 0) returned = ST_UNDEFINED;
    else { const FcST* joined = Join(t, values, NULL); returned = stIsDynamic(joined) ? NULL : joined; }
    for (i = 0; i < lam->params->count; i++) if (((FcLambdaParam*)lam->params->items[i])->defaultValue) anyDefault = true;
    return stLambdaOf(TC_, anyDefault ? -1 : lam->params->count, returned, parameterTypes);
}

static const FcST* CommonBase(FcTC* t, FcList* types);
static const FcST* Join(FcTC* t, FcList* types, FcExpr* site) {
    FcList* known = fcListNew(TC_);
    const FcST* first;
    bool all;
    int i;
    for (i = 0; i < types->count; i++) if (((const FcST*)types->items[i])->kind != STK_Undefined) fcListAdd(TC_, known, types->items[i]);
    if (known->count == 0) return ST_DYNAMIC;
    for (i = 0; i < known->count; i++) if (stIsDynamic((const FcST*)known->items[i])) return ST_DYNAMIC;
    first = (const FcST*)known->items[0];
    for (all = true, i = 0; i < known->count; i++) if (!stEq((const FcST*)known->items[i], first)) all = false;
    if (all) return first;
    for (all = true, i = 0; i < known->count; i++) { const FcST* k = (const FcST*)known->items[i]; if (!(stIsNumber(k) && k->kind == first->kind)) all = false; }
    if (all) return stWithUnit(TC_, first, NULL, false);
    for (all = true, i = 0; i < known->count; i++) if (!stIsNumber((const FcST*)known->items[i])) all = false;
    if (all) return ST_FLOAT;
    for (all = true, i = 0; i < known->count; i++) { FcSKind k = ((const FcST*)known->items[i])->kind; if (k != STK_Class && k != STK_Object) all = false; }
    if (all) {
        bool allClass = true;
        const FcST* common;
        for (i = 0; i < known->count; i++) if (((const FcST*)known->items[i])->kind != STK_Class) allClass = false;
        if (allClass && (common = CommonBase(t, known)) != NULL) return common;
        return ST_OBJECT;
    }
    if (site) {
        FcList* texts = fcListNew(TC_);
        for (i = 0; i < known->count; i++) fcListAdd(TC_, texts, (void*)S_(t, (const FcST*)known->items[i]));
        fcDictPut(&t->mixedJoins, site, (void*)fcJoinDistinct(t, texts, ", "));
    }
    return ST_DYNAMIC;
}
static const FcST* CommonBase(FcTC* t, FcList* types) {
    const FcST* first = (const FcST*)types->items[0];
    FcList* candidates = fcListNew(TC_);
    FcClassDecl* decl;
    FcList* distinct = fcListNew(TC_);
    int i, k, j;
    fcListAdd(TC_, candidates, (void*)first->cls);
    if ((decl = (FcClassDecl*)fcDictGet(t->classes, first->cls)) != NULL) {
        FcList* bases = BaseClassesOf(t, decl);
        FcList* all = fcListNew(TC_);
        for (i = 0; i < bases->count; i++) fcListAdd(TC_, candidates, (void*)((FcClassDecl*)bases->items[i])->name);
        fcListAdd(TC_, all, decl);
        for (i = 0; i < bases->count; i++) fcListAdd(TC_, all, bases->items[i]);
        for (i = 0; i < all->count; i++) {
            FcClassDecl* c = (FcClassDecl*)all->items[i];
            if (!c->baseRefs) continue;
            for (k = 0; k < c->baseRefs->count; k++) {
                const char* n = NameOf(t, (FcTypeRef*)c->baseRefs->items[k]);
                if (IsInterface(t, n)) { FcList* cl = InterfaceClosure(t, n); for (j = 0; j < cl->count; j++) fcListAdd(TC_, candidates, cl->items[j]); }
            }
        }
    } else if (IsInterface(t, first->cls)) { FcList* cl = InterfaceClosure(t, first->cls); for (j = 0; j < cl->count; j++) fcListAdd(TC_, candidates, cl->items[j]); }
    for (i = 0; i < candidates->count; i++) if (!fcListHasStr(distinct, (const char*)candidates->items[i])) fcListAdd(TC_, distinct, candidates->items[i]);
    for (i = 0; i < distinct->count; i++) {
        const char* name = (const char*)distinct->items[i];
        bool allDerive = true, sameArgs = true;
        FcList* args;
        const FcST* v0;
        for (k = 0; k < types->count; k++) if (!DerivesFrom(t, ((const FcST*)types->items[k])->cls, name)) allDerive = false;
        if (!allDerive) continue;
        v0 = streq0(first->cls, name) ? first : AsBase(t, first, name);
        args = v0 ? v0->args : NULL;
        for (k = 0; k < types->count; k++) {
            const FcST* ty = (const FcST*)types->items[k];
            const FcST* v = streq0(ty->cls, name) ? ty : AsBase(t, ty, name);
            if (v == NULL || !(v->args == NULL ? args == NULL : args != NULL && stListEq(v->args, args))) sameArgs = false;
        }
        return stClassOf(TC_, name, sameArgs ? args : NULL);
    }
    return NULL;
}

/* ---- calls ---- */
static const FcST* BuiltinCallType(FcTC* t, const FcST* target, const char* name, int argc) {
    FcST* byteT = stCopy(TC_, ST_INT);
    const FcST* bytes;
    const FcST* number;
    byteT->unit = FC_UNITLESS; byteT->raw = true;
    bytes = stArrayOf(TC_, byteT);
    if (streq0(name, "ToString") && argc == 0 && (target->kind == STK_String || target->kind == STK_Int || target->kind == STK_Float || target->kind == STK_Bool || target->kind == STK_Char
        || target->kind == STK_Word || target->kind == STK_Enum || target->kind == STK_Array || target->kind == STK_Lambda)) return ST_STRING;
    if (target->kind == STK_String) return streq0(name, "ToBytes") && argc == 0 ? bytes : NULL;
    if (target->kind != STK_Array || !target->element || target->element->kind != STK_Int || !target->element->raw) return NULL;
    number = stWithNull(TC_, stWithUnit(TC_, fcStartsWith(name, "ReadFloat") ? ST_FLOAT : ST_INT, FC_UNITLESS, false), NUL_Defined);
    if ((streq0(name, "ReadInt8") || streq0(name, "ReadUInt8")) && argc == 1) return number;
    if ((streq0(name, "ReadInt16") || streq0(name, "ReadUInt16") || streq0(name, "ReadInt32") || streq0(name, "ReadUInt32") || streq0(name, "ReadInt64")
        || streq0(name, "ReadFloat16") || streq0(name, "ReadFloat32") || streq0(name, "ReadFloat64")) && (argc == 1 || argc == 2)) return number;
    if (streq0(name, "Slice") && (argc == 1 || argc == 2)) return bytes;
    if (streq0(name, "IndexOf") && (argc == 1 || argc == 2)) return stWithNull(TC_, stWithUnit(TC_, ST_INT, FC_UNITLESS, false), NUL_Defined);
    if (streq0(name, "Compare") && argc == 1) return stWithNull(TC_, stWithUnit(TC_, ST_INT, FC_UNITLESS, false), NUL_Defined);
    return NULL;
}
static const FcST* ResultOf(FcTC* t, FcMethodDecl* method, FcDict* map) {
    const FcST* type = DeclaredOrDynamic(t, method->returnType, 0, map);
    return Checked(method->h.source) ? MayBeUndefined(t, type) : type;
}
static const FcST* ExtensionCallType(FcTC* t, const FcST* target, const char* name, int argc) {
    const char* typeName = target->kind == STK_String ? "string" : target->kind == STK_Char ? "char" : target->kind == STK_Int ? "int" : target->kind == STK_Float ? "float" : target->kind == STK_Bool ? "bool" : NULL;
    FcClassDecl* ext;
    const FcST* result = NULL;
    int i, k;
    if (typeName == NULL || !(ext = (FcClassDecl*)fcDictGet(t->classes, fcFmt(TC_, "$%s", typeName)))) return NULL;
    for (i = 0; i < ext->members->count; i++) {
        FcStmt* s = (FcStmt*)ext->members->items[i];
        FcMethodDecl* md;
        bool restDefault = true;
        const FcST* type;
        if (s->kind != SK_MethodDecl) continue;
        md = (FcMethodDecl*)s;
        for (k = argc; k < md->params->count; k++) if (((FcLambdaParam*)md->params->items[k])->defaultValue == NULL) restDefault = false;
        if (md->isStatic || !streq0(md->name, name) || md->params->count < argc || !restDefault) continue;
        if (md->returnType == NULL || fcTrIsInferred(md->returnType)) return NULL;
        type = ResultOf(t, md, NULL);
        if (stIsDynamic(type)) return NULL;
        if (result != NULL && !stEq(result, type)) return NULL;
        result = type;
    }
    return result;
}

static const FcST* InferCallCore(FcTC* t, FcCallExpr* c);
static const FcST* InferCall(FcTC* t, FcCallExpr* c) {
    const FcST* type = InferCallCore(t, c);
    const char* name = c->callee->kind == EK_MemberExpr ? ((FcMemberExpr*)c->callee)->name : c->callee->kind == EK_IdentifierExpr ? ((FcIdentifierExpr*)c->callee)->name : NULL;
    if (!fcDictHas(&t->notedCalls, c) && name) fcSetAdd(&t->unseenNames, name);
    return type;
}

/* NativeTypeRef: the type of a native's `returns` and its array ranks; false for `any` and what cannot be read */
static bool NativeTypeRef(FcTC* t, const char* text, FcTypeRef** out, int* ranksOut) {
    int ranks = 0, bits = FC_NULL_INT;
    size_t n = strlen(text);
    char* s = (char*)fcStrN(TC_, text, n);
    FcTypeRef* tr;
    while (n >= 2 && s[n - 2] == '[' && s[n - 1] == ']') { ranks++; n -= 2; while (n > 0 && isspace((unsigned char)s[n - 1])) n--; s[n] = 0; }
    {
        const char* bases[] = { "int", "float" };
        size_t q;
        for (q = 0; q < 2; q++) {
            size_t bl = strlen(bases[q]);
            if (n > bl + 2 && strncmp(s, bases[q], bl) == 0 && s[bl] == '[' && s[n - 1] == ']') {
                const char* w = s + bl + 1;
                size_t wl = n - bl - 2;
                if ((wl == 1 && w[0] == '8') || (wl == 2 && (strncmp(w, "16", 2) == 0 || strncmp(w, "32", 2) == 0 || strncmp(w, "64", 2) == 0))) {
                    bits = atoi(w);
                    s[bl] = 0;
                    n = bl;
                }
            }
        }
    }
    if ((streq0(s, "any") && ranks == 0) || n == 0) return false;
    tr = (FcTypeRef*)fcAlloc(TC_, sizeof *tr);
    if (streq0(s, "byte")) { tr->baseName = "int"; tr->bitWidth = 8; tr->isUnsigned = true; }
    else { tr->baseName = s; tr->bitWidth = bits; }
    *out = tr;
    *ranksOut = ranks;
    return true;
}

static const char* fcDottedName(FcTC* t, FcExpr* e) {
    if (e->kind == EK_IdentifierExpr) return ((FcIdentifierExpr*)e)->name;
    if (e->kind == EK_MemberExpr) {
        const char* prefix = fcDottedName(t, ((FcMemberExpr*)e)->target);
        return prefix ? fcFmt(TC_, "%s.%s", prefix, ((FcMemberExpr*)e)->name) : NULL;
    }
    return NULL;
}
static const char* StaticClassRef(FcTC* t, FcExpr* target) {
    FcExpr* root = target;
    FcIdentifierExpr* first;
    const char* name;
    const char* dotted;
    const char* dot;
    while (root->kind == EK_MemberExpr) root = ((FcMemberExpr*)root)->target;
    if (root->kind != EK_IdentifierExpr) return NULL;
    first = (FcIdentifierExpr*)root;
    if (IsLocal(t, first->name) || (t->cls != NULL && FindMember(t, ThisClass(t), first->name) != NULL)) return NULL;
    name = target->kind == EK_MemberExpr ? ((FcMemberExpr*)target)->name : ((FcIdentifierExpr*)target)->name;
    dotted = fcDottedName(t, target);
    if (dotted && !streq0(dotted, name) && fcDictHas(t->classes, dotted)) return dotted;
    if (t->cls && (dot = strrchr(t->cls->name, '.')) != NULL && dot > t->cls->name) {
        const char* full = fcFmt(TC_, "%.*s.%s", (int)(dot - t->cls->name), t->cls->name, dotted ? dotted : name);
        if (fcDictHas(t->classes, full)) return full;
    }
    return fcDictHas(t->classes, name) ? name : NULL;
}
static const FcST* CallLambda(FcTC* t, const FcST* lambda, FcList* args, FcCallExpr* c, const char* name) {
    if (lambda->paramCount >= 0 && args->count != lambda->paramCount)
        Report(t, TC_ArgumentCode, TF(t, "'%s' (%s) takes %d argument(s), not %d", name, S_(t, lambda), lambda->paramCount, args->count), c->h.line);
    return lambda->ret && lambda->ret->kind != STK_Undefined ? lambda->ret : ST_DYNAMIC;
}
static bool IsLocal(FcTC* t, const char* name) {
    int i;
    for (i = t->scopes->count - 1; i >= 1; i--) if (fcDictHas((FcDict*)t->scopes->items[i], name)) return true;
    return false;
}

static FcDict* WithOpen(FcTC* t, FcDict* map, FcDict* open) {
    FcDict* result = fcDictCopy(TC_, map);
    FC_DICT_EACH(open, e) fcDictAdd(result, e->key, (void*)ST_DYNAMIC);
    return result;
}
static void Bind(FcTC* t, const FcTypeRef* type, int arrayRanks, const FcST* arg, FcDict* map, FcDict* open) {
    int ranks;
    if (type == NULL || (stIsDynamic(arg) && type->typeArgs == NULL)) {
        if (type != NULL && fcDictHas(open, type->baseName) && arrayRanks + type->arrayRank == 0) fcDictAdd(map, type->baseName, (void*)arg);
        return;
    }
    ranks = arrayRanks + type->arrayRank;
    if (ranks > 0) {
        if (arg->kind == STK_Array) { FcTypeRef copy = *type; copy.arrayRank = 0; Bind(t, &copy, ranks - 1, arg->element, map, open); }
        return;
    }
    if (fcDictHas(open, type->baseName) && type->typeArgs == NULL) {
        void* found;
        if (arg->kind == STK_Float && fcDictTry(map, type->baseName, &found) && ((const FcST*)found)->kind == STK_Int) fcDictPut(map, type->baseName, (void*)arg);
        else if (arg->kind != STK_Undefined) fcDictAdd(map, type->baseName, (void*)arg);
        return;
    }
    if (streq0(type->baseName, "lambda") && type->lambdaSignature && !type->lambdaSignature->isSelector && arg->kind == STK_Lambda) {
        FcLambdaSignature* signature = type->lambdaSignature;
        int i;
        if (signature->returnTypeName && fcDictHas(open, signature->returnTypeName) && arg->ret && arg->ret->kind != STK_Undefined) fcDictAdd(map, signature->returnTypeName, (void*)arg->ret);
        if (arg->params)
            for (i = 0; i < signature->paramTypeNames->count && i < arg->params->count; i++)
                if (fcDictHas(open, signature->paramTypeNames->items[i]) && !stIsDynamic((const FcST*)arg->params->items[i])) fcDictAdd(map, signature->paramTypeNames->items[i], arg->params->items[i]);
        return;
    }
    if (type->typeArgs && type->typeArgs->count > 0) {
        const char* generic = GenericName(t, PlainName(t, type->baseName), type->typeArgs->count);
        FcList* actual = NULL;
        int i;
        if (arg->kind == STK_Array && streq0(PlainName(t, type->baseName), "IEnumerable")) { actual = fcListNew(TC_); fcListAdd(TC_, actual, (void*)arg->element); }
        else if (arg->kind == STK_Class) { const FcST* v = streq0(arg->cls, generic) ? arg : AsBase(t, arg, generic); actual = v ? v->args : NULL; }
        if (actual == NULL) return;
        for (i = 0; i < type->typeArgs->count && i < actual->count; i++) Bind(t, (FcTypeRef*)type->typeArgs->items[i], 0, (const FcST*)actual->items[i], map, open);
    }
}

static FcList* TreeOverloads(FcTC* t, FcCallExpr* c, FcList* candidates) {
    int i, k;
    for (i = 0; i < c->args->count; i++) {
        FcExpr* a = (FcExpr*)c->args->items[i];
        bool tree;
        FcList* kept;
        if (a->kind != EK_LambdaExpr) continue;
        tree = ((FcLambdaExpr*)a)->expressionBody && ((FcLambdaExpr*)a)->onTarget == NULL;
        kept = fcListNew(TC_);
        for (k = 0; k < candidates->count; k++) {
            FcMethodDecl* m = ((FcCand*)candidates->items[k])->method;
            bool isTree = i < m->params->count && ((FcLambdaParam*)m->params->items[i])->type && ((FcLambdaParam*)m->params->items[i])->type->lambdaSignature
                && ((FcLambdaParam*)m->params->items[i])->type->lambdaSignature->isExpression;
            if (isTree == tree) fcListAdd(TC_, kept, candidates->items[k]);
        }
        if (kept->count > 0) candidates = kept;
    }
    return candidates;
}

static void CheckStoredArguments(FcTC* t, FcMethodDecl* method, FcList* argParams, FcList* args);
static void CheckStoredTemporaries(FcTC* t, FcConstructorDecl* ctor, FcList* argParams, FcList* args, const char* className);
static FcList* fcTakeParams(FcTC* t, FcList* params, int n) {
    FcList* l = fcListNew(TC_);
    int i;
    for (i = 0; i < n && i < params->count; i++) fcListAdd(TC_, l, params->items[i]);
    return l;
}

/* _methodTypeArgs[c]: the type arguments found, as the run time takes them */
static void fcPutMethodTypeArgs(FcTC* t, FcExpr* c, FcList* typeParams, FcDict* map) {
    FcList* texts = fcListNew(TC_);
    int i;
    for (i = 0; i < typeParams->count; i++) {
        void* found;
        fcListAdd(TC_, texts, (void*)(fcDictTry(map, ((FcTypeParam*)typeParams->items[i])->name, &found) ? TypeArgTextOfType(t, (const FcST*)found) : ""));
    }
    fcDictPut(t->rr->methodTypeArgs, c, texts);
}

static const FcST* InferOverloadedCall(FcTC* t, FcCallExpr* c, FcList* candidates, const char* receiverClass, bool virtualCall) {
    FcList* args = fcListNew(TC_);
    FcCand* picked;
    FcMethodDecl* method;
    FcDict* map;
    const char* hint;
    int i;
    for (i = 0; i < c->args->count; i++) fcListAdd(TC_, args, (void*)Infer(t, (FcExpr*)c->args->items[i]));
    if (!(picked = PickOverload(t, candidates, args))) return ST_DYNAMIC;
    method = picked->method;
    map = picked->map;
    if (receiverClass != NULL && (hint = OverloadHintFor(t, method, candidates, args, receiverClass, virtualCall)) != NULL) fcDictPut(t->rr->overloadHints, c, (void*)hint);
    if (method->typeParams && method->typeParams->count > 0) {
        map = map ? fcDictCopy(TC_, map) : fcDictNew(TC_, FC_KEY_STR);
        if (c->typeArgs && c->typeArgs->count > 0)
            for (i = 0; i < method->typeParams->count; i++) fcDictPut(map, ((FcTypeParam*)method->typeParams->items[i])->name, (void*)DeclaredOrDynamic(t, (FcTypeRef*)c->typeArgs->items[i], 0, map));
        else {
            FcDict* open = fcDictNew(TC_, FC_KEY_STR);
            for (i = 0; i < method->typeParams->count; i++) fcSetAdd(open, ((FcTypeParam*)method->typeParams->items[i])->name);
            FC_DICT_EACH(open, e) fcDictRemove(map, e->key);
            for (i = 0; i < args->count && i < method->params->count; i++)
                Bind(t, ((FcLambdaParam*)method->params->items[i])->type, ((FcLambdaParam*)method->params->items[i])->arrayRanks->count, (const FcST*)args->items[i], map, open);
            fcPutMethodTypeArgs(t, (FcExpr*)c, method->typeParams, map);
            map = WithOpen(t, map, open);
        }
    }
    CheckArguments(t, method->params, args, c->args, method->name, map);
    NoteCallFull(t, (FcExpr*)c, MethodKey(t, method->name, method->params->count), method->params, fcTakeParams(t, method->params, args->count), args, c->args, method);
    CheckStoredArguments(t, method, fcTakeParams(t, method->params, args->count), c->args);
    return ResultOf(t, method, map);
}

static const FcST* InferCallCore(FcTC* t, FcCallExpr* c) {
    const char* receiverClass = NULL;
    bool virtualCall = true;
    FcMethodDecl* method = NULL;
    FcDict* map = NULL;
    const FcST* lambda = NULL;
    const char* name = "the lambda";
    const FcST* calleeTarget = NULL;
    FcList* candidates = NULL;
    FcList* argParams;
    FcDict* open = NULL;
    FcList* args;
    int i, k;
    if (c->callee->kind == EK_MemberExpr && ((FcMemberExpr*)c->callee)->target->kind == EK_IdentifierExpr) {
        bool anyTake = false;
        for (i = 0; i < c->args->count; i++) if (fcIsTakeExpr((FcExpr*)c->args->items[i])) anyTake = true;
        if (anyTake) CheckTakeIntoCopy(t, ((FcIdentifierExpr*)((FcMemberExpr*)c->callee)->target)->name, c->h.line);
    }
    if (c->callee->kind == EK_MemberExpr) {
        FcMemberExpr* m = (FcMemberExpr*)c->callee;
        const FcST* target = Infer(t, m->target);
        bool staticCall = false;
        const char* staticClass;
        const char* baseClass;
        const FcST* builtin;
        const FcST* extended;
        if (stIsDynamic(target) && (staticClass = StaticClassRef(t, m->target)) != NULL) { target = stClassOf(TC_, staticClass, NULL); staticCall = true; virtualCall = false; }
        if (m->target->kind == EK_BaseExpr) virtualCall = false;
        if (staticCall) CheckAccess(t, target, m->name, MU_Call, c->args->count, m->h.line, true, false);
        else if (m->target->kind == EK_BaseExpr && (baseClass = BaseClassName(t)) != NULL) CheckAccess(t, stClassOf(TC_, baseClass, NULL), m->name, MU_Call, c->args->count, m->h.line, false, true);
        else CheckAccess(t, target, m->name, MU_Call, c->args->count, m->h.line, false, false);
        receiverClass = target->cls;
        calleeTarget = target;
        if (!staticCall) NoteReceiverTypeArgs(t, (FcExpr*)c, target);
        if ((builtin = BuiltinCallType(t, target, m->name, c->args->count)) != NULL) { for (i = 0; i < c->args->count; i++) Infer(t, (FcExpr*)c->args->items[i]); return builtin; }
        if (!staticCall && (extended = ExtensionCallType(t, target, m->name, c->args->count)) != NULL) { for (i = 0; i < c->args->count; i++) Infer(t, (FcExpr*)c->args->items[i]); return extended; }
        if (target->kind == STK_Class) {
            const FcST* field;
            candidates = FindMethods(t, target, m->name, c->args->count);
            if (candidates->count == 0 && (field = FindMember(t, target, m->name)) != NULL && field->kind == STK_Lambda) { lambda = field; name = m->name; }
            if (candidates->count == 0 && streq0(m->name, "ToString") && c->args->count == 0 && FullyKnown(t, target)) {
                bool overridden = false;
                FC_DICT_EACH(t->classes, e) if (DerivesFrom(t, (const char*)e->key, target->cls) && HasMethodNamed(t, stClassOf(TC_, (const char*)e->key, NULL), "ToString")) overridden = true;
                if (!overridden) { for (i = 0; i < c->args->count; i++) Infer(t, (FcExpr*)c->args->items[i]); return ST_STRING; }
            }
        }
    } else if (c->callee->kind == EK_IdentifierExpr && fcDictGet(t->refs, c->callee) && ((FcResolvedRef*)fcDictGet(t->refs, c->callee))->kind == RR_Native
               && t->nativeReturns && fcDictGet(t->nativeReturns, ((FcIdentifierExpr*)c->callee)->name)) {
        FcTypeRef* returnsRef;
        int ranks;
        if (NativeTypeRef(t, (const char*)fcDictGet(t->nativeReturns, ((FcIdentifierExpr*)c->callee)->name), &returnsRef, &ranks)) {
            const FcST* returned;
            for (i = 0; i < c->args->count; i++) Infer(t, (FcExpr*)c->args->items[i]);
            returned = DeclaredOrDynamic(t, returnsRef, ranks, NULL);
            if (ranks == 0 && (streq0(returnsRef->baseName, "int") || streq0(returnsRef->baseName, "float") || streq0(returnsRef->baseName, "bool")))
                fcPutGuard(t, (FcExpr*)c, returnsRef, TF(t, "The result of '%s'", ((FcIdentifierExpr*)c->callee)->name));
            return stIsNumber(returned) && returned->unit == NULL ? stWithUnit(TC_, returned, FC_UNITLESS, false) : returned;
        }
        goto plainIdentifier;
    } else if (c->callee->kind == EK_IdentifierExpr) {
        FcIdentifierExpr* id;
        const FcST* variable;
    plainIdentifier:
        id = (FcIdentifierExpr*)c->callee;
        if (t->cls != NULL && !IsLocal(t, id->name)) {
            FcResolvedRef* bare;
            candidates = FindMethods(t, ThisClass(t), id->name, c->args->count);
            receiverClass = ThisClass(t)->cls;
            if ((bare = (FcResolvedRef*)fcDictGet(t->refs, id)) != NULL && bare->kind == RR_ImplicitThisMember) CheckAccess(t, ThisClass(t), id->name, MU_Call, c->args->count, id->h.line, false, false);
        }
        if (!(candidates && candidates->count > 0) && (variable = Lookup(t, id->name))->kind == STK_Lambda) { lambda = variable; name = id->name; }
    } else {
        const FcST* callee = Infer(t, c->callee);
        if (callee->kind == STK_Lambda) lambda = callee;
    }

    if (c->typeArgs && c->typeArgs->count > 0 && candidates != NULL) {
        FcList* kept = fcListNew(TC_);
        for (i = 0; i < candidates->count; i++) {
            FcMethodDecl* m = ((FcCand*)candidates->items[i])->method;
            if (m->typeParams && m->typeParams->count == c->typeArgs->count) fcListAdd(TC_, kept, candidates->items[i]);
        }
        candidates = kept;
        if (candidates->count == 0) {
            const char* callee = c->callee->kind == EK_MemberExpr ? ((FcMemberExpr*)c->callee)->name : c->callee->kind == EK_IdentifierExpr ? ((FcIdentifierExpr*)c->callee)->name : "";
            Report(t, TC_ArgumentCode, TF(t, "No generic method '%s' with %d type parameter(s) takes %d argument(s)", callee, c->typeArgs->count, c->args->count), c->h.line);
            for (i = 0; i < c->args->count; i++) Infer(t, (FcExpr*)c->args->items[i]);
            return ST_DYNAMIC;
        }
    }
    if (c->argNames && candidates && candidates->count > 1) {
        FcList* named = fcListNew(TC_);
        for (i = 0; i < candidates->count; i++) {
            FcMethodDecl* m = ((FcCand*)candidates->items[i])->method;
            bool all = true;
            for (k = 0; k < c->argNames->count && all; k++) {
                const char* n = (const char*)c->argNames->items[k];
                int q;
                bool has = false;
                if (!n) continue;
                for (q = 0; q < m->params->count; q++) if (streq0(((FcLambdaParam*)m->params->items[q])->name, n)) has = true;
                if (!has) all = false;
            }
            if (all) fcListAdd(TC_, named, candidates->items[i]);
        }
        if (named->count > 1) { for (i = 0; i < c->args->count; i++) Infer(t, (FcExpr*)c->args->items[i]); return ST_DYNAMIC; }
        if (named->count == 1) candidates = named;
    }
    if (candidates && candidates->count > 1 && c->argNames == NULL) candidates = TreeOverloads(t, c, candidates);
    if (candidates && candidates->count > 1) return InferOverloadedCall(t, c, candidates, receiverClass, virtualCall);
    if (candidates && candidates->count == 1) { method = ((FcCand*)candidates->items[0])->method; map = ((FcCand*)candidates->items[0])->map; }
    argParams = method == NULL ? NULL : ArgParams(t, method->params, c->argNames, c->args->count, method->name, c->h.line);

    if (method && method->typeParams && method->typeParams->count > 0 && c->typeArgs && c->typeArgs->count > 0) {
        map = map ? fcDictCopy(TC_, map) : fcDictNew(TC_, FC_KEY_STR);
        for (i = 0; i < method->typeParams->count; i++) fcDictPut(map, ((FcTypeParam*)method->typeParams->items[i])->name, (void*)DeclaredOrDynamic(t, (FcTypeRef*)c->typeArgs->items[i], 0, map));
    } else if (method && method->typeParams && method->typeParams->count > 0) {
        map = map ? fcDictCopy(TC_, map) : fcDictNew(TC_, FC_KEY_STR);
        open = fcDictNew(TC_, FC_KEY_STR);
        for (i = 0; i < method->typeParams->count; i++) fcSetAdd(open, ((FcTypeParam*)method->typeParams->items[i])->name);
        FC_DICT_EACH(open, e) fcDictRemove(map, e->key);
    }
    args = fcListNew(TC_);
    for (i = 0; i < c->args->count; i++) {
        const FcST* expected = NULL;
        FcLambdaParam* param = argParams ? (FcLambdaParam*)argParams->items[i] : NULL;
        const FcST* arg = NULL;
        FcExpr* ae = (FcExpr*)c->args->items[i];
        if (param && !param->byRef) {
            expected = Declared(t, param->type, param->arrayRanks->count, open == NULL ? map : WithOpen(t, map, open));
            if (open && param->type && param->type->typeArgs == NULL && fcDictHas(open, param->type->baseName) && param->arrayRanks->count + param->type->arrayRank == 0) {
                void* soFar;
                if (fcDictTry(map, param->type->baseName, &soFar) && ((const FcST*)soFar)->kind == STK_Int) expected = NULL;
            }
            if (open && expected && expected->kind == STK_Lambda && param->type && param->type->lambdaSignature && param->type->lambdaSignature->returnTypeName
                && fcDictHas(open, param->type->lambdaSignature->returnTypeName) && !fcDictHas(map, param->type->lambdaSignature->returnTypeName)) {
                FcST* r = stCopy(TC_, expected); r->ret = NULL; expected = r;
            }
        } else if (lambda && lambda->params && i < lambda->params->count && c->argNames == NULL) expected = (const FcST*)lambda->params->items[i];
        if (param) arg = InferTreeArg(t, ae, param, open == NULL ? map : WithOpen(t, map, open), c->h.line);
        if (!arg) arg = InferExpected(t, ae, expected);
        fcListAdd(TC_, args, (void*)arg);
        if (open && param) Bind(t, param->type, param->arrayRanks->count, arg, map, open);
        if (i == 0 && c->callee->kind == EK_MemberExpr && streq0(((FcMemberExpr*)c->callee)->name, "Send") && calleeTarget && calleeTarget->kind == STK_Class && streq0(calleeTarget->cls, "Channel")
            && c->args->count == 1 && (arg->kind == STK_Class || arg->kind == STK_Object || arg->kind == STK_Array) && !IsFreshOrWorded(ae)) {
            bool record = false;
            if (arg->kind == STK_Class && arg->cls) { FcList* ch = Chain(t, arg); record = ch->count > 0 && ((FcClassDecl*)((FcChainEntry*)ch->items[0])->decl)->isRecord; }
            if (!record) ReportError(t, TC_ChannelWordCode, "An object goes into a channel only with a word: 'Send(copy x)' (a copy) or 'Send(take x)' (moved) - or fresh ('new', 'new T[n]', an array literal, the result of a call)", c->h.line);
        }
    }
    if (open) {
        fcPutMethodTypeArgs(t, (FcExpr*)c, method->typeParams, map);
        for (i = 0; i < c->args->count; i++) {
            FcLambdaParam* p = (FcLambdaParam*)argParams->items[i];
            void* found;
            if (p && !p->byRef && p->type && p->type->typeArgs == NULL && p->type->arrayRank == 0 && p->arrayRanks->count == 0 && fcDictHas(open, p->type->baseName)
                && fcDictTry(map, p->type->baseName, &found) && ((const FcST*)found)->kind == STK_Float && ((const FcST*)args->items[i])->kind == STK_Int)
                fcDictAdd(t->rr->unitChecks, c->args->items[i], (void*)"#any");
        }
        map = WithOpen(t, map, open);
    }
    if (lambda) return CallLambda(t, lambda, args, c, name);
    if (method == NULL) {
        if (c->callee->kind == EK_MemberExpr && calleeTarget) ReportMissingMethod(t, (FcMemberExpr*)c->callee, calleeTarget, c->args->count);
        return ST_DYNAMIC;
    }
    CheckArguments(t, argParams, args, c->args, method->name, map);
    NoteCallFull(t, (FcExpr*)c, MethodKey(t, method->name, method->params->count), method->params, argParams, args, c->args, method);
    CheckStoredArguments(t, method, argParams, c->args);
    return ResultOf(t, method, map);
}

/* ---- new ---- */
static const FcST* InferNewCore(FcTC* t, FcNewExpr* n);
static const FcST* InferNew(FcTC* t, FcNewExpr* n) {
    const FcST* type = InferNewCore(t, n);
    if (!fcDictHas(&t->notedCalls, n) && type->kind == STK_Class && type->cls) fcSetAdd(&t->unseenCtors, type->cls);
    return type;
}
static const FcST* InferNewCore(FcTC* t, FcNewExpr* n) {
    FcList* typeArgs = NULL;
    const char* plain;
    FcClassDecl* decl;
    FcDict* map;
    FcList* ctors;
    FcConstructorDecl* ctor;
    FcList* args;
    int i, k;
    if (n->classRef->typeArgs && n->classRef->typeArgs->count > 0) {
        typeArgs = fcListNew(TC_);
        for (i = 0; i < n->classRef->typeArgs->count; i++) fcListAdd(TC_, typeArgs, (void*)DeclaredOrDynamic(t, (FcTypeRef*)n->classRef->typeArgs->items[i], 0, NULL));
    }
    plain = typeArgs ? GenericName(t, PlainName(t, n->classRef->baseName), typeArgs->count) : PlainName(t, n->classRef->baseName);
    if (!fcDictHas(t->classes, plain) && n->classRef->namespaces && n->classRef->namespaces->count > 0) {
        const char* resolved = NameOf(t, n->classRef);
        if (fcDictHas(t->classes, resolved)) plain = resolved;
    }
    if (!(decl = (FcClassDecl*)fcDictGet(t->classes, plain))) { for (i = 0; i < n->args->count; i++) Infer(t, (FcExpr*)n->args->items[i]); return ST_DYNAMIC; }
    CheckConstructorAccess(t, plain, n->args->count, n->h.line);
    map = MapFor(t, decl->typeParams, typeArgs);
    ctors = fcListNew(TC_);
    for (i = 0; i < decl->members->count; i++) {
        FcStmt* m = (FcStmt*)decl->members->items[i];
        if (m->kind == SK_ConstructorDecl && Fits(((FcConstructorDecl*)m)->params, n->args->count)) fcListAdd(TC_, ctors, m);
    }
    if (n->argNames) {
        FcList* kept = fcListNew(TC_);
        FcList* named;
        FcList* namedArgs;
        const char* callee = TF(t, "new %s", PlainName(t, plain));
        for (i = 0; i < ctors->count; i++) {
            FcConstructorDecl* kc = (FcConstructorDecl*)ctors->items[i];
            bool all = true;
            for (k = 0; k < n->argNames->count && all; k++) {
                const char* nm = (const char*)n->argNames->items[k];
                int q;
                bool has = false;
                if (!nm) continue;
                for (q = 0; q < kc->params->count; q++) if (streq0(((FcLambdaParam*)kc->params->items[q])->name, nm)) has = true;
                if (!has) all = false;
            }
            if (all) fcListAdd(TC_, kept, kc);
        }
        ctors = kept;
        if (ctors->count == 0) {
            FcBuf b = {0};
            bool first = true;
            for (k = 0; k < n->argNames->count; k++) if (n->argNames->items[k]) { if (!first) fcBufStr(&b, ", "); first = false; fcBufFmt(&b, "'%s'", (const char*)n->argNames->items[k]); }
            fcBufStr(&b, "");
            ReportError(t, TC_NamedArgumentCode, TF(t, "No constructor of '%s' has parameters named %s", PlainName(t, plain), fcBufDone(TC_, &b)), n->h.line);
        }
        if (ctors->count != 1) { for (i = 0; i < n->args->count; i++) Infer(t, (FcExpr*)n->args->items[i]); return stClassOf(TC_, plain, typeArgs); }
        named = ArgParams(t, ((FcConstructorDecl*)ctors->items[0])->params, n->argNames, n->args->count, callee, n->h.line);
        namedArgs = fcListNew(TC_);
        for (i = 0; i < n->args->count; i++) {
            FcLambdaParam* tp = (FcLambdaParam*)named->items[i];
            FcExpr* a = (FcExpr*)n->args->items[i];
            const FcST* r = tp ? InferTreeArg(t, a, tp, map, n->h.line) : NULL;
            if (!r) r = InferExpected(t, a, tp && !tp->byRef ? Declared(t, tp->type, tp->arrayRanks->count, map) : NULL);
            fcListAdd(TC_, namedArgs, (void*)r);
        }
        CheckArguments(t, named, namedArgs, n->args, callee, map);
        NoteCallFull(t, (FcExpr*)n, CtorKey(t, plain, ((FcConstructorDecl*)ctors->items[0])->params->count), ((FcConstructorDecl*)ctors->items[0])->params, named, namedArgs, n->args, NULL);
        CheckStoredTemporaries(t, (FcConstructorDecl*)ctors->items[0], named, n->args, plain);
        return stClassOf(TC_, plain, typeArgs);
    }
    ctor = ctors->count == 1 ? (FcConstructorDecl*)ctors->items[0] : NULL;
    if (ctors->count > 1) {
        FcList* ordered = fcListCopy(TC_, ctors);
        FcList* asMethods = fcListNew(TC_);
        FcList* ctorArgs = fcListNew(TC_);
        FcCand* picked;
        for (i = 1; i < ordered->count; i++)
            for (k = i; k > 0; k--) {
                FcConstructorDecl* a = (FcConstructorDecl*)ordered->items[k - 1];
                FcConstructorDecl* b = (FcConstructorDecl*)ordered->items[k];
                int ka = a->params->count == n->args->count ? 0 : 1, kb = b->params->count == n->args->count ? 0 : 1;
                if (ka < kb || (ka == kb && a->params->count <= b->params->count)) break;
                ordered->items[k - 1] = b; ordered->items[k] = a;
            }
        for (i = 0; i < ordered->count; i++) {
            FcConstructorDecl* kc = (FcConstructorDecl*)ordered->items[i];
            FcMethodDecl* md = FC_NEW(TC_, FcMethodDecl, .h = {SK_MethodDecl, kc->h.source, kc->h.line}, .returnType = NULL, .name = "#new", .params = kc->params, .body = kc->body);
            fcListAdd(TC_, asMethods, fcCand(t, md, map));
        }
        for (i = 0; i < n->args->count; i++) fcListAdd(TC_, ctorArgs, (void*)Infer(t, (FcExpr*)n->args->items[i]));
        if ((picked = PickOverload(t, asMethods, ctorArgs)) != NULL) {
            FcConstructorDecl* chosen = NULL;
            for (i = 0; i < asMethods->count; i++) if (((FcCand*)asMethods->items[i])->method == picked->method) { chosen = (FcConstructorDecl*)ordered->items[i]; break; }
            CheckArguments(t, chosen->params, ctorArgs, n->args, TF(t, "new %s", PlainName(t, plain)), map);
            NoteCallFull(t, (FcExpr*)n, CtorKey(t, plain, chosen->params->count), chosen->params, fcTakeParams(t, chosen->params, ctorArgs->count), ctorArgs, n->args, NULL);
            CheckStoredTemporaries(t, chosen, fcTakeParams(t, chosen->params, ctorArgs->count), n->args, plain);
            {
                FcList* methods = fcListNew(TC_);
                const char* hint;
                for (i = 0; i < asMethods->count; i++) fcListAdd(TC_, methods, ((FcCand*)asMethods->items[i])->method);
                if ((hint = ConstructorHint(t, decl, plain, chosen, methods, ctorArgs)) != NULL) fcDictPut(t->rr->overloadHints, n, (void*)hint);
            }
        }
        return stClassOf(TC_, plain, typeArgs);
    }
    args = fcListNew(TC_);
    for (i = 0; i < n->args->count; i++) {
        FcExpr* a = (FcExpr*)n->args->items[i];
        const FcST* r = NULL;
        if (ctor && i < ctor->params->count) r = InferTreeArg(t, a, (FcLambdaParam*)ctor->params->items[i], map, n->h.line);
        if (!r) {
            FcLambdaParam* p = ctor && i < ctor->params->count ? (FcLambdaParam*)ctor->params->items[i] : NULL;
            r = InferExpected(t, a, p && !p->byRef ? Declared(t, p->type, p->arrayRanks->count, map) : NULL);
        }
        fcListAdd(TC_, args, (void*)r);
    }
    if (ctor) {
        CheckArguments(t, ctor->params, args, n->args, TF(t, "new %s", PlainName(t, plain)), map);
        NoteCallFull(t, (FcExpr*)n, CtorKey(t, plain, ctor->params->count), ctor->params, fcTakeParams(t, ctor->params, args->count), args, n->args, NULL);
        CheckStoredTemporaries(t, ctor, fcTakeParams(t, ctor->params, args->count), n->args, plain);
    }
    return stClassOf(TC_, plain, typeArgs);
}

#include "typechecker4.c"
