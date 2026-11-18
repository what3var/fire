using System;
using System.Collections.Generic;
using System.Text;
using fire.Compiler;
using fire.Lexing;

namespace fire.Editor
{
    /// <summary>
    /// What the other files of a program can see of a file: its tokens without the bodies of its functions (a block in braces right after the `)` of a parameter list - a method,
    /// a constructor, an operator, also the block of an `if`/`for` of top-level code, whose names are local all the same). A change inside a body leaves the shape as it is: the
    /// errors of the other files cannot change by it, so they are not checked again while the user types in a body - only when a declaration, a signature, a field, an
    /// `#import` or `#include` changed (or a file is closed, or saved files change on the disk). Comments and blanks are not in it either.
    /// Null: the text cannot be read as tokens right now (an open string while typing) - the caller keeps the shape it knew.
    /// </summary>
    public static class DeclarationShape
    {
        public static string? Of(string text)
        {
            List<Token> tokens;
            try { tokens = new Lexer(text).Tokenize(); }
            catch (Exception) { return null; }
            var shape = new StringBuilder(text.Length / 4);
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Type == TokenType.LBrace && i > 0 && tokens[i - 1].Type == TokenType.RParen && OpensBody(tokens, i - 1))
                {
                    int depth = 0, j = i;
                    for (; j < tokens.Count; j++)
                    {
                        if (tokens[j].Type == TokenType.LBrace) depth++;
                        else if (tokens[j].Type == TokenType.RBrace && --depth == 0) break;
                    }
                    shape.Append("{}\n");
                    i = j;   // (an unclosed body: the rest of the file is in it)
                    continue;
                }
                shape.Append((int)token.Type).Append(' ').Append(token.Lexeme).Append('\n');
            }
            return shape.ToString();
        }

        /// <summary>Whether the `)` at `close` ends a parameter list (or the condition of a statement) whose block is a body: not `new()` (a constraint of a generic).</summary>
        private static bool OpensBody(List<Token> tokens, int close)
        {
            int depth = 0;
            for (int k = close; k >= 0; k--)
            {
                if (tokens[k].Type == TokenType.RParen) depth++;
                else if (tokens[k].Type == TokenType.LParen && --depth == 0)
                    return k == 0 || tokens[k - 1].Type != TokenType.New;
            }
            return false;
        }
    }
}
