/* native/forge/lexer.h - src/fire/Lexing/Token.cs (commit f18c976c) and the interface of lexer.c (src/fire.Compiler/Lexer.cs). */
#ifndef FORGE_LEXER_H
#define FORGE_LEXER_H

/* TokenType, in the order of the C# enum (the names are printed) */
#define FC_TOKEN_TYPES(X) \
    X(IntLiteral) X(FloatLiteral) X(StringLiteral) X(InterpolatedStringLiteral) X(CharLiteral) X(Identifier) \
    X(Var) X(Func) X(Class) X(Construct) X(Destruct) X(Return) \
    X(If) X(Else) X(While) X(Do) X(For) X(Foreach) X(In) X(On) X(New) X(Base) X(This) \
    X(Try) X(Catch) X(Finally) X(Throw) \
    X(Is) X(Of) X(From) X(Under) \
    X(Extern) X(Unsafe) \
    X(InlineCode) \
    X(Interface) \
    X(Readonly) X(Enum) \
    X(Static) \
    X(Override) \
    X(Public) X(Private) X(Protected) \
    X(Namespace) \
    X(With) \
    X(Extends) \
    X(Switch) X(Case) X(Default) X(Break) \
    X(Continue) \
    X(Where) \
    X(Fire) X(Taking) \
    X(Handover) \
    X(Operator) \
    X(Sync) X(Flat) \
    X(Copy) \
    X(Replace) \
    X(Take) \
    X(Leave) X(Terminate) \
    X(Actor) X(Process) \
    X(True) X(False) X(Undefined) \
    X(KwBool) X(KwInt) X(KwFloat) X(KwChar) X(KwString) \
    X(KwByte) \
    X(And) X(Or) \
    X(Plus) X(Minus) X(Star) X(Slash) X(Percent) \
    X(PlusPlus) X(MinusMinus) \
    X(Caret) \
    X(Pipe) \
    X(Assign) X(Eq) X(NotEq) X(Lt) X(LtEq) X(Gt) X(GtEq) \
    X(PlusAssign) X(MinusAssign) X(StarAssign) X(SlashAssign) X(PercentAssign) X(CaretAssign) X(AmpAssign) X(PipeAssign) X(HashAssign) X(ShlAssign) X(ShrAssign) X(UShrAssign) \
    X(Bang) \
    X(Tilde) \
    X(Amp) \
    X(Colon) \
    X(Arrow) \
    X(Dot) X(DotDot) X(Comma) X(Semicolon) \
    X(LParen) X(RParen) X(LBrace) X(RBrace) X(LBracket) X(RBracket) \
    X(Question) \
    X(Hash) \
    X(Eof) \
    X(KwWord) \
    X(QuestionDot) \
    X(QuestionQuestionDot)

typedef enum FcTokenType {
#define FC_TT_ENUM(name) TT_##name,
    FC_TOKEN_TYPES(FC_TT_ENUM)
#undef FC_TT_ENUM
    TT__COUNT
} FcTokenType;

const char* fcTokenTypeName(FcTokenType t);

/* InterpolationSegment */
typedef struct FcSegment { bool isExpression; const char* text; const char* format; } FcSegment;

typedef enum FcLiteralKind { FC_LIT_NONE, FC_LIT_INT, FC_LIT_FLOAT, FC_LIT_STRING, FC_LIT_CHAR, FC_LIT_SEGMENTS } FcLiteralKind;

typedef struct FcToken {
    FcTokenType type;
    const char* lexeme;
    int line, column;
    int length;
    const char* unitSuffix;   /* NULL: none */
    bool newlineBefore;
    FcLiteralKind literal;
    int64_t intValue;
    double floatValue;
    const char* stringValue;  /* FC_LIT_STRING, FC_LIT_CHAR (one unit) */
    FcList* segments;         /* FcSegment* */
} FcToken;

typedef struct FcTokens { FcToken* items; int count, cap; } FcTokens;

bool fcIsKeyword(const char* word);
/* Lexer.Tokenize; a LexException is thrown on the context */
FcTokens fcTokenize(FcCtx* c, const uint16_t* source, int length);
FcTokens fcTokenizeFc8(FcCtx* c, const char* source);

#endif
