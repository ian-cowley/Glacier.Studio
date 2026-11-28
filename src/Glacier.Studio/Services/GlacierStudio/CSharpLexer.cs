using System;
using System.Collections.Generic;
using System.Text;

namespace Glacier.Studio.Services;

internal enum CSharpTokenKind : byte
{
    Whitespace = 0,
    Comment = 1,
    String = 2,
    Number = 3,
    Preprocessor = 4,
    Identifier = 5,
    Keyword = 6,
    Punctuation = 7
}

internal readonly record struct CSharpToken(int Start, int Length, CSharpTokenKind Kind, int Line);

internal sealed record ExtractedTypeDeclaration(
    string Kind,
    string Name,
    int LineNumber,
    IReadOnlyList<string> BaseTypes,
    IReadOnlyList<string> MemberSummaries,
    string? RecordParameters);

internal sealed record ExtractedMethodDeclaration(string Name, int LineNumber);

internal sealed record ExtractedCSharpSymbols(
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<ExtractedTypeDeclaration> Types,
    IReadOnlyList<ExtractedMethodDeclaration> Methods);

/// <summary>
/// Zero-allocation ReadOnlySpan&lt;char&gt; C# lexer supporting single-line/block comments,
/// normal/verbatim/raw string literals, char literals, preprocessor directives, and 1-based line tracking.
/// </summary>
internal ref struct CSharpLexer
{
    private readonly ReadOnlySpan<char> _text;
    private int _index;
    private int _line;
    private bool _atLineStart;

    public CSharpLexer(ReadOnlySpan<char> text)
    {
        _text = text;
        _index = 0;
        _line = 1;
        _atLineStart = true;
    }

    public ReadOnlySpan<char> Slice(in CSharpToken token) => _text.Slice(token.Start, token.Length);

    public bool NextToken(out CSharpToken token)
    {
        if (_index >= _text.Length)
        {
            token = default;
            return false;
        }

        int start = _index;
        int tokenLine = _line;
        char c = _text[_index];

        // 1. Whitespace (maintains _line and _atLineStart)
        if (char.IsWhiteSpace(c))
        {
            while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
            {
                if (_text[_index] == '\n')
                {
                    _line++;
                    _atLineStart = true;
                }
                _index++;
            }
            token = new CSharpToken(start, _index - start, CSharpTokenKind.Whitespace, tokenLine);
            return true;
        }

        // 2. Preprocessor directive (# at start of line after optional horizontal whitespace)
        if (c == '#' && _atLineStart)
        {
            _atLineStart = false;
            while (_index < _text.Length && _text[_index] != '\n')
            {
                _index++;
            }
            token = new CSharpToken(start, _index - start, CSharpTokenKind.Preprocessor, tokenLine);
            return true;
        }

        _atLineStart = false;

        // 3. Comments (// and /* ... */)
        if (c == '/' && _index + 1 < _text.Length)
        {
            char next = _text[_index + 1];
            if (next == '/')
            {
                _index += 2;
                while (_index < _text.Length && _text[_index] != '\n')
                {
                    _index++;
                }
                token = new CSharpToken(start, _index - start, CSharpTokenKind.Comment, tokenLine);
                return true;
            }
            if (next == '*')
            {
                _index += 2;
                while (_index < _text.Length)
                {
                    if (_text[_index] == '\n')
                    {
                        _line++;
                    }
                    else if (_text[_index] == '*' && _index + 1 < _text.Length && _text[_index + 1] == '/')
                    {
                        _index += 2;
                        break;
                    }
                    _index++;
                }
                token = new CSharpToken(start, _index - start, CSharpTokenKind.Comment, tokenLine);
                return true;
            }
        }

        // 4. Raw string literals ("""...""" or $"""...""" / $$"""...""")
        if (TryScanRawString(out int rawEnd))
        {
            _index = rawEnd;
            token = new CSharpToken(start, _index - start, CSharpTokenKind.String, tokenLine);
            return true;
        }

        // 5. Verbatim strings (@"...", $@"...", @$"...")
        if (TryScanVerbatimPrefix(out int quotePos))
        {
            _index = quotePos + 1;
            while (_index < _text.Length)
            {
                char sc = _text[_index];
                if (sc == '\n')
                {
                    _line++;
                }
                else if (sc == '"')
                {
                    if (_index + 1 < _text.Length && _text[_index + 1] == '"')
                    {
                        _index += 2;
                        continue;
                    }
                    _index++;
                    break;
                }
                _index++;
            }
            token = new CSharpToken(start, _index - start, CSharpTokenKind.String, tokenLine);
            return true;
        }

        // 6. Normal / interpolated string literals ("..." or $"...")
        if (c == '"' || (c == '$' && _index + 1 < _text.Length && _text[_index + 1] == '"'))
        {
            _index += c == '$' ? 2 : 1;
            bool escaped = false;
            while (_index < _text.Length)
            {
                char sc = _text[_index];
                if (sc == '\n')
                {
                    _line++;
                    escaped = false;
                }
                else if (sc == '\\' && !escaped)
                {
                    escaped = true;
                }
                else if (sc == '"' && !escaped)
                {
                    _index++;
                    break;
                }
                else
                {
                    escaped = false;
                }
                _index++;
            }
            token = new CSharpToken(start, _index - start, CSharpTokenKind.String, tokenLine);
            return true;
        }

        // 7. Char literals ('...')
        if (c == '\'')
        {
            _index++;
            bool escaped = false;
            while (_index < _text.Length)
            {
                char sc = _text[_index];
                if (sc == '\n')
                {
                    _line++;
                    break;
                }
                if (sc == '\\' && !escaped)
                {
                    escaped = true;
                }
                else if (sc == '\'' && !escaped)
                {
                    _index++;
                    break;
                }
                else
                {
                    escaped = false;
                }
                _index++;
            }
            token = new CSharpToken(start, _index - start, CSharpTokenKind.String, tokenLine);
            return true;
        }

        // 8. Numeric literals
        if (char.IsDigit(c))
        {
            while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '_' ||
                   (_text[_index] == '.' && _index + 1 < _text.Length && char.IsDigit(_text[_index + 1]))))
            {
                _index++;
            }
            token = new CSharpToken(start, _index - start, CSharpTokenKind.Number, tokenLine);
            return true;
        }

        // 9. Identifiers & keywords (supports @identifier escaping)
        if (c == '@' && _index + 1 < _text.Length && (char.IsLetter(_text[_index + 1]) || _text[_index + 1] == '_'))
        {
            _index += 2;
            while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '_'))
            {
                _index++;
            }
            token = new CSharpToken(start, _index - start, CSharpTokenKind.Identifier, tokenLine);
            return true;
        }

        if (char.IsLetter(c) || c == '_')
        {
            while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '_'))
            {
                _index++;
            }
            int len = _index - start;
            CSharpTokenKind kind = IsKeyword(_text.Slice(start, len)) ? CSharpTokenKind.Keyword : CSharpTokenKind.Identifier;
            token = new CSharpToken(start, len, kind, tokenLine);
            return true;
        }

        // 10. Single-character punctuation / operator
        _index++;
        token = new CSharpToken(start, 1, CSharpTokenKind.Punctuation, tokenLine);
        return true;
    }

    private bool TryScanVerbatimPrefix(out int quotePos)
    {
        if (_text[_index] == '@' && _index + 1 < _text.Length && _text[_index + 1] == '"')
        {
            quotePos = _index + 1;
            return true;
        }
        if (_index + 2 < _text.Length &&
            ((_text[_index] == '$' && _text[_index + 1] == '@' && _text[_index + 2] == '"') ||
             (_text[_index] == '@' && _text[_index + 1] == '$' && _text[_index + 2] == '"')))
        {
            quotePos = _index + 2;
            return true;
        }
        quotePos = -1;
        return false;
    }

    private bool TryScanRawString(out int endIndex)
    {
        int p = _index;
        while (p < _text.Length && _text[p] == '$')
        {
            p++;
        }

        int openQuotes = 0;
        while (p + openQuotes < _text.Length && _text[p + openQuotes] == '"')
        {
            openQuotes++;
        }

        if (openQuotes < 3)
        {
            endIndex = -1;
            return false;
        }

        int scan = p + openQuotes;
        while (scan < _text.Length)
        {
            if (_text[scan] == '\n')
            {
                _line++;
                scan++;
                continue;
            }

            if (_text[scan] == '"')
            {
                int run = 0;
                while (scan + run < _text.Length && _text[scan + run] == '"')
                {
                    run++;
                }
                if (run >= openQuotes)
                {
                    endIndex = scan + openQuotes;
                    return true;
                }
                scan += run;
                continue;
            }

            scan++;
        }

        endIndex = _text.Length;
        return true;
    }

    internal static bool IsKeyword(ReadOnlySpan<char> word) => word.Length switch
    {
        2 => word.SequenceEqual("if") || word.SequenceEqual("in") || word.SequenceEqual("is") || word.SequenceEqual("as") || word.SequenceEqual("do"),
        3 => word.SequenceEqual("for") || word.SequenceEqual("new") || word.SequenceEqual("int") || word.SequenceEqual("out") || word.SequenceEqual("ref") || word.SequenceEqual("try") || word.SequenceEqual("var"),
        4 => word.SequenceEqual("void") || word.SequenceEqual("else") || word.SequenceEqual("this") || word.SequenceEqual("long") || word.SequenceEqual("bool") || word.SequenceEqual("char") || word.SequenceEqual("case") || word.SequenceEqual("byte") || word.SequenceEqual("lock") || word.SequenceEqual("true") || word.SequenceEqual("null") || word.SequenceEqual("enum") || word.SequenceEqual("goto") || word.SequenceEqual("base") || word.SequenceEqual("uint") || word.SequenceEqual("file"),
        5 => word.SequenceEqual("class") || word.SequenceEqual("using") || word.SequenceEqual("break") || word.SequenceEqual("float") || word.SequenceEqual("const") || word.SequenceEqual("throw") || word.SequenceEqual("catch") || word.SequenceEqual("while") || word.SequenceEqual("false") || word.SequenceEqual("sbyte") || word.SequenceEqual("short") || word.SequenceEqual("async"),
        6 => word.SequenceEqual("struct") || word.SequenceEqual("public") || word.SequenceEqual("double") || word.SequenceEqual("static") || word.SequenceEqual("return") || word.SequenceEqual("switch") || word.SequenceEqual("string") || word.SequenceEqual("typeof") || word.SequenceEqual("object") || word.SequenceEqual("ushort") || word.SequenceEqual("ulong") || word.SequenceEqual("sealed") || word.SequenceEqual("unsafe") || word.SequenceEqual("extern") || word.SequenceEqual("record"),
        7 => word.SequenceEqual("private") || word.SequenceEqual("partial") || word.SequenceEqual("decimal") || word.SequenceEqual("finally") || word.SequenceEqual("default") || word.SequenceEqual("virtual"),
        8 => word.SequenceEqual("readonly") || word.SequenceEqual("override") || word.SequenceEqual("internal") || word.SequenceEqual("delegate") || word.SequenceEqual("abstract") || word.SequenceEqual("required") || word.SequenceEqual("volatile"),
        9 => word.SequenceEqual("namespace") || word.SequenceEqual("interface") || word.SequenceEqual("protected"),
        _ => false
    };
}

/// <summary>
/// Single-pass zero-regex C# symbol extractor built on top of <see cref="CSharpLexer"/>.
/// Extracts namespaces, types (with inheritance &amp; body member summaries), and methods while ignoring comments and strings.
/// </summary>
internal static class CSharpSymbolExtractor
{
    public static ExtractedCSharpSymbols Extract(ReadOnlySpan<char> source)
    {
        var tokens = new List<CSharpToken>(Math.Min(4096, Math.Max(16, source.Length / 6)));
        var lexer = new CSharpLexer(source);
        while (lexer.NextToken(out var tok))
        {
            if (tok.Kind is CSharpTokenKind.Whitespace or CSharpTokenKind.Comment or CSharpTokenKind.Preprocessor)
                continue;
            tokens.Add(tok);
        }

        var namespaces = new List<string>();
        var types = new List<ExtractedTypeDeclaration>();
        var methods = new List<ExtractedMethodDeclaration>();

        int braceDepth = 0;
        int parenDepth = 0;
        int bracketDepth = 0;
        int stmtStartIdx = 0;
        var typeScopeDepths = new Stack<int>();

        for (int i = 0; i < tokens.Count; i++)
        {
            CSharpToken t = tokens[i];
            ReadOnlySpan<char> text = source.Slice(t.Start, t.Length);

            if (t.Kind == CSharpTokenKind.Punctuation && t.Length == 1)
            {
                char ch = text[0];
                if (ch == '[')
                {
                    bracketDepth++;
                    continue;
                }
                if (ch == ']')
                {
                    if (bracketDepth > 0) bracketDepth--;
                    if (bracketDepth == 0 && parenDepth == 0)
                    {
                        stmtStartIdx = i + 1;
                    }
                    continue;
                }
                if (ch == '(')
                {
                    if (parenDepth == 0 && bracketDepth == 0)
                    {
                        TryExtractMethod(source, tokens, stmtStartIdx, i, braceDepth, typeScopeDepths, methods);
                    }
                    parenDepth++;
                    continue;
                }
                if (ch == ')')
                {
                    if (parenDepth > 0) parenDepth--;
                    continue;
                }
                if (ch == '{')
                {
                    braceDepth++;
                    stmtStartIdx = i + 1;
                    continue;
                }
                if (ch == '}')
                {
                    while (typeScopeDepths.Count > 0 && typeScopeDepths.Peek() == braceDepth)
                    {
                        typeScopeDepths.Pop();
                    }
                    if (braceDepth > 0) braceDepth--;
                    stmtStartIdx = i + 1;
                    continue;
                }
                if (ch == ';')
                {
                    stmtStartIdx = i + 1;
                    continue;
                }
            }

            if (bracketDepth > 0 || parenDepth > 0)
                continue;

            // Namespace declaration at top-level or nested namespace
            if (t.Kind == CSharpTokenKind.Keyword && text.SequenceEqual("namespace") && i == stmtStartIdx)
            {
                int j = i + 1;
                var nsSb = new StringBuilder();
                while (j < tokens.Count)
                {
                    ReadOnlySpan<char> jt = source.Slice(tokens[j].Start, tokens[j].Length);
                    if (tokens[j].Kind == CSharpTokenKind.Punctuation && jt.Length == 1 && (jt[0] == ';' || jt[0] == '{'))
                        break;
                    if (tokens[j].Kind is CSharpTokenKind.Identifier or CSharpTokenKind.Keyword || (tokens[j].Kind == CSharpTokenKind.Punctuation && jt.SequenceEqual(".")))
                    {
                        nsSb.Append(jt);
                    }
                    j++;
                }
                string ns = nsSb.ToString().Trim();
                if (!string.IsNullOrEmpty(ns))
                {
                    namespaces.Add(ns);
                }
                continue;
            }

            // Type declaration: [modifiers] (class|interface|struct|enum|record [class|struct]) Name...
            if (t.Kind == CSharpTokenKind.Keyword && IsTypeKeyword(text) && IsValidDeclarationHeader(source, tokens, stmtStartIdx, i))
            {
                string kind = text.ToString();
                int nameIdx = i + 1;
                if (kind == "record" && nameIdx < tokens.Count)
                {
                    ReadOnlySpan<char> maybeSub = source.Slice(tokens[nameIdx].Start, tokens[nameIdx].Length);
                    if (tokens[nameIdx].Kind == CSharpTokenKind.Keyword && (maybeSub.SequenceEqual("class") || maybeSub.SequenceEqual("struct")))
                    {
                        nameIdx++;
                    }
                }

                if (nameIdx < tokens.Count && tokens[nameIdx].Kind is CSharpTokenKind.Identifier or CSharpTokenKind.Keyword)
                {
                    string typeName = BuildTypeNameWithGenerics(source, tokens, nameIdx, out int afterNameIdx);
                    int lineNum = tokens[i].Line;

                    // Scan until '{' or ';' for primary constructor params (records/classes) and ': Base1, Base2'
                    int scan = afterNameIdx;
                    string? recordParams = null;
                    if (kind == "record" && scan < tokens.Count && IsPunctuation(source, tokens[scan], '('))
                    {
                        recordParams = ExtractBalancedContent(source, tokens, scan, '(', ')', out int afterParen);
                        scan = afterParen;
                    }

                    var baseTypes = new List<string>();
                    if (scan < tokens.Count && IsPunctuation(source, tokens[scan], ':'))
                    {
                        scan = ParseBaseTypes(source, tokens, scan + 1, baseTypes);
                    }

                    // Advance to '{' or ';'
                    while (scan < tokens.Count &&
                           !IsPunctuation(source, tokens[scan], '{') &&
                           !IsPunctuation(source, tokens[scan], ';'))
                    {
                        scan++;
                    }

                    var members = new List<string>();
                    if (scan < tokens.Count && IsPunctuation(source, tokens[scan], '{'))
                    {
                        ExtractTypeMemberSummaries(source, tokens, scan, members);
                        typeScopeDepths.Push(braceDepth + 1);
                    }

                    types.Add(new ExtractedTypeDeclaration(kind, typeName, lineNum, baseTypes, members, recordParams));
                }
            }
        }

        return new ExtractedCSharpSymbols(namespaces, types, methods);
    }

    private static void TryExtractMethod(
        ReadOnlySpan<char> source,
        List<CSharpToken> tokens,
        int stmtStartIdx,
        int openParenIdx,
        int braceDepth,
        Stack<int> typeScopeDepths,
        List<ExtractedMethodDeclaration> methods)
    {
        if (braceDepth < 1 || typeScopeDepths.Count == 0 || typeScopeDepths.Peek() != braceDepth)
            return;

        if (openParenIdx - 1 < stmtStartIdx)
            return;

        // Skip generic type argument list immediately before '(' e.g. Foo<T>(...)
        int nameIdx = openParenIdx - 1;
        if (IsPunctuation(source, tokens[nameIdx], '>'))
        {
            int angleDepth = 1;
            nameIdx--;
            while (nameIdx >= stmtStartIdx && angleDepth > 0)
            {
                if (IsPunctuation(source, tokens[nameIdx], '>')) angleDepth++;
                else if (IsPunctuation(source, tokens[nameIdx], '<')) angleDepth--;
                nameIdx--;
            }
        }

        if (nameIdx < stmtStartIdx || tokens[nameIdx].Kind != CSharpTokenKind.Identifier)
            return;

        // Must have access modifier (public/internal/protected) matching MethodDeclRegex semantics
        // and at least a return type token before the method name (so constructors/control statements are skipped unless return type present).
        bool hasAccessModifier = false;
        bool hasNonModifierReturnToken = false;
        for (int k = stmtStartIdx; k < nameIdx; k++)
        {
            CSharpToken tk = tokens[k];
            ReadOnlySpan<char> kw = source.Slice(tk.Start, tk.Length);
            if (tk.Kind == CSharpTokenKind.Punctuation)
            {
                char ch = kw[0];
                if (ch is '=' or ';' or '.' or '+' or '-' or '*' or '/' or '!')
                    return; // expression / assignment / invocation, not a declaration
                continue;
            }

            if (tk.Kind == CSharpTokenKind.Keyword)
            {
                if (kw.SequenceEqual("public") || kw.SequenceEqual("internal") || kw.SequenceEqual("protected"))
                {
                    hasAccessModifier = true;
                }
                else if (kw.SequenceEqual("new") || kw.SequenceEqual("return") || kw.SequenceEqual("throw") ||
                         kw.SequenceEqual("if") || kw.SequenceEqual("for") || kw.SequenceEqual("while") ||
                         kw.SequenceEqual("switch") || kw.SequenceEqual("using") || kw.SequenceEqual("catch"))
                {
                    return;
                }
                else if (!IsMethodModifierKeyword(kw))
                {
                    hasNonModifierReturnToken = true;
                }
            }
            else if (tk.Kind == CSharpTokenKind.Identifier)
            {
                hasNonModifierReturnToken = true;
            }
        }

        if (!hasAccessModifier || !hasNonModifierReturnToken)
            return;

        string methodName = source.Slice(tokens[nameIdx].Start, tokens[nameIdx].Length).ToString();
        methods.Add(new ExtractedMethodDeclaration(methodName, tokens[nameIdx].Line));
    }

    private static bool IsValidDeclarationHeader(ReadOnlySpan<char> source, List<CSharpToken> tokens, int stmtStartIdx, int keywordIdx)
    {
        for (int k = stmtStartIdx; k < keywordIdx; k++)
        {
            CSharpToken tk = tokens[k];
            ReadOnlySpan<char> s = source.Slice(tk.Start, tk.Length);
            if (tk.Kind == CSharpTokenKind.Keyword &&
                (s.SequenceEqual("public") || s.SequenceEqual("internal") || s.SequenceEqual("private") ||
                 s.SequenceEqual("protected") || s.SequenceEqual("file") || s.SequenceEqual("static") ||
                 s.SequenceEqual("sealed") || s.SequenceEqual("abstract") || s.SequenceEqual("partial") ||
                 s.SequenceEqual("readonly") || s.SequenceEqual("unsafe") || s.SequenceEqual("new")))
            {
                continue;
            }
            return false;
        }
        return true;
    }

    private static string BuildTypeNameWithGenerics(ReadOnlySpan<char> source, List<CSharpToken> tokens, int nameIdx, out int afterNameIdx)
    {
        var sb = new StringBuilder();
        sb.Append(source.Slice(tokens[nameIdx].Start, tokens[nameIdx].Length));
        int idx = nameIdx + 1;
        if (idx < tokens.Count && IsPunctuation(source, tokens[idx], '<'))
        {
            int depth = 0;
            while (idx < tokens.Count)
            {
                ReadOnlySpan<char> s = source.Slice(tokens[idx].Start, tokens[idx].Length);
                sb.Append(s);
                if (tokens[idx].Kind == CSharpTokenKind.Punctuation && s.Length == 1)
                {
                    if (s[0] == '<') depth++;
                    else if (s[0] == '>')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            idx++;
                            break;
                        }
                    }
                }
                idx++;
            }
        }
        afterNameIdx = idx;
        return sb.ToString();
    }

    private static string? ExtractBalancedContent(ReadOnlySpan<char> source, List<CSharpToken> tokens, int openIdx, char openChar, char closeChar, out int afterIdx)
    {
        int startToken = openIdx + 1;
        int depth = 1;
        int idx = openIdx + 1;
        while (idx < tokens.Count && depth > 0)
        {
            if (IsPunctuation(source, tokens[idx], openChar)) depth++;
            else if (IsPunctuation(source, tokens[idx], closeChar)) depth--;
            idx++;
        }
        afterIdx = idx;
        int endToken = idx - 1;
        if (endToken <= startToken)
            return null;

        int charStart = tokens[startToken].Start;
        int charEnd = tokens[endToken - 1].Start + tokens[endToken - 1].Length;
        if (charEnd - charStart > 600)
            return null;

        return NormalizeWhitespace(source.Slice(charStart, charEnd - charStart));
    }

    private static int ParseBaseTypes(ReadOnlySpan<char> source, List<CSharpToken> tokens, int startIdx, List<string> baseTypes)
    {
        int idx = startIdx;
        int angleDepth = 0;
        int parenDepth = 0;
        bool expectingBase = true;

        while (idx < tokens.Count)
        {
            CSharpToken tk = tokens[idx];
            ReadOnlySpan<char> s = source.Slice(tk.Start, tk.Length);

            if (tk.Kind == CSharpTokenKind.Punctuation && s.Length == 1)
            {
                char ch = s[0];
                if (angleDepth == 0 && parenDepth == 0 && (ch == '{' || ch == ';'))
                    break;
                if (ch == '<') angleDepth++;
                else if (ch == '>' && angleDepth > 0) angleDepth--;
                else if (ch == '(') parenDepth++;
                else if (ch == ')' && parenDepth > 0) parenDepth--;
                else if (ch == ',' && angleDepth == 0 && parenDepth == 0)
                {
                    expectingBase = true;
                }
                idx++;
                continue;
            }

            if (tk.Kind == CSharpTokenKind.Identifier && s.SequenceEqual("where") && angleDepth == 0 && parenDepth == 0)
                break;

            if (expectingBase && angleDepth == 0 && parenDepth == 0 && tk.Kind == CSharpTokenKind.Identifier)
            {
                // Keep walking dotted qualification if e.g. System.IDisposable -> take final simple identifier or full name?
                // Existing regex split on space and took first token (e.g. IFoo or BaseClass). Let's read dotted identifier.
                var sb = new StringBuilder();
                sb.Append(s);
                while (idx + 2 < tokens.Count && IsPunctuation(source, tokens[idx + 1], '.') && tokens[idx + 2].Kind == CSharpTokenKind.Identifier)
                {
                    sb.Append('.');
                    sb.Append(source.Slice(tokens[idx + 2].Start, tokens[idx + 2].Length));
                    idx += 2;
                }
                string candidate = sb.ToString();
                if (candidate.Length > 0 && char.IsLetter(candidate[0]))
                {
                    baseTypes.Add(candidate);
                }
                expectingBase = false;
            }

            idx++;
        }

        return idx;
    }

    private static void ExtractTypeMemberSummaries(ReadOnlySpan<char> source, List<CSharpToken> tokens, int openBraceIdx, List<string> members)
    {
        int depth = 1;
        int idx = openBraceIdx + 1;
        int closeBraceIdx = -1;
        while (idx < tokens.Count && depth > 0)
        {
            if (IsPunctuation(source, tokens[idx], '{')) depth++;
            else if (IsPunctuation(source, tokens[idx], '}'))
            {
                depth--;
                if (depth == 0)
                {
                    closeBraceIdx = idx;
                    break;
                }
            }
            idx++;
        }

        if (closeBraceIdx < 0)
            return;

        int bodyCharLen = tokens[closeBraceIdx].Start - tokens[openBraceIdx].Start;
        if (bodyCharLen >= 2500)
            return;

        // Collect clean line-based member summaries from the bounded type body (ignoring comments via lexer tokens!)
        int scan = openBraceIdx + 1;
        int currentLine = -1;
        var lineSb = new StringBuilder();
        int innerDepth = 0;
        int attrDepth = 0;

        for (int k = scan; k < closeBraceIdx && members.Count < 10; k++)
        {
            CSharpToken tk = tokens[k];
            ReadOnlySpan<char> s = source.Slice(tk.Start, tk.Length);

            if (tk.Kind == CSharpTokenKind.Punctuation && s.Length == 1)
            {
                char ch = s[0];
                if (ch == '[') { attrDepth++; continue; }
                if (ch == ']') { if (attrDepth > 0) attrDepth--; continue; }
                if (ch == '{') { innerDepth++; continue; }
                if (ch == '}') { if (innerDepth > 0) innerDepth--; continue; }
            }

            if (attrDepth > 0 || innerDepth > 0)
                continue;

            if (currentLine != tk.Line)
            {
                FlushMemberLine(lineSb, members);
                currentLine = tk.Line;
            }

            if (tk.Kind == CSharpTokenKind.Punctuation && s.Length == 1 && (s[0] == ',' || s[0] == ';'))
            {
                FlushMemberLine(lineSb, members);
                continue;
            }

            if (lineSb.Length > 0 && NeedsSpaceBetween(lineSb[^1], s[0]))
            {
                lineSb.Append(' ');
            }
            lineSb.Append(s);
        }

        FlushMemberLine(lineSb, members);
    }

    private static void FlushMemberLine(StringBuilder sb, List<string> members)
    {
        if (sb.Length == 0 || members.Count >= 10)
        {
            sb.Clear();
            return;
        }
        string clean = sb.ToString().Trim();
        sb.Clear();
        if (!string.IsNullOrEmpty(clean) && char.IsLetter(clean[0]) && clean.Length < 90)
        {
            members.Add(clean);
        }
    }

    private static bool NeedsSpaceBetween(char prev, char next)
    {
        if (prev is '<' or '(' or '[' or '.' || next is '<' or '>' or '(' or ')' or '[' or ']' or '.' or ',')
            return false;
        return true;
    }

    private static string NormalizeWhitespace(ReadOnlySpan<char> span)
    {
        var sb = new StringBuilder(span.Length);
        bool inWs = false;
        for (int i = 0; i < span.Length; i++)
        {
            if (char.IsWhiteSpace(span[i]))
            {
                if (!inWs && sb.Length > 0)
                {
                    sb.Append(' ');
                    inWs = true;
                }
            }
            else
            {
                sb.Append(span[i]);
                inWs = false;
            }
        }
        return sb.ToString().Trim();
    }

    private static bool IsTypeKeyword(ReadOnlySpan<char> word) =>
        word.SequenceEqual("class") ||
        word.SequenceEqual("interface") ||
        word.SequenceEqual("struct") ||
        word.SequenceEqual("record") ||
        word.SequenceEqual("enum");

    private static bool IsMethodModifierKeyword(ReadOnlySpan<char> word) =>
        word.SequenceEqual("static") ||
        word.SequenceEqual("async") ||
        word.SequenceEqual("virtual") ||
        word.SequenceEqual("override") ||
        word.SequenceEqual("unsafe") ||
        word.SequenceEqual("sealed") ||
        word.SequenceEqual("abstract") ||
        word.SequenceEqual("extern") ||
        word.SequenceEqual("partial");

    private static bool IsPunctuation(ReadOnlySpan<char> source, in CSharpToken token, char c) =>
        token.Kind == CSharpTokenKind.Punctuation && token.Length == 1 && source[token.Start] == c;
}
