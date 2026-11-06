/* fire devices package: serial and parallel ports for the devices bridge on DOS (real mode; Open Watcom): the ports the BIOS found (its data area at
 * 0040:0000) as "COM1".."COM4" and "LPT1".."LPT3".
 *   - COM: the 8250/16550 UART directly, 115200 baud 8N1, the FIFO on; what comes in is taken by an interrupt (IRQ 4 for COM1/COM3, IRQ 3 for COM2/COM4)
 *     into a ring buffer of the port - nothing is lost while the program does something else (the devices bridge reads when the program asks). The vector and
 *     the mask are put back when the port is closed and at the end of the program.
 *   - LPT: only output, byte by byte with the handshake of the printer port (BUSY, STROBE); reading gives nothing.
 * The interface is the one of fire_dev_c.h. */
#ifndef FIRE_DEV_DOS_H
#define FIRE_DEV_DOS_H

#include "fire_dev_c.h"
#include <conio.h>
#include <dos.h>
#include <i86.h>

#define FIRE_DEV_DOS_RING 2048u   /* bytes a COM port keeps until the program reads them (a power of two) */
typedef struct FireDevDosCom {
    unsigned base;
    int irq;
    int open;
    volatile unsigned head, tail;   /* the interrupt writes at head, the program reads at tail */
    volatile int overrun;
    unsigned char ring[FIRE_DEV_DOS_RING];
} FireDevDosCom;
typedef struct FireDevSerialPort { int kind; int index; } FireDevSerialPort;   /* kind 0 closed, 1 COM, 2 LPT */

static FireDevDosCom fire_dev_dosCom[4];
typedef void (__interrupt __far *fire_dev_dosIsr)(void);
static fire_dev_dosIsr fire_dev_dosOld[2];   /* the vectors before: [0] IRQ 3, [1] IRQ 4 */
static int fire_dev_dosHooks[2], fire_dev_dosMasked[2], fire_dev_dosAtExit = 0;

static unsigned fire_dev_dosBios(unsigned off) { return *(unsigned short __far*)MK_FP(0x40, off); }
static int fire_dev_dosLine(int irq) { return irq == 3 ? 0 : 1; }

/* the interrupt of IRQ 3 or 4: every open COM port on the line empties its UART into its ring */
static void fire_dev_dosServe(int irq) {
    int i;
    for (i = 0; i < 4; i++) {
        FireDevDosCom* c = &fire_dev_dosCom[i];
        if (!c->open || c->irq != irq) continue;
        while (inp(c->base + 5) & 1) {
            unsigned char b = (unsigned char)inp(c->base);
            unsigned next = (c->head + 1) & (FIRE_DEV_DOS_RING - 1);
            if (next == c->tail) c->overrun = 1;
            else { c->ring[c->head] = b; c->head = next; }
        }
        (void)inp(c->base + 2);   /* (IIR: the interrupt is taken) */
    }
    outp(0x20, 0x20);
}
static void __interrupt __far fire_dev_dosIsr3(void) { fire_dev_dosServe(3); }
static void __interrupt __far fire_dev_dosIsr4(void) { fire_dev_dosServe(4); }

static void fire_dev_dosUnhook(int irq) {
    int k = fire_dev_dosLine(irq);
    if (!fire_dev_dosHooks[k]) return;
    if (--fire_dev_dosHooks[k] > 0) return;
    _disable();
    if (fire_dev_dosMasked[k]) outp(0x21, inp(0x21) | (1u << irq));
    _dos_setvect(0x08 + irq, fire_dev_dosOld[k]);
    _enable();
}
static void fire_dev_dosCloseAll(void) {
    int i;
    for (i = 0; i < 4; i++) {
        FireDevDosCom* c = &fire_dev_dosCom[i];
        if (!c->open) continue;
        outp(c->base + 1, 0);   /* no more interrupts from the UART */
        outp(c->base + 4, 0x03);   /* DTR, RTS; OUT2 off: its line no longer reaches the PIC */
        c->open = 0;
        fire_dev_dosUnhook(c->irq);
    }
}
static void fire_dev_dosHook(int irq) {
    int k = fire_dev_dosLine(irq);
    if (!fire_dev_dosAtExit) { atexit(fire_dev_dosCloseAll); fire_dev_dosAtExit = 1; }
    if (fire_dev_dosHooks[k]++ > 0) return;
    _disable();
    fire_dev_dosOld[k] = _dos_getvect(0x08 + irq);
    _dos_setvect(0x08 + irq, irq == 3 ? fire_dev_dosIsr3 : fire_dev_dosIsr4);
    fire_dev_dosMasked[k] = (inp(0x21) & (1u << irq)) != 0;
    outp(0x21, inp(0x21) & ~(1u << irq));
    _enable();
}

static void fire_dev_serialNames(FireDevNames* names) {
    int i;
    char name[8];
    for (i = 0; i < 4; i++) if (fire_dev_dosBios(2 * i)) { sprintf(name, "COM%d", i + 1); fire_dev_namesAdd(names, name); }
    for (i = 0; i < 3; i++) if (fire_dev_dosBios(8 + 2 * i)) { sprintf(name, "LPT%d", i + 1); fire_dev_namesAdd(names, name); }
}
static void fire_dev_serialInit(FireDevSerialPort* p) { p->kind = 0; p->index = -1; }
static void fire_dev_serialClose(FireDevSerialPort* p) {
    if (p->kind == 1) {
        FireDevDosCom* c = &fire_dev_dosCom[p->index];
        if (c->open) {
            outp(c->base + 1, 0);
            outp(c->base + 4, 0x03);
            c->open = 0;
            fire_dev_dosUnhook(c->irq);
        }
    }
    p->kind = 0;
    p->index = -1;
}
static int fire_dev_serialOpen(FireDevSerialPort* p, const char* name) {
    int n;
    fire_dev_serialClose(p);
    if ((name[0] == 'C' || name[0] == 'c') && (name[1] == 'O' || name[1] == 'o') && (name[2] == 'M' || name[2] == 'm') && name[3] >= '1' && name[3] <= '4' && !name[4]) {
        FireDevDosCom* c;
        unsigned base;
        n = name[3] - '1';
        base = fire_dev_dosBios(2 * n);
        c = &fire_dev_dosCom[n];
        if (!base || c->open) return 0;
        c->base = base;
        c->irq = (n & 1) ? 3 : 4;
        c->head = c->tail = 0;
        c->overrun = 0;
        outp(base + 1, 0);           /* no interrupts while it is set up */
        outp(base + 3, 0x80);        /* DLAB: the divisor */
        outp(base + 0, 1);           /* 115200 baud */
        outp(base + 1, 0);
        outp(base + 3, 0x03);        /* 8N1 */
        outp(base + 2, 0xC7);        /* the FIFO on and empty, an interrupt at 14 bytes */
        outp(base + 4, 0x0B);        /* DTR, RTS, OUT2 (the line to the PIC) */
        (void)inp(base); (void)inp(base + 5); (void)inp(base + 2); (void)inp(base + 6);
        c->open = 1;
        fire_dev_dosHook(c->irq);
        outp(base + 1, 0x01);        /* an interrupt when data came */
        p->kind = 1;
        p->index = n;
        return 1;
    }
    if ((name[0] == 'L' || name[0] == 'l') && (name[1] == 'P' || name[1] == 'p') && (name[2] == 'T' || name[2] == 't') && name[3] >= '1' && name[3] <= '3' && !name[4]) {
        n = name[3] - '1';
        if (!fire_dev_dosBios(8 + 2 * n)) return 0;
        outp(fire_dev_dosBios(8 + 2 * n) + 2, 0x0C);   /* not INIT (active low), SELECT */
        p->kind = 2;
        p->index = n;
        return 1;
    }
    return 0;
}
static int64_t fire_dev_serialRead(FireDevSerialPort* p, uint8_t* buffer, size_t n) {
    FireDevDosCom* c;
    size_t got = 0;
    if (p->kind == 2) return 0;
    if (p->kind != 1) return -1;
    c = &fire_dev_dosCom[p->index];
    while (got < n && c->tail != c->head) {
        buffer[got++] = c->ring[c->tail];
        c->tail = (c->tail + 1) & (FIRE_DEV_DOS_RING - 1);
    }
    return (int64_t)got;
}
/* waits up to about a second for a bit of a status port (the clock ticks of the BIOS: 18.2 per second) */
static int fire_dev_dosWait(unsigned port, unsigned char mask, unsigned char want) {
    unsigned start = fire_dev_dosBios(0x6C);
    while ((inp(port) & mask) != want) if ((unsigned)(fire_dev_dosBios(0x6C) - start) > 18) return 0;
    return 1;
}
static int fire_dev_serialWrite(FireDevSerialPort* p, const uint8_t* data, size_t n) {
    size_t i;
    if (p->kind == 1) {
        unsigned base = fire_dev_dosCom[p->index].base;
        for (i = 0; i < n; i++) {
            if (!fire_dev_dosWait(base + 5, 0x20, 0x20)) return 0;   /* the transmitter takes a byte */
            outp(base, data[i]);
        }
        return 1;
    }
    if (p->kind == 2) {
        unsigned base = fire_dev_dosBios(8 + 2 * p->index);
        for (i = 0; i < n; i++) {
            if (!fire_dev_dosWait(base + 1, 0x80, 0x80)) return 0;   /* not BUSY (the bit is inverted) */
            outp(base, data[i]);
            outp(base + 2, 0x0D);                                    /* STROBE */
            outp(base + 2, 0x0C);
        }
        return 1;
    }
    return 0;
}

#endif
