/* native/forge/tools/dump.c - `forge-c dump` (Phase D 4, commit a3e43117): src/fire/Bytecode/ProgramDump.cs - a linked program as text, everything a program
 * file holds (programfile.c) in the order it holds it, one thing per line, made to be compared; a function or a chunk the program holds twice is printed once
 * and then named by its number. The texts are fc8; Q escapes by UTF-16 units as C# does. */

typedef struct TdDumper { FcCtx* c; FcBuf* w; bool lines; FcDict* protos; FcDict* chunks; } TdDumper;

static void tdLine(TdDumper* d, int indent, const char* text) {
    int i;
    for (i = 0; i < indent * 2; i++) fcBufChar(d->w, ' ');
    fcBufStr(d->w, text);
    fcBufChar(d->w, '\n');
}
/* Q: a text in quotes, '"' '\' and the controls escaped, everything outside 0x20-0x7E as \uxxxx; null without quotes */
static const char* tdQ(FcCtx* c, const char* s) {
    FcBuf b = {0};
    const unsigned char* p = (const unsigned char*)s;
    if (!s) return "null";
    fcBufChar(&b, '"');
    while (*p) {
        uint16_t u = fc8Next(&p);
        if (u == '"' || u == '\\') { fcBufChar(&b, '\\'); fcBufChar(&b, (char)u); }
        else if (u == '\n') fcBufStr(&b, "\\n");
        else if (u == '\r') fcBufStr(&b, "\\r");
        else if (u == '\t') fcBufStr(&b, "\\t");
        else if (u < 0x20 || u > 0x7E) fcBufFmt(&b, "\\u%04x", u);
        else fcBufChar(&b, (char)u);
    }
    fcBufChar(&b, '"');
    return fcBufDone(c, &b);
}
static const char* tdL(FcCtx* c, const FcList* l) {
    FcBuf b = {0};
    int i;
    if (!l) return "null";
    fcBufChar(&b, '[');
    for (i = 0; i < l->count; i++) { if (i) fcBufStr(&b, ", "); fcBufStr(&b, tdQ(c, (const char*)l->items[i])); }
    fcBufChar(&b, ']');
    return fcBufDone(c, &b);
}
/* the keys of a set (a HashSet in the order of .NET) as L does it; NULL: null */
static const char* tdLKeys(FcCtx* c, FcDict* set) {
    FcList* l;
    if (!set) return "null";
    l = fcListNew(c);
    FC_DICT_EACH(set, e) fcListAdd(c, l, (void*)e->key);
    return tdL(c, l);
}
static const char* tdBool(bool b) { return b ? "True" : "False"; }
/* F: double.ToString("R") (gen/misc.c), " (-0)" behind a negative zero */
static const char* tdF(FcCtx* c, double v) {
    FgGen* g;
    if (isnan(v) || isinf(v)) return fcDoubleR(c, v);
    if (v == 0) return signbit(v) ? "-0 (-0)" : "0";
    g = (FgGen*)fcAlloc(c, sizeof *g);
    g->c = c;
    return fgRoundTrip(g, v, false);
}
static const char* tdDims(FcCtx* c, const FcUnit* u) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "{");
    for (i = 0; i < u->dimCount; i++) fcBufFmt(&b, "%s%s^%d", i ? " " : "", u->dimSym[i], u->dimExp[i]);
    fcBufChar(&b, '}');
    return fcBufDone(c, &b);
}
static const char* tdUnitText(FcCtx* c, const FcU* u) {
    if (!u) return "none";
    return fcFmt(c, "%s scale=%s symbol=%s offset=%s converter=%s unitless=%s", tdDims(c, &u->u), tdF(c, u->u.scale), tdQ(c, u->display), tdF(c, u->u.offset),
        tdQ(c, u->u.converter), tdQ(c, u->u.unitlessConverter));
}
static const char* tdConstantText(FcCtx* c, const FcConst* v) {
    switch (v->kind) {
        case CK_Undefined: return fcFmt(c, "undefined unit=%s", tdUnitText(c, v->unit));
        case CK_Bool: return v->i ? "true" : "false";
        case CK_Int: {
            FcBuf b = {0};
            int k;
            fcBufFmt(&b, "int %" PRId64 " width=%d wide=%d", v->i, v->width, v->wide);
            if (v->en) {
                fcBufFmt(&b, " enum=%s%s {", tdQ(c, v->en->name), v->en->isFlags ? " flags" : "");
                for (k = 0; k < v->en->names->count; k++) fcBufFmt(&b, "%s%s=%" PRId64, k ? ", " : "", (const char*)v->en->names->items[k], *(int64_t*)v->en->values->items[k]);
                fcBufChar(&b, '}');
            } else fcBufFmt(&b, " unit=%s", tdUnitText(c, v->unit));
            return fcBufDone(c, &b);
        }
        case CK_Float: return fcFmt(c, "float %s width=%d unit=%s", tdF(c, v->f), v->width, tdUnitText(c, v->unit));
        case CK_Char: { FcBuf b = {0}; fcBufUnit(&b, (uint16_t)v->i); return fcFmt(c, "char %s", tdQ(c, fcBufDone(c, &b))); }
        case CK_String: return fcFmt(c, "string %s", tdQ(c, v->s));
        default: return fcFmt(c, "word 0x%" PRIx64, v->w);
    }
}
static const char* tdTypeText(FcCtx* c, const FcTypeRef* t) {
    FcBuf b = {0};
    int i;
    if (!t) return "none";
    fcBufStr(&b, "(");
    fcBufStr(&b, tdQ(c, t->baseName));
    if (t->bitWidth != FC_NULL_INT) fcBufFmt(&b, " bits=%d", t->bitWidth);
    if (t->pointerDepth != 0) fcBufFmt(&b, " pointer=%d", t->pointerDepth);
    if (t->lambdaSignature) {
        const FcLambdaSignature* s = t->lambdaSignature;
        fcBufFmt(&b, " lambda(returns=%s params=%s selector=%s kind=%s expression=%s)", tdQ(c, s->returnTypeName), tdL(c, s->paramTypeNames), tdBool(s->isSelector),
            tdQ(c, s->selectorKind), tdBool(s->isExpression));
    }
    if (t->namespaces) fcBufFmt(&b, " namespaces=%s", tdL(c, t->namespaces));
    if (t->unit) fcBufFmt(&b, " unit=%s", tdQ(c, t->unit));
    if (t->arrayRank != 0) fcBufFmt(&b, " rank=%d", t->arrayRank);
    if (t->typeArgCount != 0) fcBufFmt(&b, " typeArgCount=%d", t->typeArgCount);
    if (t->typeArgs) {
        fcBufStr(&b, " typeArgs=<");
        for (i = 0; i < t->typeArgs->count; i++) fcBufFmt(&b, "%s%s", i ? ", " : "", tdTypeText(c, (const FcTypeRef*)t->typeArgs->items[i]));
        fcBufChar(&b, '>');
    }
    if (t->isUnsigned) fcBufStr(&b, " unsigned");
    if (t->nonNull) fcBufStr(&b, " defined");
    fcBufChar(&b, ')');
    return fcBufDone(c, &b);
}
static void tdAttributes(TdDumper* d, int indent, const FcList* list) {
    int i;
    for (i = 0; i < list->count; i++) {
        const FcAttributeMeta* a = (const FcAttributeMeta*)list->items[i];
        tdLine(d, indent, fcFmt(d->c, "attribute %s args=%s json=%s", tdQ(d->c, a->name), tdL(d->c, a->argTexts), tdQ(d->c, a->argsJson)));
    }
}
static const char* const tdAccess[] = { "Public", "Private", "Protected" };
static void tdChunkRef(TdDumper* d, int indent, FcChunk* chunk);
static void tdProtoRef(TdDumper* d, int indent, FcProto* proto) {
    FcCtx* c = d->c;
    void* at;
    int id, i;
    if (!proto) { tdLine(d, indent, "function none"); return; }
    if (fcDictTry(d->protos, proto, &at)) { tdLine(d, indent, fcFmt(c, "function #%d (above)", FC_VALI(at))); return; }
    id = fcDictCount(d->protos);
    fcDictPut(d->protos, proto, FC_IVAL(id));
    tdLine(d, indent, fcFmt(c, "function #%d params=%d access=%s static=%s readsOnly=%s refMask=0x%x captureCopy=0x%" PRIx64 " captureMoved=0x%" PRIx64 " typeParams=%d", id, proto->paramCount,
        proto->access >= 0 && proto->access <= 2 ? tdAccess[proto->access] : "none", tdBool(proto->isStatic), tdBool(proto->readsOnly), (unsigned)proto->refMask, proto->captureCopyMask,
        proto->captureMovedMask, proto->typeParamCount));
    tdLine(d, indent + 1, fcFmt(c, "paramNames=%s paramTypes=%s selectorPath=%s fieldGetterOf=%s", tdL(c, proto->paramNames), tdL(c, proto->paramTypes), tdL(c, proto->selectorPath),
        tdQ(c, proto->fieldGetterOf)));
    if (proto->specOwner || proto->specArgs || proto->specDefinedMask != 0 || proto->specParamKindCount >= 0) {
        FcBuf kinds = {0};
        if (proto->specParamKindCount < 0) fcBufStr(&kinds, "null");
        else { fcBufStr(&kinds, ""); for (i = 0; i < proto->specParamKindCount; i++) fcBufFmt(&kinds, "%02x", proto->specParamKinds[i]); }
        tdLine(d, indent + 1, fcFmt(c, "spec owner=%s args=%s definedMask=0x%" PRIx64 " paramKinds=%s", tdQ(c, proto->specOwner), tdL(c, proto->specArgs), (uint64_t)proto->specDefinedMask,
            fcBufDone(c, &kinds)));
    }
    for (i = 0; i < proto->paramDefaults->count; i++)
        if (proto->paramDefaults->items[i]) { tdLine(d, indent + 1, fcFmt(c, "default %d", i)); tdProtoRef(d, indent + 2, (FcProto*)proto->paramDefaults->items[i]); }
        else tdLine(d, indent + 1, fcFmt(c, "default %d none", i));
    tdChunkRef(d, indent + 1, proto->chunk);
    if (proto->specializations)
        for (i = 0; i < proto->specializations->count; i++) { tdLine(d, indent + 1, fcFmt(c, "specialization %d", i)); tdProtoRef(d, indent + 2, (FcProto*)proto->specializations->items[i]); }
}
/* the operand sizes of the opcodes (OperandSizes.Table) */
static const char* const tdOpNames[] = {
#define FIRE_OP(name, number, operands) [number] = #name,
#include "../../tables/opcodes.def"
#undef FIRE_OP
};
static const char* const tdOpSizes[] = {
#define FIRE_OP(name, number, operands) [number] = operands,
#include "../../tables/opcodes.def"
#undef FIRE_OP
};
static void tdCode(TdDumper* d, int indent, const FcChunk* chunk) {
    const uint8_t* code = chunk->code;
    int ip = 0, n = chunk->len;
    while (ip < n) {
        int op = code[ip], at = ip++, i, operands[8];
        const char* sizes = op < (int)(sizeof tdOpNames / sizeof tdOpNames[0]) && tdOpNames[op] ? tdOpSizes[op] : NULL;
        FcBuf b = {0};
        if (!sizes) {
            /* (an opcode without a row in the table: the rest as bytes, so that nothing is lost) */
            fcBufFmt(&b, "%05d ?%d ", at, code[at]);
            for (i = ip; i < n; i++) fcBufFmt(&b, "%02x", code[i]);
            tdLine(d, indent, fcBufDone(d->c, &b));
            return;
        }
        fcBufFmt(&b, "%05d %s", at, tdOpNames[op]);
        memset(operands, 0, sizeof operands);
        for (i = 0; sizes[i] && i < 8; i++) {
            int size = sizes[i] - '0';
            if (ip + size > n) break;
            /* `catch threads()` without a type has no type operand (OpInfo.Decode) */
            if (op == OP_RegisterThreadsCatch && i == 2 && operands[1] == 0) break;
            operands[i] = size == 1 ? code[ip] : code[ip] | (code[ip + 1] << 8);
            ip += size;
            fcBufFmt(&b, " %d", operands[i]);
        }
        tdLine(d, indent, fcBufDone(d->c, &b));
    }
}
static void tdChunkRef(TdDumper* d, int indent, FcChunk* chunk) {
    FcCtx* c = d->c;
    void* at;
    int id, i, k;
    if (fcDictTry(d->chunks, chunk, &at)) { tdLine(d, indent, fcFmt(c, "chunk @%d (above)", FC_VALI(at))); return; }
    id = fcDictCount(d->chunks);
    fcDictPut(d->chunks, chunk, FC_IVAL(id));
    tdLine(d, indent, fcFmt(c, "chunk @%d code=%d", id, chunk->len));
    for (i = 0; i < chunk->constants->count; i++) tdLine(d, indent + 1, fcFmt(c, "const %d %s", i, tdConstantText(c, (const FcConst*)chunk->constants->items[i])));
    for (i = 0; i < chunk->units->count; i++) tdLine(d, indent + 1, fcFmt(c, "unit %d %s", i, tdUnitText(c, (const FcU*)chunk->units->items[i])));
    if (chunk->receiverOffsets)
        for (i = 0; i < chunk->receiverOffsets->count; i++) tdLine(d, indent + 1, fcFmt(c, "receiverTypeArgs %d %s", FC_VALI(chunk->receiverOffsets->items[i]), tdQ(c, (const char*)chunk->receiverTexts->items[i])));
    for (i = 0; i < chunk->handlers->count; i++) {
        const FcHandler* h = (const FcHandler*)chunk->handlers->items[i];
        FcBuf b = {0};
        fcBufFmt(&b, "handler %d catches=[", i);
        for (k = 0; k < h->catchTypes->count; k++) fcBufFmt(&b, "%s%s@%d", k ? ", " : "", tdQ(c, (const char*)h->catchTypes->items[k]), FC_VALI(h->catchAddrs->items[k]));
        if (h->finallyAddr >= 0) fcBufFmt(&b, "] finally=%d", h->finallyAddr); else fcBufStr(&b, "] finally=none");
        tdLine(d, indent + 1, fcBufDone(c, &b));
    }
    for (i = 0; i < chunk->nameCount; i++) tdLine(d, indent + 1, fcFmt(c, "name %d %d %s", chunk->names[i].depth, chunk->names[i].slot, tdQ(c, chunk->names[i].name)));
    if (d->lines)
        for (i = 0; i < chunk->lineCount; i++) tdLine(d, indent + 1, fcFmt(c, "line %d source=%d line=%d", chunk->lines[i].offset, chunk->lines[i].source, chunk->lines[i].line));
    tdCode(d, indent + 1, chunk);
    for (i = 0; i < chunk->functions->count; i++) { tdLine(d, indent + 1, fcFmt(c, "proto %d", i)); tdProtoRef(d, indent + 2, (FcProto*)chunk->functions->items[i]); }
}
static void tdClass(TdDumper* d, const char* key, FcRtClass* rc) {
    FcCtx* c = d->c;
    FcBuf flags = {0};
    int i;
    fcBufStr(&flags, "");
#define TD_FLAG(x, name) if (x) fcBufFmt(&flags, "%s%s", flags.len ? " " : "", name)
    TD_FLAG(rc->isActor, "actor"); TD_FLAG(rc->isRecord, "record"); TD_FLAG(rc->isReflectionHelper, "reflectionHelper"); TD_FLAG(rc->keep, "keep");
    TD_FLAG(rc->notSerializable, "notSerializable"); TD_FLAG(rc->noCopy, "noCopy");
#undef TD_FLAG
    tdLine(d, 0, fcFmt(c, "class %s%s base=%s flags=[%s]", tdQ(c, key), strcmp(key, rc->name) != 0 ? fcFmt(c, " name=%s", tdQ(c, rc->name)) : "", tdQ(c, rc->base ? rc->base->name : NULL), fcBufDone(c, &flags)));
    tdLine(d, 1, fcFmt(c, "interfaces %s", tdL(c, rc->interfaces)));
    tdLine(d, 1, fcFmt(c, "baseTypeArgs %s", tdL(c, rc->baseTypeArgs)));
    tdLine(d, 1, fcFmt(c, "keptMembers %s", tdL(c, rc->keptMembers)));
    tdLine(d, 1, fcFmt(c, "readonlyStatics %s", tdLKeys(c, rc->readonlyStatics)));
    if (rc->meta) {
        tdLine(d, 1, fcFmt(c, "meta bases=%s declaredInterfaces=%s", tdL(c, rc->meta->baseNames), tdL(c, rc->meta->declaredInterfaces)));
        tdAttributes(d, 2, rc->meta->attributes);
        for (i = 0; i < rc->meta->members->count; i++) {
            const FcMemberMeta* m = (const FcMemberMeta*)rc->meta->members->items[i];
            tdLine(d, 2, fcFmt(c, "member %s kind=%s type=%s access=%s static=%s readonly=%s read=%s write=%s unit=%s params=%s types=%s", tdQ(c, m->name), tdQ(c, m->kind), tdQ(c, m->typeName),
                tdQ(c, m->access), tdBool(m->isStatic), tdBool(m->isReadonly), tdBool(m->canRead), tdBool(m->canWrite), tdQ(c, m->unit), tdL(c, m->paramNames), tdL(c, m->paramTypes)));
            tdAttributes(d, 3, m->attributes);
        }
    }
    FC_DICT_EACH(rc->ownFieldInfo, e) {
        const FcFieldInfo* f = (const FcFieldInfo*)e->value;
        tdLine(d, 1, fcFmt(c, "fieldInfo %s access=%s static=%s type=%s unit=%s kind=%d requiredType=%s array=%d arrayType=%s arrayOfParam=%s derivedDefined=%s", tdQ(c, (const char*)e->key),
            f->access >= 0 && f->access <= 2 ? tdAccess[f->access] : "?", tdBool(f->isStatic), tdQ(c, f->typeText), tdQ(c, f->requiredUnit), f->requiredKind, tdQ(c, f->requiredType),
            (int)(uint16_t)f->requiredArray, tdQ(c, f->requiredArrayType), tdQ(c, f->arrayOfParam), tdBool(f->derivedDefined)));
        if (f->attributes) { tdLine(d, 2, "attributes"); tdAttributes(d, 3, f->attributes); }
    }
    /* (StaticFieldValues: the compiler fills none) */
    for (i = 0; i < rc->fieldNames->count; i++) { tdLine(d, 1, fcFmt(c, "field %s", tdQ(c, (const char*)rc->fieldNames->items[i]))); tdProtoRef(d, 2, (FcProto*)rc->fieldInits->items[i]); }
    for (i = 0; i < rc->staticFieldNames->count; i++) { tdLine(d, 1, fcFmt(c, "staticField %s", tdQ(c, (const char*)rc->staticFieldNames->items[i]))); tdProtoRef(d, 2, (FcProto*)rc->staticFieldInits->items[i]); }
    FC_DICT_EACH(rc->methods, e) {
        FcList* overloads = (FcList*)e->value;
        for (i = 0; i < overloads->count; i++) { tdLine(d, 1, fcFmt(c, "method %s", tdQ(c, (const char*)e->key))); tdProtoRef(d, 2, (FcProto*)overloads->items[i]); }
    }
    for (i = 0; i < rc->ctorArities->count; i++) { tdLine(d, 1, fcFmt(c, "constructor %d", FC_VALI(rc->ctorArities->items[i]))); tdProtoRef(d, 2, (FcProto*)rc->ctorProtos->items[i]); }
    for (i = 0; i < rc->extraConstructors->count; i++) { tdLine(d, 1, "constructor extra"); tdProtoRef(d, 2, (FcProto*)rc->extraConstructors->items[i]); }
    if (rc->destructor) { tdLine(d, 1, "destructor"); tdProtoRef(d, 2, rc->destructor); }
    if (rc->thisReads) FC_DICT_EACH(rc->thisReads, e) tdLine(d, 1, fcFmt(c, "thisReads %s %s", tdQ(c, (const char*)e->key), tdL(c, (FcList*)e->value)));
}
static const char* const tdModes[] = { "Debug", "Release", "Performance" };
/* ProgramDump.Text: the program as text into `w` (fc8) */
static void tdProgram(FcCtx* c, const FcLinked* linked, bool lineTables, FcBuf* w) {
    TdDumper D;
    TdDumper* d = &D;
    const FcCompiled* p = linked->program;
    FcDict* printed = fcDictNew(c, FC_KEY_PTR);
    int i, k;
    d->c = c; d->w = w; d->lines = lineTables; d->protos = fcDictNew(c, FC_KEY_PTR); d->chunks = fcDictNew(c, FC_KEY_PTR);
    tdLine(d, 0, fcFmt(c, "program mode=%s float=%d int=%d word=%d firstUserSource=%d", tdModes[linked->mode], linked->floatWidth, linked->intWidth, linked->wordWidth, linked->firstUserSource));
    tdLine(d, 0, fcFmt(c, "nativeImports %s", tdLKeys(c, linked->nativeImports)));
    tdLine(d, 0, fcFmt(c, "packageNatives %s", tdL(c, linked->packageNatives)));
    tdLine(d, 0, fcFmt(c, "packageNativeLibraries %s", tdL(c, linked->packageNativeLibraries)));
    tdLine(d, 0, fcFmt(c, "graphicsNatives %s", tdL(c, linked->graphicsNatives)));
    for (i = 0; i < p->customUnits->count; i++) {
        const FcCustomUnit* u = (const FcCustomUnit*)p->customUnits->items[i];
        tdLine(d, 0, fcFmt(c, "unit %s symbol=%s prefixed=%s %s scale=%s offset=%s converter=%s unitless=%s", tdQ(c, u->name), tdQ(c, u->symbol), tdBool(u->prefixed), tdDims(c, &u->unit),
            tdF(c, u->unit.scale), tdF(c, u->unit.offset), tdQ(c, u->unit.converter), tdQ(c, u->unit.unitlessConverter)));
    }
    for (i = 0; i < p->enums->count; i++) {
        const FcEnumType* e = (const FcEnumType*)p->enums->items[i];
        FcBuf b = {0};
        fcBufFmt(&b, "enum %s%s {", tdQ(c, e->name), e->isFlags ? " flags" : "");
        for (k = 0; k < e->names->count; k++) fcBufFmt(&b, "%s%s=%" PRId64, k ? ", " : "", (const char*)e->names->items[k], *(int64_t*)e->values->items[k]);
        fcBufChar(&b, '}');
        tdLine(d, 0, fcBufDone(c, &b));
    }
    for (i = 0; i < p->resources->count; i++) {
        const FcResourceEntry* r = (const FcResourceEntry*)p->resources->items[i];
        FcSha256 h;
        fcShaInit(&h);
        fcShaUpdate(&h, r->data, r->size);
        tdLine(d, 0, fcFmt(c, "resource %s bytes=%zu sha256=%s", tdQ(c, r->name), r->size, fcShaHex(c, &h)));
    }
    for (i = 0; i < p->externNames->count; i++) {
        const FcExternSig* sig = (const FcExternSig*)p->externSigs->items[i];
        FcBuf params = {0};
        fcBufStr(&params, "");
        for (k = 0; k < sig->paramTypes->count; k++) fcBufFmt(&params, "%s%s", k ? ", " : "", tdTypeText(c, (const FcTypeRef*)sig->paramTypes->items[k]));
        tdLine(d, 0, fcFmt(c, "extern %s lib=%s display=%s returns=%s params=[%s] names=%s", tdQ(c, (const char*)p->externNames->items[i]), tdQ(c, sig->libName), tdQ(c, sig->displayName),
            tdTypeText(c, sig->returnType), fcBufDone(c, &params), tdL(c, sig->paramNames)));
        if (sig->inlineC) tdLine(d, 1, fcFmt(c, "inline source=%d line=%d c=%s", sig->inlineSource, sig->inlineLine, tdQ(c, sig->inlineC)));
    }
    FC_DICT_EACH(p->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        if (!fcSetAdd(printed, rc)) { tdLine(d, 0, fcFmt(c, "class %s = class %s (above)", tdQ(c, (const char*)e->key), tdQ(c, rc->name))); continue; }
        tdClass(d, (const char*)e->key, rc);
    }
    tdLine(d, 0, "top");
    tdChunkRef(d, 1, p->topLevel);
}
