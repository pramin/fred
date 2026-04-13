// AWK Engine for FredDotNet - Complete AWK interpreter
// Supports: patterns, actions, BEGIN/END, field splitting, arrays,
// control flow, built-in functions, printf, getline, regex matching

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FredDotNet;

#region Token types and AST nodes

internal enum AwkTokenType
{
    // Literals
    Number, String, Regex, Eof,
    // Identifiers and keywords
    Ident, BuiltinFunc,
    Begin, End,
    If, Else, While, For, Do, In,
    Break, Continue, Next, Exit,
    Delete, Getline,
    Print, Printf,
    // Operators
    Plus, Minus, Star, Slash, Percent, Caret,
    Assign, PlusAssign, MinusAssign, StarAssign, SlashAssign, PercentAssign,
    Increment, Decrement,
    Eq, Ne, Lt, Gt, Le, Ge,
    And, Or, Not,
    Match, NotMatch,    // ~ !~
    Question, Colon,
    Comma, Semicolon,
    LParen, RParen, LBrace, RBrace, LBracket, RBracket,
    Dollar,
    Append,  // >>
    Pipe,    // |
    Newline,
    // Special
    Function, Return,
}

internal sealed class AwkToken
{
    public AwkTokenType Type;
    public string Value = "";
    public double NumValue;
    public int Line;
    public int Col;
}

// AST Node types
internal abstract class AwkNode { }

internal sealed class ProgramNode : AwkNode
{
    public List<RuleNode> Rules = new();
    public List<FunctionDefNode> Functions = new();
}

internal sealed class RuleNode : AwkNode
{
    public AwkNode? Pattern;
    public AwkNode? Pattern2; // for range patterns
    public bool IsBegin;
    public bool IsEnd;
    public BlockNode? Action;
}

internal sealed class FunctionDefNode : AwkNode
{
    public string Name = "";
    public List<string> Params = new();
    public BlockNode Body = new();
}

internal sealed class BlockNode : AwkNode
{
    public List<AwkNode> Statements = new();
}

internal sealed class IfNode : AwkNode
{
    public AwkNode Condition = null!;
    public AwkNode ThenBranch = null!;
    public AwkNode? ElseBranch;
}

internal sealed class WhileNode : AwkNode
{
    public AwkNode Condition = null!;
    public AwkNode Body = null!;
}

internal sealed class DoWhileNode : AwkNode
{
    public AwkNode Body = null!;
    public AwkNode Condition = null!;
}

internal sealed class ForNode : AwkNode
{
    public AwkNode? Init;
    public AwkNode? Condition;
    public AwkNode? Update;
    public AwkNode Body = null!;
}

internal sealed class ForInNode : AwkNode
{
    public string Var = "";
    public string Array = "";
    public AwkNode Body = null!;
}

internal sealed class PrintNode : AwkNode
{
    public List<AwkNode> Args = new();
    public AwkNode? Dest;        // > file or | cmd
    public bool IsAppend;
    public bool IsPipe;
}

internal sealed class PrintfNode : AwkNode
{
    public List<AwkNode> Args = new(); // first arg is format string
    public AwkNode? Dest;
    public bool IsAppend;
    public bool IsPipe;
}

internal sealed class DeleteNode : AwkNode
{
    public string Array = "";
    public List<AwkNode>? Subscripts;  // null means delete entire array
}

internal sealed class GetlineNode : AwkNode
{
    public string? Var;
}

internal sealed class ExitNode : AwkNode
{
    public AwkNode? Code;
}

internal sealed class NextNode : AwkNode { }
internal sealed class BreakNode : AwkNode { }
internal sealed class ContinueNode : AwkNode { }

internal sealed class ReturnNode : AwkNode
{
    public AwkNode? Value;
}

internal sealed class BinaryNode : AwkNode
{
    public string Op = "";
    public AwkNode Left = null!;
    public AwkNode Right = null!;
}

internal sealed class UnaryNode : AwkNode
{
    public string Op = "";
    public AwkNode Operand = null!;
    public bool Prefix = true;
}

internal sealed class AssignNode : AwkNode
{
    public string Op = "";  // = += -= *= /= %=
    public AwkNode Target = null!;
    public AwkNode Value = null!;
}

internal sealed class TernaryNode : AwkNode
{
    public AwkNode Condition = null!;
    public AwkNode TrueExpr = null!;
    public AwkNode FalseExpr = null!;
}

internal sealed class FieldNode : AwkNode
{
    public AwkNode Index = null!;
}

internal sealed class ArrayRefNode : AwkNode
{
    public string Name = "";
    public List<AwkNode> Subscripts = new();
}

internal sealed class InArrayNode : AwkNode
{
    // (expr) in array  OR  (e1,e2) in array
    public List<AwkNode> Subscripts = new();
    public string Array = "";
}

internal sealed class MatchExprNode : AwkNode
{
    public AwkNode Expr = null!;
    public string Pattern = "";
    public bool Negated;
}

internal sealed class RegexNode : AwkNode
{
    public string Pattern = "";
}

internal sealed class NumberNode : AwkNode
{
    public double Value;
}

internal sealed class StringNode : AwkNode
{
    public string Value = "";
}

internal sealed class IdentNode : AwkNode
{
    public string Name = "";
}

internal sealed class FuncCallNode : AwkNode
{
    public string Name = "";
    public List<AwkNode> Args = new();
}

internal sealed class ConcatNode : AwkNode
{
    public AwkNode Left = null!;
    public AwkNode Right = null!;
}

internal sealed class ExpressionStatementNode : AwkNode
{
    public AwkNode Expr = null!;
}

#endregion

#region Lexer

internal sealed class AwkLexer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;
    private AwkTokenType _prevTokenType = AwkTokenType.Newline;
    private static readonly HashSet<string> s_builtinFuncs = new(StringComparer.Ordinal)
    {
        "length", "substr", "split", "sub", "gsub", "index", "sprintf",
        "toupper", "tolower", "match",
        "int", "sqrt", "sin", "cos", "atan2", "exp", "log", "rand", "srand",
        "system", "close", "fflush",
    };

    public AwkLexer(string source)
    {
        _src = source;
    }

    private char Peek() => _pos < _src.Length ? _src[_pos] : '\0';
    private char PeekAt(int offset) => (_pos + offset) < _src.Length ? _src[_pos + offset] : '\0';
    private char Advance()
    {
        char c = _src[_pos++];
        if (c == '\n') { _line++; _col = 1; }
        else { _col++; }
        return c;
    }

    public AwkToken NextToken()
    {
        // skip whitespace (NOT newlines)
        while (_pos < _src.Length && _src[_pos] is ' ' or '\t')
            Advance();

        // skip comments
        if (Peek() == '#')
        {
            while (_pos < _src.Length && _src[_pos] != '\n')
                Advance();
        }

        if (_pos >= _src.Length)
            return MakeToken(AwkTokenType.Eof, "");

        int startLine = _line;
        int startCol = _col;
        char c = Peek();

        // Newlines are significant in AWK (statement terminators)
        if (c == '\n')
        {
            Advance();
            var tok = MakeToken(AwkTokenType.Newline, "\\n");
            tok.Line = startLine;
            tok.Col = startCol;
            _prevTokenType = AwkTokenType.Newline;
            return tok;
        }

        if (c == '\\' && PeekAt(1) == '\n')
        {
            Advance(); Advance(); // skip backslash-newline continuation
            return NextToken();
        }

        // Numbers
        if (char.IsDigit(c) || (c == '.' && char.IsDigit(PeekAt(1))))
        {
            return ReadNumber(startLine, startCol);
        }

        // Strings
        if (c == '"')
        {
            return ReadString(startLine, startCol);
        }

        // Regex - only in contexts where / is not division
        if (c == '/' && CanBeRegex())
        {
            return ReadRegex(startLine, startCol);
        }

        // Identifiers / keywords
        if (char.IsLetter(c) || c == '_')
        {
            return ReadIdentOrKeyword(startLine, startCol);
        }

        // Operators and punctuation
        return ReadOperator(startLine, startCol);
    }

    private bool CanBeRegex()
    {
        // / is regex if previous token was: none, newline, operator, comma, etc.
        // / is division if previous token was: number, string, ident, ), ]
        switch (_prevTokenType)
        {
            case AwkTokenType.Number:
            case AwkTokenType.String:
            case AwkTokenType.Ident:
            case AwkTokenType.BuiltinFunc:
            case AwkTokenType.RParen:
            case AwkTokenType.RBracket:
            case AwkTokenType.Increment:
            case AwkTokenType.Decrement:
                return false;
            default:
                return true;
        }
    }

    private AwkToken ReadNumber(int line, int col)
    {
        var sb = new StringBuilder();
        bool hasDot = false;
        bool hasE = false;
        // hex
        if (Peek() == '0' && (PeekAt(1) == 'x' || PeekAt(1) == 'X'))
        {
            sb.Append(Advance()); sb.Append(Advance());
            while (_pos < _src.Length && IsHexDigit(Peek()))
                sb.Append(Advance());
            var htok = MakeToken(AwkTokenType.Number, sb.ToString());
            htok.NumValue = Convert.ToInt64(sb.ToString(), 16);
            htok.Line = line; htok.Col = col;
            _prevTokenType = AwkTokenType.Number;
            return htok;
        }
        while (_pos < _src.Length)
        {
            char ch = Peek();
            if (char.IsDigit(ch)) { sb.Append(Advance()); }
            else if (ch == '.' && !hasDot && !hasE) { hasDot = true; sb.Append(Advance()); }
            else if ((ch == 'e' || ch == 'E') && !hasE) { hasE = true; sb.Append(Advance()); if (Peek() is '+' or '-') sb.Append(Advance()); }
            else break;
        }
        var tok = MakeToken(AwkTokenType.Number, sb.ToString());
        tok.NumValue = double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        tok.Line = line; tok.Col = col;
        _prevTokenType = AwkTokenType.Number;
        return tok;
    }

    private static bool IsHexDigit(char c) => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private AwkToken ReadString(int line, int col)
    {
        Advance(); // skip opening "
        var sb = new StringBuilder();
        while (_pos < _src.Length && Peek() != '"')
        {
            if (Peek() == '\\')
            {
                Advance();
                char esc = _pos < _src.Length ? Advance() : '\\';
                switch (esc)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'v': sb.Append('\v'); break;
                    case '/': sb.Append('/'); break;
                    default:
                        if (esc >= '0' && esc <= '7')
                        {
                            // octal
                            int val = esc - '0';
                            for (int i = 0; i < 2 && _pos < _src.Length && Peek() >= '0' && Peek() <= '7'; i++)
                                val = val * 8 + (Advance() - '0');
                            sb.Append((char)val);
                        }
                        else
                        {
                            sb.Append('\\');
                            sb.Append(esc);
                        }
                        break;
                }
            }
            else
            {
                sb.Append(Advance());
            }
        }
        if (_pos < _src.Length) Advance(); // skip closing "
        var tok = MakeToken(AwkTokenType.String, sb.ToString());
        tok.Line = line; tok.Col = col;
        _prevTokenType = AwkTokenType.String;
        return tok;
    }

    private AwkToken ReadRegex(int line, int col)
    {
        Advance(); // skip opening /
        var sb = new StringBuilder();
        while (_pos < _src.Length && Peek() != '/')
        {
            if (Peek() == '\\' && _pos + 1 < _src.Length)
            {
                sb.Append(Advance()); // backslash
                sb.Append(Advance()); // escaped char
            }
            else
            {
                sb.Append(Advance());
            }
        }
        if (_pos < _src.Length) Advance(); // skip closing /
        var tok = MakeToken(AwkTokenType.Regex, sb.ToString());
        tok.Line = line; tok.Col = col;
        _prevTokenType = AwkTokenType.Regex;
        return tok;
    }

    private AwkToken ReadIdentOrKeyword(int line, int col)
    {
        var sb = new StringBuilder();
        while (_pos < _src.Length && (char.IsLetterOrDigit(Peek()) || Peek() == '_'))
            sb.Append(Advance());
        string word = sb.ToString();

        AwkTokenType type = word switch
        {
            "BEGIN" => AwkTokenType.Begin,
            "END" => AwkTokenType.End,
            "if" => AwkTokenType.If,
            "else" => AwkTokenType.Else,
            "while" => AwkTokenType.While,
            "for" => AwkTokenType.For,
            "do" => AwkTokenType.Do,
            "in" => AwkTokenType.In,
            "break" => AwkTokenType.Break,
            "continue" => AwkTokenType.Continue,
            "next" => AwkTokenType.Next,
            "exit" => AwkTokenType.Exit,
            "delete" => AwkTokenType.Delete,
            "getline" => AwkTokenType.Getline,
            "print" => AwkTokenType.Print,
            "printf" => AwkTokenType.Printf,
            "function" => AwkTokenType.Function,
            "return" => AwkTokenType.Return,
            _ => s_builtinFuncs.Contains(word) ? AwkTokenType.BuiltinFunc : AwkTokenType.Ident,
        };

        var tok = MakeToken(type, word);
        tok.Line = line; tok.Col = col;
        _prevTokenType = type;
        return tok;
    }

    private AwkToken ReadOperator(int line, int col)
    {
        char c = Advance();
        AwkTokenType type;
        string val = c.ToString();

        switch (c)
        {
            case '+':
                if (Peek() == '+') { Advance(); type = AwkTokenType.Increment; val = "++"; }
                else if (Peek() == '=') { Advance(); type = AwkTokenType.PlusAssign; val = "+="; }
                else type = AwkTokenType.Plus;
                break;
            case '-':
                if (Peek() == '-') { Advance(); type = AwkTokenType.Decrement; val = "--"; }
                else if (Peek() == '=') { Advance(); type = AwkTokenType.MinusAssign; val = "-="; }
                else type = AwkTokenType.Minus;
                break;
            case '*':
                if (Peek() == '=') { Advance(); type = AwkTokenType.StarAssign; val = "*="; }
                else type = AwkTokenType.Star;
                break;
            case '/':
                if (Peek() == '=') { Advance(); type = AwkTokenType.SlashAssign; val = "/="; }
                else type = AwkTokenType.Slash;
                break;
            case '%':
                if (Peek() == '=') { Advance(); type = AwkTokenType.PercentAssign; val = "%="; }
                else type = AwkTokenType.Percent;
                break;
            case '^':
                type = AwkTokenType.Caret;
                break;
            case '=':
                if (Peek() == '=') { Advance(); type = AwkTokenType.Eq; val = "=="; }
                else type = AwkTokenType.Assign;
                break;
            case '!':
                if (Peek() == '=') { Advance(); type = AwkTokenType.Ne; val = "!="; }
                else if (Peek() == '~') { Advance(); type = AwkTokenType.NotMatch; val = "!~"; }
                else type = AwkTokenType.Not;
                break;
            case '<':
                if (Peek() == '=') { Advance(); type = AwkTokenType.Le; val = "<="; }
                else type = AwkTokenType.Lt;
                break;
            case '>':
                if (Peek() == '=') { Advance(); type = AwkTokenType.Ge; val = ">="; }
                else if (Peek() == '>') { Advance(); type = AwkTokenType.Append; val = ">>"; }
                else type = AwkTokenType.Gt;
                break;
            case '&':
                if (Peek() == '&') { Advance(); type = AwkTokenType.And; val = "&&"; }
                else { type = AwkTokenType.Eof; val = "&"; } // & alone not valid in AWK
                break;
            case '|':
                if (Peek() == '|') { Advance(); type = AwkTokenType.Or; val = "||"; }
                else { type = AwkTokenType.Pipe; val = "|"; }
                break;
            case '~':
                type = AwkTokenType.Match; break;
            case '?':
                type = AwkTokenType.Question; break;
            case ':':
                type = AwkTokenType.Colon; break;
            case ',':
                type = AwkTokenType.Comma; break;
            case ';':
                type = AwkTokenType.Semicolon; break;
            case '(':
                type = AwkTokenType.LParen; break;
            case ')':
                type = AwkTokenType.RParen; break;
            case '{':
                type = AwkTokenType.LBrace; break;
            case '}':
                type = AwkTokenType.RBrace; break;
            case '[':
                type = AwkTokenType.LBracket; break;
            case ']':
                type = AwkTokenType.RBracket; break;
            case '$':
                type = AwkTokenType.Dollar; break;
            default:
                type = AwkTokenType.Eof; break;
        }

        var tok = MakeToken(type, val);
        tok.Line = line; tok.Col = col;
        _prevTokenType = type;
        return tok;
    }

    private AwkToken MakeToken(AwkTokenType type, string value) => new AwkToken { Type = type, Value = value, Line = _line, Col = _col };
}

#endregion

#region Parser

internal sealed class AwkParser
{
    private readonly List<AwkToken> _tokens;
    private int _pos;

    public AwkParser(string source)
    {
        var lexer = new AwkLexer(source);
        _tokens = new List<AwkToken>();
        AwkToken t;
        do
        {
            t = lexer.NextToken();
            _tokens.Add(t);
        } while (t.Type != AwkTokenType.Eof);
    }

    private AwkToken Peek() => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];
    private AwkToken PeekAt(int offset)
    {
        int idx = _pos + offset;
        return idx < _tokens.Count ? _tokens[idx] : _tokens[^1];
    }
    private AwkToken Advance() => _tokens[_pos++];
    private bool Check(AwkTokenType type) => Peek().Type == type;
    private bool Match(AwkTokenType type) { if (Check(type)) { Advance(); return true; } return false; }
    private AwkToken Expect(AwkTokenType type)
    {
        if (Check(type)) return Advance();
        throw new AwkException($"Expected {type} but got {Peek().Type} ('{Peek().Value}') at line {Peek().Line}");
    }

    private void SkipNewlines()
    {
        while (Check(AwkTokenType.Newline) || Check(AwkTokenType.Semicolon))
            Advance();
    }

    public ProgramNode Parse()
    {
        var prog = new ProgramNode();
        SkipNewlines();

        while (!Check(AwkTokenType.Eof))
        {
            if (Check(AwkTokenType.Function))
            {
                prog.Functions.Add(ParseFunctionDef());
            }
            else
            {
                prog.Rules.Add(ParseRule());
            }
            SkipNewlines();
        }
        return prog;
    }

    private FunctionDefNode ParseFunctionDef()
    {
        Expect(AwkTokenType.Function);
        var func = new FunctionDefNode();
        func.Name = Expect(AwkTokenType.Ident).Value;
        Expect(AwkTokenType.LParen);
        if (!Check(AwkTokenType.RParen))
        {
            func.Params.Add(Expect(AwkTokenType.Ident).Value);
            while (Match(AwkTokenType.Comma))
                func.Params.Add(Expect(AwkTokenType.Ident).Value);
        }
        Expect(AwkTokenType.RParen);
        SkipNewlines();
        func.Body = ParseBlock();
        return func;
    }

    private RuleNode ParseRule()
    {
        var rule = new RuleNode();

        if (Check(AwkTokenType.Begin))
        {
            Advance();
            rule.IsBegin = true;
            SkipNewlines();
            rule.Action = ParseBlock();
            return rule;
        }

        if (Check(AwkTokenType.End))
        {
            Advance();
            rule.IsEnd = true;
            SkipNewlines();
            rule.Action = ParseBlock();
            return rule;
        }

        // Pattern or action
        if (Check(AwkTokenType.LBrace))
        {
            rule.Action = ParseBlock();
            return rule;
        }

        // Has a pattern
        rule.Pattern = ParseExpression();
        SkipNewlines();

        // Comma for range pattern
        if (Check(AwkTokenType.Comma))
        {
            Advance();
            SkipNewlines();
            rule.Pattern2 = ParseExpression();
            SkipNewlines();
        }

        // Action block is optional (default is { print })
        if (Check(AwkTokenType.LBrace))
        {
            rule.Action = ParseBlock();
        }

        return rule;
    }

    private BlockNode ParseBlock()
    {
        var block = new BlockNode();
        Expect(AwkTokenType.LBrace);
        SkipNewlines();
        while (!Check(AwkTokenType.RBrace) && !Check(AwkTokenType.Eof))
        {
            block.Statements.Add(ParseStatement());
            SkipNewlines();
        }
        Expect(AwkTokenType.RBrace);
        return block;
    }

    private AwkNode ParseStatement()
    {
        SkipNewlines();
        switch (Peek().Type)
        {
            case AwkTokenType.If: return ParseIf();
            case AwkTokenType.While: return ParseWhile();
            case AwkTokenType.For: return ParseFor();
            case AwkTokenType.Do: return ParseDoWhile();
            case AwkTokenType.LBrace: return ParseBlock();
            case AwkTokenType.Print: return ParsePrint();
            case AwkTokenType.Printf: return ParsePrintf();
            case AwkTokenType.Next: Advance(); while (Match(AwkTokenType.Semicolon)) { } return new NextNode();
            case AwkTokenType.Break: Advance(); while (Match(AwkTokenType.Semicolon)) { } return new BreakNode();
            case AwkTokenType.Continue: Advance(); while (Match(AwkTokenType.Semicolon)) { } return new ContinueNode();
            case AwkTokenType.Exit: return ParseExit();
            case AwkTokenType.Delete: return ParseDelete();
            case AwkTokenType.Return: return ParseReturn();
            default:
                var expr = ParseExpression();
                // consume optional semicolons
                while (Match(AwkTokenType.Semicolon)) { }
                return new ExpressionStatementNode { Expr = expr };
        }
    }

    private AwkNode ParseIf()
    {
        Expect(AwkTokenType.If);
        Expect(AwkTokenType.LParen);
        var cond = ParseExpression();
        Expect(AwkTokenType.RParen);
        SkipNewlines();
        var then = ParseStatement();
        SkipNewlines();
        AwkNode? elseBranch = null;
        if (Check(AwkTokenType.Else))
        {
            Advance();
            SkipNewlines();
            elseBranch = ParseStatement();
        }
        return new IfNode { Condition = cond, ThenBranch = then, ElseBranch = elseBranch };
    }

    private AwkNode ParseWhile()
    {
        Expect(AwkTokenType.While);
        Expect(AwkTokenType.LParen);
        var cond = ParseExpression();
        Expect(AwkTokenType.RParen);
        SkipNewlines();
        var body = ParseStatement();
        return new WhileNode { Condition = cond, Body = body };
    }

    private AwkNode ParseFor()
    {
        Expect(AwkTokenType.For);
        Expect(AwkTokenType.LParen);
        SkipNewlines();

        // Check for for-in: for (var in array)
        if (Check(AwkTokenType.Ident) && PeekAt(1).Type == AwkTokenType.In)
        {
            string varName = Advance().Value;
            Advance(); // skip 'in'
            string arrayName = Expect(AwkTokenType.Ident).Value;
            Expect(AwkTokenType.RParen);
            SkipNewlines();
            var body = ParseStatement();
            return new ForInNode { Var = varName, Array = arrayName, Body = body };
        }

        // C-style for
        AwkNode? init = null;
        if (!Check(AwkTokenType.Semicolon))
            init = ParseExpression();
        Expect(AwkTokenType.Semicolon);
        SkipNewlines();

        AwkNode? cond = null;
        if (!Check(AwkTokenType.Semicolon))
            cond = ParseExpression();
        Expect(AwkTokenType.Semicolon);
        SkipNewlines();

        AwkNode? update = null;
        if (!Check(AwkTokenType.RParen))
            update = ParseExpression();
        Expect(AwkTokenType.RParen);
        SkipNewlines();
        var forBody = ParseStatement();
        return new ForNode { Init = init, Condition = cond, Update = update, Body = forBody };
    }

    private AwkNode ParseDoWhile()
    {
        Expect(AwkTokenType.Do);
        SkipNewlines();
        var body = ParseStatement();
        SkipNewlines();
        Expect(AwkTokenType.While);
        Expect(AwkTokenType.LParen);
        var cond = ParseExpression();
        Expect(AwkTokenType.RParen);
        return new DoWhileNode { Body = body, Condition = cond };
    }

    private AwkNode ParsePrint()
    {
        Advance(); // skip 'print'
        var node = new PrintNode();

        // Check for empty print (just 'print' with no args)
        if (Check(AwkTokenType.Semicolon) || Check(AwkTokenType.Newline) ||
            Check(AwkTokenType.RBrace) || Check(AwkTokenType.Eof) ||
            Check(AwkTokenType.Pipe) || Check(AwkTokenType.Gt) || Check(AwkTokenType.Append))
        {
            // no args - will print $0
        }
        else
        {
            node.Args.Add(ParseNonAssignExpr());
            while (Check(AwkTokenType.Comma))
            {
                Advance();
                node.Args.Add(ParseNonAssignExpr());
            }
        }

        // Output redirection
        ParseOutputRedirect(node.Args, out var dest, out bool isAppend, out bool isPipe);
        node.Dest = dest;
        node.IsAppend = isAppend;
        node.IsPipe = isPipe;

        // consume optional trailing semicolons
        while (Match(AwkTokenType.Semicolon)) { }
        return node;
    }

    private AwkNode ParsePrintf()
    {
        Advance(); // skip 'printf'
        var node = new PrintfNode();
        node.Args.Add(ParseNonAssignExpr());
        while (Check(AwkTokenType.Comma))
        {
            Advance();
            node.Args.Add(ParseNonAssignExpr());
        }

        ParseOutputRedirect(node.Args, out var dest, out bool isAppend, out bool isPipe);
        node.Dest = dest;
        node.IsAppend = isAppend;
        node.IsPipe = isPipe;

        // consume optional trailing semicolons
        while (Match(AwkTokenType.Semicolon)) { }
        return node;
    }

    private void ParseOutputRedirect(List<AwkNode> args, out AwkNode? dest, out bool isAppend, out bool isPipe)
    {
        dest = null;
        isAppend = false;
        isPipe = false;

        if (Check(AwkTokenType.Gt))
        {
            Advance();
            dest = ParsePrimary();
        }
        else if (Check(AwkTokenType.Append))
        {
            Advance();
            isAppend = true;
            dest = ParsePrimary();
        }
        else if (Check(AwkTokenType.Pipe))
        {
            Advance();
            isPipe = true;
            dest = ParsePrimary();
        }
    }

    private AwkNode ParseExit()
    {
        Advance(); // skip 'exit'
        AwkNode? code = null;
        if (!Check(AwkTokenType.Semicolon) && !Check(AwkTokenType.Newline) &&
            !Check(AwkTokenType.RBrace) && !Check(AwkTokenType.Eof))
        {
            code = ParseExpression();
        }
        return new ExitNode { Code = code };
    }

    private AwkNode ParseDelete()
    {
        Advance(); // skip 'delete'
        string name = Expect(AwkTokenType.Ident).Value;
        if (Match(AwkTokenType.LBracket))
        {
            var subs = new List<AwkNode> { ParseExpression() };
            while (Match(AwkTokenType.Comma))
                subs.Add(ParseExpression());
            Expect(AwkTokenType.RBracket);
            return new DeleteNode { Array = name, Subscripts = subs };
        }
        // delete entire array
        return new DeleteNode { Array = name };
    }

    private AwkNode ParseReturn()
    {
        Advance(); // skip 'return'
        AwkNode? val = null;
        if (!Check(AwkTokenType.Semicolon) && !Check(AwkTokenType.Newline) &&
            !Check(AwkTokenType.RBrace) && !Check(AwkTokenType.Eof))
        {
            val = ParseExpression();
        }
        return new ReturnNode { Value = val };
    }

    // Expression parsing with precedence
    private AwkNode ParseExpression() => ParseAssign();

    // Non-assignment expression (for print args, to avoid ambiguity with > redirect)
    private AwkNode ParseNonAssignExpr() => ParseTernary();

    private AwkNode ParseAssign()
    {
        var left = ParseTernary();

        if (Check(AwkTokenType.Assign) || Check(AwkTokenType.PlusAssign) ||
            Check(AwkTokenType.MinusAssign) || Check(AwkTokenType.StarAssign) ||
            Check(AwkTokenType.SlashAssign) || Check(AwkTokenType.PercentAssign))
        {
            var op = Advance().Value;
            var right = ParseAssign(); // right-associative
            return new AssignNode { Op = op, Target = left, Value = right };
        }
        return left;
    }

    private AwkNode ParseTernary()
    {
        var cond = ParseOr();
        if (Check(AwkTokenType.Question))
        {
            Advance();
            var trueExpr = ParseAssign();
            Expect(AwkTokenType.Colon);
            var falseExpr = ParseAssign();
            return new TernaryNode { Condition = cond, TrueExpr = trueExpr, FalseExpr = falseExpr };
        }
        return cond;
    }

    private AwkNode ParseOr()
    {
        var left = ParseAnd();
        while (Check(AwkTokenType.Or))
        {
            Advance();
            left = new BinaryNode { Op = "||", Left = left, Right = ParseAnd() };
        }
        return left;
    }

    private AwkNode ParseAnd()
    {
        var left = ParseInArray();
        while (Check(AwkTokenType.And))
        {
            Advance();
            left = new BinaryNode { Op = "&&", Left = left, Right = ParseInArray() };
        }
        return left;
    }

    private AwkNode ParseInArray()
    {
        var left = ParseMatch();

        if (Check(AwkTokenType.In))
        {
            Advance();
            string arr = Expect(AwkTokenType.Ident).Value;
            // Build in-array node
            var subs = new List<AwkNode>();
            if (left is GroupedExprsNode ge)
            {
                subs.AddRange(ge.Exprs);
            }
            else
            {
                subs.Add(left);
            }
            return new InArrayNode { Subscripts = subs, Array = arr };
        }
        return left;
    }

    private AwkNode ParseMatch()
    {
        var left = ParseComparison();
        while (Check(AwkTokenType.Match) || Check(AwkTokenType.NotMatch))
        {
            bool negated = Peek().Type == AwkTokenType.NotMatch;
            Advance();
            if (Check(AwkTokenType.Regex))
            {
                var pat = Advance().Value;
                left = new MatchExprNode { Expr = left, Pattern = pat, Negated = negated };
            }
            else
            {
                // dynamic regex from expression
                var right = ParseComparison();
                left = new BinaryNode { Op = negated ? "!~" : "~", Left = left, Right = right };
            }
        }
        return left;
    }

    private AwkNode ParseComparison()
    {
        var left = ParseConcat();
        if (Check(AwkTokenType.Lt) || Check(AwkTokenType.Gt) ||
            Check(AwkTokenType.Le) || Check(AwkTokenType.Ge) ||
            Check(AwkTokenType.Eq) || Check(AwkTokenType.Ne))
        {
            var op = Advance().Value;
            var right = ParseConcat();
            return new BinaryNode { Op = op, Left = left, Right = right };
        }
        return left;
    }

    private AwkNode ParseConcat()
    {
        var left = ParseAddSub();
        // Concatenation: two expressions next to each other with no operator
        // But not if next token is an operator, keyword, etc.
        while (CanStartExpr() && !Check(AwkTokenType.Gt) && !Check(AwkTokenType.Append) && !Check(AwkTokenType.Pipe))
        {
            var right = ParseAddSub();
            left = new ConcatNode { Left = left, Right = right };
        }
        return left;
    }

    private bool CanStartExpr()
    {
        var t = Peek().Type;
        return t == AwkTokenType.Number || t == AwkTokenType.String ||
               t == AwkTokenType.Ident || t == AwkTokenType.BuiltinFunc ||
               t == AwkTokenType.Dollar || t == AwkTokenType.LParen ||
               t == AwkTokenType.Not || t == AwkTokenType.Minus ||
               t == AwkTokenType.Increment || t == AwkTokenType.Decrement;
    }

    private AwkNode ParseAddSub()
    {
        var left = ParseMulDiv();
        while (Check(AwkTokenType.Plus) || Check(AwkTokenType.Minus))
        {
            var op = Advance().Value;
            left = new BinaryNode { Op = op, Left = left, Right = ParseMulDiv() };
        }
        return left;
    }

    private AwkNode ParseMulDiv()
    {
        var left = ParsePower();
        while (Check(AwkTokenType.Star) || Check(AwkTokenType.Slash) || Check(AwkTokenType.Percent))
        {
            var op = Advance().Value;
            left = new BinaryNode { Op = op, Left = left, Right = ParsePower() };
        }
        return left;
    }

    private AwkNode ParsePower()
    {
        var left = ParseUnary();
        if (Check(AwkTokenType.Caret))
        {
            Advance();
            var right = ParsePower(); // right-associative
            left = new BinaryNode { Op = "^", Left = left, Right = right };
        }
        return left;
    }

    private AwkNode ParseUnary()
    {
        if (Check(AwkTokenType.Not))
        {
            Advance();
            return new UnaryNode { Op = "!", Operand = ParseUnary() };
        }
        if (Check(AwkTokenType.Minus))
        {
            Advance();
            return new UnaryNode { Op = "-", Operand = ParseUnary() };
        }
        if (Check(AwkTokenType.Plus))
        {
            Advance();
            return ParseUnary();
        }
        if (Check(AwkTokenType.Increment))
        {
            Advance();
            return new UnaryNode { Op = "++", Operand = ParseUnary(), Prefix = true };
        }
        if (Check(AwkTokenType.Decrement))
        {
            Advance();
            return new UnaryNode { Op = "--", Operand = ParseUnary(), Prefix = true };
        }
        return ParsePostfix();
    }

    private AwkNode ParsePostfix()
    {
        var expr = ParsePrimary();
        while (true)
        {
            if (Check(AwkTokenType.Increment))
            {
                Advance();
                expr = new UnaryNode { Op = "++", Operand = expr, Prefix = false };
            }
            else if (Check(AwkTokenType.Decrement))
            {
                Advance();
                expr = new UnaryNode { Op = "--", Operand = expr, Prefix = false };
            }
            else if (Check(AwkTokenType.LBracket))
            {
                // array subscript
                Advance();
                var subs = new List<AwkNode> { ParseExpression() };
                while (Match(AwkTokenType.Comma))
                    subs.Add(ParseExpression());
                Expect(AwkTokenType.RBracket);
                if (expr is IdentNode idn)
                    expr = new ArrayRefNode { Name = idn.Name, Subscripts = subs };
                else
                    break; // shouldn't happen
            }
            else
            {
                break;
            }
        }
        return expr;
    }

    private AwkNode ParsePrimary()
    {
        var tok = Peek();
        switch (tok.Type)
        {
            case AwkTokenType.Number:
                Advance();
                return new NumberNode { Value = tok.NumValue };

            case AwkTokenType.String:
                Advance();
                return new StringNode { Value = tok.Value };

            case AwkTokenType.Regex:
                Advance();
                return new RegexNode { Pattern = tok.Value };

            case AwkTokenType.Dollar:
                Advance();
                var idx = ParsePrimary();
                return new FieldNode { Index = idx };

            case AwkTokenType.LParen:
            {
                Advance();
                var expr = ParseExpression();
                // Check for (expr, expr) in array -- multi-subscript
                if (Check(AwkTokenType.Comma) && !IsPartOfPrint())
                {
                    var exprs = new List<AwkNode> { expr };
                    while (Match(AwkTokenType.Comma))
                        exprs.Add(ParseExpression());
                    Expect(AwkTokenType.RParen);
                    return new GroupedExprsNode { Exprs = exprs };
                }
                Expect(AwkTokenType.RParen);
                return expr;
            }

            case AwkTokenType.Getline:
            {
                Advance();
                var gl = new GetlineNode();
                if (Check(AwkTokenType.Ident) && !Check(AwkTokenType.Semicolon))
                {
                    gl.Var = Advance().Value;
                }
                return gl;
            }

            case AwkTokenType.Ident:
            {
                string name = Advance().Value;
                // Check for function call
                if (Check(AwkTokenType.LParen))
                {
                    Advance();
                    var args = new List<AwkNode>();
                    if (!Check(AwkTokenType.RParen))
                    {
                        args.Add(ParseExpression());
                        while (Match(AwkTokenType.Comma))
                            args.Add(ParseExpression());
                    }
                    Expect(AwkTokenType.RParen);
                    return new FuncCallNode { Name = name, Args = args };
                }
                return new IdentNode { Name = name };
            }

            case AwkTokenType.BuiltinFunc:
            {
                string name = Advance().Value;
                // length can be called without parens
                if (name == "length" && !Check(AwkTokenType.LParen))
                {
                    return new FuncCallNode { Name = name, Args = new List<AwkNode>() };
                }
                Expect(AwkTokenType.LParen);
                var args = new List<AwkNode>();
                if (!Check(AwkTokenType.RParen))
                {
                    // sub/gsub have special syntax: sub(/regex/, replacement [, target])
                    if ((name == "sub" || name == "gsub") && Check(AwkTokenType.Regex))
                    {
                        args.Add(new RegexNode { Pattern = Advance().Value });
                    }
                    else
                    {
                        args.Add(ParseExpression());
                    }
                    while (Match(AwkTokenType.Comma))
                        args.Add(ParseExpression());
                }
                Expect(AwkTokenType.RParen);
                return new FuncCallNode { Name = name, Args = args };
            }

            default:
                throw new AwkException($"Unexpected token: {tok.Type} ('{tok.Value}') at line {tok.Line}");
        }
    }

    private bool IsPartOfPrint()
    {
        // Heuristic: in a print statement's arg list, commas separate args
        // In (expr) in array, commas build multi-subscripts
        // We treat parens as grouping unless followed by 'in'
        // Look ahead to see if there's a matching ) followed by 'in'
        int depth = 1;
        int i = _pos;
        while (i < _tokens.Count && depth > 0)
        {
            if (_tokens[i].Type == AwkTokenType.LParen) depth++;
            else if (_tokens[i].Type == AwkTokenType.RParen) depth--;
            if (depth == 0 && i + 1 < _tokens.Count && _tokens[i + 1].Type == AwkTokenType.In)
                return false;
            i++;
        }
        return true;
    }
}

// Helper node for grouped expressions like (e1, e2) used in multi-dim array access
internal sealed class GroupedExprsNode : AwkNode
{
    public List<AwkNode> Exprs = new();
}

#endregion

#region Interpreter

/// <summary>
/// Exception type for AWK runtime/parse errors.
/// </summary>
public sealed class AwkException : Exception
{
    /// <inheritdoc />
    public AwkException(string message) : base(message) { }
}

// Signal exceptions for control flow
internal sealed class AwkNextException : Exception { }
internal sealed class AwkBreakException : Exception { }
internal sealed class AwkContinueException : Exception { }
internal sealed class AwkExitException : Exception
{
    public int Code;
    public AwkExitException(int code) { Code = code; }
}
internal sealed class AwkReturnException : Exception
{
    public AwkValue Value;
    public AwkReturnException(AwkValue value) { Value = value; }
}

/// <summary>
/// AWK value - can be string or number, converts between the two as needed.
/// </summary>
internal struct AwkValue
{
    private string? _str;
    private double _num;
    private bool _hasNum;
    private bool _hasStr;

    public static readonly AwkValue Uninitialized = new() { _str = "", _num = 0, _hasStr = true, _hasNum = true };
    public static readonly AwkValue Zero = new() { _num = 0, _hasNum = true, _str = "0", _hasStr = true };
    public static readonly AwkValue One = new() { _num = 1, _hasNum = true, _str = "1", _hasStr = true };

    public static AwkValue FromString(string s) => new() { _str = s, _hasStr = true };
    public static AwkValue FromNumber(double n) => new() { _num = n, _hasNum = true };

    public double AsNumber()
    {
        if (_hasNum) return _num;
        _num = ParseAwkNumber(_str ?? "");
        _hasNum = true;
        return _num;
    }

    public string AsString()
    {
        if (_hasStr) return _str ?? "";
        _str = FormatAwkNumber(_num);
        _hasStr = true;
        return _str;
    }

    public bool AsBool()
    {
        // In AWK, 0 and "" are false, everything else is true.
        // When we have a numeric value, use numeric comparison.
        if (_hasNum) return _num != 0;
        if (_hasStr) return !string.IsNullOrEmpty(_str);
        return false;
    }

    private static double ParseAwkNumber(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        s = s.TrimStart();
        // Extract leading numeric part
        int end = 0;
        if (end < s.Length && (s[end] == '+' || s[end] == '-')) end++;
        bool hasDig = false;
        while (end < s.Length && char.IsDigit(s[end])) { end++; hasDig = true; }
        if (end < s.Length && s[end] == '.')
        {
            end++;
            while (end < s.Length && char.IsDigit(s[end])) { end++; hasDig = true; }
        }
        if (hasDig && end < s.Length && (s[end] == 'e' || s[end] == 'E'))
        {
            end++;
            if (end < s.Length && (s[end] == '+' || s[end] == '-')) end++;
            while (end < s.Length && char.IsDigit(s[end])) end++;
        }
        if (!hasDig) return 0;
        if (double.TryParse(s.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            return val;
        return 0;
    }

    internal static string FormatAwkNumber(double n)
    {
        if (n == Math.Floor(n) && !double.IsInfinity(n) && Math.Abs(n) < 1e16)
            return ((long)n).ToString(CultureInfo.InvariantCulture);
        return n.ToString("G6", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// AWK interpreter environment.
/// </summary>
internal sealed class AwkInterpreter
{
    // Global variables
    private readonly Dictionary<string, AwkValue> _globals = new(StringComparer.Ordinal);
    // Arrays
    private readonly Dictionary<string, Dictionary<string, AwkValue>> _arrays = new(StringComparer.Ordinal);
    // User-defined functions
    private readonly Dictionary<string, FunctionDefNode> _functions = new(StringComparer.Ordinal);
    // Local variable stack for function calls
    private readonly Stack<Dictionary<string, AwkValue>> _localStack = new();
    private readonly Stack<Dictionary<string, Dictionary<string, AwkValue>>> _localArrayStack = new();

    // Fields
    private string[] _fields = Array.Empty<string>();
    private string _record = "";
    private int _nr;
    private int _fnr;
    private string _filename = "";

    // Output
    private readonly StringBuilder _output = new();
    private int _exitCode;
    private bool _inEndBlock;

    // Getline support
    private string[] _currentRecords = Array.Empty<string>();
    private int _recordIndex;

    // Range pattern state
    private readonly Dictionary<int, bool> _rangeActive = new();

    // Random state
    private Random _rng = new Random();

    // SUBSEP default
    private const string DefaultSubsep = "\x1c";

    public AwkInterpreter()
    {
        _globals["FS"] = AwkValue.FromString(" ");
        _globals["RS"] = AwkValue.FromString("\n");
        _globals["OFS"] = AwkValue.FromString(" ");
        _globals["ORS"] = AwkValue.FromString("\n");
        _globals["NR"] = AwkValue.FromNumber(0);
        _globals["NF"] = AwkValue.FromNumber(0);
        _globals["FNR"] = AwkValue.FromNumber(0);
        _globals["FILENAME"] = AwkValue.FromString("");
        _globals["SUBSEP"] = AwkValue.FromString(DefaultSubsep);
        _globals["RSTART"] = AwkValue.FromNumber(0);
        _globals["RLENGTH"] = AwkValue.FromNumber(0);
    }

    public (string Output, int ExitCode) Execute(ProgramNode program, string input, string[] filenames,
        Dictionary<string, string>? presetVars = null)
    {
        // Register functions
        foreach (var fn in program.Functions)
            _functions[fn.Name] = fn;

        // Apply preset variables (from -v)
        if (presetVars != null)
        {
            foreach (var kv in presetVars)
                SetVariable(kv.Key, AwkValue.FromString(kv.Value));
        }

        try
        {
            // Execute BEGIN blocks
            foreach (var rule in program.Rules)
            {
                if (rule.IsBegin && rule.Action != null)
                    ExecuteBlock(rule.Action);
            }

            // Process input
            if (filenames.Length == 0)
            {
                // Read from stdin (input string)
                ProcessInput(input, "");
            }
            else
            {
                foreach (var file in filenames)
                {
                    _fnr = 0;
                    _filename = file;
                    _globals["FILENAME"] = AwkValue.FromString(file);
                    string content = File.ReadAllText(file);
                    ProcessInput(content, file);
                }
            }

            // Execute END blocks
            _globals["NR"] = AwkValue.FromNumber(_nr);
            _inEndBlock = true;
            foreach (var rule in program.Rules)
            {
                if (rule.IsEnd && rule.Action != null)
                    ExecuteBlock(rule.Action);
            }
        }
        catch (AwkExitException ex)
        {
            _exitCode = ex.Code;
            // Still run END blocks after exit, but NOT if exit was from an END block
            if (!_inEndBlock)
            {
                try
                {
                    _inEndBlock = true;
                    foreach (var rule in program.Rules)
                    {
                        if (rule.IsEnd && rule.Action != null)
                            ExecuteBlock(rule.Action);
                    }
                }
                catch (AwkExitException ex2)
                {
                    _exitCode = ex2.Code;
                }
            }
        }

        return (_output.ToString(), _exitCode);
    }

    private void ProcessInput(string input, string filename)
    {
        string rs = GetVariable("RS").AsString();
        string[] records;

        if (rs == "\n")
        {
            // Default: split on newlines
            bool hadTrailingNewline = input.Length > 0 && input[^1] == '\n';
            if (hadTrailingNewline)
                input = input[..^1]; // remove trailing newline
            if (input.Length == 0 && hadTrailingNewline)
                records = new[] { "" }; // one empty record
            else if (input.Length == 0)
                records = Array.Empty<string>();
            else
                records = input.Split('\n');
        }
        else if (rs.Length == 1)
        {
            records = input.Split(rs[0]);
            // Remove trailing empty record if input ends with RS
            if (records.Length > 0 && records[^1] == "")
                records = records[..^1];
        }
        else if (rs.Length == 0)
        {
            // Paragraph mode: split on blank lines
            records = Regex.Split(input, @"\n\n+");
            records = records.Where(r => r.Length > 0).ToArray();
        }
        else
        {
            // RS is a regex (awk extension)
            records = Regex.Split(input, rs);
            if (records.Length > 0 && records[^1] == "")
                records = records[..^1];
        }

        var program = GetCurrentProgram();
        _currentRecords = records;

        for (_recordIndex = 0; _recordIndex < _currentRecords.Length; _recordIndex++)
        {
            _nr++;
            _fnr++;
            _record = _currentRecords[_recordIndex];
            _globals["NR"] = AwkValue.FromNumber(_nr);
            _globals["FNR"] = AwkValue.FromNumber(_fnr);
            SplitRecord();

            try
            {
                ExecuteMainRules(program);
            }
            catch (AwkNextException)
            {
                // continue to next record
            }
        }
    }

    // Store reference to current program for use in ProcessInput
    private ProgramNode? _currentProgram;

    private ProgramNode GetCurrentProgram() => _currentProgram!;

    public (string Output, int ExitCode) Execute(ProgramNode program, string input,
        Dictionary<string, string>? presetVars = null)
    {
        _currentProgram = program;
        return Execute(program, input, Array.Empty<string>(), presetVars);
    }

    public (string Output, int ExitCode) ExecuteWithFiles(ProgramNode program, string[] filenames,
        Dictionary<string, string>? presetVars = null)
    {
        _currentProgram = program;
        return Execute(program, "", filenames, presetVars);
    }

    private void SplitRecord()
    {
        string fs = GetVariable("FS").AsString();
        if (fs == " ")
        {
            // Default: split on whitespace, strip leading/trailing
            var parts = _record.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            _fields = parts;
        }
        else if (fs.Length == 1)
        {
            _fields = _record.Split(fs[0]);
        }
        else
        {
            // regex split
            try
            {
                _fields = Regex.Split(_record, fs);
            }
            catch
            {
                _fields = new[] { _record };
            }
        }
        _globals["NF"] = AwkValue.FromNumber(_fields.Length);
    }

    private void ExecuteMainRules(ProgramNode program)
    {
        for (int i = 0; i < program.Rules.Count; i++)
        {
            var rule = program.Rules[i];
            if (rule.IsBegin || rule.IsEnd) continue;

            bool matched = false;

            if (rule.Pattern == null && rule.Pattern2 == null)
            {
                matched = true;
            }
            else if (rule.Pattern2 != null)
            {
                // Range pattern
                if (!_rangeActive.ContainsKey(i))
                    _rangeActive[i] = false;

                if (!_rangeActive[i])
                {
                    if (EvalBool(rule.Pattern!))
                    {
                        _rangeActive[i] = true;
                        matched = true;
                    }
                }
                else
                {
                    matched = true;
                    if (EvalBool(rule.Pattern2))
                        _rangeActive[i] = false;
                }
            }
            else
            {
                matched = EvalBool(rule.Pattern!);
            }

            if (matched)
            {
                if (rule.Action != null)
                    ExecuteBlock(rule.Action);
                else
                {
                    // Default action: print $0
                    _output.Append(_record);
                    _output.Append(GetVariable("ORS").AsString());
                }
            }
        }
    }

    private bool EvalBool(AwkNode node)
    {
        if (node is RegexNode rx)
        {
            return Regex.IsMatch(_record, rx.Pattern);
        }
        var val = Eval(node);
        // Numeric 0 or empty string is false
        return val.AsBool();
    }

    private void ExecuteBlock(BlockNode block)
    {
        foreach (var stmt in block.Statements)
            ExecuteStatement(stmt);
    }

    private void ExecuteStatement(AwkNode node)
    {
        switch (node)
        {
            case BlockNode block:
                ExecuteBlock(block);
                break;
            case PrintNode pn:
                ExecutePrint(pn);
                break;
            case PrintfNode pf:
                ExecutePrintf(pf);
                break;
            case IfNode ifn:
                if (EvalBool(ifn.Condition))
                    ExecuteStatement(ifn.ThenBranch);
                else if (ifn.ElseBranch != null)
                    ExecuteStatement(ifn.ElseBranch);
                break;
            case WhileNode wh:
                while (EvalBool(wh.Condition))
                {
                    try { ExecuteStatement(wh.Body); }
                    catch (AwkBreakException) { break; }
                    catch (AwkContinueException) { }
                }
                break;
            case DoWhileNode dw:
                do
                {
                    try { ExecuteStatement(dw.Body); }
                    catch (AwkBreakException) { break; }
                    catch (AwkContinueException) { }
                } while (EvalBool(dw.Condition));
                break;
            case ForNode fn:
                if (fn.Init != null) Eval(fn.Init);
                while (fn.Condition == null || EvalBool(fn.Condition))
                {
                    try { ExecuteStatement(fn.Body); }
                    catch (AwkBreakException) { break; }
                    catch (AwkContinueException) { }
                    if (fn.Update != null) Eval(fn.Update);
                }
                break;
            case ForInNode fi:
                var arr = GetArray(fi.Array);
                foreach (var key in arr.Keys.ToList())
                {
                    SetVariable(fi.Var, AwkValue.FromString(key));
                    try { ExecuteStatement(fi.Body); }
                    catch (AwkBreakException) { break; }
                    catch (AwkContinueException) { }
                }
                break;
            case NextNode:
                throw new AwkNextException();
            case BreakNode:
                throw new AwkBreakException();
            case ContinueNode:
                throw new AwkContinueException();
            case ExitNode ex:
                int code = ex.Code != null ? (int)Eval(ex.Code).AsNumber() : 0;
                throw new AwkExitException(code);
            case DeleteNode del:
                if (del.Subscripts == null)
                {
                    // delete entire array
                    if (_arrays.ContainsKey(del.Array))
                        _arrays[del.Array].Clear();
                }
                else
                {
                    string key = BuildSubscript(del.Subscripts);
                    if (_arrays.TryGetValue(del.Array, out var a))
                        a.Remove(key);
                }
                break;
            case ReturnNode ret:
                var rv = ret.Value != null ? Eval(ret.Value) : AwkValue.Uninitialized;
                throw new AwkReturnException(rv);
            case ExpressionStatementNode es:
                Eval(es.Expr);
                break;
            default:
                Eval(node);
                break;
        }
    }

    private void ExecutePrint(PrintNode pn)
    {
        string ofs = GetVariable("OFS").AsString();
        string ors = GetVariable("ORS").AsString();

        if (pn.Args.Count == 0)
        {
            _output.Append(_record);
        }
        else
        {
            for (int i = 0; i < pn.Args.Count; i++)
            {
                if (i > 0) _output.Append(ofs);
                _output.Append(Eval(pn.Args[i]).AsString());
            }
        }
        _output.Append(ors);
    }

    private void ExecutePrintf(PrintfNode pf)
    {
        if (pf.Args.Count == 0) return;
        string fmt = Eval(pf.Args[0]).AsString();
        var args = new List<AwkValue>();
        for (int i = 1; i < pf.Args.Count; i++)
            args.Add(Eval(pf.Args[i]));
        _output.Append(FormatPrintf(fmt, args));
    }

    internal static string FormatPrintf(string fmt, List<AwkValue> args)
    {
        var sb = new StringBuilder();
        int argIdx = 0;
        int i = 0;
        while (i < fmt.Length)
        {
            if (fmt[i] == '%')
            {
                i++;
                if (i >= fmt.Length) break;
                if (fmt[i] == '%') { sb.Append('%'); i++; continue; }

                // Parse format spec
                bool leftAlign = false;
                bool zeroPad = false;
                bool space = false;
                bool plus = false;

                // Flags
                while (i < fmt.Length && "- +0#".Contains(fmt[i]))
                {
                    switch (fmt[i])
                    {
                        case '-': leftAlign = true; break;
                        case '0': zeroPad = true; break;
                        case ' ': space = true; break;
                        case '+': plus = true; break;
                    }
                    i++;
                }

                // Width
                int width = 0;
                if (i < fmt.Length && fmt[i] == '*')
                {
                    i++;
                    if (argIdx < args.Count)
                        width = (int)args[argIdx++].AsNumber();
                }
                else
                {
                    while (i < fmt.Length && char.IsDigit(fmt[i]))
                    {
                        width = width * 10 + (fmt[i] - '0');
                        i++;
                    }
                }

                // Precision
                int precision = -1;
                if (i < fmt.Length && fmt[i] == '.')
                {
                    i++;
                    precision = 0;
                    if (i < fmt.Length && fmt[i] == '*')
                    {
                        i++;
                        if (argIdx < args.Count)
                            precision = (int)args[argIdx++].AsNumber();
                    }
                    else
                    {
                        while (i < fmt.Length && char.IsDigit(fmt[i]))
                        {
                            precision = precision * 10 + (fmt[i] - '0');
                            i++;
                        }
                    }
                }

                if (i >= fmt.Length) break;
                char spec = fmt[i++];
                var arg = argIdx < args.Count ? args[argIdx++] : AwkValue.Uninitialized;

                string formatted = spec switch
                {
                    'd' or 'i' => FormatInt((long)arg.AsNumber(), width, leftAlign, zeroPad, plus, space),
                    'o' => FormatOctal((long)arg.AsNumber(), width, leftAlign, zeroPad),
                    'x' => FormatHex((long)arg.AsNumber(), width, leftAlign, zeroPad, false),
                    'X' => FormatHex((long)arg.AsNumber(), width, leftAlign, zeroPad, true),
                    'f' => FormatFloat(arg.AsNumber(), width, precision < 0 ? 6 : precision, leftAlign, zeroPad),
                    'e' => FormatSci(arg.AsNumber(), width, precision < 0 ? 6 : precision, leftAlign, 'e'),
                    'E' => FormatSci(arg.AsNumber(), width, precision < 0 ? 6 : precision, leftAlign, 'E'),
                    'g' or 'G' => FormatG(arg.AsNumber(), width, precision < 0 ? 6 : precision, leftAlign, spec),
                    's' => FormatString(arg.AsString(), width, precision, leftAlign),
                    'c' => FormatChar(arg, width, leftAlign),
                    _ => spec.ToString(),
                };
                sb.Append(formatted);
            }
            else if (fmt[i] == '\\')
            {
                i++;
                if (i < fmt.Length)
                {
                    switch (fmt[i])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case 'a': sb.Append('\a'); break;
                        default: sb.Append('\\'); sb.Append(fmt[i]); break;
                    }
                    i++;
                }
            }
            else
            {
                sb.Append(fmt[i++]);
            }
        }
        return sb.ToString();
    }

    private static string FormatInt(long val, int width, bool left, bool zeroPad, bool plus, bool space)
    {
        string s = val.ToString(CultureInfo.InvariantCulture);
        if (plus && val >= 0) s = "+" + s;
        else if (space && val >= 0) s = " " + s;
        return Pad(s, width, left, zeroPad);
    }

    private static string FormatOctal(long val, int width, bool left, bool zeroPad)
    {
        string s = Convert.ToString(val, 8);
        return Pad(s, width, left, zeroPad);
    }

    private static string FormatHex(long val, int width, bool left, bool zeroPad, bool upper)
    {
        string s = val.ToString(upper ? "X" : "x", CultureInfo.InvariantCulture);
        return Pad(s, width, left, zeroPad);
    }

    private static string FormatFloat(double val, int width, int prec, bool left, bool zeroPad)
    {
        string s = val.ToString("F" + prec, CultureInfo.InvariantCulture);
        return Pad(s, width, left, zeroPad);
    }

    private static string FormatSci(double val, int width, int prec, bool left, char eChar)
    {
        string format = eChar == 'e' ? "e" : "E";
        string s = val.ToString($"{format}{prec}", CultureInfo.InvariantCulture);
        return Pad(s, width, left, false);
    }

    private static string FormatG(double val, int width, int prec, bool left, char spec)
    {
        if (prec == 0) prec = 1;
        string s = val.ToString($"G{prec}", CultureInfo.InvariantCulture);
        if (spec == 'G') s = s.Replace('e', 'E');
        return Pad(s, width, left, false);
    }

    private static string FormatString(string val, int width, int prec, bool left)
    {
        if (prec >= 0 && val.Length > prec)
            val = val[..prec];
        return Pad(val, width, left, false);
    }

    private static string FormatChar(AwkValue arg, int width, bool left)
    {
        char c;
        // In AWK, %c with a numeric arg converts to character by ASCII code
        string s = arg.AsString();
        if (s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            c = (char)(int)num;
        else if (s.Length > 0)
            c = s[0];
        else
            c = '\0';
        return Pad(c.ToString(), width, left, false);
    }

    private static string Pad(string s, int width, bool left, bool zeroPad)
    {
        if (s.Length >= width) return s;
        int pad = width - s.Length;
        if (left)
            return s + new string(' ', pad);
        return new string(zeroPad ? '0' : ' ', pad) + s;
    }

    private AwkValue Eval(AwkNode node)
    {
        switch (node)
        {
            case NumberNode nn:
                return AwkValue.FromNumber(nn.Value);

            case StringNode sn:
                return AwkValue.FromString(sn.Value);

            case RegexNode rx:
                // Standalone regex matches against $0
                return Regex.IsMatch(_record, rx.Pattern) ? AwkValue.One : AwkValue.Zero;

            case IdentNode id:
                return GetVariable(id.Name);

            case FieldNode fn:
            {
                int idx = (int)Eval(fn.Index).AsNumber();
                return AwkValue.FromString(GetField(idx));
            }

            case ArrayRefNode ar:
            {
                string key = BuildSubscript(ar.Subscripts);
                var arr = GetArray(ar.Name);
                return arr.TryGetValue(key, out var v) ? v : AwkValue.Uninitialized;
            }

            case InArrayNode ia:
            {
                string key = BuildSubscript(ia.Subscripts);
                var arr = GetArray(ia.Array);
                return arr.ContainsKey(key) ? AwkValue.One : AwkValue.Zero;
            }

            case AssignNode an:
                return EvalAssign(an);

            case BinaryNode bn:
                return EvalBinary(bn);

            case UnaryNode un:
                return EvalUnary(un);

            case TernaryNode tn:
                return EvalBool(tn.Condition) ? Eval(tn.TrueExpr) : Eval(tn.FalseExpr);

            case ConcatNode cn:
                return AwkValue.FromString(Eval(cn.Left).AsString() + Eval(cn.Right).AsString());

            case MatchExprNode me:
            {
                string val = Eval(me.Expr).AsString();
                bool matches = Regex.IsMatch(val, me.Pattern);
                if (me.Negated) matches = !matches;
                return matches ? AwkValue.One : AwkValue.Zero;
            }

            case FuncCallNode fc:
                return EvalFuncCall(fc);

            case GetlineNode gl:
                return EvalGetline(gl);

            case ExpressionStatementNode es:
                return Eval(es.Expr);

            case GroupedExprsNode ge:
                // Evaluate all, return last
                AwkValue last = AwkValue.Uninitialized;
                foreach (var e in ge.Exprs)
                    last = Eval(e);
                return last;

            default:
                return AwkValue.Uninitialized;
        }
    }

    private AwkValue EvalAssign(AssignNode an)
    {
        AwkValue newVal;
        if (an.Op == "=")
        {
            newVal = Eval(an.Value);
        }
        else
        {
            var cur = Eval(an.Target);
            var rhs = Eval(an.Value);
            newVal = an.Op switch
            {
                "+=" => AwkValue.FromNumber(cur.AsNumber() + rhs.AsNumber()),
                "-=" => AwkValue.FromNumber(cur.AsNumber() - rhs.AsNumber()),
                "*=" => AwkValue.FromNumber(cur.AsNumber() * rhs.AsNumber()),
                "/=" => AwkValue.FromNumber(cur.AsNumber() / rhs.AsNumber()),
                "%=" => AwkValue.FromNumber(cur.AsNumber() % rhs.AsNumber()),
                _ => rhs,
            };
        }
        SetLValue(an.Target, newVal);
        return newVal;
    }

    private AwkValue EvalBinary(BinaryNode bn)
    {
        switch (bn.Op)
        {
            case "+": return AwkValue.FromNumber(Eval(bn.Left).AsNumber() + Eval(bn.Right).AsNumber());
            case "-": return AwkValue.FromNumber(Eval(bn.Left).AsNumber() - Eval(bn.Right).AsNumber());
            case "*": return AwkValue.FromNumber(Eval(bn.Left).AsNumber() * Eval(bn.Right).AsNumber());
            case "/": return AwkValue.FromNumber(Eval(bn.Left).AsNumber() / Eval(bn.Right).AsNumber());
            case "%": return AwkValue.FromNumber(Eval(bn.Left).AsNumber() % Eval(bn.Right).AsNumber());
            case "^": return AwkValue.FromNumber(Math.Pow(Eval(bn.Left).AsNumber(), Eval(bn.Right).AsNumber()));

            case "&&":
                return EvalBool(bn.Left) && EvalBool(bn.Right) ? AwkValue.One : AwkValue.Zero;
            case "||":
                return EvalBool(bn.Left) || EvalBool(bn.Right) ? AwkValue.One : AwkValue.Zero;

            case "~":
            {
                string s = Eval(bn.Left).AsString();
                string p = Eval(bn.Right).AsString();
                return Regex.IsMatch(s, p) ? AwkValue.One : AwkValue.Zero;
            }
            case "!~":
            {
                string s = Eval(bn.Left).AsString();
                string p = Eval(bn.Right).AsString();
                return !Regex.IsMatch(s, p) ? AwkValue.One : AwkValue.Zero;
            }

            // Comparison - string vs numeric depends on operand types
            case "==": case "!=": case "<": case ">": case "<=": case ">=":
                return EvalComparison(bn);

            default:
                return AwkValue.Uninitialized;
        }
    }

    private AwkValue EvalComparison(BinaryNode bn)
    {
        var left = Eval(bn.Left);
        var right = Eval(bn.Right);

        // If both look numeric, compare numerically
        bool leftNum = IsNumericValue(left);
        bool rightNum = IsNumericValue(right);

        int cmp;
        if (leftNum && rightNum)
        {
            double ld = left.AsNumber(), rd = right.AsNumber();
            cmp = ld.CompareTo(rd);
        }
        else
        {
            cmp = string.Compare(left.AsString(), right.AsString(), StringComparison.Ordinal);
        }

        bool result = bn.Op switch
        {
            "==" => cmp == 0,
            "!=" => cmp != 0,
            "<" => cmp < 0,
            ">" => cmp > 0,
            "<=" => cmp <= 0,
            ">=" => cmp >= 0,
            _ => false,
        };
        return result ? AwkValue.One : AwkValue.Zero;
    }

    private static bool IsNumericValue(AwkValue v)
    {
        string s = v.AsString();
        if (s.Length == 0) return true; // empty is 0
        return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private AwkValue EvalUnary(UnaryNode un)
    {
        if (un.Op == "!")
            return EvalBool(un.Operand) ? AwkValue.Zero : AwkValue.One;

        if (un.Op == "-")
            return AwkValue.FromNumber(-Eval(un.Operand).AsNumber());

        if (un.Op == "++" || un.Op == "--")
        {
            var cur = Eval(un.Operand);
            double val = cur.AsNumber();
            double delta = un.Op == "++" ? 1 : -1;
            var newVal = AwkValue.FromNumber(val + delta);
            SetLValue(un.Operand, newVal);
            return un.Prefix ? newVal : cur;
        }

        return Eval(un.Operand);
    }

    private AwkValue EvalFuncCall(FuncCallNode fc)
    {
        // Built-in functions
        switch (fc.Name)
        {
            case "length":
            {
                if (fc.Args.Count == 0)
                    return AwkValue.FromNumber(_record.Length);
                var arg = fc.Args[0];
                // Check if it's an array name
                if (arg is IdentNode idn && _arrays.ContainsKey(idn.Name))
                    return AwkValue.FromNumber(_arrays[idn.Name].Count);
                return AwkValue.FromNumber(Eval(arg).AsString().Length);
            }

            case "substr":
            {
                string s = Eval(fc.Args[0]).AsString();
                int start = (int)Eval(fc.Args[1]).AsNumber();
                if (start < 1) start = 1;
                if (start > s.Length) return AwkValue.FromString("");
                if (fc.Args.Count >= 3)
                {
                    int len = (int)Eval(fc.Args[2]).AsNumber();
                    if (len < 0) len = 0;
                    int actual = Math.Min(len, s.Length - start + 1);
                    if (actual <= 0) return AwkValue.FromString("");
                    return AwkValue.FromString(s.Substring(start - 1, actual));
                }
                return AwkValue.FromString(s[(start - 1)..]);
            }

            case "index":
            {
                string s = Eval(fc.Args[0]).AsString();
                string t = Eval(fc.Args[1]).AsString();
                int idx = s.IndexOf(t, StringComparison.Ordinal);
                return AwkValue.FromNumber(idx < 0 ? 0 : idx + 1);
            }

            case "split":
            {
                string s = Eval(fc.Args[0]).AsString();
                // fc.Args[1] is the array name
                string arrName;
                if (fc.Args[1] is IdentNode ain)
                    arrName = ain.Name;
                else
                    arrName = Eval(fc.Args[1]).AsString();

                string sep = fc.Args.Count >= 3 ? Eval(fc.Args[2]).AsString() : GetVariable("FS").AsString();
                string[] parts;
                if (sep == " ")
                    parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                else if (sep.Length == 1)
                    parts = s.Split(sep[0]);
                else
                    parts = Regex.Split(s, sep);

                var arr = GetArray(arrName);
                arr.Clear();
                for (int i = 0; i < parts.Length; i++)
                    arr[(i + 1).ToString()] = AwkValue.FromString(parts[i]);
                return AwkValue.FromNumber(parts.Length);
            }

            case "sub":
            {
                string pattern = fc.Args[0] is RegexNode rx ? rx.Pattern : Eval(fc.Args[0]).AsString();
                string replacement = Eval(fc.Args[1]).AsString();
                // Process & in replacement (match reference)
                replacement = ProcessAwkReplacement(replacement);

                if (fc.Args.Count >= 3)
                {
                    // Target is a variable/field
                    string target = Eval(fc.Args[2]).AsString();
                    var regex = new Regex(pattern);
                    string result = regex.Replace(target, replacement, 1);
                    bool changed = result != target;
                    SetLValue(fc.Args[2], AwkValue.FromString(result));
                    return AwkValue.FromNumber(changed ? 1 : 0);
                }
                else
                {
                    // Default target is $0
                    var regex = new Regex(pattern);
                    string result = regex.Replace(_record, replacement, 1);
                    bool changed = result != _record;
                    SetField(0, result);
                    return AwkValue.FromNumber(changed ? 1 : 0);
                }
            }

            case "gsub":
            {
                string pattern = fc.Args[0] is RegexNode rx ? rx.Pattern : Eval(fc.Args[0]).AsString();
                string replacement = Eval(fc.Args[1]).AsString();
                replacement = ProcessAwkReplacement(replacement);

                string target;
                if (fc.Args.Count >= 3)
                    target = Eval(fc.Args[2]).AsString();
                else
                    target = _record;

                var regex = new Regex(pattern);
                int count = 0;
                string result = regex.Replace(target, m => { count++; return m.Result(replacement); });

                if (fc.Args.Count >= 3)
                    SetLValue(fc.Args[2], AwkValue.FromString(result));
                else
                    SetField(0, result);

                return AwkValue.FromNumber(count);
            }

            case "match":
            {
                string s = Eval(fc.Args[0]).AsString();
                string pat = fc.Args[1] is RegexNode rx ? rx.Pattern : Eval(fc.Args[1]).AsString();
                var m = Regex.Match(s, pat);
                if (m.Success)
                {
                    _globals["RSTART"] = AwkValue.FromNumber(m.Index + 1);
                    _globals["RLENGTH"] = AwkValue.FromNumber(m.Length);
                    return AwkValue.FromNumber(m.Index + 1);
                }
                _globals["RSTART"] = AwkValue.FromNumber(0);
                _globals["RLENGTH"] = AwkValue.FromNumber(-1);
                return AwkValue.FromNumber(0);
            }

            case "sprintf":
            {
                string fmt = Eval(fc.Args[0]).AsString();
                var args = new List<AwkValue>();
                for (int i = 1; i < fc.Args.Count; i++)
                    args.Add(Eval(fc.Args[i]));
                return AwkValue.FromString(FormatPrintf(fmt, args));
            }

            case "toupper":
                return AwkValue.FromString(Eval(fc.Args[0]).AsString().ToUpperInvariant());

            case "tolower":
                return AwkValue.FromString(Eval(fc.Args[0]).AsString().ToLowerInvariant());

            case "int":
                return AwkValue.FromNumber((long)Eval(fc.Args[0]).AsNumber());

            case "sqrt":
                return AwkValue.FromNumber(Math.Sqrt(Eval(fc.Args[0]).AsNumber()));

            case "sin":
                return AwkValue.FromNumber(Math.Sin(Eval(fc.Args[0]).AsNumber()));

            case "cos":
                return AwkValue.FromNumber(Math.Cos(Eval(fc.Args[0]).AsNumber()));

            case "atan2":
                return AwkValue.FromNumber(Math.Atan2(Eval(fc.Args[0]).AsNumber(), Eval(fc.Args[1]).AsNumber()));

            case "exp":
                return AwkValue.FromNumber(Math.Exp(Eval(fc.Args[0]).AsNumber()));

            case "log":
                return AwkValue.FromNumber(Math.Log(Eval(fc.Args[0]).AsNumber()));

            case "rand":
                return AwkValue.FromNumber(_rng.NextDouble());

            case "srand":
            {
                int seed = fc.Args.Count > 0 ? (int)Eval(fc.Args[0]).AsNumber() : Environment.TickCount;
                _rng = new Random(seed);
                return AwkValue.FromNumber(seed);
            }

            case "system":
            {
                string cmd = Eval(fc.Args[0]).AsString();
                try
                {
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "/bin/sh", Arguments = $"-c \"{cmd}\"",
                        UseShellExecute = false
                    });
                    p?.WaitForExit();
                    return AwkValue.FromNumber(p?.ExitCode ?? -1);
                }
                catch { return AwkValue.FromNumber(-1); }
            }

            default:
                // User-defined function
                if (_functions.TryGetValue(fc.Name, out var funcDef))
                    return CallUserFunction(funcDef, fc.Args);
                throw new AwkException($"Unknown function: {fc.Name}");
        }
    }

    private static string ProcessAwkReplacement(string replacement)
    {
        // AWK uses & to refer to the matched text, \& for literal &
        // Convert to .NET regex replacement: & -> $0, \& -> &
        var sb = new StringBuilder();
        for (int i = 0; i < replacement.Length; i++)
        {
            if (replacement[i] == '\\' && i + 1 < replacement.Length && replacement[i + 1] == '&')
            {
                sb.Append('&');
                i++;
            }
            else if (replacement[i] == '&')
            {
                sb.Append("$0");
            }
            else
            {
                sb.Append(replacement[i]);
            }
        }
        return sb.ToString();
    }

    private AwkValue CallUserFunction(FunctionDefNode func, List<AwkNode> argNodes)
    {
        // Evaluate arguments
        var argValues = new List<AwkValue>();
        for (int i = 0; i < argNodes.Count; i++)
            argValues.Add(Eval(argNodes[i]));

        // Save current locals and create new scope
        var savedVars = new Dictionary<string, AwkValue>(StringComparer.Ordinal);
        var savedArrays = new Dictionary<string, Dictionary<string, AwkValue>>(StringComparer.Ordinal);

        foreach (var param in func.Params)
        {
            if (_globals.TryGetValue(param, out var v))
                savedVars[param] = v;
            if (_arrays.TryGetValue(param, out var a))
                savedArrays[param] = a;
        }

        _localStack.Push(savedVars);
        _localArrayStack.Push(savedArrays);

        try
        {
            // Set parameters
            for (int i = 0; i < func.Params.Count; i++)
            {
                if (i < argValues.Count)
                    _globals[func.Params[i]] = argValues[i];
                else
                    _globals[func.Params[i]] = AwkValue.Uninitialized;

                // Clear any array with same name
                _arrays.Remove(func.Params[i]);
            }

            ExecuteBlock(func.Body);
            return AwkValue.Uninitialized;
        }
        catch (AwkReturnException ret)
        {
            return ret.Value;
        }
        finally
        {
            // Restore saved variables
            var sv = _localStack.Pop();
            var sa = _localArrayStack.Pop();

            foreach (var param in func.Params)
            {
                if (sv.TryGetValue(param, out var v))
                    _globals[param] = v;
                else
                    _globals.Remove(param);

                if (sa.TryGetValue(param, out var a))
                    _arrays[param] = a;
                else
                    _arrays.Remove(param);
            }
        }
    }

    private AwkValue EvalGetline(GetlineNode gl)
    {
        // Simple getline: reads next line from current input
        _recordIndex++;
        if (_recordIndex >= _currentRecords.Length)
            return AwkValue.FromNumber(0); // no more input

        _nr++;
        _fnr++;
        _record = _currentRecords[_recordIndex];
        _globals["NR"] = AwkValue.FromNumber(_nr);
        _globals["FNR"] = AwkValue.FromNumber(_fnr);
        SplitRecord();

        // If getline has a variable, assign to it; otherwise update $0
        if (gl.Var != null)
            SetVariable(gl.Var, AwkValue.FromString(_record));

        return AwkValue.FromNumber(1); // success
    }

    private string GetField(int idx)
    {
        if (idx == 0) return _record;
        if (idx < 1 || idx > _fields.Length) return "";
        return _fields[idx - 1];
    }

    private void SetField(int idx, string value)
    {
        string ofs = GetVariable("OFS").AsString();
        if (idx == 0)
        {
            _record = value;
            SplitRecord();
        }
        else
        {
            // Extend fields if needed
            if (idx > _fields.Length)
            {
                var newFields = new string[idx];
                Array.Copy(_fields, newFields, _fields.Length);
                for (int i = _fields.Length; i < idx; i++)
                    newFields[i] = "";
                _fields = newFields;
            }
            _fields[idx - 1] = value;
            _globals["NF"] = AwkValue.FromNumber(_fields.Length);
            // Rebuild $0
            _record = string.Join(ofs, _fields);
        }
    }

    private AwkValue GetVariable(string name)
    {
        if (_globals.TryGetValue(name, out var v)) return v;
        return AwkValue.Uninitialized;
    }

    private void SetVariable(string name, AwkValue value)
    {
        _globals[name] = value;
        // Handle special variables
        if (name == "NF")
        {
            int nf = (int)value.AsNumber();
            if (nf < _fields.Length)
            {
                _fields = _fields[..nf];
                string ofs = GetVariable("OFS").AsString();
                _record = string.Join(ofs, _fields);
            }
            else if (nf > _fields.Length)
            {
                var newFields = new string[nf];
                Array.Copy(_fields, newFields, _fields.Length);
                for (int i = _fields.Length; i < nf; i++)
                    newFields[i] = "";
                _fields = newFields;
                string ofs = GetVariable("OFS").AsString();
                _record = string.Join(ofs, _fields);
            }
        }
        else if (name == "FS")
        {
            // FS changed, will take effect on next record
        }
        else if (name == "$0" || name == "0")
        {
            // hmm, this shouldn't happen via SetVariable
        }
    }

    private void SetLValue(AwkNode target, AwkValue value)
    {
        switch (target)
        {
            case IdentNode id:
                SetVariable(id.Name, value);
                break;
            case FieldNode fn:
                int idx = (int)Eval(fn.Index).AsNumber();
                SetField(idx, value.AsString());
                break;
            case ArrayRefNode ar:
                string key = BuildSubscript(ar.Subscripts);
                var arr = GetArray(ar.Name);
                arr[key] = value;
                break;
            default:
                break;
        }
    }

    private Dictionary<string, AwkValue> GetArray(string name)
    {
        if (!_arrays.TryGetValue(name, out var arr))
        {
            arr = new Dictionary<string, AwkValue>(StringComparer.Ordinal);
            _arrays[name] = arr;
        }
        return arr;
    }

    private string BuildSubscript(List<AwkNode> subs)
    {
        if (subs.Count == 1)
            return Eval(subs[0]).AsString();

        string subsep = GetVariable("SUBSEP").AsString();
        var sb = new StringBuilder();
        for (int i = 0; i < subs.Count; i++)
        {
            if (i > 0) sb.Append(subsep);
            sb.Append(Eval(subs[i]).AsString());
        }
        return sb.ToString();
    }
}

#endregion


#region Public API

/// <summary>
/// Compiled AWK program that can be executed multiple times without re-parsing.
/// Analogous to SedScript for sed. The AST is immutable; each Execute call
/// creates a fresh interpreter so executions are independent.
/// </summary>
public sealed class AwkScript
{
    internal ProgramNode Ast { get; }

    internal AwkScript(ProgramNode ast)
    {
        Ast = ast;
    }

    /// <summary>
    /// Execute the compiled AWK program on string input.
    /// </summary>
    public (string Output, int ExitCode) Execute(string input,
        string? fieldSeparator = null, Dictionary<string, string>? variables = null)
    {
        var interp = new AwkInterpreter();
        var vars = variables != null ? new Dictionary<string, string>(variables) : new Dictionary<string, string>();
        if (fieldSeparator != null)
            vars["FS"] = fieldSeparator;
        return interp.Execute(Ast, input, vars);
    }

    /// <summary>
    /// Execute the compiled AWK program reading from files.
    /// </summary>
    public (string Output, int ExitCode) Execute(string[] filenames,
        string? fieldSeparator = null, Dictionary<string, string>? variables = null)
    {
        var interp = new AwkInterpreter();
        var vars = variables != null ? new Dictionary<string, string>(variables) : new Dictionary<string, string>();
        if (fieldSeparator != null)
            vars["FS"] = fieldSeparator;
        return interp.ExecuteWithFiles(Ast, filenames, vars);
    }

    /// <summary>
    /// Execute the compiled AWK program with TextReader/TextWriter for pipeline use.
    /// </summary>
    public int Execute(TextReader input, TextWriter output,
        string? fieldSeparator = null, Dictionary<string, string>? variables = null)
    {
        string text = input.ReadToEnd();
        var (result, exitCode) = Execute(text, fieldSeparator, variables);
        output.Write(result);
        return exitCode;
    }
}

/// <summary>
/// Public AWK engine API for FredDotNet.
/// </summary>
public static class AwkEngine
{
    /// <summary>
    /// Compile an AWK program into a reusable AwkScript.
    /// </summary>
    public static AwkScript Compile(string program)
    {
        var parser = new AwkParser(program);
        var ast = parser.Parse();
        return new AwkScript(ast);
    }

    /// <summary>
    /// Execute an AWK program on input text.
    /// </summary>
    public static (string Output, int ExitCode) Execute(string program, string input,
        string? fieldSeparator = null, Dictionary<string, string>? variables = null)
    {
        return Compile(program).Execute(input, fieldSeparator, variables);
    }

    /// <summary>
    /// Execute an AWK program reading from files.
    /// </summary>
    public static (string Output, int ExitCode) ExecuteWithFiles(string program, string[] filenames,
        string? fieldSeparator = null, Dictionary<string, string>? variables = null)
    {
        return Compile(program).Execute(filenames, fieldSeparator, variables);
    }
}

#endregion

