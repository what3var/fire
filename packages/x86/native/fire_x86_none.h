/* fire x86 package: no switch of the processor's mode - the virtual machine and every target with an operating system of its own (the processor is in its mode). */
#ifndef FIRE_X86_NONE_H
#define FIRE_X86_NONE_H
#include "fire_x86_c.h"
static const char* fire_x86_unavailable(void) { return "The processor's mode can only be switched by a program in the real mode of DOS (target dos16)."; }
static const char* fire_x86_mode(void) { return "none"; }
static int fire_x86_a20(void) { return 0; }
static int fire_x86_runProtected(uint32_t code, uint32_t regs[6]) { (void)code; (void)regs; return 0; }
static int fire_x86_canLong(void) { return 0; }
static int fire_x86_runLong(uint32_t code, uint32_t regs[6]) { (void)code; (void)regs; return 0; }
static uint32_t fire_x86_linearOf(const void* p) { (void)p; return 0; }
#endif
