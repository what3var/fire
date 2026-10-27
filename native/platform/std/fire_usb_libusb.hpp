// fire native platform layer, USB part through libusb-1.0 (Linux, macOS; see fire_usb_common.hpp for the interface). libusb is loaded with dlopen when it is first needed and only the few
// functions used here are looked up - a program runs without it (the list of devices is then empty and opening fails with a message that says what to install). On Linux the user needs
// access to the device nodes (/dev/bus/usb/...: a udev rule, or the group of the device); a device that a kernel driver holds is detached from it while an interface is claimed.
#pragma once

#include "fire_usb_common.hpp"
#include <cstdio>
#include <dlfcn.h>
#include <mutex>

namespace fire {
namespace plat {
namespace usb {

// the parts of libusb.h used here (its ABI has been stable since libusb 1.0)
struct lu_device_descriptor {
    uint8_t bLength, bDescriptorType; uint16_t bcdUSB; uint8_t bDeviceClass, bDeviceSubClass, bDeviceProtocol, bMaxPacketSize0;
    uint16_t idVendor, idProduct, bcdDevice; uint8_t iManufacturer, iProduct, iSerialNumber, bNumConfigurations;
};
struct lu_endpoint_descriptor {
    uint8_t bLength, bDescriptorType, bEndpointAddress, bmAttributes; uint16_t wMaxPacketSize; uint8_t bInterval, bRefresh, bSynchAddress;
    const unsigned char* extra; int extra_length;
};
struct lu_interface_descriptor {
    uint8_t bLength, bDescriptorType, bInterfaceNumber, bAlternateSetting, bNumEndpoints, bInterfaceClass, bInterfaceSubClass, bInterfaceProtocol, iInterface;
    const lu_endpoint_descriptor* endpoint; const unsigned char* extra; int extra_length;
};
struct lu_interface { const lu_interface_descriptor* altsetting; int num_altsetting; };
struct lu_config_descriptor {
    uint8_t bLength, bDescriptorType; uint16_t wTotalLength; uint8_t bNumInterfaces, bConfigurationValue, iConfiguration, bmAttributes, MaxPower;
    const lu_interface* interfaces; const unsigned char* extra; int extra_length;
};
struct lu_context; struct lu_device; struct lu_handle;

struct Lib {
    void* handle = nullptr;
    bool tried = false;
    lu_context* ctx = nullptr;
    std::string error;
    int (*init)(lu_context**) = nullptr;
    long (*get_device_list)(lu_context*, lu_device***) = nullptr;
    void (*free_device_list)(lu_device**, int) = nullptr;
    int (*get_device_descriptor)(lu_device*, lu_device_descriptor*) = nullptr;
    uint8_t (*get_bus_number)(lu_device*) = nullptr;
    uint8_t (*get_device_address)(lu_device*) = nullptr;
    lu_device* (*ref_device)(lu_device*) = nullptr;
    void (*unref_device)(lu_device*) = nullptr;
    int (*open)(lu_device*, lu_handle**) = nullptr;
    void (*close)(lu_handle*) = nullptr;
    int (*get_string_descriptor_ascii)(lu_handle*, uint8_t, unsigned char*, int) = nullptr;
    int (*get_active_config_descriptor)(lu_device*, lu_config_descriptor**) = nullptr;
    void (*free_config_descriptor)(lu_config_descriptor*) = nullptr;
    int (*claim_interface)(lu_handle*, int) = nullptr;
    int (*release_interface)(lu_handle*, int) = nullptr;
    int (*set_interface_alt_setting)(lu_handle*, int, int) = nullptr;
    int (*set_auto_detach_kernel_driver)(lu_handle*, int) = nullptr;
    int (*control_transfer)(lu_handle*, uint8_t, uint8_t, uint16_t, uint16_t, unsigned char*, uint16_t, unsigned int) = nullptr;
    int (*bulk_transfer)(lu_handle*, unsigned char, unsigned char*, int, int*, unsigned int) = nullptr;
    int (*interrupt_transfer)(lu_handle*, unsigned char, unsigned char*, int, int*, unsigned int) = nullptr;
    int (*clear_halt)(lu_handle*, unsigned char) = nullptr;

    template <typename F> bool bind(const char* name, F& fn) { fn = reinterpret_cast<F>(dlsym(handle, name)); return fn != nullptr; }

    bool load() {
        if (tried) return ctx != nullptr;
        tried = true;
        const char* names[] = {"libusb-1.0.so.0", "libusb-1.0.so", "libusb-1.0.0.dylib", "libusb-1.0.dylib", "/opt/homebrew/lib/libusb-1.0.0.dylib", "/usr/local/lib/libusb-1.0.0.dylib"};
        for (const char* n : names) { handle = dlopen(n, RTLD_LAZY | RTLD_LOCAL); if (handle) break; }
        if (!handle) { error = "libusb-1.0 is not installed (Debian/Ubuntu: apt install libusb-1.0-0, macOS: brew install libusb)."; return false; }
        bool all = bind("libusb_init", init) && bind("libusb_get_device_list", get_device_list) && bind("libusb_free_device_list", free_device_list)
            && bind("libusb_get_device_descriptor", get_device_descriptor) && bind("libusb_get_bus_number", get_bus_number) && bind("libusb_get_device_address", get_device_address)
            && bind("libusb_ref_device", ref_device) && bind("libusb_unref_device", unref_device) && bind("libusb_open", open) && bind("libusb_close", close)
            && bind("libusb_get_string_descriptor_ascii", get_string_descriptor_ascii) && bind("libusb_get_active_config_descriptor", get_active_config_descriptor)
            && bind("libusb_free_config_descriptor", free_config_descriptor) && bind("libusb_claim_interface", claim_interface) && bind("libusb_release_interface", release_interface)
            && bind("libusb_set_interface_alt_setting", set_interface_alt_setting) && bind("libusb_control_transfer", control_transfer)
            && bind("libusb_bulk_transfer", bulk_transfer) && bind("libusb_interrupt_transfer", interrupt_transfer) && bind("libusb_clear_halt", clear_halt);
        bind("libusb_set_auto_detach_kernel_driver", set_auto_detach_kernel_driver);   // (not on every platform)
        if (!all) { error = "The libusb-1.0 found is too old."; return false; }
        if (init(&ctx) != 0) { ctx = nullptr; error = "libusb could not be started."; return false; }
        return true;
    }
};
inline Lib& lib() { static Lib l; return l; }
inline std::mutex& lock() { static std::mutex m; return m; }

/// The status of a libusb error code.
inline Status status(int r, const std::string& what) {
    switch (r) {
        case -3: return fail(E_Permission, what + ": no permission (on Linux a udev rule must give the user access to the device).");
        case -4: return fail(E_Disconnected, what + ": the device is gone.");
        case -5: return fail(E_NotFound, what + ": not found.");
        case -6: return fail(E_Busy, what + ": busy (another program or a driver holds it).");
        case -7: return fail(E_Timeout, what + ": timed out.");
        case -8: return fail(E_Overflow, what + ": the device sent more than was asked for (read whole packets).");
        case -9: return fail(E_Stall, what + ": the endpoint stalled.");
        case -2: return fail(E_InvalidArgument, what + ": invalid argument.");
        case -12: return fail(E_Unsupported, what + ": not supported.");
        default: return fail(E_Other, what + ": error " + std::to_string(r) + ".");
    }
}

struct Device {
    lu_device* device = nullptr;
    lu_handle* handle = nullptr;
};

inline std::string idOf(int bus, int address) {
    char text[16];
    std::snprintf(text, sizeof text, "%03d:%03d", bus, address);
    return text;
}

inline bool supported() {
    std::lock_guard<std::mutex> g(lock());
    return lib().load();
}

inline Status devices(std::vector<DeviceInfo>& out) {
    out.clear();
    std::lock_guard<std::mutex> g(lock());
    Lib& l = lib();
    if (!l.load()) return success();   // no libusb: no devices (opening says why)
    lu_device** list = nullptr;
    long n = l.get_device_list(l.ctx, &list);
    if (n < 0) return status((int)n, "Listing the USB devices");
    for (long i = 0; i < n; i++) {
        lu_device_descriptor d{};
        if (l.get_device_descriptor(list[i], &d) != 0) continue;
        DeviceInfo info;
        info.vendorId = d.idVendor;
        info.productId = d.idProduct;
        info.deviceClass = d.bDeviceClass;
        info.bus = l.get_bus_number(list[i]);
        info.address = l.get_device_address(list[i]);
        info.id = idOf(info.bus, info.address);
        // the strings need the device opened: without permission they stay empty
        lu_handle* h = nullptr;
        if (l.open(list[i], &h) == 0 && h) {
            unsigned char text[256];
            auto read = [&](uint8_t index, std::string& into) {
                if (index == 0) return;
                int k = l.get_string_descriptor_ascii(h, index, text, sizeof text - 1);
                if (k > 0) into.assign((const char*)text, (size_t)k);
            };
            read(d.iManufacturer, info.manufacturer);
            read(d.iProduct, info.product);
            read(d.iSerialNumber, info.serial);
            l.close(h);
        }
        out.push_back(info);
    }
    l.free_device_list(list, 1);
    return success();
}

inline Status openDevice(const std::string& id, Device*& out) {
    out = nullptr;
    std::lock_guard<std::mutex> g(lock());
    Lib& l = lib();
    if (!l.load()) return fail(E_Unsupported, l.error);
    lu_device** list = nullptr;
    long n = l.get_device_list(l.ctx, &list);
    if (n < 0) return status((int)n, "Listing the USB devices");
    lu_device* found = nullptr;
    for (long i = 0; i < n && !found; i++)
        if (idOf(l.get_bus_number(list[i]), l.get_device_address(list[i])) == id) found = l.ref_device(list[i]);
    l.free_device_list(list, 1);
    if (!found) return fail(E_NotFound, "There is no USB device " + id + ".");
    lu_handle* h = nullptr;
    int r = l.open(found, &h);
    if (r != 0) { l.unref_device(found); return status(r, "Opening the USB device " + id); }
    if (l.set_auto_detach_kernel_driver) l.set_auto_detach_kernel_driver(h, 1);
    out = new Device();
    out->device = found;
    out->handle = h;
    return success();
}

inline Status endpoints(Device* d, std::vector<EndpointInfo>& out) {
    out.clear();
    std::lock_guard<std::mutex> g(lock());
    Lib& l = lib();
    lu_config_descriptor* config = nullptr;
    int r = l.get_active_config_descriptor(d->device, &config);
    if (r != 0) return status(r, "Reading the configuration");
    for (int i = 0; i < config->bNumInterfaces; i++) {
        const lu_interface& itf = config->interfaces[i];
        for (int a = 0; a < itf.num_altsetting; a++) {
            const lu_interface_descriptor& alt = itf.altsetting[a];
            for (int e = 0; e < alt.bNumEndpoints; e++) {
                EndpointInfo info;
                info.interfaceNumber = alt.bInterfaceNumber;
                info.alternate = alt.bAlternateSetting;
                info.address = alt.endpoint[e].bEndpointAddress;
                info.type = alt.endpoint[e].bmAttributes & 3;
                info.maxPacket = alt.endpoint[e].wMaxPacketSize & 0x7FF;
                info.interfaceClass = alt.bInterfaceClass;
                out.push_back(info);
            }
        }
    }
    l.free_config_descriptor(config);
    return success();
}

inline Status claim(Device* d, int itf) {
    int r = lib().claim_interface(d->handle, itf);
    return r == 0 ? success() : status(r, "Claiming interface " + std::to_string(itf));
}
inline Status release(Device* d, int itf) {
    int r = lib().release_interface(d->handle, itf);
    return r == 0 ? success() : status(r, "Releasing interface " + std::to_string(itf));
}
inline Status setAlternate(Device* d, int itf, int alt) {
    int r = lib().set_interface_alt_setting(d->handle, itf, alt);
    return r == 0 ? success() : status(r, "Setting the alternate setting");
}
inline Status control(Device* d, int requestType, int request, int value, int index, uint8_t* data, int length, int timeout, int& transferred) {
    transferred = 0;
    int r = lib().control_transfer(d->handle, (uint8_t)requestType, (uint8_t)request, (uint16_t)value, (uint16_t)index, data, (uint16_t)length, (unsigned)timeout);
    if (r < 0) return status(r, "The control transfer");
    transferred = r;
    return success();
}
inline Status transfer(Device* d, int type, int endpoint, uint8_t* data, int length, int timeout, int& transferred) {
    transferred = 0;
    int done = 0;
    int r = type == 3 ? lib().interrupt_transfer(d->handle, (unsigned char)endpoint, data, length, &done, (unsigned)timeout)
                      : lib().bulk_transfer(d->handle, (unsigned char)endpoint, data, length, &done, (unsigned)timeout);
    transferred = done;
    if (r != 0) return status(r, (endpoint & 0x80 ? std::string("Reading from endpoint ") : std::string("Writing to endpoint ")) + std::to_string(endpoint & 0x7F));
    return success();
}
inline Status clearHalt(Device* d, int endpoint) {
    int r = lib().clear_halt(d->handle, (unsigned char)endpoint);
    return r == 0 ? success() : status(r, "Clearing the halt");
}
inline void closeDevice(Device* d) {
    if (!d) return;
    if (d->handle) lib().close(d->handle);
    if (d->device) lib().unref_device(d->device);
    delete d;
}

}  // namespace usb
}  // namespace plat
}  // namespace fire
