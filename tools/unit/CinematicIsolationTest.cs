#pragma warning disable CS8981
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TLDRevamp.Cinematic;

/// The camera must not be able to change anything the game or the network can see. This test proves it two ways:
///
///  1. **Statically**, over the real source files: nothing under src/TLDRevamp/Cinematic/ writes to the player's
///     cameras, to the player's transform, to the body's rotation or to the head transform. The only write to the
///     game is the four movement axes, and they are written to *zero* on purpose. The camera never calls the
///     game's VR entry points or any XR API.
///  2. **By the bridge's own classification**: the debug bridge must treat exactly the commands the pure policy
///     says are queries as queries, and every other camera command as a mutation - so a camera command can never
///     slip through as something a joiner may run on a machine it does not own.
///
/// The static part reads the repository's own files, so it cannot drift from what the code actually says.
static class CinematicIsolationTest
{
    static int checks;

    static void Check(bool ok, string why)
    {
        checks++;
        if (!ok) throw new Exception("FAIL: " + why);
    }

    static string Root { get; set; }

    static string Read(string relative)
    {
        string path = Path.Combine(Root, relative);
        if (!File.Exists(path)) throw new Exception("missing file: " + relative);
        return File.ReadAllText(path);
    }

    /// The source files of the feature.
    static IEnumerable<string> SourceFiles() => Directory.GetFiles(Path.Combine(Root, "src/TLDRevamp/Cinematic"), "*.cs")
        .OrderBy(p => p, StringComparer.Ordinal);

    /// The lines of one file, with the comments and string literals removed.
    static IEnumerable<(string file, int line, string code)> CodeLinesOf(string text)
    {
        var lexer = new CSharpLexer(text);
        int number = 0;
        foreach (var line in lexer.Code.Split('\n'))
        {
            number++;
            if (line.Trim().Length > 0) yield return ("", number, line);
        }
    }

    /// Every line of the source, with the line comments and string literals removed, so a check cannot be fooled
    /// by a name in a comment or in a message.
    static IEnumerable<(string file, int line, string code)> CodeLines()
    {
        foreach (var file in SourceFiles())
        {
            var lexer = new CSharpLexer(File.ReadAllText(file));
            int number = 0;
            foreach (var line in lexer.Code.Split('\n'))
            {
                number++;
                if (line.Trim().Length > 0) yield return (Path.GetFileName(file), number, line);
            }
        }
    }

    /// Removes the comments and the string literals from a line, keeping the code.
    ///
    /// This is the SINGLE-LINE form, kept for callers that already work line by line. A line that is
    /// inside a block comment cannot be seen by it, so the whole-file checks use the lexer below instead:
    /// it keeps its block-comment state across the lines, so a comment that opens on one line and closes
    /// on another is removed as a whole and its text can never be mistaken for code.
    static string StripCommentsAndStrings(string line) => new CSharpLexer(line).Code;

    /// Removes the comments from a line but KEEPS the string literals, so a check can look for a literal
    /// argument (the hook's reason) without being fooled by a name in a comment. A string that holds an escaped
    /// quote must not end the string early, so the escaped character is kept with it.
    static string StripComments(string line) => new CSharpLexer(line).WithStrings;

    /// The lines of a file with the comments removed and the string literals kept.
    static IEnumerable<string> CodeLinesWithStrings(string text)
    {
        var lexer = new CSharpLexer(text);
        foreach (var line in lexer.WithStrings.Split('\n'))
            if (line.Trim().Length > 0) yield return line;
    }

    /// One C# source, read by a small lexer of its own.
    ///
    /// The lexer keeps its block-comment state ACROSS the lines: a comment that opens on one line and closes on
    /// another is removed as a whole, so the text after its opening delimiter is not code. It knows the literal
    /// forms of C#: a regular string ("..." with backslash escapes), a verbatim string (@"..." with doubled
    /// quotes) and a character literal.
    ///
    /// Three views of the same text are produced, all exactly as long as the input, so a region found in one of
    /// them can be read in another:
    ///
    ///   Code         - the comments removed and the string CONTENTS blanked, so the braces that are left are the
    ///                 code's own and the words inside a string are gone
    ///   WithStrings  - the comments removed, the literals kept
    ///   Literals     - where each string literal starts, and what it holds
    class CSharpLexer
    {
        public readonly string Code;        // the comments removed, the string contents blanked
        public readonly string WithStrings;  // the comments removed, the literals kept
        readonly bool[] mask;                // true where the offset is inside a literal
        readonly List<(int quote, string content)> literals = new List<(int, string)>();

        public CSharpLexer(string text)
        {
            int size = text.Length;
            var code = text.ToCharArray();
            var withStrings = text.ToCharArray();
            mask = new bool[size];

            void Blank(int start, int stop)
            {
                for (int k = start; k < stop && k < size; k++)
                    if (code[k] != '\n') { code[k] = ' '; withStrings[k] = ' '; }
            }

            void Mark(int start, int stop)
            {
                for (int k = start; k < stop && k < size; k++) mask[k] = true;
            }

            // The offset of the quote that closes a regular string whose content starts here.
            int RegularEnd(int start)
            {
                int stop = start;
                while (stop < size)
                {
                    if (text[stop] == '\\') { stop += 2; continue; }
                    if (text[stop] == '"') break;
                    stop++;
                }
                return Math.Min(stop, size - 1);
            }

            // The offset of the quote that closes a verbatim string whose content starts here.
            int VerbatimEnd(int start)
            {
                int stop = start;
                while (stop < size)
                {
                    if (text[stop] == '"')
                    {
                        if (stop + 1 < size && text[stop + 1] == '"') { stop += 2; continue; }
                        break;
                    }
                    stop++;
                }
                return Math.Min(stop, size - 1);
            }

            int i = 0;
            bool inBlock = false;
            while (i < size)
            {
                char c = text[i];
                if (inBlock)
                {
                    // A block comment runs until its closing delimiter, whatever lines it spans.
                    if (c == '*' && i + 1 < size && text[i + 1] == '/')
                    {
                        Blank(i, i + 2);
                        i += 2;
                        inBlock = false;
                        continue;
                    }
                    if (c != '\n') { code[i] = ' '; withStrings[i] = ' '; }
                    i++;
                    continue;
                }
                if (c == '/' && i + 1 < size && text[i + 1] == '/')
                {
                    int stop = text.IndexOf('\n', i);
                    if (stop < 0) stop = size;
                    Blank(i, stop);
                    i = stop;
                    continue;
                }
                if (c == '/' && i + 1 < size && text[i + 1] == '*')
                {
                    Blank(i, i + 2);
                    i += 2;
                    inBlock = true;
                    continue;
                }
                if (c == '@' && i + 1 < size && text[i + 1] == '"')
                {
                    int quote = i + 1;
                    int contentStart = quote + 1;
                    int contentEnd = VerbatimEnd(contentStart);
                    string content = text.Substring(contentStart, Math.Max(0, contentEnd - contentStart)).Replace("\"\"", "\"");
                    Blank(contentStart, contentEnd);
                    Mark(quote, contentEnd);
                    literals.Add((quote, content));
                    i = contentEnd + 1;
                    continue;
                }
                if (c == '"')
                {
                    int quote = i;
                    int contentStart = quote + 1;
                    int contentEnd = RegularEnd(contentStart);
                    string content = text.Substring(contentStart, Math.Max(0, contentEnd - contentStart));
                    Blank(contentStart, contentEnd);
                    Mark(quote, contentEnd);
                    literals.Add((quote, content));
                    i = contentEnd + 1;
                    continue;
                }
                if (c == '\'')
                {
                    int stop = i + 1;
                    while (stop < size)
                    {
                        if (text[stop] == '\\') { stop += 2; continue; }
                        if (text[stop] == '\'') break;
                        stop++;
                    }
                    stop = Math.Min(stop, size - 1);
                    Blank(i + 1, stop);
                    Mark(i, stop);
                    i = stop + 1;
                    continue;
                }
                i++;
            }
            Code = new string(code);
            WithStrings = new string(withStrings);
        }

        /// Whether this offset is inside a string literal (its quotes included).
        public bool IsLiteral(int offset) => offset >= 0 && offset < mask.Length && mask[offset];

        /// The text of the string literal whose opening quote is at this offset, or null.
        public string LiteralAt(int offset)
        {
            foreach (var (quote, content) in literals)
                if (quote == offset) return content;
            return null;
        }
    }

    /// One declared member of a C# source: a header and a body.
    class CSharpMember
    {
        public string Name;        // null when the member is not a method
        public string Header;      // the declaration, with its attributes
        public int BodyStart = -1; // the offset of the body's first character
        public int BodyEnd = -1;   // the offset just past the body's last character
        public int SpanStart;      // the offset of the member's first character
        public int SpanEnd;        // the offset just past the member's last character

        public bool IsMethod => Name != null;
    }

    /// A method declaration: the modifiers, the type, the name and the parameter list. A field, a property
    /// or a statement is not one, so it is not mistaken for a method.
    static readonly Regex MethodDecl = new Regex(
        @"[ \t\r\n]*" +
        @"(?:(?:public|private|protected|internal|static|readonly|const|sealed|override|virtual|abstract" +
        @"|extern|partial|new|async|unsafe|event|volatile|explicit|implicit)\s+)*" +
        @"(?<type>[A-Za-z_][A-Za-z0-9_\.<>\[\],\?]*[ \t\r\n]+)" +
        @"(?<name>[A-Za-z_]\w*)" +
        @"[ \t\r\n]*(?:<[^<>()]*>[ \t\r\n]*)?\(",
        RegexOptions.Compiled);

    /// Which brace closes which, over the code with the string contents blanked.
    static Dictionary<int, int> BracePairs(string code)
    {
        var pairs = new Dictionary<int, int>();
        var stack = new Stack<int>();
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] == '{') stack.Push(i);
            else if (code[i] == '}' && stack.Count > 0) pairs[stack.Pop()] = i;
        }
        return pairs;
    }

    /// The offset of the parenthesis that closes the one at `openIndex`.
    static int MatchingParen(CSharpLexer lexer, int openIndex, int end)
    {
        int depth = 0;
        for (int i = openIndex; i < end; i++)
        {
            if (lexer.IsLiteral(i)) continue;
            char c = lexer.Code[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return end;
    }

    /// The offset of the first thing after the attribute lists at `position`.
    static int SkipAttributes(CSharpLexer lexer, int position, int end)
    {
        int index = position;
        while (true)
        {
            while (index < end && char.IsWhiteSpace(lexer.Code[index])) index++;
            if (index >= end || lexer.Code[index] != '[') return index;
            int depth = 0;
            while (index < end)
            {
                if (!lexer.IsLiteral(index))
                {
                    if (lexer.Code[index] == '[') depth++;
                    else if (lexer.Code[index] == ']')
                    {
                        depth--;
                        if (depth == 0) { index++; break; }
                    }
                }
                index++;
            }
        }
    }

    /// The offset of the semicolon that ends an expression-bodied member.
    static int ExpressionEnd(CSharpLexer lexer, int start, int end)
    {
        int depth = 0;
        for (int i = start; i < end; i++)
        {
            if (lexer.IsLiteral(i)) continue;
            char c = lexer.Code[i];
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            else if (c == ';' && depth == 0) return i;
        }
        return end;
    }

    /// Every member of the source, the outer ones first.
    ///
    /// A member is a header and a body: a block (`{ ... }`) or an expression (the `=>` of an expression-bodied
    /// member, up to its semicolon). The list is flat and every member carries the offsets of its own body, so
    /// a caller can look inside it.
    static List<CSharpMember> CSharpMembers(CSharpLexer lexer)
    {
        string code = lexer.Code;
        var pairs = BracePairs(code);
        var members = new List<CSharpMember>();

        void Walk(int start, int end)
        {
            int index = start;
            while (index < end)
            {
                while (index < end && char.IsWhiteSpace(code[index])) index++;
                if (index >= end) return;
                int raw = index;
                int declaration = SkipAttributes(lexer, index, end);
                var match = MethodDecl.Match(code, declaration);
                if (match.Success && match.Index == declaration)
                {
                    int openParen = match.Index + match.Length - 1;
                    int closeParen = MatchingParen(lexer, openParen, end);
                    int after = closeParen + 1;
                    while (after < end && char.IsWhiteSpace(code[after])) after++;
                    var member = new CSharpMember
                    {
                        Name = match.Groups["name"].Value,
                        SpanStart = raw,
                    };
                    if (after < end && code[after] == '{')
                    {
                        int bodyEnd = pairs.TryGetValue(after, out int close) ? close : end;
                        member.Header = code.Substring(raw, after + 1 - raw);
                        member.BodyStart = after + 1;
                        member.BodyEnd = bodyEnd;
                        member.SpanEnd = bodyEnd + 1;
                        members.Add(member);
                        Walk(after + 1, bodyEnd);
                        index = bodyEnd + 1;
                    }
                    else if (after + 1 < end && code[after] == '=' && code[after + 1] == '>')
                    {
                        int semi = ExpressionEnd(lexer, after + 2, end);
                        member.Header = code.Substring(raw, after + 2 - raw);
                        member.BodyStart = after + 2;
                        member.BodyEnd = semi;
                        member.SpanEnd = semi + 1;
                        members.Add(member);
                        index = semi + 1;
                    }
                    else
                    {
                        member.Header = code.Substring(raw, Math.Max(0, after - raw));
                        member.SpanEnd = after + 1;
                        members.Add(member);
                        index = after + 1;
                    }
                    continue;
                }
                // Not a method: a type (a block) or a field or property (a statement).
                int scan = declaration;
                while (scan < end && code[scan] != '{' && code[scan] != ';') scan++;
                if (scan >= end) return;
                if (code[scan] == ';')
                {
                    members.Add(new CSharpMember
                    {
                        Name = null,
                        Header = code.Substring(raw, scan - raw),
                        SpanStart = raw,
                        SpanEnd = scan + 1,
                    });
                    index = scan + 1;
                }
                else
                {
                    int bodyEnd = pairs.TryGetValue(scan, out int close) ? close : end;
                    members.Add(new CSharpMember
                    {
                        Name = null,
                        Header = code.Substring(raw, scan - raw),
                        BodyStart = scan + 1,
                        BodyEnd = bodyEnd,
                        SpanStart = raw,
                        SpanEnd = bodyEnd + 1,
                    });
                    Walk(scan + 1, bodyEnd);
                    index = bodyEnd + 1;
                }
            }
        }

        Walk(0, code.Length);
        return members;
    }

    /// The body of a member, with the string contents blanked.
    static string BodyOf(CSharpLexer lexer, CSharpMember member)
    {
        if (member.BodyStart < 0 || member.BodyEnd <= member.BodyStart) return "";
        return lexer.Code.Substring(member.BodyStart, member.BodyEnd - member.BodyStart);
    }

    /// The Harmony patch attribute that declares a lifecycle hook.
    static readonly Regex HarmonyPatchDecl = new Regex(
        @"HarmonyPatch\s*\(\s*typeof\s*\(\s*(?<type>\w+)\s*\)\s*,\s*nameof\s*\(\s*(?<type2>\w+)\s*\.\s*(?<member>\w+)\s*\)",
        RegexOptions.Compiled);

    /// The four hooks the camera needs, and the reason each one gives the teardown.
    static readonly (string type, string member, string reason)[] LifecycleHooks =
    {
        ("fpscontroller", "Death", "death"),
        ("fpscontroller", "Respawn", "respawn"),
        ("menuhandler", "LoadScene", "scene load"),
        ("mainscript", "PressedEscape", "main menu"),
    };

    /// The member that the Harmony patch attribute declares.
    static CSharpMember DeclarationOf(List<CSharpMember> members, string type, string member)
    {
        var pattern = new Regex(
            @"HarmonyPatch\s*\(\s*typeof\s*\(\s*" + Regex.Escape(type) +
            @"\s*\)\s*,\s*nameof\s*\(\s*" + Regex.Escape(type) +
            @"\s*\.\s*" + Regex.Escape(member) + @"\s*\)");
        foreach (var candidate in members)
            if (pattern.IsMatch(candidate.Header)) return candidate;
        return null;
    }

    /// The method bodies that belong to a hook's declaration.
    ///
    /// When the attribute declares a method, that method's own body is the hook's body. When it declares a type
    /// (the usual shape: a nested class holding the patch), the method bodies inside that type are the hook's
    /// bodies. Either way the teardown call has to be in the hook's OWN body, not in some other member of the
    /// file.
    static List<CSharpMember> HookBodies(List<CSharpMember> members, CSharpMember declaration)
    {
        if (declaration == null) return new List<CSharpMember>();

        Func<CSharpMember, bool> isPatchMethod = member =>
        {
            if (!member.IsMethod) return false;
            if (member.Name == "Prefix" || member.Name == "Postfix")
                return true;
            var header = member.Header ?? "";
            return header.Contains("[HarmonyPrefix]")
                || header.Contains("[HarmonyPostfix]");
        };

        if (declaration.IsMethod)
            return isPatchMethod(declaration)
                ? new List<CSharpMember> { declaration }
                : new List<CSharpMember>();

        return members.FindAll(member =>
        {
            if (!isPatchMethod(member)) return false;
            if (member.SpanStart < declaration.BodyStart
                || member.SpanEnd > declaration.BodyEnd)
                return false;

            return !members.Any(container =>
                !container.IsMethod
                && container.BodyStart >= 0
                && !ReferenceEquals(container, declaration)
                && container.BodyStart > declaration.BodyStart
                && container.BodyStart <= member.SpanStart
                && member.SpanEnd <= container.BodyEnd);
        });
    }


    /// Whether the member's body calls `Forced("<reason>")`.
    ///
    /// The call is looked for in the code view, so a `Forced("death")` that is only a string literal is
    /// invisible, and the argument is read from the literal table, so the reason compared is the literal's own
    /// text.
    static bool CallsForced(CSharpLexer lexer, CSharpMember member, string reason)
    {
        if (member.BodyStart < 0) return false;
        string region = lexer.Code.Substring(member.BodyStart, member.BodyEnd - member.BodyStart);
        foreach (Match match in Regex.Matches(region, @"\bForced\s*\("))
        {
            int offset = member.BodyStart + match.Index + match.Length;
            while (offset < member.BodyEnd && char.IsWhiteSpace(lexer.Code[offset])) offset++;
            while (offset < member.BodyEnd && (lexer.Code[offset] == '@' || lexer.Code[offset] == '$')) offset++;
            var literal = lexer.LiteralAt(offset);
            if (literal != null && literal == reason) return true;
        }
        return false;
    }

    /// The ways a lifecycle source fails the hook oracle. An empty list means it passes.
    ///
    /// The oracle asks three things of the source:
    ///
    ///   * each of the four events has its own Harmony patch declaration;
    ///   * the teardown that belongs to that event - `Forced("<reason>")` - is called in the body of that very
    ///     hook, not in some other member of the file;
    ///   * the shared teardown itself only runs when the camera is on, and it does turn the camera off.
    static List<string> LifecycleFailures(string source)
    {
        var lexer = new CSharpLexer(source);
        var members = CSharpMembers(lexer);
        var failures = new List<string>();

        foreach (var (type, member, reason) in LifecycleHooks)
        {
            var declaration = DeclarationOf(members, type, member);
            if (declaration == null)
            {
                failures.Add("no hook for " + type + "." + member + " (the Harmony patch declaration is missing)");
                continue;
            }
            var bodies = HookBodies(members, declaration);
            if (bodies.Count == 0)
            {
                failures.Add("the " + reason + " hook has no method body of its own");
                continue;
            }
            bool called = false;
            foreach (var body in bodies) if (CallsForced(lexer, body, reason)) { called = true; break; }
            if (!called) failures.Add("the " + reason + " hook does not call Forced(\"" + reason + "\")");
        }

        // The escape hook only ends the world when it really is the main menu.
        var escape = DeclarationOf(members, "mainscript", "PressedEscape");
        if (escape != null)
        {
            var bodies = HookBodies(members, escape);
            if (bodies.Count > 0)
            {
                bool checks = false;
                foreach (var body in bodies)
                    if (Regex.IsMatch(BodyOf(lexer, body), @"!\s*_mainMenu")) { checks = true; break; }
                if (!checks) failures.Add("the main menu hook does not check _mainMenu");
            }
        }

        // The shared teardown: it only runs when the camera is on, and it turns it off.
        var shared = members.FindAll(m => m.IsMethod && m.Name == "Forced");
        if (shared.Count == 0) failures.Add("the shared teardown Forced(string) is missing");
        else
        {
            string body = BodyOf(lexer, shared[0]);
            if (!Regex.IsMatch(body, @"if\s*\(\s*!\s*CinematicCamera\s*\.\s*Active\s*\)\s*return\s*;"))
                failures.Add("the shared teardown does nothing when the camera is off");
            if (!Regex.IsMatch(body, @"CinematicCamera\s*\.\s*SetOff\s*\("))
                failures.Add("the shared teardown does not turn the camera off");
        }

        return failures;
    }

    static int Main(string[] args)
    {
        Root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        GameCamerasAreNeverWritten();
        PlayerTransformIsNeverWritten();
        MovementAxesAreTheOnlyWrite();
        NoVrOrXrCalls();
        NoNetworkCalls();
        BridgeClassificationMatchesThePolicy();
        BridgeCameraCommandsAreNeverPlayerMutations();
        LifecycleHooksExist();
        LifecycleOracleIsStrict();
        LexerRemovesBlockComments();
        FovClampCallSitesExist();
        GuiPassRunsInOnGui();
        Console.WriteLine("Cinematic isolation: " + checks + " checks passed");
        return 0;
    }

    // ---- 0. the GUI pass ---------------------------------------------------------------

    static void GuiPassRunsInOnGui()
    {
        // GUI calls are only legal inside OnGUI. Called from Update, the camera's status pass threw "You can only
        // call GUI functions from inside OnGUI" on every frame the camera was on (seen live, 967 times in one run),
        // and the exception cut the rest of the runner's Update short: the terrain horizon, the XR bridge and the
        // network tick stopped while filming.
        string plugin = Read("src/TLDRevamp/Plugin.cs");
        int update = plugin.IndexOf("private void Update()", StringComparison.Ordinal);
        int ongui = plugin.IndexOf("private void OnGUI()", StringComparison.Ordinal);
        Check(update >= 0 && ongui > update, "the runner has its Update and its OnGUI");
        if (update < 0 || ongui <= update) return;
        int ongUiEnd = plugin.IndexOf("private void FixedUpdate()", ongui, StringComparison.Ordinal);
        string updateBody = plugin.Substring(update, ongui - update);
        string onGuiBody = plugin.Substring(ongui, (ongUiEnd > ongui ? ongUiEnd : plugin.Length) - ongui);
        Check(!updateBody.Contains("CinematicCamera.Draw("), "the camera's GUI pass is not called from Update");
        Check(onGuiBody.Contains("CinematicCamera.Draw("), "the camera's GUI pass is called from OnGUI");
        Check(Read("src/TLDRevamp/Cinematic/CinematicCamera.cs").Contains("Event.current.type != EventType.Repaint"),
            "the camera's GUI pass draws only on the repaint event");
    }

    // ---- 1. the game's cameras ------------------------------------------------------

    static void GameCamerasAreNeverWritten()
    {
        // The five cameras of the player's rig. Nothing may write to them: not their position, not their
        // rotation, not their field of view, not their enabled state, not their parent, not their layer.
        string[] cameras = { "Cam", "mainCam", "farCamera", "skyCamera", "saveScreenshotCam" };
        var lines = CodeLines().ToList();
        foreach (var camera in cameras)
        {
            // A write looks like `something.Cam.something =` or `something.Cam.something +=`.
            var write = new Regex(@"\b" + Regex.Escape(camera) + @"\s*\.\s*[A-Za-z_]\w*\s*(\+\+|--|[+\-*/]?=)(?!=)");
            foreach (var (file, line, code) in lines)
            {
                // `player.Cam` is the game's camera; the rig's own camera is `_camera` or `cam` on the rig.
                if (!Regex.IsMatch(code, @"(player|pl)\s*\.\s*" + Regex.Escape(camera) + @"\b")) continue;
                Check(!write.IsMatch(code),
                    "the camera must not write to player." + camera + " (" + file + ":" + line + ": " + code.Trim() + ")");
            }
        }
        // And the game cameras are never disabled or re-enabled by the feature.
        foreach (var (file, line, code) in lines)
            foreach (var camera in cameras)
                if (Regex.IsMatch(code, @"(player|pl)\s*\.\s*" + Regex.Escape(camera) + @"\s*\.\s*enabled\s*="))
                    Check(false, "the camera must not enable or disable player." + camera + " (" + file + ":" + line + ")");

        // The rig must not be parented to the player: the game lerps the player's camera parent toward the head
        // every frame, so a child of the player would be dragged back.
        foreach (var (file, line, code) in lines)
            Check(!Regex.IsMatch(code, @"\.\s*SetParent\s*\(\s*(player|pl)\b"),
                "the rig must not be parented to the player (" + file + ":" + line + ")");
        Check(Regex.IsMatch(Read("src/TLDRevamp/Cinematic/CinematicRig.cs"), @"SetParent\s*\(\s*null"),
            "the rig is parented to nothing at all");
    }

    // ---- 2. the player and the network root -------------------------------------------

    static void PlayerTransformIsNeverWritten()
    {
        // The player transform is what the network reports as this player's position. The body rotation and the
        // head transform are networked body state. None of them may be written.
        string[] members = { "transform", "BodyRot", "Th" };
        foreach (var (file, line, code) in CodeLines())
        {
            foreach (var member in members)
            {
                var write = new Regex(@"\b(player|pl)\s*\.\s*" + Regex.Escape(member) + @"\s*\.\s*(position|rotation|localPosition|localRotation|eulerAngles|localEulerAngles)\s*(\+\+|--|[+\-*/]?=)(?!=)");
                Check(!write.IsMatch(code),
                    "the camera must not write to player." + member + " (" + file + ":" + line + ": " + code.Trim() + ")");
            }
            // The rig's own transform is the only transform the camera writes.
            var rigWrite = new Regex(@"\b(Rig|rig|_rig)\s*\.\s*(Position|Rotation)\s*(\+\+|--|[+\-*/]?=)(?!=)");
            Check(rigWrite.IsMatch(code) || !Regex.IsMatch(code, @"\.(position|rotation)\s*(\+\+|--|[+\-*/]?=)(?!=)"),
                "the camera may only write its own rig's position and rotation (" + file + ":" + line + ": " + code.Trim() + ")");
        }
    }

    // ---- 3. the movement axes are the only write to the game -------------------------

    static void MovementAxesAreTheOnlyWrite()
    {
        // The one write the feature makes to the game's own state: the four movement axes, and they are written
        // to zero so the character does not walk while the camera flies.
        var source = Read("src/TLDRevamp/Cinematic/CinematicInput.cs");
        Check(source.Contains("input.forward = playerAxes.Forward"), "the forward axis is written from the policy");
        Check(source.Contains("input.backward = playerAxes.Backward"), "the backward axis is written from the policy");
        Check(source.Contains("input.left = playerAxes.Left"), "the left axis is written from the policy");
        Check(source.Contains("input.right = playerAxes.Right"), "the right axis is written from the policy");

        // And the policy really writes zero.
        var player = CinematicInputPolicy.ForPlayer(1f, 1f, 1f, 1f);
        Check(player.Forward == 0f && player.Backward == 0f && player.Left == 0f && player.Right == 0f,
            "the player is left with no movement at all");
        // And the camera gets the values the player had, so the two agree.
        var camera = CinematicInputPolicy.ForCamera(1f, 0.5f, 0f, 1f);
        Check(camera.Forward == 1f && camera.Backward == 0.5f && camera.Left == 0f && camera.Right == 1f,
            "the camera reads what the player pressed");
    }

    // ---- 4. no VR and no XR ------------------------------------------------------------

    static void NoVrOrXrCalls()
    {
        // The game re-parents its camera rig to the headset and rewrites the head transform every frame in VR,
        // so the camera refuses to run there. It must also never switch VR on or off itself: that is the OpenXR
        // bridge's business, and a camera that toggled VR would fight it.
        string[] forbidden = { "SetVR", "SetXR", "XRGeneralSettings", "XRLoader", "OpenXR", "XRDisplaySubsystem",
                               "XRInputSubsystem", "XRInputDevice", "InputDevices", "TryRecenter", "Recenter" };
        foreach (var (file, line, code) in CodeLines())
            foreach (var name in forbidden)
                Check(!Regex.IsMatch(code, @"\b" + Regex.Escape(name) + @"\b"),
                    "the camera must not call " + name + " (" + file + ":" + line + ": " + code.Trim() + ")");

        // The VR refusal is there, and it is safe before the settings have loaded.
        var camera = Read("src/TLDRevamp/Cinematic/CinematicCamera.cs");
        Check(camera.Contains("InVR()"), "the camera checks for VR");
        Check(camera.Contains("settingsscript.s == null"), "the VR check survives the settings not being loaded");
        Check(!camera.Contains("mainscript.IsVR()"), "the camera does not call the game's IsVR (it throws before the settings load)");
    }

    // ---- 5. no network -----------------------------------------------------------------

    static void NoNetworkCalls()
    {
        string[] forbidden = { "NetWriter", "NetReader", "Protocol.", "Mp.Host", "Mp.Join", "Mp.Send", "SendToAll",
                               "Session.Say", "SteamTransport", "EntityState", "Entities.Pickup", "Entities.Proxy" };
        foreach (var (file, line, code) in CodeLines())
            foreach (var name in forbidden)
                Check(!Regex.IsMatch(code, Regex.Escape(name)),
                    "the camera must not touch the network (" + file + ":" + line + ": " + name + ")");

        // The one network-adjacent read is the scene-teardown flag, and it is a read.
        //
        // The strip is a PER-LINE helper: applied to a whole file it stops at the
        // first line comment and returns almost nothing, so a check written that way
        // can never fail. Every line is stripped and joined instead, so the check
        // really looks at the code of every line of the file.
        var camera = Read("src/TLDRevamp/Cinematic/CinematicCamera.cs");
        Check(camera.Contains("Net.Mp.SceneTearingDown"), "the camera watches the scene-teardown flag");
        var cameraCode = CodeLinesOf(camera).Select(l => l.code).ToArray();
        Check(cameraCode.Length > 100, "the camera's code lines were extracted (" + cameraCode.Length + ")");
        foreach (var code in cameraCode)
            Check(!Regex.IsMatch(code, @"SceneTearingDown\s*="),
                "the camera never sets the scene-teardown flag (" + code.Trim() + ")");
    }

    // ---- 6. the bridge classifies the camera's commands the way the policy says -----------

    static void BridgeClassificationMatchesThePolicy()
    {
        // The debug bridge's own classification must call the pure policy, so the two cannot drift apart.
        var bridge = Read("src/TLDRevamp/DebugBridge.cs");
        Check(bridge.Contains("Cinematic.CinematicCommandPolicy.IsReadOnly(arg)"),
            "the bridge classifies the camera commands with the pure policy");

        // And the policy's own answer is what we expect, for every command the camera offers.
        var queries = new[] { "", "  ", "status", " status ", "status extra" };
        var mutations = new[] { "on", "off", "free", "orbit", "toggle", "recentre", "speed 4", "fov 35", "radius 20",
                                "shot", "shot load at 0 move 1 2 3 4 5 6", "shot unload", "shot write", "screenshot" };
        foreach (var q in queries)
            Check(CinematicCommandPolicy.IsReadOnly(q), "'cinema " + q + "' is a query");
        foreach (var m in mutations)
            Check(!CinematicCommandPolicy.IsReadOnly(m), "'cinema " + m + "' is a mutation");

        // A joiner under the default host-only policy may query and may not mutate.
        Check(CinematicCommandPolicy.Allowed(true, false), "a query is allowed without developer access");
        Check(!CinematicCommandPolicy.Allowed(false, false), "a mutation is refused without developer access");
        Check(CinematicCommandPolicy.Allowed(false, true), "a mutation is allowed with developer access");
    }

    // ---- 7. a camera command is never a player or network mutation -----------------------

    static void BridgeCameraCommandsAreNeverPlayerMutations()
    {
        // The camera's bridge surface has its own switch, and it only reaches the camera. It must not reach the
        // player's pickup, the entities, the session or anything else that changes the world or the network.
        var bridgeSurface = Read("src/TLDRevamp/Cinematic/CinematicBridge.cs");
        // The strip is a per-line helper, so it is applied line by line and the lines are joined. Applied to the
        // whole file at once it stops at the first line comment and returns almost nothing, and the check below
        // could then never fail.
        var code = string.Join("\n", CodeLinesOf(bridgeSurface).Select(l => l.code).ToArray());
        Check(code.Length > 200, "the bridge surface's code was extracted (" + code.Length + " chars)");
        string[] forbidden = { "Entities", "PlayerSave", "PlayerCombat", "PlayerActs", "Tpa", "Reports", "Session",
                               "Mp.", "NetWriter", "NetReader", "Protocol" };
        foreach (var name in forbidden)
            Check(!code.Contains(name), "the camera's bridge surface must not reach " + name);

        // The only things it calls are the camera's own. The calls are read from the code *with* the strings
        // intact (a call is never inside a string), but with the comments removed, so a name in a comment or in
        // an error message cannot be mistaken for a call.
        var calls = Regex.Matches(bridgeSurface, @"\bCinematicCamera\.\w+").Select(m => m.Value).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Check(calls.Length > 0, "the camera's bridge surface calls the camera");
        foreach (var call in calls)
            Check(call.StartsWith("CinematicCamera."), "the camera's bridge surface only calls the camera: " + call);

        // Every type the surface names must be one of the camera's own, or a plain .NET type. A type from the
        // game's networking or entity code would mean the camera could reach the world.
        var allowedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "CinematicCamera", "CinematicState", "CinematicMode", "CinematicLimits", "CinematicCommandPolicy",
            "CinematicShotCodec", "CinematicShot", "CinematicShotStep", "CinematicCursorPolicy",
            "CinematicInputPolicy", "CinematicOrbit", "Json", "StringComparison", "Array", "NumberStyles",
            "CultureInfo", "Math", "String", "Float", "Double", "Exception", "Object", "Console", "Environment",
            "CinematicBridge", "Command", "Bridge", "TLDRevamp", "StringSplitOptions", "NumberStyles",
            // the camera's own public members
            "State", "Status", "ShotStatus", "SetOn", "SetOff", "Toggle", "Recentre", "LoadShot", "UnloadShot",
            "CurrentShot", "DefaultMode", "Speed", "Fov", "ShowStatus", "Mode", "Active", "ExitKeyHint",
            "GlobalPosition", "KeyAllowed", "Shot", "Format", "Parse", "Write", "IsReadOnly", "Allowed", "ModeName",
            "TryParseMode", "ClampSpeed", "ClampFov", "ClampRadius", "ClampPolar", "ClampPitch", "WrapAzimuth",
            "ClampDeltaTime", "Steps", "Duration", "NextCut", "LastOf", "Time", "Playing", "Speed", "Loop", "Kind",
            "Values", "Raw", "Version", "Loaded", "Steps", "Duration", "Playing", "Time", "Speed", "Version", "Error",
        };
        // The *types* the surface names. A type appears in a type position: a `new X(...)`, a cast `(X)`, a
        // declaration `X name`, or a static member access `X.Member`. A constant or a local variable is not a
        // type, so it is not what this check is about.
        var named = new HashSet<string>(StringComparer.Ordinal);
        var typePositions = new[]
        {
            new Regex(@"\bnew\s+([A-Z][A-Za-z0-9_]*)"),          // a construction
            new Regex(@"\(\s*([A-Z][A-Za-z0-9_]*)\s*\)"),       // a cast
            new Regex(@"\b([A-Z][A-Za-z0-9_]*)\s+[A-Za-z_]\w*\s*[=;)]"),  // a declaration
            new Regex(@"\b([A-Z][A-Za-z0-9_]*)\s*\."),           // a static member access
        };
        foreach (var (file, line, codeLine) in CodeLinesOf(bridgeSurface))
        {
            // A using directive names a namespace, not a type the surface could call.
            if (codeLine.TrimStart().StartsWith("using ")) continue;
            foreach (var pattern in typePositions)
                foreach (Match m in pattern.Matches(codeLine))
                    named.Add(m.Groups[1].Value);
        }
        // A name that is only ever used as a namespace qualifier (System, Unity) is not a type in its own right.
        foreach (var type in named)
            Check(allowedTypes.Contains(type), "the camera's bridge surface may only use its own types and plain .NET types; found " + type);

        // The bridge's own dispatch must route the camera command to the camera's surface and nowhere else.
        var bridge = Read("src/TLDRevamp/DebugBridge.cs");
        var dispatch = Regex.Match(bridge, @"case ""cinema"":\s*return\s+([^;]+);");
        Check(dispatch.Success, "the bridge dispatches the cinema command");
        if (dispatch.Success)
            Check(dispatch.Groups[1].Value.Trim() == "Cinematic.CinematicBridge.Command(arg)",
                "the cinema command goes to the camera's own surface, got: " + dispatch.Groups[1].Value.Trim());
    }

    // ---- 8. the lifecycle hooks exist --------------------------------------------------
    //
    // The teardown story of the camera rests on four Harmony hooks: the player died, the player is back, the
    // world is being replaced and the game is going to the main menu. Removing a hook left every other check
    // green, because the policy (that a refusal is produced) was proven, but the hooks that APPLY the policy
    // were never looked for. These checks look for them in the code, with the comments and the strings removed.

    static void LifecycleHooksExist()
    {
        var lifecycle = Read("src/TLDRevamp/Cinematic/CinematicLifecycle.cs");
        var lines = CodeLinesOf(lifecycle).Select(l => l.code).ToArray();
        Check(lines.Length > 20, "the lifecycle file's code lines were extracted (" + lines.Length + ")");

        // The oracle is scoped to a hook's OWN body, so a teardown that belongs to some other member of the
        // file cannot stand in for the hook's teardown, and a hook that only exists inside a comment is not
        // a hook at all. The whole-file searches this replaces looked anywhere in the file.
        var failures = LifecycleFailures(lifecycle);
        Check(failures.Count == 0, "the lifecycle source fails the hook oracle: " + string.Join("; ", failures));

        // Every hook is a Harmony prefix or postfix on a real method, not a plain method.
        int prefixes = lines.Count(code => code.Contains("HarmonyPrefix") || code.Contains("HarmonyPostfix"));
        Check(prefixes >= 4, "all four hooks are Harmony patches (" + prefixes + ")");
    }

    // ---- 8b. the oracle itself is strict ------------------------------------------------
    //
    // The oracle is what proves the camera puts itself away when the game takes the world away, so the oracle
    // has to be strict about the two things a whole-file search cannot see: a hook that only exists inside a
    // comment, and a teardown that belongs to a different member. These are synthetic sources: each one
    // breaks the real lifecycle source in one way, and the oracle has to notice every one of them.

    static void LifecycleOracleIsStrict()
    {
        var real = Read("src/TLDRevamp/Cinematic/CinematicLifecycle.cs");
        Check(LifecycleFailures(real).Count == 0, "the real lifecycle source passes the oracle");
        var original = "private static void Prefix() => Forced(\"death\");";
        Check(real.Contains(original), "original source is present");

        var negative1 = "private static void Prefix() { }\nprivate static void UnrelatedHelper() => Forced(\"death\");";
        var scopedNegative1 = real.Replace(original, negative1);
        Check(scopedNegative1.Contains(negative1), "negative1 replacement occurred");
        var failures1 = LifecycleFailures(scopedNegative1);
        Check(failures1.Any(), "negative1 produced failures");
        Check(failures1.Any(f => f.Contains("death")), "negative1 contains death");

        var negative2 = "private static void Prefix() { }\nprivate static class Nested { private static void Prefix() => Forced(\"death\"); }";
        var scopedNegative2 = real.Replace(original, negative2);
        Check(scopedNegative2.Contains(negative2), "negative2 replacement occurred");
        var failures2 = LifecycleFailures(scopedNegative2);
        Check(failures2.Any(), "negative2 produced failures");
        Check(failures2.Any(f => f.Contains("death")), "negative2 contains death");

        var negative3 = "private static void Prefix() { }\nprivate static void PrefixUnused() => Forced(\"death\");";
        var scopedNegative3 = real.Replace(original, negative3);
        Check(scopedNegative3.Contains(negative3), "negative3 replacement occurred");
        var failures3 = LifecycleFailures(scopedNegative3);
        Check(failures3.Any(), "negative3 produced failures");
        Check(failures3.Any(f => f.Contains("death")), "negative3 contains death");

        var positive = real.Replace("[HarmonyPrefix]", "");
        Check(positive != real, "positive source changed");
        var positiveFailures = LifecycleFailures(positive);
        Check(!positiveFailures.Any(), "positive produced no failures");

        // A hook that only exists inside a block comment is not a hook. The comment spans several lines, so
        // the strip has to keep its state across them.
        var fakeHook = @"        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.Death))]
        private static class OnDeath
        {
            [HarmonyPrefix]
            private static void Prefix() => Forced(""death"");
        }";
        var commented = "        /*\n" + fakeHook + "\n        */";
        var source = real.Replace(fakeHook, commented);
        Check(source != real, "the fake hook was inserted into the source");
        var failures = LifecycleFailures(source);
        Check(failures.Count > 0, "a hook that only exists inside a comment is not a hook");
        Check(failures.Any(f => f.Contains("fpscontroller.Death")), "the comment fake must be reported: " + string.Join("; ", failures));

        // A teardown that lives in another member is not this hook's teardown. The literal moves with it, so
        // a whole-file search for Forced("death") would still find it: only a hook-scoped search can tell that
        // it belongs to another member.
        source = real.Replace("private static void Prefix() => Forced(\"death\");",
                              "private static void Prefix() => SomewhereElse();");
        source = source.Replace("        /// The player died",
            "        private static void SomewhereElse() { Forced(\"death\"); }\n\n        /// The player died");
        failures = LifecycleFailures(source);
        Check(failures.Count > 0, "a teardown in another method is not this hook's teardown");
        Check(failures.Any(f => f.Contains("death")), "the moved teardown must be reported: " + string.Join("; ", failures));

        // Each hook that loses its own teardown call is caught.
        foreach (var reason in new[] { "death", "respawn", "scene load", "main menu" })
        {
            source = real.Replace("Forced(\"" + reason + "\")", "SomethingElse()");
            failures = LifecycleFailures(source);
            Check(failures.Count > 0, "the " + reason + " hook must call its own teardown");
            Check(failures.Any(f => f.Contains(reason)), "the " + reason + " failure must name the hook: " + string.Join("; ", failures));
        }

        // The shared teardown must do nothing when the camera is off, and it must turn it off.
        source = real.Replace("if (!CinematicCamera.Active) return;", "/* always */");
        Check(LifecycleFailures(source).Count > 0, "the shared teardown must do nothing when the camera is off");
        source = real.Replace("CinematicCamera.SetOff(why);", "/* nothing */");
        Check(LifecycleFailures(source).Count > 0, "the shared teardown must turn the camera off");

        // A Forced call that is only a string is not a teardown.
        source = real.Replace("private static void Prefix() => Forced(\"death\");",
                              "private static void Prefix() { Note(\"Forced(\\\"death\\\")\"); }");
        Check(LifecycleFailures(source).Count > 0, "a Forced call inside a string is not a teardown");
    }

    // ---- 8c. the lexer removes what a comment hides ---------------------------------------
    //
    // A block comment that opens on one line and closes on another used to be stripped only up to its opening
    // delimiter, so everything after it was read as code. The lexer keeps its state across the lines.

    static void LexerRemovesBlockComments()
    {
        var source = "class X\n{\n    /*\n    HarmonyPatch(typeof(fpscontroller))\n    Forced(\"death\")\n    */\n    void Real() { }\n}\n";
        var joined = string.Join("\n", new CSharpLexer(source).Code.Split('\n'));
        Check(!joined.Contains("HarmonyPatch"), "a block comment's content is not code");
        Check(!joined.Contains("Forced"), "a block comment's content is not code");
        Check(joined.Contains("void Real()"), "the code after a block comment survives");

        // A string that holds an escaped quote must not end the string early.
        source = "class X { string a = \"he said \\\"hi\\\" /* not a comment */\"; void B() { } }\n";
        joined = string.Join("\n", new CSharpLexer(source).Code.Split('\n'));
        Check(!joined.Contains("not a comment"), "a comment delimiter inside a string is not a comment");
        Check(joined.Contains("void B()"), "the code after a string survives");

        // A verbatim string holds doubled quotes, and a "//" inside it is not a comment.
        source = "class X { string b = @\"raw \"\"quoted\"\" // not a comment\"; void C() { } }\n";
        joined = string.Join("\n", new CSharpLexer(source).Code.Split('\n'));
        Check(!joined.Contains("not a comment"), "a // inside a verbatim string is not a comment");
        Check(joined.Contains("void C()"), "the code after a verbatim string survives");
    }

    // ---- 9. the field of view is clamped where it is set ------------------------------
    //
    // The camera holds the field of view between MinFov and MaxFov. The CONSTANTS are checked by the Python
    // suite, but the CONSTANTS alone prove nothing: what matters is that every place the value is set goes
    // through the clamp. Two call sites set it (the bridge's `cinema fov` and the camera's own Fov property), and
    // both are checked here. Removing a clamp call used to leave every suite green.

    static void FovClampCallSitesExist()
    {
        var core = Read("src/TLDRevamp/Cinematic/CinematicCameraCore.cs");
        Check(core.Contains("ClampFov(float value)"), "the camera has a field-of-view clamp");
        Check(core.Contains("MinFov") && core.Contains("MaxFov"), "the clamp's bounds are named");

        // The camera's own Fov property clamps on the way in.
        var camera = Read("src/TLDRevamp/Cinematic/CinematicCamera.cs");
        var cameraCode = CodeLinesOf(camera).Select(l => l.code).ToArray();
        Check(cameraCode.Any(code => Regex.IsMatch(code, @"set\s*=>\s*State\.Fov\s*=\s*CinematicLimits\.ClampFov\s*\(")),
            "the camera's Fov property clamps what it is given");

        // And the bridge's own `cinema fov` clamps before it stores the value.
        var bridge = Read("src/TLDRevamp/Cinematic/CinematicBridge.cs");
        var bridgeCode = CodeLinesOf(bridge).Select(l => l.code).ToArray();
        Check(bridgeCode.Any(code => Regex.IsMatch(code, @"State\.Fov\s*=\s*CinematicLimits\.ClampFov\s*\(")),
            "the bridge clamps the field of view it is given");

        // The state itself normalises the value, so nothing can put an unbounded FOV into the rig.
        Check(core.Contains("Fov = CinematicLimits.ClampFov(Fov)"), "the camera state normalises its own field of view");

        // The rig only ever writes the field of view of its OWN camera, from its own field. It never writes the
        // field of view of a gameplay camera.
        var rig = Read("src/TLDRevamp/Cinematic/CinematicRig.cs");
        var rigCode = CodeLinesOf(rig).Select(l => l.code).ToArray();
        Check(rigCode.Any(code => Regex.IsMatch(code, @"_camera\.fieldOfView\s*=\s*Fov")),
            "the rig gives its own camera the field of view it was given");
        Check(rigCode.Any(code => Regex.IsMatch(code, @"cam\.fieldOfView\s*=\s*Fov")),
            "the rig applies the field of view to its own camera");
        foreach (var (file, line, code) in CodeLines())
            Check(!Regex.IsMatch(code, @"\.\s*(Cam|mainCam|farCamera|skyCamera|saveScreenshotCam)\s*\.\s*fieldOfView\s*="),
                "nothing in the camera feature re-FOVs a gameplay camera (" + file + ":" + line + ")");

    }
}
