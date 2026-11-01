/* JSON (docs/JSON.md, `#import "json"`) for native programs: the tables of the program's classes that the JSON engine reads. The program (C) defines them and the
 * functions below (generated, after this file); the engine (fire_json.hpp, in the natives of the program) calls them. Both include this file after fire_rt.h. */
#ifndef FIRE_JSON_H
#define FIRE_JSON_H

FIRE_BEGIN

/* A field as JSON sees it (JsonMethods.JsonField): the name in the JSON text, the slot in the object, the declared type (`int`, `Point`, `List<Point>`, `$0`, "" for
 * an untyped one) and the unit it is declared with (0: none). */
typedef struct JsonFieldInfo { const char* name; uint32_t slot; const char* type; uint32_t unit; } JsonFieldInfo;
/* A class: its name (as the program knows it, `Box`1` for a generic one whose plain name is taken), the plain name, its fields, `flags` (1 actor, 2 has
 * OnDeserialized(), 4 the List of the prelude, 8 JsonObject) and the number of its type parameters. */
typedef struct JsonClassInfo { const char* name; const char* plain; uint32_t nfields; const JsonFieldInfo* fields; uint32_t njson; uint8_t flags; uint8_t ntparams; } JsonClassInfo;
/* The slots of the classes JSON fills itself (-1: not in the program). */
typedef struct JsonSpecial { int32_t listCls, listItems, listCount; int32_t objCls, objKeys, objValues, objCount; } JsonSpecial;

/* Generated (the program): the class with this id (null: none), the id of a class name (-1: none), the text of a type argument (null: not known), the type
 * argument of a text (null: none), the special slots. */
const JsonClassInfo* jsonClassAt(uint32_t cls);
int32_t jsonClassNamed(const char* name);
const char* jsonTArgText(const TArg* t);
const TArg* jsonTArgFor(const char* text);
const JsonSpecial* jsonSpecial(void);

/* The engine (the natives of the program): `__JsonCall(id, ...)` - 1 Serialize(value, flags), 2 Deserialize(text[, typeName]) -> [value, objects with
 * OnDeserialized], 3 Parse(text). `callType` is the type argument of the generic method that calls it (`Json.Deserialize<Person>`), null when there is none. */
Value fire_jsonCall(int argc, Value op, Value a1, Value a2, const char* callType, OwnList* list);

FIRE_END

#endif
