/* The devices of the host in another process (docs/spec/VM_C.md, "Das geteilte Gerät"; M1 (3) of docs/ROADMAP_C_PORT.md): spark holds the device manager
 * (its drivers, the sharing of a device between the IDE and a program, the packet trace) and starts a program in `fire` with
 * FIRE_DEVICE_HOST=tcp:127.0.0.1:PORT;token=T. Then the `dev_*` of fire_host (native/abi/fire_pkg_abi.h) - what the library of the package devices asks the host
 * (fire_bridge_devices.h: a device of the host is forwarded to the host's manager) - are calls over that connection instead of the C# delegates the C# VM
 * gives the library (src/fire.Runtime/Host/PackageHost.Devices.cs). Without the variable, or when nobody answers, the `dev_*` stay NULL: the library takes
 * the devices of the platform itself (loopback, serial ports), as before.
 *
 * The protocol (little-endian; one call at a time - a lock - and the answer before the next; src/fire.Device.Manager/DeviceManager/DeviceHostServer.cs):
 *   request  u32 n (the bytes after it), u8 op, i32 a, the payload (n - 5 bytes)
 *   answer   u32 n (the bytes after it), i32 result, the payload (n - 4 bytes)
 *   op 0 hello (payload: the token; result 1 - otherwise the host closes), 1 refresh(a: fast), 2 count, 3 handle_at(a: index), 4 identifier(a: handle; the
 *   text in the payload, the result its length or -1), 5 default, 6 manager_shared, 7 shared(a), 8 availability(a), 9 test_availability(a), 10 connected(a),
 *   11 port_name(a; text), 12 connect(a), 13 disconnect(a), 14 write(a, payload: the bytes), 15 send_command(a, payload: the text), 16 poll(a, payload:
 *   i32 room; the result the length of the next packet or -1, the packet in the payload when it fits - else kept for the next poll)
 * Received data is not pushed: poll asks (the library polls for it anyway). A broken connection makes every call fail (no device, nothing sent). */

enum { VM_DEV_HELLO = 0, VM_DEV_REFRESH, VM_DEV_COUNT, VM_DEV_HANDLE_AT, VM_DEV_IDENTIFIER, VM_DEV_DEFAULT, VM_DEV_MANAGER_SHARED, VM_DEV_SHARED,
       VM_DEV_AVAILABILITY, VM_DEV_TEST_AVAILABILITY, VM_DEV_CONNECTED, VM_DEV_PORT_NAME, VM_DEV_CONNECT, VM_DEV_DISCONNECT, VM_DEV_WRITE,
       VM_DEV_SEND_COMMAND, VM_DEV_POLL };

static dbg_socket g_vmDevSock = DBG_NO_SOCKET;
static dbg_mutex g_vmDevLock;
static unsigned char* g_vmDevAnswer;   /* the payload of the last answer */
static uint32_t g_vmDevAnswerLen, g_vmDevAnswerCap;

static bool vmDevSendAll(const unsigned char* p, size_t n) {
    while (n > 0) {
        int k = dbgSockSend(g_vmDevSock, (const char*)p, n);
        if (k <= 0) return false;
        p += k; n -= (size_t)k;
    }
    return true;
}
static bool vmDevRecvAll(unsigned char* p, size_t n) {
    while (n > 0) {
        int k = dbgSockRecv(g_vmDevSock, (char*)p, n);
        if (k <= 0) return false;
        p += k; n -= (size_t)k;
    }
    return true;
}
static void vmDevPut32(unsigned char* p, uint32_t v) { p[0] = (unsigned char)v; p[1] = (unsigned char)(v >> 8); p[2] = (unsigned char)(v >> 16); p[3] = (unsigned char)(v >> 24); }
static uint32_t vmDevGet32(const unsigned char* p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }

/* One call (with the lock held): the result, the payload of the answer in g_vmDevAnswer. `failed` when the connection is gone. */
static int32_t vmDevCallLocked(int op, int32_t a, const void* payload, uint32_t size, int32_t failed) {
    unsigned char head[9], ahead[8];
    uint32_t n;
    if (g_vmDevSock == DBG_NO_SOCKET) return failed;
    vmDevPut32(head, 5 + size);
    head[4] = (unsigned char)op;
    vmDevPut32(head + 5, (uint32_t)a);
    if (!vmDevSendAll(head, sizeof head) || (size > 0 && !vmDevSendAll((const unsigned char*)payload, size)) || !vmDevRecvAll(ahead, sizeof ahead)) goto broken;
    n = vmDevGet32(ahead);
    if (n < 4 || n > (64u << 20)) goto broken;
    g_vmDevAnswerLen = n - 4;
    if (g_vmDevAnswerLen > g_vmDevAnswerCap) {
        unsigned char* grown = (unsigned char*)realloc(g_vmDevAnswer, g_vmDevAnswerLen);
        if (!grown) goto broken;
        g_vmDevAnswer = grown;
        g_vmDevAnswerCap = g_vmDevAnswerLen;
    }
    if (g_vmDevAnswerLen > 0 && !vmDevRecvAll(g_vmDevAnswer, g_vmDevAnswerLen)) goto broken;
    return (int32_t)vmDevGet32(ahead + 4);
broken:
    dbgCloseSocket(g_vmDevSock);
    g_vmDevSock = DBG_NO_SOCKET;
    g_vmDevAnswerLen = 0;
    return failed;
}
static int32_t vmDevCall(int op, int32_t a, const void* payload, uint32_t size, int32_t failed) {
    int32_t r;
    dbgLock(&g_vmDevLock);
    r = vmDevCallLocked(op, a, payload, size, failed);
    dbgUnlock(&g_vmDevLock);
    return r;
}
/* A text of the host into `buffer` (zero terminated, cut to `size`); its whole length, -1: none. */
static int32_t vmDevText(int op, int32_t handle, char* buffer, int32_t size) {
    int32_t r;
    dbgLock(&g_vmDevLock);
    r = vmDevCallLocked(op, handle, NULL, 0, -1);
    if (buffer != NULL && size > 0) {
        uint32_t n = r < 0 ? 0 : g_vmDevAnswerLen;
        if (n > (uint32_t)size - 1) n = (uint32_t)size - 1;
        if (n > 0) memcpy(buffer, g_vmDevAnswer, n);
        buffer[n] = 0;
    }
    dbgUnlock(&g_vmDevLock);
    return r;
}

static int32_t vmDevRefresh(int32_t fast) { return vmDevCall(VM_DEV_REFRESH, fast, NULL, 0, 0); }
static int32_t vmDevCount(void) { return vmDevCall(VM_DEV_COUNT, 0, NULL, 0, 0); }
static int32_t vmDevHandleAt(int32_t index) { return vmDevCall(VM_DEV_HANDLE_AT, index, NULL, 0, -1); }
static int32_t vmDevIdentifier(int32_t handle, char* buffer, int32_t size) { return vmDevText(VM_DEV_IDENTIFIER, handle, buffer, size); }
static int32_t vmDevDefault(void) { return vmDevCall(VM_DEV_DEFAULT, 0, NULL, 0, -1); }
static int32_t vmDevManagerShared(void) { return vmDevCall(VM_DEV_MANAGER_SHARED, 0, NULL, 0, 0); }
static int32_t vmDevShared(int32_t handle) { return vmDevCall(VM_DEV_SHARED, handle, NULL, 0, 0); }
static int32_t vmDevAvailability(int32_t handle) { return vmDevCall(VM_DEV_AVAILABILITY, handle, NULL, 0, 0); }
static int32_t vmDevTestAvailability(int32_t handle) { return vmDevCall(VM_DEV_TEST_AVAILABILITY, handle, NULL, 0, 0); }
static int32_t vmDevConnected(int32_t handle) { return vmDevCall(VM_DEV_CONNECTED, handle, NULL, 0, 0); }
static int32_t vmDevPortName(int32_t handle, char* buffer, int32_t size) { return vmDevText(VM_DEV_PORT_NAME, handle, buffer, size); }
static int32_t vmDevConnect(int32_t handle) { return vmDevCall(VM_DEV_CONNECT, handle, NULL, 0, 0); }
static void vmDevDisconnect(int32_t handle) { (void)vmDevCall(VM_DEV_DISCONNECT, handle, NULL, 0, 0); }
static int32_t vmDevWrite(int32_t handle, const uint8_t* data, int32_t count) { return vmDevCall(VM_DEV_WRITE, handle, data, count > 0 ? (uint32_t)count : 0, 0); }
static int32_t vmDevSendCommand(int32_t handle, const char* text) { return vmDevCall(VM_DEV_SEND_COMMAND, handle, text, text ? (uint32_t)strlen(text) : 0, 0); }
static int32_t vmDevPoll(int32_t handle, uint8_t* buffer, int32_t size) {
    unsigned char room[4];
    int32_t r;
    vmDevPut32(room, (uint32_t)(size > 0 ? size : 0));
    dbgLock(&g_vmDevLock);
    r = vmDevCallLocked(VM_DEV_POLL, handle, room, 4, -1);
    if (r >= 0 && r <= size && g_vmDevAnswerLen == (uint32_t)r && r > 0) memcpy(buffer, g_vmDevAnswer, (size_t)r);
    dbgUnlock(&g_vmDevLock);
    return r;
}

/* FIRE_DEVICE_HOST=tcp:HOST:PORT;token=T: connects and says hello; then the `dev_*` of `host` go there. Nothing happens without the variable or without an
 * answer (the library then takes the devices of the platform). */
static void vmDevHostInstall(fire_host* host) {
    const char* spec = getenv("FIRE_DEVICE_HOST");
    char addrText[64] = "127.0.0.1", token[128] = "";
    const char *p, *colon, *semi, *tok;
    struct sockaddr_in addr;
    int port, one = 1;
    int32_t ok;
    if (spec == NULL || strncmp(spec, "tcp:", 4) != 0) return;
    p = spec + 4;
    semi = strchr(p, ';');
    colon = strchr(p, ':');
    if (!colon || (semi && colon > semi)) { fprintf(stderr, "fire: FIRE_DEVICE_HOST must be tcp:HOST:PORT;token=T\n"); return; }
    if ((size_t)(colon - p) < sizeof addrText) { memcpy(addrText, p, (size_t)(colon - p)); addrText[colon - p] = 0; }
    port = atoi(colon + 1);
    tok = semi ? strstr(semi, ";token=") : NULL;
    if (tok) {
        size_t n;
        tok += 7;
        n = strcspn(tok, ";");
        if (n >= sizeof token) n = sizeof token - 1;
        memcpy(token, tok, n);
        token[n] = 0;
    }
#if defined(_WIN32)
    { WSADATA wsa; if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return; }
#endif
    g_vmDevSock = socket(AF_INET, SOCK_STREAM, 0);
    memset(&addr, 0, sizeof addr);
    addr.sin_family = AF_INET;
    addr.sin_port = htons((unsigned short)port);
    if (g_vmDevSock == DBG_NO_SOCKET || inet_pton(AF_INET, addrText, &addr.sin_addr) != 1 || connect(g_vmDevSock, (struct sockaddr*)&addr, sizeof addr) != 0) {
        fprintf(stderr, "fire: cannot reach the devices of the host at %s:%d - the program uses the devices of this machine\n", addrText, port);
        if (g_vmDevSock != DBG_NO_SOCKET) dbgCloseSocket(g_vmDevSock);
        g_vmDevSock = DBG_NO_SOCKET;
        return;
    }
    setsockopt(g_vmDevSock, IPPROTO_TCP, TCP_NODELAY, (const char*)&one, sizeof one);
    dbgMutexInit(&g_vmDevLock);
    ok = vmDevCallLocked(VM_DEV_HELLO, 1, token, (uint32_t)strlen(token), 0);
    if (ok != 1) {
        fprintf(stderr, "fire: the host did not take the connection to its devices - the program uses the devices of this machine\n");
        if (g_vmDevSock != DBG_NO_SOCKET) dbgCloseSocket(g_vmDevSock);
        g_vmDevSock = DBG_NO_SOCKET;
        return;
    }
    host->dev_refresh = vmDevRefresh;
    host->dev_count = vmDevCount;
    host->dev_handle_at = vmDevHandleAt;
    host->dev_identifier = vmDevIdentifier;
    host->dev_default = vmDevDefault;
    host->dev_manager_shared = vmDevManagerShared;
    host->dev_shared = vmDevShared;
    host->dev_availability = vmDevAvailability;
    host->dev_test_availability = vmDevTestAvailability;
    host->dev_connected = vmDevConnected;
    host->dev_port_name = vmDevPortName;
    host->dev_connect = vmDevConnect;
    host->dev_disconnect = vmDevDisconnect;
    host->dev_write = vmDevWrite;
    host->dev_send_command = vmDevSendCommand;
    host->dev_poll = vmDevPoll;
}
