/* fire native platform layer, console part: no console of its own (a board, the browser) - the console bridge writes plain text to standard output and reads
 * standard input (fire_con_open says 0; the other functions are never called). */
#ifndef FIRE_CONSOLE_NONE_H
#define FIRE_CONSOLE_NONE_H
#include "fire_console_c.h"

static int fire_con_open(void) { return 0; }
static void fire_con_write(const char* utf8, uint32_t n) { (void)utf8; (void)n; }
static void fire_con_flush(void) {}
static void fire_con_size(int* cols, int* rows) { *cols = 80; *rows = 25; }
static void fire_con_getCursor(int* col, int* row) { *col = 0; *row = 0; }
static void fire_con_setCursor(int col, int row) { (void)col; (void)row; }
static void fire_con_setColor(int fg, int bg) { (void)fg; (void)bg; }
static void fire_con_clear(void) {}
static void fire_con_showCursor(int show) { (void)show; }
static int fire_con_keyAvailable(void) { return 0; }
static int fire_con_readKey(FireConKey* key) { (void)key; return 0; }
static void fire_con_close(void) {}

#endif
