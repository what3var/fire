/* fire native platform layer, serial ports for the devices bridge on Windows: the Win32 communications API, 115200 baud 8N1, reading without waiting. The
 * interface is described in fire_dev_c.h. (Not tried on a Windows machine yet.) */
#ifndef FIRE_DEV_WIN32_H
#define FIRE_DEV_WIN32_H

#include "fire_dev_c.h"
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

typedef struct FireDevSerialPort { HANDLE h; } FireDevSerialPort;

/* The ports of HKLM\HARDWARE\DEVICEMAP\SERIALCOMM (`COM3`, ...), like SerialPort.GetPortNames. */
static void fire_dev_serialNames(FireDevNames* names) {
    HKEY key;
    DWORD i;
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, "HARDWARE\\DEVICEMAP\\SERIALCOMM", 0, KEY_READ, &key) != ERROR_SUCCESS) return;
    for (i = 0;; i++) {
        char valueName[256], data[256];
        DWORD valueLen = sizeof valueName, dataLen = sizeof data - 1, type = 0;
        if (RegEnumValueA(key, i, valueName, &valueLen, NULL, &type, (LPBYTE)data, &dataLen) != ERROR_SUCCESS) break;
        if (type == REG_SZ) { data[dataLen] = 0; fire_dev_namesAdd(names, data); }
    }
    RegCloseKey(key);
    fire_dev_namesSort(names);
}

static void fire_dev_serialInit(FireDevSerialPort* p) { p->h = INVALID_HANDLE_VALUE; }
static void fire_dev_serialClose(FireDevSerialPort* p) {
    if (p->h != INVALID_HANDLE_VALUE) { CloseHandle(p->h); p->h = INVALID_HANDLE_VALUE; }
}
static int fire_dev_serialOpen(FireDevSerialPort* p, const char* name) {
    char path[300];
    DCB dcb;
    COMMTIMEOUTS timeouts;
    fire_dev_serialClose(p);
    if (strncmp(name, "\\\\.\\", 4) == 0) snprintf(path, sizeof path, "%s", name);   /* COM10 and above need the device namespace */
    else snprintf(path, sizeof path, "\\\\.\\%s", name);
    p->h = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
    if (p->h == INVALID_HANDLE_VALUE) return 0;
    memset(&dcb, 0, sizeof dcb);
    dcb.DCBlength = sizeof dcb;
    if (!GetCommState(p->h, &dcb)) { fire_dev_serialClose(p); return 0; }
    dcb.BaudRate = CBR_115200;
    dcb.ByteSize = 8;
    dcb.Parity = NOPARITY;
    dcb.StopBits = ONESTOPBIT;
    if (!SetCommState(p->h, &dcb)) { fire_dev_serialClose(p); return 0; }
    memset(&timeouts, 0, sizeof timeouts);
    timeouts.ReadIntervalTimeout = MAXDWORD;   /* a read returns at once with what is there */
    timeouts.WriteTotalTimeoutConstant = 1000;
    SetCommTimeouts(p->h, &timeouts);
    return 1;
}
static int64_t fire_dev_serialRead(FireDevSerialPort* p, uint8_t* buffer, size_t n) {
    DWORD got = 0;
    if (p->h == INVALID_HANDLE_VALUE) return -1;
    if (!ReadFile(p->h, buffer, (DWORD)n, &got, NULL)) return -1;
    return (int64_t)got;
}
static int fire_dev_serialWrite(FireDevSerialPort* p, const uint8_t* data, size_t n) {
    DWORD put = 0;
    if (p->h == INVALID_HANDLE_VALUE) return 0;
    return WriteFile(p->h, data, (DWORD)n, &put, NULL) && put == n;
}

#endif
