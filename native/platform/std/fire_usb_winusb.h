/* fire native platform layer, USB part through WinUSB (Windows; see fire_usb_c.h for the interface). The devices come from SetupAPI (every present device with
 * the interface GUID_DEVINTERFACE_USB_DEVICE); a device can be opened when WinUSB is its driver (a device with Microsoft OS descriptors gets it by itself,
 * others get it with Zadig or an INF). The id of a device is its device path. setupapi.dll and winusb.dll are loaded when first needed, so nothing has to be
 * linked. */
#ifndef FIRE_USB_WINUSB_H
#define FIRE_USB_WINUSB_H

#include "fire_usb_c.h"
#include <windows.h>
#include <stdlib.h>
#include <string.h>

/* the parts of setupapi.h and winusb.h used here */
typedef struct fire_wu_SP_DEVICE_INTERFACE_DATA { DWORD cbSize; GUID InterfaceClassGuid; DWORD Flags; ULONG_PTR Reserved; } fire_wu_SP_DEVICE_INTERFACE_DATA;
typedef struct fire_wu_SP_DEVINFO_DATA { DWORD cbSize; GUID ClassGuid; DWORD DevInst; ULONG_PTR Reserved; } fire_wu_SP_DEVINFO_DATA;
#pragma pack(push, 1)
typedef struct fire_wu_SETUP_PACKET { UCHAR RequestType; UCHAR Request; USHORT Value; USHORT Index; USHORT Length; } fire_wu_SETUP_PACKET;
#pragma pack(pop)
typedef struct fire_wu_INTERFACE_DESCRIPTOR {
    UCHAR bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
} fire_wu_INTERFACE_DESCRIPTOR;
typedef struct fire_wu_PIPE_INFORMATION { int PipeType; UCHAR PipeId; USHORT MaximumPacketSize; UCHAR Interval; } fire_wu_PIPE_INFORMATION;
#pragma pack(push, 1)
typedef struct fire_wu_DEVICE_DESCRIPTOR {
    UCHAR bLength, bDescriptorType; USHORT bcdUSB; UCHAR bDeviceClass, bDeviceSubClass, bDeviceProtocol, bMaxPacketSize0;
    USHORT idVendor, idProduct, bcdDevice; UCHAR iManufacturer, iProduct, iSerialNumber, bNumConfigurations;
} fire_wu_DEVICE_DESCRIPTOR;
#pragma pack(pop)
typedef void* fire_wu_HANDLE;

typedef struct FireUsbWinLib {
    int tried, ok;
    char error[176];
    HMODULE setup, winusb;
    void* (WINAPI* GetClassDevsW)(const GUID*, const wchar_t*, HWND, DWORD);
    BOOL (WINAPI* EnumDeviceInterfaces)(void*, void*, const GUID*, DWORD, fire_wu_SP_DEVICE_INTERFACE_DATA*);
    BOOL (WINAPI* GetDeviceInterfaceDetailW)(void*, fire_wu_SP_DEVICE_INTERFACE_DATA*, void*, DWORD, DWORD*, fire_wu_SP_DEVINFO_DATA*);
    BOOL (WINAPI* GetDeviceRegistryPropertyW)(void*, fire_wu_SP_DEVINFO_DATA*, DWORD, DWORD*, BYTE*, DWORD, DWORD*);
    BOOL (WINAPI* DestroyDeviceInfoList)(void*);
    BOOL (WINAPI* Initialize)(HANDLE, fire_wu_HANDLE*);
    BOOL (WINAPI* Free)(fire_wu_HANDLE);
    BOOL (WINAPI* GetAssociatedInterface)(fire_wu_HANDLE, UCHAR, fire_wu_HANDLE*);
    BOOL (WINAPI* GetDescriptor)(fire_wu_HANDLE, UCHAR, UCHAR, USHORT, PUCHAR, ULONG, PULONG);
    BOOL (WINAPI* QueryInterfaceSettings)(fire_wu_HANDLE, UCHAR, fire_wu_INTERFACE_DESCRIPTOR*);
    BOOL (WINAPI* QueryPipe)(fire_wu_HANDLE, UCHAR, UCHAR, fire_wu_PIPE_INFORMATION*);
    BOOL (WINAPI* SetPipePolicy)(fire_wu_HANDLE, UCHAR, ULONG, ULONG, PVOID);
    BOOL (WINAPI* ReadPipe)(fire_wu_HANDLE, UCHAR, PUCHAR, ULONG, PULONG, LPOVERLAPPED);
    BOOL (WINAPI* WritePipe)(fire_wu_HANDLE, UCHAR, PUCHAR, ULONG, PULONG, LPOVERLAPPED);
    BOOL (WINAPI* ControlTransfer)(fire_wu_HANDLE, fire_wu_SETUP_PACKET, PUCHAR, ULONG, PULONG, LPOVERLAPPED);
    BOOL (WINAPI* ResetPipe)(fire_wu_HANDLE, UCHAR);
    BOOL (WINAPI* SetCurrentAlternateSetting)(fire_wu_HANDLE, UCHAR);
} FireUsbWinLib;

static FireUsbWinLib g_fireUsbWin;
static SRWLOCK g_fireUsbWinLock = SRWLOCK_INIT;

/* Looks up `name` in `m` into the function pointer at `fn` (a pointer to it): 0 when the library does not have it. */
static int fire_usb_wuBind(HMODULE m, const char* name, void* fn) {
    FARPROC p = GetProcAddress(m, name);
    memcpy(fn, &p, sizeof p);
    return p != NULL;
}

static int fire_usb_wuLoad(void) {
    FireUsbWinLib* l = &g_fireUsbWin;
    if (l->tried) return l->ok;
    l->tried = 1;
    l->setup = LoadLibraryA("setupapi.dll");
    l->winusb = LoadLibraryA("winusb.dll");
    if (!l->setup || !l->winusb) { snprintf(l->error, sizeof l->error, "WinUSB is not available on this Windows."); return 0; }
    l->ok = fire_usb_wuBind(l->setup, "SetupDiGetClassDevsW", &l->GetClassDevsW) && fire_usb_wuBind(l->setup, "SetupDiEnumDeviceInterfaces", &l->EnumDeviceInterfaces)
        && fire_usb_wuBind(l->setup, "SetupDiGetDeviceInterfaceDetailW", &l->GetDeviceInterfaceDetailW)
        && fire_usb_wuBind(l->setup, "SetupDiGetDeviceRegistryPropertyW", &l->GetDeviceRegistryPropertyW)
        && fire_usb_wuBind(l->setup, "SetupDiDestroyDeviceInfoList", &l->DestroyDeviceInfoList)
        && fire_usb_wuBind(l->winusb, "WinUsb_Initialize", &l->Initialize) && fire_usb_wuBind(l->winusb, "WinUsb_Free", &l->Free)
        && fire_usb_wuBind(l->winusb, "WinUsb_GetAssociatedInterface", &l->GetAssociatedInterface)
        && fire_usb_wuBind(l->winusb, "WinUsb_GetDescriptor", &l->GetDescriptor) && fire_usb_wuBind(l->winusb, "WinUsb_QueryInterfaceSettings", &l->QueryInterfaceSettings)
        && fire_usb_wuBind(l->winusb, "WinUsb_QueryPipe", &l->QueryPipe) && fire_usb_wuBind(l->winusb, "WinUsb_SetPipePolicy", &l->SetPipePolicy)
        && fire_usb_wuBind(l->winusb, "WinUsb_ReadPipe", &l->ReadPipe) && fire_usb_wuBind(l->winusb, "WinUsb_WritePipe", &l->WritePipe)
        && fire_usb_wuBind(l->winusb, "WinUsb_ControlTransfer", &l->ControlTransfer) && fire_usb_wuBind(l->winusb, "WinUsb_ResetPipe", &l->ResetPipe)
        && fire_usb_wuBind(l->winusb, "WinUsb_SetCurrentAlternateSetting", &l->SetCurrentAlternateSetting);
    if (!l->ok) snprintf(l->error, sizeof l->error, "WinUSB is not complete on this Windows.");
    return l->ok;
}

/* GUID_DEVINTERFACE_USB_DEVICE */
static const GUID g_fireUsbDeviceGuid = {0xA5DCBF10, 0x6530, 0x11D2, {0x90, 0x1F, 0x00, 0xC0, 0x4F, 0xB9, 0x51, 0xED}};

/* A wide text as ASCII (malloc; '?' for what is not ASCII); NULL when out of memory. */
static char* fire_usb_wuNarrow(const wchar_t* w) {
    size_t n = 0, i;
    char* out;
    while (w && w[n]) n++;
    out = (char*)malloc(n + 1);
    if (out == NULL) return NULL;
    for (i = 0; i < n; i++) out[i] = w[i] < 128 ? (char)w[i] : '?';
    out[n] = 0;
    return out;
}
static void fire_usb_wuNarrowInto(const wchar_t* w, char* out, size_t size) {
    size_t n = 0;
    for (; w && *w && n + 1 < size; w++) out[n++] = *w < 128 ? (char)*w : '?';
    out[n] = 0;
}
/* A text as a wide one, byte for byte (malloc); NULL when out of memory. */
static wchar_t* fire_usb_wuWiden(const char* s) {
    size_t n = strlen(s), i;
    wchar_t* out = (wchar_t*)malloc((n + 1) * sizeof(wchar_t));
    if (out == NULL) return NULL;
    for (i = 0; i < n; i++) out[i] = (wchar_t)(unsigned char)s[i];
    out[n] = 0;
    return out;
}
static int fire_usb_wuHexAfter(const char* path, const char* key) {
    size_t n = strlen(path), i;
    char* lower = (char*)malloc(n + 1);
    const char* at;
    int value = 0;
    if (lower == NULL) return 0;
    for (i = 0; i <= n; i++) lower[i] = (char)(path[i] >= 'A' && path[i] <= 'Z' ? path[i] + 32 : path[i]);
    at = strstr(lower, key);
    if (at != NULL) value = (int)strtol(at + strlen(key), NULL, 16);
    free(lower);
    return value;
}

/* The status of the last Windows error: always 0. */
static int fire_usb_wuLastError(const char* what, FireUsbStatus* st) {
    DWORD e = GetLastError();
    switch (e) {
        case 121: return fire_usb_fail(st, FIRE_USB_E_TIMEOUT, "%s: timed out.", what);                                   /* ERROR_SEM_TIMEOUT */
        case 31: return fire_usb_fail(st, FIRE_USB_E_STALL, "%s: the device did not accept it (stall).", what);         /* ERROR_GEN_FAILURE */
        case 5: return fire_usb_fail(st, FIRE_USB_E_BUSY, "%s: access denied (another program holds it, or WinUSB is not its driver).", what);
        case 2: case 1167: return fire_usb_fail(st, FIRE_USB_E_DISCONNECTED, "%s: the device is gone.", what);            /* ERROR_FILE_NOT_FOUND, ERROR_DEVICE_NOT_CONNECTED */
        case 50: return fire_usb_fail(st, FIRE_USB_E_UNSUPPORTED, "%s: not supported.", what);                            /* ERROR_NOT_SUPPORTED */
        case 87: return fire_usb_fail(st, FIRE_USB_E_INVALID_ARGUMENT, "%s: invalid argument.", what);                     /* ERROR_INVALID_PARAMETER */
        case 234: return fire_usb_fail(st, FIRE_USB_E_OVERFLOW, "%s: the device sent more than was asked for.", what);    /* ERROR_MORE_DATA */
        default: return fire_usb_fail(st, FIRE_USB_E_OTHER, "%s: Windows error %lu.", what, (unsigned long)e);
    }
}

typedef struct FireUsbDevice {
    HANDLE file;
    fire_wu_HANDLE first;
    fire_wu_HANDLE* associated;   /* the other interfaces (index 0 = interface 1) */
    size_t associatedCount, associatedCap;
} FireUsbDevice;

static fire_wu_HANDLE fire_usb_wuForInterface(FireUsbDevice* d, int itf) {
    if (itf <= 0) return d->first;
    while ((int)d->associatedCount < itf) {
        fire_wu_HANDLE h = NULL;
        if (!g_fireUsbWin.GetAssociatedInterface(d->first, (UCHAR)d->associatedCount, &h)) return NULL;
        if (d->associatedCount == d->associatedCap) {
            size_t cap = d->associatedCap ? d->associatedCap * 2 : 4;
            fire_wu_HANDLE* grown = (fire_wu_HANDLE*)realloc(d->associated, sizeof(fire_wu_HANDLE) * cap);
            if (grown == NULL) { g_fireUsbWin.Free(h); return NULL; }
            d->associated = grown;
            d->associatedCap = cap;
        }
        d->associated[d->associatedCount++] = h;
    }
    return d->associated[(size_t)itf - 1];
}
/* the handle of the interface an endpoint belongs to */
static fire_wu_HANDLE fire_usb_wuForEndpoint(FireUsbDevice* d, int endpoint) {
    int itf;
    for (itf = 0; itf < 32; itf++) {
        fire_wu_HANDLE h = fire_usb_wuForInterface(d, itf);
        fire_wu_INTERFACE_DESCRIPTOR desc;
        UCHAR p;
        if (!h) break;
        memset(&desc, 0, sizeof desc);
        if (!g_fireUsbWin.QueryInterfaceSettings(h, 0, &desc)) continue;
        for (p = 0; p < desc.bNumEndpoints; p++) {
            fire_wu_PIPE_INFORMATION pipe;
            memset(&pipe, 0, sizeof pipe);
            if (g_fireUsbWin.QueryPipe(h, 0, p, &pipe) && pipe.PipeId == endpoint) return h;
        }
    }
    return d->first;
}

static int fire_usb_supported(void) {
    int r;
    AcquireSRWLockExclusive(&g_fireUsbWinLock);
    r = fire_usb_wuLoad();
    ReleaseSRWLockExclusive(&g_fireUsbWinLock);
    return r;
}

static int fire_usb_devices(FireUsbDeviceList* out, FireUsbStatus* st) {
    FireUsbWinLib* l = &g_fireUsbWin;
    void* set;
    DWORD i;
    out->count = 0;
    AcquireSRWLockExclusive(&g_fireUsbWinLock);
    if (!fire_usb_wuLoad()) { ReleaseSRWLockExclusive(&g_fireUsbWinLock); return fire_usb_success(st); }
    set = l->GetClassDevsW(&g_fireUsbDeviceGuid, NULL, NULL, 0x00000002 | 0x00000010);   /* DIGCF_PRESENT | DIGCF_DEVICEINTERFACE */
    if (set == INVALID_HANDLE_VALUE) {
        int r = fire_usb_wuLastError("Listing the USB devices", st);
        ReleaseSRWLockExclusive(&g_fireUsbWinLock);
        return r;
    }
    for (i = 0;; i++) {
        fire_wu_SP_DEVICE_INTERFACE_DATA itf;
        fire_wu_SP_DEVINFO_DATA info;
        FireUsbDeviceInfo* d;
        DWORD size = 0;
        BYTE* detail;
        char* path;
        const char *a, *b, *c, *amp;
        wchar_t text[256];
        memset(&itf, 0, sizeof itf);
        itf.cbSize = sizeof itf;
        if (!l->EnumDeviceInterfaces(set, NULL, &g_fireUsbDeviceGuid, i, &itf)) break;
        l->GetDeviceInterfaceDetailW(set, &itf, NULL, 0, &size, NULL);
        if (size == 0) continue;
        detail = (BYTE*)calloc(1, size + sizeof(wchar_t));
        if (detail == NULL) break;
        *(DWORD*)detail = sizeof(void*) == 8 ? 8 : 6;   /* cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W */
        memset(&info, 0, sizeof info);
        info.cbSize = sizeof info;
        if (!l->GetDeviceInterfaceDetailW(set, &itf, detail, size, NULL, &info)) { free(detail); continue; }
        path = fire_usb_wuNarrow((const wchar_t*)(detail + sizeof(DWORD)));
        free(detail);
        if (path == NULL) break;
        d = fire_usb_deviceListAdd(out);
        if (d == NULL) { free(path); break; }
        snprintf(d->id, sizeof d->id, "%s", path);
        d->vendorId = fire_usb_wuHexAfter(path, "vid_");
        d->productId = fire_usb_wuHexAfter(path, "pid_");
        if (l->GetDeviceRegistryPropertyW(set, &info, 0x0000000B, NULL, (BYTE*)text, sizeof text, NULL)) fire_usb_wuNarrowInto(text, d->manufacturer, sizeof d->manufacturer);   /* SPDRP_MFG */
        if (l->GetDeviceRegistryPropertyW(set, &info, 0x00000000, NULL, (BYTE*)text, sizeof text, NULL)) fire_usb_wuNarrowInto(text, d->product, sizeof d->product);             /* SPDRP_DEVICEDESC */
        /* the serial number is the part of the path between the second and the third '#' */
        a = strchr(path, '#');
        b = a ? strchr(a + 1, '#') : NULL;
        c = b ? strchr(b + 1, '#') : NULL;
        amp = b ? strchr(b + 1, '&') : NULL;
        if (c != NULL && (amp == NULL || amp > c)) snprintf(d->serial, sizeof d->serial, "%.*s", (int)(c - b - 1), b + 1);
        free(path);
    }
    l->DestroyDeviceInfoList(set);
    ReleaseSRWLockExclusive(&g_fireUsbWinLock);
    return fire_usb_success(st);
}

static int fire_usb_openDevice(const char* id, FireUsbDevice** out, FireUsbStatus* st) {
    FireUsbWinLib* l = &g_fireUsbWin;
    HANDLE file;
    fire_wu_HANDLE h = NULL;
    wchar_t* wide;
    *out = NULL;
    AcquireSRWLockExclusive(&g_fireUsbWinLock);
    if (!fire_usb_wuLoad()) { ReleaseSRWLockExclusive(&g_fireUsbWinLock); return fire_usb_fail(st, FIRE_USB_E_UNSUPPORTED, "%s", l->error); }
    wide = fire_usb_wuWiden(id);
    if (wide == NULL) { ReleaseSRWLockExclusive(&g_fireUsbWinLock); return fire_usb_fail(st, FIRE_USB_E_OTHER, "Out of memory."); }
    file = CreateFileW(wide, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, NULL);
    free(wide);
    if (file == INVALID_HANDLE_VALUE) {
        int r;
        if (GetLastError() == 2 || GetLastError() == 3) r = fire_usb_fail(st, FIRE_USB_E_NOT_FOUND, "There is no USB device %s.", id);
        else r = fire_usb_wuLastError("Opening the USB device", st);
        ReleaseSRWLockExclusive(&g_fireUsbWinLock);
        return r;
    }
    if (!l->Initialize(file, &h)) {
        fire_usb_wuLastError("Opening the USB device with WinUSB (is WinUSB its driver?)", st);
        CloseHandle(file);
        ReleaseSRWLockExclusive(&g_fireUsbWinLock);
        return 0;
    }
    *out = (FireUsbDevice*)calloc(1, sizeof(FireUsbDevice));
    if (*out == NULL) {
        l->Free(h);
        CloseHandle(file);
        ReleaseSRWLockExclusive(&g_fireUsbWinLock);
        return fire_usb_fail(st, FIRE_USB_E_OTHER, "Out of memory.");
    }
    (*out)->file = file;
    (*out)->first = h;
    ReleaseSRWLockExclusive(&g_fireUsbWinLock);
    return fire_usb_success(st);
}

static int fire_usb_endpoints(FireUsbDevice* d, FireUsbEndpointList* out, FireUsbStatus* st) {
    int itf;
    out->count = 0;
    for (itf = 0; itf < 32; itf++) {
        fire_wu_HANDLE h = fire_usb_wuForInterface(d, itf);
        UCHAR alt;
        if (!h) break;
        for (alt = 0; alt < 16; alt++) {
            fire_wu_INTERFACE_DESCRIPTOR desc;
            UCHAR p;
            memset(&desc, 0, sizeof desc);
            if (!g_fireUsbWin.QueryInterfaceSettings(h, alt, &desc)) break;
            for (p = 0; p < desc.bNumEndpoints; p++) {
                fire_wu_PIPE_INFORMATION pipe;
                FireUsbEndpointInfo e;
                memset(&pipe, 0, sizeof pipe);
                if (!g_fireUsbWin.QueryPipe(h, alt, p, &pipe)) continue;
                e.interfaceNumber = desc.bInterfaceNumber;
                e.alternate = desc.bAlternateSetting;
                e.address = pipe.PipeId;
                e.type = pipe.PipeType;
                e.maxPacket = pipe.MaximumPacketSize & 0x7FF;
                e.interfaceClass = desc.bInterfaceClass;
                fire_usb_endpointListAdd(out, &e);
            }
        }
    }
    return fire_usb_success(st);
}

/* WinUSB claims every interface when it opens the device: claim only checks that the interface is there */
static int fire_usb_claim(FireUsbDevice* d, int itf, FireUsbStatus* st) {
    return fire_usb_wuForInterface(d, itf) ? fire_usb_success(st) : fire_usb_fail(st, FIRE_USB_E_NOT_FOUND, "The device has no interface %d.", itf);
}
static int fire_usb_release(FireUsbDevice* d, int itf, FireUsbStatus* st) {
    return fire_usb_wuForInterface(d, itf) ? fire_usb_success(st) : fire_usb_fail(st, FIRE_USB_E_NOT_FOUND, "The device has no interface %d.", itf);
}
static int fire_usb_setAlternate(FireUsbDevice* d, int itf, int alt, FireUsbStatus* st) {
    fire_wu_HANDLE h = fire_usb_wuForInterface(d, itf);
    if (!h) return fire_usb_fail(st, FIRE_USB_E_NOT_FOUND, "The device has no interface %d.", itf);
    return g_fireUsbWin.SetCurrentAlternateSetting(h, (UCHAR)alt) ? fire_usb_success(st) : fire_usb_wuLastError("Setting the alternate setting", st);
}
static void fire_usb_wuTimeoutOf(fire_wu_HANDLE h, UCHAR pipe, int timeout) {
    ULONG ms = (ULONG)(timeout < 0 ? 0 : timeout);
    g_fireUsbWin.SetPipePolicy(h, pipe, 0x03, sizeof ms, &ms);   /* PIPE_TRANSFER_TIMEOUT */
}
static int fire_usb_control(FireUsbDevice* d, int requestType, int request, int value, int index, uint8_t* data, int length, int timeout, int* transferred,
                            FireUsbStatus* st) {
    fire_wu_SETUP_PACKET setup;
    ULONG done = 0;
    *transferred = 0;
    fire_usb_wuTimeoutOf(d->first, 0, timeout);
    setup.RequestType = (UCHAR)requestType;
    setup.Request = (UCHAR)request;
    setup.Value = (USHORT)value;
    setup.Index = (USHORT)index;
    setup.Length = (USHORT)length;
    if (!g_fireUsbWin.ControlTransfer(d->first, setup, data, (ULONG)length, &done, NULL)) return fire_usb_wuLastError("The control transfer", st);
    *transferred = (int)done;
    return fire_usb_success(st);
}
static int fire_usb_transfer(FireUsbDevice* d, int type, int endpoint, uint8_t* data, int length, int timeout, int* transferred, FireUsbStatus* st) {
    fire_wu_HANDLE h;
    ULONG done = 0;
    BOOL ok;
    (void)type;
    *transferred = 0;
    h = fire_usb_wuForEndpoint(d, endpoint);
    fire_usb_wuTimeoutOf(h, (UCHAR)endpoint, timeout);
    ok = (endpoint & 0x80) ? g_fireUsbWin.ReadPipe(h, (UCHAR)endpoint, data, (ULONG)length, &done, NULL)
                           : g_fireUsbWin.WritePipe(h, (UCHAR)endpoint, data, (ULONG)length, &done, NULL);
    *transferred = (int)done;
    if (!ok) {
        char what[64];
        DWORD e = GetLastError();   /* (the error of the transfer, not one of making the text) */
        snprintf(what, sizeof what, "%s%d", endpoint & 0x80 ? "Reading from endpoint " : "Writing to endpoint ", endpoint & 0x7F);
        SetLastError(e);
        return fire_usb_wuLastError(what, st);
    }
    return fire_usb_success(st);
}
static int fire_usb_clearHalt(FireUsbDevice* d, int endpoint, FireUsbStatus* st) {
    return g_fireUsbWin.ResetPipe(fire_usb_wuForEndpoint(d, endpoint), (UCHAR)endpoint) ? fire_usb_success(st) : fire_usb_wuLastError("Clearing the halt", st);
}
static void fire_usb_closeDevice(FireUsbDevice* d) {
    size_t i;
    if (!d) return;
    for (i = 0; i < d->associatedCount; i++) if (d->associated[i]) g_fireUsbWin.Free(d->associated[i]);
    free(d->associated);
    if (d->first) g_fireUsbWin.Free(d->first);
    if (d->file != INVALID_HANDLE_VALUE && d->file != NULL) CloseHandle(d->file);
    free(d);
}

#endif
