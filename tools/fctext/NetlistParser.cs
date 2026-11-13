using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LastCallBBS.FoodCourt;

public sealed class NetlistException : Exception
{
    public NetlistException(int line, int column, string message) : base($"line {line}:{column}: {message}") { }
}

/// <summary>A position in the source, for messages.</summary>
public readonly struct SourceSpot
{
    public readonly int Line, Column;
    public SourceSpot(int line, int column) { Line = line; Column = column; }
    public NetlistException Error(string message) => new(Line, Column, message);
}

public abstract class NetExpr
{
    public SourceSpot At;
}

/// <summary>A name, optionally with a socket: <c>go</c>, <c>scan.WHISKY</c>, <c>a.B</c>.</summary>
public sealed class NetRef : NetExpr
{
    public string Name, Socket;
    public override string ToString() => Socket == null ? Name : Name + "." + Socket;
}

public sealed class NetOr : NetExpr
{
    public List<NetExpr> Terms = new();
}

public sealed class NetAnd : NetExpr
{
    public NetExpr Left, Right;
}

/// <summary>A function such as <c>delay(go, 3)</c>. Arguments are expressions or numbers.</summary>
public sealed class NetCall : NetExpr
{
    public string Name;
    public List<NetExpr> Args = new();
}

/// <summary><c>count(3*a + b - 2) == 0</c>: a weighted count of earlier ticks, compared with a number.</summary>
public sealed class NetCount : NetExpr
{
    public List<(int Weight, NetExpr Signal)> Terms = new();
    public int Constant, Rhs;
    public string Op;
}

public sealed class NetNumber : NetExpr
{
    public int Value;
}

public sealed class NetPort
{
    public SourceSpot At;
    public string Socket;
    public NetExpr Value;
}

public abstract class NetStatement
{
    public SourceSpot At;
}

public sealed class NetBlock : NetStatement
{
    public bool Fixed;
    public string Type, Name;
    public List<int> Params;
    public int? FloorX, FloorY, Facing;
    public List<NetPort> Ports = new();
    /// <summary>Sequencers: channel index to the steps it's on.</summary>
    public Dictionary<int, List<int>> Steps;
}

public sealed class NetAssign : NetStatement
{
    public string Name;
    public NetExpr Value;
}

public sealed class NetWireDecl : NetStatement
{
    public List<string> Names = new();
}

public sealed class Netlist
{
    public string Level, Name;
    public SourceSpot LevelAt, NameAt;
    public List<NetStatement> Statements = new();
}

/// <summary>Reads the netlist language described in specs/food-court-netlist.md.</summary>
public static class NetlistParser
{
    private enum Kind { Ident, Int, String, Punct, End }

    private sealed class Token
    {
        public Kind Kind;
        public string Text;
        public int Line, Column;
        public SourceSpot At => new(Line, Column);
        public override string ToString() => Kind == Kind.End ? "end of file" : Kind == Kind.String ? "a string" : $"\"{Text}\"";
    }

    public static readonly string[] Facings = { "right", "up", "left", "down" };

    public static Netlist Parse(string source) => new Reader(Lex(source)).File();

    /// <summary>An expression as source text, for messages and the report.</summary>
    public static string Describe(NetExpr e) => e switch
    {
        NetRef r => r.ToString(),
        NetNumber n => n.Value.ToString(CultureInfo.InvariantCulture),
        NetOr or => "(" + string.Join(" | ", or.Terms.Select(Describe)) + ")",
        NetAnd and => "(" + Describe(and.Left) + " & " + Describe(and.Right) + ")",
        NetCall call => call.Name + "(" + string.Join(", ", call.Args.Select(Describe)) + ")",
        NetCount c => "count(" + Sum(c) + ") " + c.Op + " " + c.Rhs,
        _ => "?",
    };

    private static string Sum(NetCount c)
    {
        var parts = c.Terms.Select(t => (t.Weight, Text: (Math.Abs(t.Weight) == 1 ? "" : Math.Abs(t.Weight) + "*") + Describe(t.Signal))).ToList();
        if (c.Constant != 0)
            parts.Add((c.Constant, Math.Abs(c.Constant).ToString(CultureInfo.InvariantCulture)));
        if (parts.Count == 0)
            return "0";
        var text = new StringBuilder(parts[0].Weight < 0 ? "-" + parts[0].Text : parts[0].Text);
        foreach (var part in parts.Skip(1))
            text.Append(part.Weight < 0 ? " - " : " + ").Append(part.Text);
        return text.ToString();
    }

    private static List<Token> Lex(string s)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1, lineStart = 0;
        while (i < s.Length)
        {
            char c = s[i];
            int column = i - lineStart + 1;
            if (c == '\n')
            {
                i++;
                line++;
                lineStart = i;
            }
            else if (char.IsWhiteSpace(c))
                i++;
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n')
                    i++;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    throw new NetlistException(line, column, "comment is never closed");
                for (int k = i; k < end; k++)
                {
                    if (s[k] == '\n')
                    {
                        line++;
                        lineStart = k + 1;
                    }
                }
                i = end + 2;
            }
            else if (char.IsLetter(c) || c == '_' || (char.IsDigit(c) && tokens.Count > 0 && tokens[tokens.Count - 1].Text == "."))
            {
                // After a dot, a socket name can start with a digit, such as scan.3_PCS.
                int start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                    i++;
                tokens.Add(new Token { Kind = Kind.Ident, Text = s.Substring(start, i - start), Line = line, Column = column });
            }
            else if (char.IsDigit(c))
            {
                int start = i++;
                while (i < s.Length && char.IsDigit(s[i]))
                    i++;
                tokens.Add(new Token { Kind = Kind.Int, Text = s.Substring(start, i - start), Line = line, Column = column });
            }
            else if (c == '"')
            {
                var text = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= s.Length || s[i] == '\n')
                        throw new NetlistException(line, column, "string is never closed");
                    if (s[i] == '"')
                        break;
                    if (s[i] == '\\' && i + 1 < s.Length)
                        i++;
                    text.Append(s[i++]);
                }
                i++;
                tokens.Add(new Token { Kind = Kind.String, Text = text.ToString(), Line = line, Column = column });
            }
            else if (i + 1 < s.Length && (c is '=' or '<' or '>' or '!') && s[i + 1] == '=')
            {
                tokens.Add(new Token { Kind = Kind.Punct, Text = s.Substring(i, 2), Line = line, Column = column });
                i += 2;
            }
            else if (";,()[]{}.#@=|&+-*<>".IndexOf(c) >= 0)
            {
                tokens.Add(new Token { Kind = Kind.Punct, Text = c.ToString(), Line = line, Column = column });
                i++;
            }
            else if (c is '~' or '!')
                throw new NetlistException(line, column, "there's no NOT; inversion needs a counter");
            else
                throw new NetlistException(line, column, $"unexpected character '{c}'");
        }
        tokens.Add(new Token { Kind = Kind.End, Text = "", Line = line, Column = i - lineStart + 1 });
        return tokens;
    }

    private sealed class Reader
    {
        private readonly List<Token> _t;
        private int _p;

        public Reader(List<Token> tokens) => _t = tokens;

        private Token Peek => _t[_p];
        private Token Next() => _t[_p++];
        private bool IsPunct(string p) => Peek.Kind == Kind.Punct && Peek.Text == p;
        private bool IsWord(string w) => Peek.Kind == Kind.Ident && Peek.Text == w;

        private bool Accept(string p)
        {
            if (!IsPunct(p))
                return false;
            _p++;
            return true;
        }

        private void Expect(string p, string what = null)
        {
            if (!Accept(p))
                throw Peek.At.Error($"expected \"{p}\"{(what == null ? "" : " " + what)}, found {Peek}");
        }

        private string Ident(string what)
        {
            if (Peek.Kind != Kind.Ident)
                throw Peek.At.Error($"expected {what}, found {Peek}");
            return Next().Text;
        }

        private int Int(string what)
        {
            if (IsPunct("-") && _t[_p + 1].Kind == Kind.Int)
            {
                Next();
                return -Int(what);
            }
            if (Peek.Kind != Kind.Int)
                throw Peek.At.Error($"expected {what}, found {Peek}");
            Token t = Next();
            if (!int.TryParse(t.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
                throw t.At.Error($"{t.Text} is out of range");
            return value;
        }

        public Netlist File()
        {
            var n = new Netlist();
            while (Peek.Kind != Kind.End)
            {
                SourceSpot at = Peek.At;
                if (IsWord("level"))
                {
                    Next();
                    if (n.Level != null)
                        throw at.Error("level is given twice");
                    n.Level = Ident("a level name");
                    n.LevelAt = at;
                    Expect(";");
                }
                else if (IsWord("name"))
                {
                    Next();
                    if (n.Name != null)
                        throw at.Error("name is given twice");
                    if (Peek.Kind != Kind.String)
                        throw Peek.At.Error($"expected the solution name in quotes, found {Peek}");
                    n.Name = Next().Text;
                    n.NameAt = at;
                    Expect(";");
                }
                else if (IsWord("assign"))
                {
                    Next();
                    var a = new NetAssign { At = at, Name = Ident("a net name") };
                    Expect("=");
                    a.Value = Expr();
                    Expect(";");
                    n.Statements.Add(a);
                }
                else if (IsWord("wire"))
                {
                    Next();
                    var w = new NetWireDecl { At = at };
                    do
                        w.Names.Add(Ident("a net name"));
                    while (Accept(","));
                    Expect(";");
                    n.Statements.Add(w);
                }
                else
                    n.Statements.Add(Block());
            }
            if (n.Level == null)
                throw new NetlistException(1, 1, "missing \"level <name>;\"");
            if (n.Name == null)
                throw new NetlistException(1, 1, "missing \"name \\\"...\\\";\"");
            return n;
        }

        private NetBlock Block()
        {
            var b = new NetBlock { At = Peek.At };
            if (IsWord("fixed"))
            {
                Next();
                b.Fixed = true;
            }
            b.Type = Ident("a block type, assign, wire, level or name");
            if (Accept("#"))
            {
                Expect("(", "after #");
                b.Params = new List<int>();
                if (!IsPunct(")"))
                {
                    do
                        b.Params.Add(Int("a number"));
                    while (Accept(","));
                }
                Expect(")");
            }
            if (Peek.Kind == Kind.Ident)
                b.Name = Next().Text;
            if (Accept("@"))
            {
                Expect("(", "after @");
                b.FloorX = Int("an x position");
                Expect(",");
                b.FloorY = Int("a y position");
                Expect(")");
                Token facing = Peek;
                string word = Ident("a facing (right, up, left or down)");
                int index = Array.IndexOf(Facings, word);
                if (index < 0)
                    throw facing.At.Error($"\"{word}\" isn't a facing; use right, up, left or down");
                b.Facing = index;
            }
            if (Accept("("))
            {
                if (!IsPunct(")"))
                {
                    do
                    {
                        SourceSpot at = Peek.At;
                        Expect(".", "before a port name");
                        var port = new NetPort { At = at, Socket = Ident("a port name") };
                        Expect("(");
                        port.Value = Expr();
                        Expect(")");
                        b.Ports.Add(port);
                    }
                    while (Accept(","));
                }
                Expect(")");
            }
            if (IsPunct("{"))
            {
                b.Steps = Steps();
                Accept(";");
            }
            else
                Expect(";", "at the end of the block");
            return b;
        }

        private Dictionary<int, List<int>> Steps()
        {
            Expect("{");
            var steps = new Dictionary<int, List<int>>();
            while (!Accept("}"))
            {
                Token channel = Peek;
                string name = Ident("a channel (A to D)");
                int index = name.Length == 1 ? "ABCD".IndexOf(name[0]) : -1;
                if (index < 0)
                    throw channel.At.Error($"\"{name}\" isn't a channel; use A, B, C or D");
                if (steps.ContainsKey(index))
                    throw channel.At.Error($"channel {name} is given twice");
                Expect("=");
                Expect("[");
                var list = new List<int>();
                if (!IsPunct("]"))
                {
                    do
                    {
                        Token step = Peek;
                        int value = Int("a step number");
                        if (value < 0 || value >= ModuleCatalog.SequencerRows)
                            throw step.At.Error($"step {value} is out of range; steps are 0 to {ModuleCatalog.SequencerRows - 1}");
                        if (list.Contains(value))
                            throw step.At.Error($"step {value} is listed twice");
                        list.Add(value);
                    }
                    while (Accept(","));
                }
                Expect("]");
                Expect(";");
                steps[index] = list;
            }
            return steps;
        }

        // expr := and ('|' and)* ;  and := atom ('&' atom)* ;  atom := ref | '(' expr ')'
        private NetExpr Expr()
        {
            SourceSpot at = Peek.At;
            NetExpr first = AndExpr();
            if (!IsPunct("|"))
                return first;
            var or = new NetOr { At = at };
            or.Terms.Add(first);
            while (Accept("|"))
                or.Terms.Add(AndExpr());
            return or;
        }

        // count(sum) op number, where sum is terms like 3*a, a, -b, 2, joined by + and -.
        private NetCount Count()
        {
            var c = new NetCount { At = Next().At };
            Expect("(", "after count");
            int sign = Accept("-") ? -1 : 1;
            if (sign == 1)
                Accept("+");
            while (true)
            {
                SourceSpot at = Peek.At;
                if (Peek.Kind == Kind.Int)
                {
                    int n = Int("a number");
                    if (Accept("*"))
                        c.Terms.Add((sign * n, Atom()));
                    else
                        c.Constant += sign * n;
                }
                else
                {
                    NetExpr signal = Atom();
                    int n = Accept("*") ? Int("a weight") : 1;
                    c.Terms.Add((sign * n, signal));
                }
                if (Accept("+"))
                    sign = 1;
                else if (Accept("-"))
                    sign = -1;
                else
                    break;
            }
            Expect(")", "to close count(");
            Token op = Peek;
            if (op.Kind != Kind.Punct || op.Text is not ("==" or "!=" or "<" or ">" or "<=" or ">="))
                throw op.At.Error($"count(...) needs a comparison, such as == 0 or > 0; found {op}");
            Next();
            c.Op = op.Text;
            c.Rhs = Int("a number after " + op.Text);
            return c;
        }

        private NetExpr AndExpr()
        {
            NetExpr left = Atom();
            while (IsPunct("&"))
            {
                SourceSpot at = Next().At;
                left = new NetAnd { At = at, Left = left, Right = Atom() };
            }
            return left;
        }

        private NetExpr Atom()
        {
            SourceSpot at = Peek.At;
            if (Accept("("))
            {
                NetExpr inner = Expr();
                Expect(")");
                return inner;
            }
            if (Peek.Kind == Kind.Int || (IsPunct("-") && _t[_p + 1].Kind == Kind.Int))
                return new NetNumber { At = at, Value = Int("a number") };
            if (IsWord("count") && _t[_p + 1].Kind == Kind.Punct && _t[_p + 1].Text == "(")
                return Count();
            string name = Ident("a signal");
            if (Accept("("))
            {
                var call = new NetCall { At = at, Name = name };
                if (!IsPunct(")"))
                {
                    do
                        call.Args.Add(Expr());
                    while (Accept(","));
                }
                Expect(")");
                return call;
            }
            var r = new NetRef { At = at, Name = name };
            if (Accept("."))
                r.Socket = Ident("a socket name");
            return r;
        }
    }
}
