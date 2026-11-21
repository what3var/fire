/* native/forge/programfile.c - port of src/fire/Bytecode/ProgramFile.cs (commit 6bc618e7), the writer: a linked program as the bytes of the
 * format `.fbc` (docs/spec/BYTECODE.md, section 48) - the names, the units and the enums as tables in front of the program, functions and chunks
 * written where they stand first and named by their number afterwards. With SHA-256 (the hashes of the sources, LinkedProgram.SourceHashes). */

/* ---- SHA-256 (FIPS 180-4) ---- */
typedef struct FcSha256 { uint32_t h[8]; uint8_t block[64]; size_t blockLen; uint64_t total; } FcSha256;
static const uint32_t fcShaK[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
    0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
    0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
    0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2 };
#define FC_ROR(x, n) (((x) >> (n)) | ((x) << (32 - (n))))
static void fcShaBlock(FcSha256* s, const uint8_t* p) {
    uint32_t w[64], a, b, c, d, e, f, g, h;
    int i;
    for (i = 0; i < 16; i++) w[i] = (uint32_t)p[i * 4] << 24 | (uint32_t)p[i * 4 + 1] << 16 | (uint32_t)p[i * 4 + 2] << 8 | p[i * 4 + 3];
    for (i = 16; i < 64; i++) {
        uint32_t s0 = FC_ROR(w[i - 15], 7) ^ FC_ROR(w[i - 15], 18) ^ (w[i - 15] >> 3);
        uint32_t s1 = FC_ROR(w[i - 2], 17) ^ FC_ROR(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    a = s->h[0]; b = s->h[1]; c = s->h[2]; d = s->h[3]; e = s->h[4]; f = s->h[5]; g = s->h[6]; h = s->h[7];
    for (i = 0; i < 64; i++) {
        uint32_t t1 = h + (FC_ROR(e, 6) ^ FC_ROR(e, 11) ^ FC_ROR(e, 25)) + ((e & f) ^ (~e & g)) + fcShaK[i] + w[i];
        uint32_t t2 = (FC_ROR(a, 2) ^ FC_ROR(a, 13) ^ FC_ROR(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
        h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
    }
    s->h[0] += a; s->h[1] += b; s->h[2] += c; s->h[3] += d; s->h[4] += e; s->h[5] += f; s->h[6] += g; s->h[7] += h;
}
static void fcShaInit(FcSha256* s) {
    static const uint32_t init[8] = { 0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19 };
    memcpy(s->h, init, sizeof init);
    s->blockLen = 0;
    s->total = 0;
}
static void fcShaUpdate(FcSha256* s, const uint8_t* p, size_t n) {
    s->total += n;
    while (n > 0) {
        size_t take = 64 - s->blockLen < n ? 64 - s->blockLen : n;
        memcpy(s->block + s->blockLen, p, take);
        s->blockLen += take; p += take; n -= take;
        if (s->blockLen == 64) { fcShaBlock(s, s->block); s->blockLen = 0; }
    }
}
/* the hash as hex, lower case */
static const char* fcShaHex(FcCtx* c, FcSha256* s) {
    uint64_t bits = s->total * 8;
    uint8_t pad = 0x80, len[8], out[32];
    char hex[65];
    int i;
    fcShaUpdate(s, &pad, 1);
    pad = 0;
    while (s->blockLen != 56) fcShaUpdate(s, &pad, 1);
    for (i = 0; i < 8; i++) len[i] = (uint8_t)(bits >> (56 - 8 * i));
    fcShaUpdate(s, len, 8);
    for (i = 0; i < 8; i++) { out[i * 4] = (uint8_t)(s->h[i] >> 24); out[i * 4 + 1] = (uint8_t)(s->h[i] >> 16); out[i * 4 + 2] = (uint8_t)(s->h[i] >> 8); out[i * 4 + 3] = (uint8_t)s->h[i]; }
    for (i = 0; i < 32; i++) snprintf(hex + i * 2, 3, "%02x", out[i]);
    return fcStr(c, hex);
}
/* Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant() of an fc8 text */
static const char* fcSha256OfText(FcCtx* c, const char* fc8) {
    FcSha256 s;
    FcBuf utf8 = {0};
    fcOut8(&utf8, fc8);
    fcShaInit(&s);
    if (utf8.len) fcShaUpdate(&s, (const uint8_t*)utf8.p, utf8.len);
    fcBufFree(&utf8);
    return fcShaHex(c, &s);
}

/* ---- the writer ---- */
typedef struct FcPfWriter {
    FcCtx* c;
    bool lines;
    FcBuf* out;
    FcDict* strings; FcList* stringList;
    FcDict* units; FcList* unitList;
    FcDict* enums; FcList* enumList;
    FcDict* protos; FcDict* chunks; FcDict* open;
    FcDict* classes; FcList* classList;
} FcPfWriter;

static void pfU8(FcPfWriter* w, int b) { fcBufChar(w->out, (char)(uint8_t)b); }
static void pfBool(FcPfWriter* w, bool b) { pfU8(w, b ? 1 : 0); }
static void pfUInt(FcPfWriter* w, uint64_t v) { while (v >= 0x80) { pfU8(w, (int)(v | 0x80)); v >>= 7; } pfU8(w, (int)v); }
static void pfCount(FcPfWriter* w, int n) { pfUInt(w, (uint64_t)n); }
static void pfInt(FcPfWriter* w, int64_t v) { pfUInt(w, ((uint64_t)v << 1) ^ (v < 0 ? UINT64_MAX : 0)); }
static void pfF64(FcPfWriter* w, double d) { uint64_t bits; int i; memcpy(&bits, &d, 8); for (i = 0; i < 8; i++) pfU8(w, (int)(bits >> (8 * i))); }
static void pfStr(FcPfWriter* w, const char* s) {
    void* at;
    if (!s) { pfUInt(w, 0); return; }
    if (!fcDictTry(w->strings, s, &at)) {
        fcListAdd(w->c, w->stringList, (void*)s);
        at = FC_IVAL(w->stringList->count);
        fcDictPut(w->strings, s, at);
    }
    pfUInt(w, (uint64_t)FC_VALI(at));
}
static void pfStrList(FcPfWriter* w, FcList* l) { int i; pfCount(w, l ? l->count : 0); if (l) for (i = 0; i < l->count; i++) pfStr(w, (const char*)l->items[i]); }
static void pfOptStrList(FcPfWriter* w, FcList* l) { int i; if (!l) { pfUInt(w, 0); return; } pfCount(w, l->count + 1); for (i = 0; i < l->count; i++) pfStr(w, (const char*)l->items[i]); }
static void pfDims(FcPfWriter* w, const FcUnit* u) { int i; pfCount(w, u->dimCount); for (i = 0; i < u->dimCount; i++) { pfStr(w, u->dimSym[i]); pfInt(w, u->dimExp[i]); } }

static void pfUnitRef(FcPfWriter* w, const FcU* u) {
    FcBuf key = {0};
    int i;
    uint64_t bits;
    void* at;
    const char* k;
    if (!u) { pfUInt(w, 0); return; }
    for (i = 0; i < u->u.dimCount; i++) fcBufFmt(&key, "%s^%d;", u->u.dimSym[i], u->u.dimExp[i]);
    memcpy(&bits, &u->u.scale, 8);
    fcBufFmt(&key, "|%" PRId64 "|", (int64_t)bits);
    if (u->display) fcBufStr(&key, u->display); else fcBufStr(&key, "\x01\x01");
    memcpy(&bits, &u->u.offset, 8);
    fcBufFmt(&key, "|%" PRId64 "|", (int64_t)bits);
    if (u->u.converter) fcBufStr(&key, u->u.converter); else fcBufStr(&key, "\x01\x01");
    fcBufChar(&key, '|');
    if (u->u.unitlessConverter) fcBufStr(&key, u->u.unitlessConverter); else fcBufStr(&key, "\x01\x01");
    k = fcBufDone(w->c, &key);
    if (!fcDictTry(w->units, k, &at)) {
        fcListAdd(w->c, w->unitList, (void*)u);
        at = FC_IVAL(w->unitList->count);
        fcDictPut(w->units, k, at);
    }
    pfUInt(w, (uint64_t)FC_VALI(at));
}
static void pfEnumRef(FcPfWriter* w, const FcEnumType* e) {
    void* at;
    if (!e) { pfUInt(w, 0); return; }
    if (!fcDictTry(w->enums, e->key, &at)) {
        fcListAdd(w->c, w->enumList, (void*)e);
        at = FC_IVAL(w->enumList->count);
        fcDictPut(w->enums, e->key, at);
    }
    pfUInt(w, (uint64_t)FC_VALI(at));
}
static void pfConstant(FcPfWriter* w, const FcConst* v) {
    switch (v->kind) {
        case CK_Undefined: pfU8(w, 0); pfUnitRef(w, v->unit); break;
        case CK_Bool: pfU8(w, 1); pfBool(w, v->i != 0); break;
        case CK_Int: pfU8(w, 2); pfInt(w, v->i); pfU8(w, v->width); pfU8(w, v->wide); pfUnitRef(w, v->unit); pfEnumRef(w, v->en); break;
        case CK_Float: pfU8(w, 3); pfF64(w, v->f); pfU8(w, v->width); pfUnitRef(w, v->unit); break;
        case CK_Char: pfU8(w, 4); pfUInt(w, (uint64_t)(uint16_t)v->i); break;
        case CK_String: {
            FcU16 units = {0};
            int i;
            fcU16FromFc8(v->s, &units);
            pfU8(w, 5);
            pfCount(w, units.len);
            for (i = 0; i < units.len; i++) { pfU8(w, units.p[i] & 0xFF); pfU8(w, units.p[i] >> 8); }
            fcU16Free(&units);
            break;
        }
        default: { int i; pfU8(w, 6); for (i = 0; i < 8; i++) pfU8(w, (int)(v->w >> (8 * i))); break; }
    }
}

static void pfChunkRef(FcPfWriter* w, FcChunk* chunk);
static void pfProtoRef(FcPfWriter* w, FcProto* proto) {
    void* at;
    int i;
    if (!proto) { pfUInt(w, 0); return; }
    if (fcDictTry(w->protos, proto, &at)) {
        if (fcDictHas(w->open, proto)) fcThrow(w->c, FC_ERR_INTERNAL, 0, 0, NULL, "A function of the program contains itself.");
        pfUInt(w, (uint64_t)FC_VALI(at) + 2);
        return;
    }
    pfUInt(w, 1);
    fcDictPut(w->protos, proto, FC_IVAL(fcDictCount(w->protos)));
    fcSetAdd(w->open, proto);
    pfChunkRef(w, proto->chunk);
    pfInt(w, proto->paramCount);
    pfCount(w, proto->paramDefaults->count);
    for (i = 0; i < proto->paramDefaults->count; i++) pfProtoRef(w, (FcProto*)proto->paramDefaults->items[i]);
    pfU8(w, proto->access >= 0 ? proto->access + 1 : 0);
    pfU8(w, (proto->isStatic ? 1 : 0) | (proto->readsOnly ? 2 : 0));
    pfUInt(w, proto->refMask);
    pfUInt(w, proto->captureCopyMask);
    pfUInt(w, proto->captureMovedMask);
    pfOptStrList(w, proto->selectorPath);
    pfOptStrList(w, proto->paramTypes);
    pfStr(w, proto->fieldGetterOf);
    pfOptStrList(w, proto->paramNames);
    pfInt(w, proto->typeParamCount);
    if (!proto->specializations) pfUInt(w, 0);
    else { pfCount(w, proto->specializations->count + 1); for (i = 0; i < proto->specializations->count; i++) pfProtoRef(w, (FcProto*)proto->specializations->items[i]); }
    pfStr(w, proto->specOwner);
    pfOptStrList(w, proto->specArgs);
    pfInt(w, proto->specDefinedMask);
    if (proto->specParamKindCount < 0) pfUInt(w, 0);
    else { pfCount(w, proto->specParamKindCount + 1); for (i = 0; i < proto->specParamKindCount; i++) pfU8(w, proto->specParamKinds[i]); }
    fcDictRemove(w->open, proto);
}
static void pfChunkRef(FcPfWriter* w, FcChunk* chunk) {
    void* at;
    int i, k;
    if (fcDictTry(w->chunks, chunk, &at)) {
        if (fcDictHas(w->open, chunk)) fcThrow(w->c, FC_ERR_INTERNAL, 0, 0, NULL, "A chunk of the program contains itself.");
        pfUInt(w, (uint64_t)FC_VALI(at) + 2);
        return;
    }
    pfUInt(w, 1);
    fcDictPut(w->chunks, chunk, FC_IVAL(fcDictCount(w->chunks)));
    fcSetAdd(w->open, chunk);
    pfCount(w, chunk->len);
    fcBufPut(w->out, (const char*)chunk->code, (size_t)chunk->len);
    pfCount(w, chunk->constants->count);
    for (i = 0; i < chunk->constants->count; i++) pfConstant(w, (FcConst*)chunk->constants->items[i]);
    pfCount(w, chunk->units->count);
    for (i = 0; i < chunk->units->count; i++) pfUnitRef(w, (const FcU*)chunk->units->items[i]);
    pfCount(w, chunk->functions->count);
    for (i = 0; i < chunk->functions->count; i++) pfProtoRef(w, (FcProto*)chunk->functions->items[i]);
    if (!chunk->receiverOffsets) pfUInt(w, 0);
    else {
        pfCount(w, chunk->receiverOffsets->count + 1);
        for (i = 0; i < chunk->receiverOffsets->count; i++) { pfInt(w, FC_VALI(chunk->receiverOffsets->items[i])); pfStr(w, (const char*)chunk->receiverTexts->items[i]); }
    }
    pfCount(w, chunk->handlers->count);
    for (i = 0; i < chunk->handlers->count; i++) {
        FcHandler* h = (FcHandler*)chunk->handlers->items[i];
        pfCount(w, h->catchTypes->count);
        for (k = 0; k < h->catchTypes->count; k++) { pfStr(w, (const char*)h->catchTypes->items[k]); pfInt(w, FC_VALI(h->catchAddrs->items[k])); }
        pfInt(w, h->finallyAddr >= 0 ? (int64_t)h->finallyAddr + 1 : 0);
    }
    if (w->lines) {
        pfCount(w, chunk->lineCount);
        for (i = 0; i < chunk->lineCount; i++) { pfInt(w, chunk->lines[i].offset); pfInt(w, chunk->lines[i].source); pfInt(w, chunk->lines[i].line); }
    }
    pfCount(w, chunk->nameCount);
    for (i = 0; i < chunk->nameCount; i++) { pfInt(w, chunk->names[i].depth); pfInt(w, chunk->names[i].slot); pfStr(w, chunk->names[i].name); }
    fcDictRemove(w->open, chunk);
}
static void pfAttributes(FcPfWriter* w, FcList* list) {
    int i;
    pfCount(w, list->count);
    for (i = 0; i < list->count; i++) { FcAttributeMeta* a = (FcAttributeMeta*)list->items[i]; pfStr(w, a->name); pfStrList(w, a->argTexts); pfStr(w, a->argsJson); }
}
static void pfClass(FcPfWriter* w, FcRtClass* rc) {
    int i;
    pfStr(w, rc->name);
    if (!rc->base) pfUInt(w, 0); else pfCount(w, FC_VALI(fcDictGet(w->classes, rc->base)));
    pfU8(w, (rc->isActor ? 1 : 0) | (rc->isRecord ? 2 : 0) | (rc->isReflectionHelper ? 4 : 0) | (rc->keep ? 8 : 0) | (rc->notSerializable ? 16 : 0) | (rc->noCopy ? 32 : 0));
    if (!rc->meta) pfBool(w, false);
    else {
        pfBool(w, true);
        pfCount(w, rc->meta->members->count);
        for (i = 0; i < rc->meta->members->count; i++) {
            FcMemberMeta* m = (FcMemberMeta*)rc->meta->members->items[i];
            pfStr(w, m->name); pfStr(w, m->kind); pfStr(w, m->typeName); pfStr(w, m->access);
            pfU8(w, (m->isStatic ? 1 : 0) | (m->isReadonly ? 2 : 0) | (m->canRead ? 4 : 0) | (m->canWrite ? 8 : 0));
            pfStr(w, m->unit); pfStrList(w, m->paramNames); pfStrList(w, m->paramTypes);
            pfAttributes(w, m->attributes);
        }
        pfStrList(w, rc->meta->baseNames);
        pfAttributes(w, rc->meta->attributes);
        pfStrList(w, rc->meta->declaredInterfaces);
    }
    pfStrList(w, rc->interfaces);
    pfOptStrList(w, rc->baseTypeArgs);
    pfOptStrList(w, rc->keptMembers);
    pfCount(w, rc->fieldNames->count);
    for (i = 0; i < rc->fieldNames->count; i++) { pfStr(w, (const char*)rc->fieldNames->items[i]); pfProtoRef(w, (FcProto*)rc->fieldInits->items[i]); }
    pfCount(w, fcDictCount(rc->ownFieldInfo));
    FC_DICT_EACH(rc->ownFieldInfo, e) {
        FcFieldInfo* f = (FcFieldInfo*)e->value;
        pfStr(w, (const char*)e->key);
        pfStr(w, f->requiredUnit); pfU8(w, f->requiredKind); pfStr(w, f->requiredType); pfUInt(w, (uint64_t)(uint16_t)f->requiredArray); pfStr(w, f->requiredArrayType); pfStr(w, f->arrayOfParam);
        pfU8(w, (f->derivedDefined ? 1 : 0) | (f->isStatic ? 2 : 0));
        pfU8(w, f->access);
        pfStr(w, f->typeText);
        if (!f->attributes) pfBool(w, false); else { pfBool(w, true); pfAttributes(w, f->attributes); }
    }
    pfCount(w, 0);   /* StaticFieldValues: the compiler fills none */
    pfCount(w, rc->staticFieldNames->count);
    for (i = 0; i < rc->staticFieldNames->count; i++) { pfStr(w, (const char*)rc->staticFieldNames->items[i]); pfProtoRef(w, (FcProto*)rc->staticFieldInits->items[i]); }
    if (!rc->readonlyStatics) pfUInt(w, 0);
    else { pfCount(w, fcDictCount(rc->readonlyStatics) + 1); FC_DICT_EACH(rc->readonlyStatics, e) pfStr(w, (const char*)e->key); }
    pfCount(w, fcDictCount(rc->methods));
    FC_DICT_EACH(rc->methods, e) {
        FcList* overloads = (FcList*)e->value;
        pfStr(w, (const char*)e->key);
        pfCount(w, overloads->count);
        for (i = 0; i < overloads->count; i++) pfProtoRef(w, (FcProto*)overloads->items[i]);
    }
    pfCount(w, rc->ctorArities->count);
    for (i = 0; i < rc->ctorArities->count; i++) { pfInt(w, FC_VALI(rc->ctorArities->items[i])); pfProtoRef(w, (FcProto*)rc->ctorProtos->items[i]); }
    pfCount(w, rc->extraConstructors->count);
    for (i = 0; i < rc->extraConstructors->count; i++) pfProtoRef(w, (FcProto*)rc->extraConstructors->items[i]);
    pfProtoRef(w, rc->destructor);
    if (!rc->thisReads) pfUInt(w, 0);
    else {
        pfCount(w, fcDictCount(rc->thisReads) + 1);
        FC_DICT_EACH(rc->thisReads, e) { pfStr(w, (const char*)e->key); pfStrList(w, (FcList*)e->value); }
    }
}
static void pfType(FcPfWriter* w, const FcTypeRef* t) {
    int i;
    if (!t) { pfBool(w, false); return; }
    pfBool(w, true);
    pfStr(w, t->baseName);
    if (t->bitWidth != FC_NULL_INT) pfInt(w, (int64_t)t->bitWidth + 1); else pfInt(w, 0);
    pfInt(w, t->pointerDepth);
    if (!t->lambdaSignature) pfBool(w, false);
    else {
        const FcLambdaSignature* sig = t->lambdaSignature;
        pfBool(w, true);
        pfStr(w, sig->returnTypeName); pfStrList(w, sig->paramTypeNames); pfBool(w, sig->isSelector); pfStr(w, sig->selectorKind); pfBool(w, sig->isExpression);
    }
    pfOptStrList(w, t->namespaces);
    pfStr(w, t->unit);
    pfInt(w, t->arrayRank);
    pfInt(w, t->typeArgCount);
    if (!t->typeArgs) pfUInt(w, 0);
    else { pfCount(w, t->typeArgs->count + 1); for (i = 0; i < t->typeArgs->count; i++) pfType(w, (const FcTypeRef*)t->typeArgs->items[i]); }
    pfU8(w, (t->isUnsigned ? 1 : 0) | (t->nonNull ? 2 : 0));
}
static void pfExtern(FcPfWriter* w, const FcExternSig* sig) {
    int i;
    pfStr(w, sig->libName);
    pfCount(w, sig->paramTypes->count);
    for (i = 0; i < sig->paramTypes->count; i++) pfType(w, (const FcTypeRef*)sig->paramTypes->items[i]);
    pfType(w, sig->returnType);
    pfStr(w, sig->inlineC);
    pfOptStrList(w, sig->paramNames);
    pfInt(w, sig->inlineSource);
    pfInt(w, sig->inlineLine);
    pfStr(w, sig->displayName);
}
static void pfAddClass(FcPfWriter* w, FcRtClass* rc) {
    if (fcDictHas(w->classes, rc)) return;
    fcListAdd(w->c, w->classList, rc);
    fcDictPut(w->classes, rc, FC_IVAL(w->classList->count));   /* index + 1 */
    if (rc->base) pfAddClass(w, rc->base);
}

/* ProgramFile.Write: the bytes of the program into `result` */
static void fcProgramFileWrite(FcCtx* c, const FcLinked* linked, bool lineTables, FcBuf* result) {
    FcPfWriter W;
    FcPfWriter* w = &W;
    FcBuf body = {0}, tables = {0};
    const FcCompiled* p = linked->program;
    bool sources, trace;
    int i, k;
    memset(w, 0, sizeof W);
    w->c = c;
    w->lines = lineTables;
    w->strings = fcDictNew(c, FC_KEY_STR); w->stringList = fcListNew(c);
    w->units = fcDictNew(c, FC_KEY_STR); w->unitList = fcListNew(c);
    w->enums = fcDictNew(c, FC_KEY_STR); w->enumList = fcListNew(c);
    w->protos = fcDictNew(c, FC_KEY_PTR); w->chunks = fcDictNew(c, FC_KEY_PTR); w->open = fcDictNew(c, FC_KEY_PTR);
    w->classes = fcDictNew(c, FC_KEY_PTR); w->classList = fcListNew(c);
    w->out = &body;
    FC_DICT_EACH(p->classes, e) pfAddClass(w, (FcRtClass*)e->value);

    pfU8(w, linked->mode);
    pfU8(w, linked->floatWidth);
    pfU8(w, linked->intWidth);
    pfU8(w, linked->wordWidth);
    pfInt(w, linked->firstUserSource);
    pfCount(w, fcDictCount(linked->nativeImports));
    FC_DICT_EACH(linked->nativeImports, e) pfStr(w, (const char*)e->key);
    pfOptStrList(w, linked->packageNatives);
    pfOptStrList(w, linked->packageNativeLibraries);
    pfOptStrList(w, linked->graphicsNatives);

    pfCount(w, p->customUnits->count);
    for (i = 0; i < p->customUnits->count; i++) {
        FcCustomUnit* u = (FcCustomUnit*)p->customUnits->items[i];
        pfStr(w, u->name); pfStr(w, u->symbol); pfBool(w, u->prefixed);
        pfDims(w, &u->unit);
        pfF64(w, u->unit.scale); pfF64(w, u->unit.offset); pfStr(w, u->unit.converter); pfStr(w, u->unit.unitlessConverter);
    }

    pfCount(w, p->enums->count);
    for (i = 0; i < p->enums->count; i++) {
        FcEnumType* e = (FcEnumType*)p->enums->items[i];
        pfStr(w, e->name); pfBool(w, e->isFlags);
        pfCount(w, e->names->count);
        for (k = 0; k < e->names->count; k++) { pfStr(w, (const char*)e->names->items[k]); pfInt(w, *(int64_t*)e->values->items[k]); }
    }

    pfCount(w, p->resources->count);
    for (i = 0; i < p->resources->count; i++) {
        FcResourceEntry* r = (FcResourceEntry*)p->resources->items[i];
        pfStr(w, r->name); pfCount(w, (int)r->size); fcBufPut(w->out, (const char*)r->data, r->size);
    }

    pfCount(w, p->externNames->count);
    for (i = 0; i < p->externNames->count; i++) { pfStr(w, (const char*)p->externNames->items[i]); pfExtern(w, (const FcExternSig*)p->externSigs->items[i]); }

    pfCount(w, w->classList->count);
    for (i = 0; i < w->classList->count; i++) pfClass(w, (FcRtClass*)w->classList->items[i]);
    pfCount(w, fcDictCount(p->classes));
    FC_DICT_EACH(p->classes, e) { pfStr(w, (const char*)e->key); pfCount(w, FC_VALI(fcDictGet(w->classes, e->value)) - 1); }

    pfChunkRef(w, p->topLevel);
    sources = lineTables && linked->mode == 0 && linked->sourceFiles != NULL;
    if (sources) {
        pfCount(w, linked->sourceFiles->count);
        for (i = 0; i < linked->sourceFiles->count; i++) {
            pfStr(w, (const char*)linked->sourceFiles->items[i]);
            pfStr(w, linked->sourceHashes && i < linked->sourceHashes->count ? (const char*)linked->sourceHashes->items[i] : NULL);
        }
    }
    /* the stack traces (FlagTrace, docs/spec/STACKTRACE.md): the setting and the name of each source in a trace - not with `#stacktrace off` */
    trace = linked->stackTrace != STM_Off;
    if (trace) {
        pfU8(w, linked->stackTrace);
        pfCount(w, linked->traceFiles ? linked->traceFiles->count : 0);
        for (i = 0; linked->traceFiles && i < linked->traceFiles->count; i++) pfStr(w, (const char*)linked->traceFiles->items[i]);
    }

    /* the tables: the units and the enums first (they add names), then the names */
    w->out = &tables;
    pfCount(w, w->unitList->count);
    for (i = 0; i < w->unitList->count; i++) {
        const FcU* u = (const FcU*)w->unitList->items[i];
        pfDims(w, &u->u);
        pfF64(w, u->u.scale); pfStr(w, u->display); pfF64(w, u->u.offset); pfStr(w, u->u.converter); pfStr(w, u->u.unitlessConverter);
    }
    pfCount(w, w->enumList->count);
    for (i = 0; i < w->enumList->count; i++) {
        const FcEnumType* e = (const FcEnumType*)w->enumList->items[i];
        pfStr(w, e->name); pfBool(w, e->isFlags);
        pfCount(w, e->names->count);
        for (k = 0; k < e->names->count; k++) { pfStr(w, (const char*)e->names->items[k]); pfInt(w, *(int64_t*)e->values->items[k]); }
    }

    w->out = result;
    fcBufPut(result, "FBC\x1A", 4);
    pfU8(w, 1);
    pfU8(w, (lineTables ? 1 : 0) | (sources ? 2 : 0) | (trace ? 4 : 0));
    pfCount(w, w->stringList->count);
    for (i = 0; i < w->stringList->count; i++) {
        FcBuf utf8 = {0};
        fcOut8(&utf8, (const char*)w->stringList->items[i]);
        pfCount(w, (int)utf8.len);
        if (utf8.len) fcBufPut(result, utf8.p, utf8.len);
        fcBufFree(&utf8);
    }
    if (tables.len) fcBufPut(result, tables.p, tables.len);
    if (body.len) fcBufPut(result, body.p, body.len);
    fcBufFree(&tables);
    fcBufFree(&body);
}
