/* native/forge/serve_nav.c - hover, signature, definition, references, outline and folding of `forge serve` (NavigationEngine.cs). */

static void svNavRequest(SvServer* S, SvDoc* d, const char* method, int offset, FcBuf* b) {
    (void)S; (void)d; (void)method; (void)offset;
    fcBufStr(b, "null");
}
static void svPreludeSourceResult(SvServer* S, const char* name, FcBuf* b) { (void)S; (void)name; fcBufStr(b, "null"); }
static void svMarkupCompletion(SvServer* S, SvDoc* d, int offset, FcBuf* b) { (void)S; (void)d; (void)offset; fcBufStr(b, "{\"replaceStart\":0,\"items\":[]}"); }
