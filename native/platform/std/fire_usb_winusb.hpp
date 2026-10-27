// fire native platform layer, USB part through WinUSB (Windows; see fire_usb_common.hpp for the interface). The devices come from SetupAPI (every present device with the interface
// GUID_DEVINTERFACE_USB_DEVICE); a device can be opened when WinUSB is its driver (a device with Microsoft OS descriptors gets it by itself, others get it with Zadig or an INF).
// The id of a device is its device path. setupapi.dll and winusb.dll are loaded when first needed, so nothing has to be linked.
#pragma once

#include "fire_usb_common.hpp"
#include <windows.h>
#include <cstdlib>
#include <cstring>
#include <mutex>

namespace fire {
namespace plat {
namespace usb {

// the parts of setupapi.h and winusb.h used here
struct wu_SP_DEVICE_INTERFACE_DATA { DWORD cbSize; GUID InterfaceClassGuid; DWORD Flags; ULONG_PTR Reserved; };
struct wu_SP_DEVINFO_DATA { DWORD cbSize; GUID ClassGuid; DWORD DevInst; ULONG_PTR Reserved; };
#pragma pack(push, 1)
struct wu_SETUP_PACKET { UCHAR RequestType; UCHAR Request; USHORT Value; USHORT Index; USHORT Length; };
#pragma pack(pop)
struct wu_INTERFACE_DESCRIPTOR { UCHAR bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface; };
struct wu_PIPE_INFORMATION { int PipeType; UCHAR PipeId; USHORT MaximumPacketSize; UCHAR Interval; };
#pragma pack(push, 1)
struct wu_DEVICE_DESCRIPTOR {
    UCHAR bLength, bDescriptorType; USHORT bcdUSB; UCHAR bDeviceClass, bDeviceSubClass, bDeviceProtocol, bMaxPacketSize0;
    USHORT idVendor, idProduct, bcdDevice; UCHAR iManufacturer, iProduct, iSerialNumber, bNumConfigurations;
};
#pragma pack(pop)
typedef void* wu_HANDLE;

struct WinLib {
    bool tried = false, ok = false;
    std::string error;
    HMODULE setup = nullptr, winusb = nullptr;
    void* (WINAPI* GetClassDevsW)(const GUID*, const wchar_t*, HWND, DWORD) = nullptr;
    BOOL (WINAPI* EnumDeviceInterfaces)(void*, void*, const GUID*, DWORD, wu_SP_DEVICE_INTERFACE_DATA*) = nullptr;
    BOOL (WINAPI* GetDeviceInterfaceDetailW)(void*, wu_SP_DEVICE_INTERFACE_DATA*, void*, DWORD, DWORD*, wu_SP_DEVINFO_DATA*) = nullptr;
    BOOL (WINAPI* GetDeviceRegistryPropertyW)(void*, wu_SP_DEVINFO_DATA*, DWORD, DWORD*, BYTE*, DWORD, DWORD*) = nullptr;
    BOOL (WINAPI* DestroyDeviceInfoList)(void*) = nullptr;
    BOOL (WINAPI* Initialize)(HANDLE, wu_HANDLE*) = nullptr;
    BOOL (WINAPI* Free)(wu_HANDLE) = nullptr;
    BOOL (WINAPI* GetAssociatedInterface)(wu_HANDLE, UCHAR, wu_HANDLE*) = nullptr;
    BOOL (WINAPI* GetDescriptor)(wu_HANDLE, UCHAR, UCHAR, USHORT, PUCHAR, ULONG, PULONG) = nullptr;
    BOOL (WINAPI* QueryInterfaceSettings)(wu_HANDLE, UCHAR, wu_INTERFACE_DESCRIPTOR*) = nullptr;
    BOOL (WINAPI* QueryPipe)(wu_HANDLE, UCHAR, UCHAR, wu_PIPE_INFORMATION*) = nullptr;
    BOOL (WINAPI* SetPipePolicy)(wu_HANDLE, UCHAR, ULONG, ULONG, PVOID) = nullptr;
    BOOL (WINAPI* ReadPipe)(wu_HANDLE, UCHAR, PUCHAR, ULONG, PULONG, LPOVERLAPPED) = nullptr;
    BOOL (WINAPI* WritePipe)(wu_HANDLE, UCHAR, PUCHAR, ULONG, PULONG, LPOVERLAPPED) = nullptr;
    BOOL (WINAPI* ControlTransfer)(wu_HANDLE, wu_SETUP_PACKET, PUCHAR, ULONG, PULONG, LPOVERLAPPED) = nullptr;
    BOOL (WINAPI* ResetPipe)(wu_HANDLE, UCHAR) = nullptr;
    BOOL (WINAPI* SetCurrentAlternateSetting)(wu_HANDLE, UCHAR) = nullptr;

    template <typename F> static bool bind(HMODULE m, const char* name, F& fn) { fn = reinterpret_cast<F>(reinterpret_cast<void*>(GetProcAddress(m, name))); return fn != nullptr; }

    bool load() {
        if (tried) return ok;
        tried = true;
        setup = LoadLibraryA("setupapi.dll");
        winusb = LoadLibraryA("winusb.dll");
        if (!setup || !winusb) { error = "WinUSB is not available on this Windows."; return false; }
        ok = bind(setup, "SetupDiGetClassDevsW", GetClassDevsW) && bind(setup, "SetupDiEnumDeviceInterfaces", EnumDeviceInterfaces)
            && bind(setup, "SetupDiGetDeviceInterfaceDetailW", GetDeviceInterfaceDetailW) && bind(setup, "SetupDiGetDeviceRegistryPropertyW", GetDeviceRegistryPropertyW)
            && bind(setup, "SetupDiDestroyDeviceInfoList", DestroyDeviceInfoList)
            && bind(winusb, "WinUsb_Initialize", Initialize) && bind(winusb, "WinUsb_Free", Free) && bind(winusb, "WinUsb_GetAssociatedInterface", GetAssociatedInterface)
            && bind(winusb, "WinUsb_GetDescriptor", GetDescriptor) && bind(winusb, "WinUsb_QueryInterfaceSettings", QueryInterfaceSettings)
            && bind(winusb, "WinUsb_QueryPipe", QueryPipe) && bind(winusb, "WinUsb_SetPipePolicy", SetPipePolicy) && bind(winusb, "WinUsb_ReadPipe", ReadPipe)
            && bind(winusb, "WinUsb_WritePipe", WritePipe) && bind(winusb, "WinUsb_ControlTransfer", ControlTransfer) && bind(winusb, "WinUsb_ResetPipe", ResetPipe)
            && bind(winusb, "WinUsb_SetCurrentAlternateSetting", SetCurrentAlternateSetting);
        if (!ok) error = "WinUSB is not complete on this Windows.";
        return ok;
    }
};
inline WinLib& winlib() { static WinLib l; return l; }
inline std::mutex& winlock() { static std::mutex m; return m; }

// GUID_DEVINTERFACE_USB_DEVICE
inline const GUID& usbDeviceGuid() { static const GUID g = {0xA5DCBF10, 0x6530, 0x11D2, {0x90, 0x1F, 0x00, 0xC0, 0x4F, 0xB9, 0x51, 0xED}}; return g; }

inline std::string narrow(const wchar_t* w) {
    std::string out;
    for (; w && *w; w++) out.push_back(*w < 128 ? (char)*w : '?');
    return out;
}
inline std::wstring widen(const std::string& s) {
    std::wstring out;
    for (char c : s) out.push_back((wchar_t)(unsigned char)c);
    return out;
}
inline int hexAfter(const std::string& path, const char* key) {
    std::string lower;
    for (char c : path) lower.push_back((char)(c >= 'A' && c <= 'Z' ? c + 32 : c));
    size_t at = lower.find(key);
    if (at == std::string::npos) return 0;
    return (int)std::strtol(lower.c_str() + at + std::strlen(key), nullptr, 16);
}

inline Status lastError(const std::string& what) {
    DWORD e = GetLastError();
    switch (e) {
        case 121: return fail(E_Timeout, what + ": timed out.");                                    // ERROR_SEM_TIMEOUT
        case 31: return fail(E_Stall, what + ": the device did not accept it (stall).");          // ERROR_GEN_FAILURE
        case 5: return fail(E_Busy, what + ": access denied (another program holds it, or WinUSB is not its driver).");
        case 2: case 1167: return fail(E_Disconnected, what + ": the device is gone.");             // ERROR_FILE_NOT_FOUND, ERROR_DEVICE_NOT_CONNECTED
        case 50: return fail(E_Unsupported, what + ": not supported.");                             // ERROR_NOT_SUPPORTED
        case 87: return fail(E_InvalidArgument, what + ": invalid argument.");                      // ERROR_INVALID_PARAMETER
        case 234: return fail(E_Overflow, what + ": the device sent more than was asked for.");     // ERROR_MORE_DATA
        default: return fail(E_Other, what + ": Windows error " + std::to_string((unsigned long)e) + ".");
    }
}

struct Device {
    HANDLE file = INVALID_HANDLE_VALUE;
    wu_HANDLE first = nullptr;
    std::vector<wu_HANDLE> associated;   // the other interfaces (index 0 = interface 1)
    wu_HANDLE forInterface(int itf) {
        if (itf <= 0) return first;
        while ((int)associated.size() < itf) {
            wu_HANDLE h = nullptr;
            if (!winlib().GetAssociatedInterface(first, (UCHAR)associated.size(), &h)) return nullptr;
            associated.push_back(h);
        }
        return associated[(size_t)itf - 1];
    }
    // the handle of the interface an endpoint belongs to
    wu_HANDLE forEndpoint(int endpoint) {
        for (int itf = 0; itf < 32; itf++) {
            wu_HANDLE h = forInterface(itf);
            if (!h) break;
            wu_INTERFACE_DESCRIPTOR desc{};
            if (!winlib().QueryInterfaceSettings(h, 0, &desc)) continue;
            for (UCHAR p = 0; p < desc.bNumEndpoints; p++) {
                wu_PIPE_INFORMATION pipe{};
                if (winlib().QueryPipe(h, 0, p, &pipe) && pipe.PipeId == endpoint) return h;
            }
        }
        return first;
    }
};

inline bool supported() {
    std::lock_guard<std::mutex> g(winlock());
    return winlib().load();
}

inline Status devices(std::vector<DeviceInfo>& out) {
    out.clear();
    std::lock_guard<std::mutex> g(winlock());
    WinLib& l = winlib();
    if (!l.load()) return success();
    void* set = l.GetClassDevsW(&usbDeviceGuid(), nullptr, nullptr, 0x00000002 | 0x00000010);   // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
    if (set == INVALID_HANDLE_VALUE) return lastError("Listing the USB devices");
    for (DWORD i = 0;; i++) {
        wu_SP_DEVICE_INTERFACE_DATA itf{};
        itf.cbSize = sizeof itf;
        if (!l.EnumDeviceInterfaces(set, nullptr, &usbDeviceGuid(), i, &itf)) break;
        DWORD size = 0;
        l.GetDeviceInterfaceDetailW(set, &itf, nullptr, 0, &size, nullptr);
        if (size == 0) continue;
        std::vector<BYTE> detail(size);
        *reinterpret_cast<DWORD*>(detail.data()) = sizeof(void*) == 8 ? 8 : 6;   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
        wu_SP_DEVINFO_DATA info{};
        info.cbSize = sizeof info;
        if (!l.GetDeviceInterfaceDetailW(set, &itf, detail.data(), size, nullptr, &info)) continue;
        std::string path = narrow(reinterpret_cast<const wchar_t*>(detail.data() + sizeof(DWORD)));
        DeviceInfo d;
        d.id = path;
        d.vendorId = hexAfter(path, "vid_");
        d.productId = hexAfter(path, "pid_");
        wchar_t text[256];
        if (l.GetDeviceRegistryPropertyW(set, &info, 0x0000000B, nullptr, reinterpret_cast<BYTE*>(text), sizeof text, nullptr)) d.manufacturer = narrow(text);   // SPDRP_MFG
        if (l.GetDeviceRegistryPropertyW(set, &info, 0x00000000, nullptr, reinterpret_cast<BYTE*>(text), sizeof text, nullptr)) d.product = narrow(text);        // SPDRP_DEVICEDESC
        // the serial number is the part of the path between the second and the third '#'
        size_t a = path.find('#'), b = a == std::string::npos ? a : path.find('#', a + 1), c = b == std::string::npos ? b : path.find('#', b + 1);
        if (c != std::string::npos && path.find('&', b + 1) > c) d.serial = path.substr(b + 1, c - b - 1);
        out.push_back(d);
    }
    l.DestroyDeviceInfoList(set);
    return success();
}

inline Status openDevice(const std::string& id, Device*& out) {
    out = nullptr;
    std::lock_guard<std::mutex> g(winlock());
    WinLib& l = winlib();
    if (!l.load()) return fail(E_Unsupported, l.error);
    HANDLE file = CreateFileW(widen(id).c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                              FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        if (GetLastError() == 2 || GetLastError() == 3) return fail(E_NotFound, "There is no USB device " + id + ".");
        return lastError("Opening the USB device");
    }
    wu_HANDLE h = nullptr;
    if (!l.Initialize(file, &h)) {
        Status s = lastError("Opening the USB device with WinUSB (is WinUSB its driver?)");
        CloseHandle(file);
        return s;
    }
    out = new Device();
    out->file = file;
    out->first = h;
    return success();
}

inline Status endpoints(Device* d, std::vector<EndpointInfo>& out) {
    out.clear();
    for (int itf = 0; itf < 32; itf++) {
        wu_HANDLE h = d->forInterface(itf);
        if (!h) break;
        for (UCHAR alt = 0; alt < 16; alt++) {
            wu_INTERFACE_DESCRIPTOR desc{};
            if (!winlib().QueryInterfaceSettings(h, alt, &desc)) break;
            for (UCHAR p = 0; p < desc.bNumEndpoints; p++) {
                wu_PIPE_INFORMATION pipe{};
                if (!winlib().QueryPipe(h, alt, p, &pipe)) continue;
                EndpointInfo e;
                e.interfaceNumber = desc.bInterfaceNumber;
                e.alternate = desc.bAlternateSetting;
                e.address = pipe.PipeId;
                e.type = pipe.PipeType;
                e.maxPacket = pipe.MaximumPacketSize & 0x7FF;
                e.interfaceClass = desc.bInterfaceClass;
                out.push_back(e);
            }
        }
    }
    return success();
}

// WinUSB claims every interface when it opens the device: claim only checks that the interface is there
inline Status claim(Device* d, int itf) { return d->forInterface(itf) ? success() : fail(E_NotFound, "The device has no interface " + std::to_string(itf) + "."); }
inline Status release(Device* d, int itf) { return d->forInterface(itf) ? success() : fail(E_NotFound, "The device has no interface " + std::to_string(itf) + "."); }
inline Status setAlternate(Device* d, int itf, int alt) {
    wu_HANDLE h = d->forInterface(itf);
    if (!h) return fail(E_NotFound, "The device has no interface " + std::to_string(itf) + ".");
    return winlib().SetCurrentAlternateSetting(h, (UCHAR)alt) ? success() : lastError("Setting the alternate setting");
}
inline void timeoutOf(wu_HANDLE h, UCHAR pipe, int timeout) {
    ULONG ms = (ULONG)(timeout < 0 ? 0 : timeout);
    winlib().SetPipePolicy(h, pipe, 0x03, sizeof ms, &ms);   // PIPE_TRANSFER_TIMEOUT
}
inline Status control(Device* d, int requestType, int request, int value, int index, uint8_t* data, int length, int timeout, int& transferred) {
    transferred = 0;
    timeoutOf(d->first, 0, timeout);
    wu_SETUP_PACKET setup{(UCHAR)requestType, (UCHAR)request, (USHORT)value, (USHORT)index, (USHORT)length};
    ULONG done = 0;
    if (!winlib().ControlTransfer(d->first, setup, data, (ULONG)length, &done, nullptr)) return lastError("The control transfer");
    transferred = (int)done;
    return success();
}
inline Status transfer(Device* d, int, int endpoint, uint8_t* data, int length, int timeout, int& transferred) {
    transferred = 0;
    wu_HANDLE h = d->forEndpoint(endpoint);
    timeoutOf(h, (UCHAR)endpoint, timeout);
    ULONG done = 0;
    BOOL ok = (endpoint & 0x80) ? winlib().ReadPipe(h, (UCHAR)endpoint, data, (ULONG)length, &done, nullptr)
                                : winlib().WritePipe(h, (UCHAR)endpoint, data, (ULONG)length, &done, nullptr);
    transferred = (int)done;
    if (!ok) return lastError((endpoint & 0x80 ? std::string("Reading from endpoint ") : std::string("Writing to endpoint ")) + std::to_string(endpoint & 0x7F));
    return success();
}
inline Status clearHalt(Device* d, int endpoint) {
    return winlib().ResetPipe(d->forEndpoint(endpoint), (UCHAR)endpoint) ? success() : lastError("Clearing the halt");
}
inline void closeDevice(Device* d) {
    if (!d) return;
    for (wu_HANDLE h : d->associated) if (h) winlib().Free(h);
    if (d->first) winlib().Free(d->first);
    if (d->file != INVALID_HANDLE_VALUE) CloseHandle(d->file);
    delete d;
}

}  // namespace usb
}  // namespace plat
}  // namespace fire
