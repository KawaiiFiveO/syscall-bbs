using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

/// <summary>
/// Desktop programs: an icon in the desktop's icon block and a window class emitted at runtime
/// for each program. Everything is found by code shape. See docs/z-interpreter-plan.md.
/// </summary>
internal static class DesktopPrograms
{
    public static readonly IProgramHost Host = new ProgramHost();

    private const int CellW = 9, CellH = 15;
    private const float IconColumnX = 168f, IconColumnStep = 110f;
    private static readonly float[] IconRows = { 421f, 308f, 201f, 86f };

    private static readonly List<Registration> Registrations = new();
    private static readonly ConditionalWeakTable<object, Session> Sessions = new();

    private static string _failure;
    private static bool _resolved, _attached, _iconsFailed, _desktopShown;
    private static Harmony _harmony;
    private static ModuleBuilder _module;

    // Window base and desktop.
    private static Type _base, _maybeBase;
    private static MethodInfo _title, _size, _palette, _update, _opened, _closed, _menu;
    private static ConstructorInfo _baseCtor, _vector2Ctor, _menuItemCtor, _menuSeparatorCtor;
    private static Type _menuItemType, _noShortcut;
    private static MethodInfo _getDesktop, _openWindow, _drawIcon, _desktopUpdate;
    private static object _iconColor, _themePalette;
    private static Func<object> _makePalette;
    private static FieldInfo[] _textureVectors;

    // Drawing.
    private static Action<int> _clear;
    private static Action<float, float, float, float, int> _fill;
    private static Action<char, float, float, object, int> _drawChar;
    private static Func<object, char, float> _glyphWidth, _glyphHeight;
    private static Func<object> _font, _fontSet;
    private static Func<TextFont, object> _gridFont;
    private static readonly Dictionary<TextFont, Face> Faces = new();
    private static readonly Dictionary<(TextFont, char), float> GlyphWidths = new();
    private static readonly System.Text.Encoding CodePage437 = System.Text.Encoding.GetEncoding(437,
        new System.Text.EncoderReplacementFallback("?"), new System.Text.DecoderReplacementFallback("?"));

    // Input.
    private static Func<List<char>> _typedChars;
    private static Func<int> _modifiers;
    private static Func<int, int, bool> _pressed;
    private static (ProgramKey Program, int Game)[] _keys;

    // Save folder.
    private static Func<string> _saveDirectory;

    /// <summary>Finds the game code and attaches the desktop patches. Returns one message on failure.</summary>
    public static List<string> Verify()
    {
        try
        {
            Attach();
            Log.Info("Programs: OK");
            return new List<string>();
        }
        catch (ShapeMismatchException e)
        {
            Log.Error($"Programs: FAILED - {e.Message}");
            return new List<string> { $"programs: {e.Message}" };
        }
    }

    private static void Attach()
    {
        Resolve();
        if (_attached)
            return;
        try
        {
            _harmony = new Harmony("modloader.programs");
            _harmony.Patch(_drawIcon, prefix: new HarmonyMethod(typeof(DesktopPrograms), nameof(IconPrefix)));
            _harmony.Patch(_desktopUpdate, transpiler: new HarmonyMethod(typeof(DesktopPrograms), nameof(DesktopTranspiler)));
            _attached = true;
        }
        catch (Exception e)
        {
            _harmony?.UnpatchAll(_harmony.Id);
            _failure = (e as ShapeMismatchException ?? e.InnerException as ShapeMismatchException)?.Message ?? e.Message;
            throw new ShapeMismatchException(_failure);
        }
    }

    private static void Resolve()
    {
        if (_failure != null)
            throw new ShapeMismatchException(_failure);
        if (_resolved)
            return;
        try
        {
            ResolveWindow();
            ResolveDesktop();
            ResolveDrawing();
            ResolveInput();
            ResolveSaveFolder();
            _resolved = true;
        }
        catch (ShapeMismatchException e)
        {
            _failure = e.Message;
            throw;
        }
    }

    // ---- Finding the game code ----

    private static void ResolveWindow()
    {
        Assembly game = HookHost.Game;
        Type terminal = CodeShape.RequireType(game, "Terminal.MainWindow");
        Type organizer = CodeShape.RequireType(game, "Organizer.MainWindow");
        _base = terminal.BaseType;
        if (_base == null || !_base.IsAbstract || !_base.IsPublic || organizer.BaseType != _base)
            throw new ShapeMismatchException("Terminal and Organizer windows don't share a public abstract base");
        Type vector2 = CodeShape.RequireType(game, "Vector2");
        Type texture = CodeShape.RequireType(game, "Texture");
        _maybeBase = CodeShape.RequireType(game, "Maybe`1").MakeGenericType(_base);
        _vector2Ctor = vector2.GetConstructor(new[] { typeof(float), typeof(float) }) ?? throw new ShapeMismatchException("Vector2(float, float) not found");
        _baseCtor = CodeShape.Single(_base.GetConstructors(CodeShape.AllDeclared).Where(c => !c.IsStatic && c.GetParameters().Length == 0 && (c.IsFamily || c.IsPublic)),
            "parameterless window base constructor");

        List<MethodInfo> abstracts = _base.GetMethods(CodeShape.AllDeclared).Where(m => m.IsAbstract).ToList();
        if (abstracts.Count != 4)
            throw new ShapeMismatchException($"expected 4 abstract window members, found {abstracts.Count}");
        _title = CodeShape.Single(abstracts.Where(m => m.ReturnType == typeof(string) && m.GetParameters().Length == 0), "abstract window title");
        _size = CodeShape.Single(abstracts.Where(m => m.ReturnType == vector2 && m.GetParameters().Length == 0), "abstract window size");
        _palette = CodeShape.Single(abstracts.Where(m => m.ReturnType.GetConstructor(new[] { texture }) != null), "abstract window palette");
        _update = CodeShape.Single(abstracts.Where(m => m.ReturnType == typeof(void) && m.GetParameters().Length == 0), "abstract window update");

        // The menu: a virtual returning an array of an abstract item type, whose nested kinds are an
        // item (label, Maybe<Key> shortcut, enabled, action) and a separator. A window with a menu
        // has a taller frame and a minimize button.
        _noShortcut = CodeShape.RequireType(game, "Maybe`1").MakeGenericType(CodeShape.RequireType(game, "Key"));
        _menu = CodeShape.Single(_base.GetMethods(CodeShape.AllDeclared).Where(m => m.IsVirtual && !m.IsAbstract && m.GetParameters().Length == 0 &&
            m.ReturnType.IsArray && m.ReturnType.GetElementType().IsAbstract && m.ReturnType.GetElementType().GetNestedTypes().Length == 2), "window menu");
        _menuItemType = _menu.ReturnType.GetElementType();
        Type[] kinds = _menuItemType.GetNestedTypes().Where(t => _menuItemType.IsAssignableFrom(t)).ToArray();
        _menuItemCtor = CodeShape.Single(kinds.Select(t => t.GetConstructor(new[] { typeof(string), _noShortcut, typeof(bool), typeof(Action) })).Where(c => c != null), "menu item constructor");
        _menuSeparatorCtor = CodeShape.Single(kinds.Where(t => t.GetConstructors().Length == 1).Select(t => t.GetConstructor(Type.EmptyTypes)).Where(c => c != null), "menu separator constructor");

        // Borrow the Organizer's palette: its constructor builds one from a texture reached by a
        // static getter and a chain of field reads.
        ConstructorInfo paletteCtor = _palette.ReturnType.GetConstructor(new[] { texture });
        List<CodeInstruction> ctorIL = CodeShape.ReadIL(CodeShape.Single(CodeShape.InstanceConstructors(organizer), "Organizer constructor"))
            ?? throw new ShapeMismatchException("Organizer constructor has no body");
        int newPalette = ctorIL.IndexOf(CodeShape.Single(ctorIL.Where(i => i.opcode == OpCodes.Newobj && Equals(i.operand, paletteCtor)), "palette creation in Organizer constructor"));
        int start = newPalette - 1;
        while (start >= 0 && ctorIL[start].opcode == OpCodes.Ldfld)
            start--;
        if (start < 0 || start == newPalette - 1 || !(ctorIL[start].operand is MethodInfo getter) || !getter.IsStatic || getter.GetParameters().Length != 0)
            throw new ShapeMismatchException("Organizer palette texture isn't a static getter followed by field reads");
        FieldInfo[] chain = ctorIL.Skip(start + 1).Take(newPalette - start - 1).Select(i => (FieldInfo)i.operand).ToArray();
        _makePalette = () =>
        {
            object value = getter.Invoke(null, null);
            foreach (FieldInfo field in chain)
                value = field.GetValue(value);
            return paletteCtor.Invoke(new[] { value });
        };

        // Opened is the virtual called right after a window is inserted into the desktop's
        // window list, and Closed the one called right after it's removed.
        Type desktopWindow = CodeShape.RequireType(game, "DesktopWindow");
        Type list = typeof(List<>).MakeGenericType(desktopWindow);
        MethodInfo insert = list.GetMethod("Insert"), remove = list.GetMethod("Remove");
        var virtuals = new HashSet<MethodInfo>(_base.GetMethods(CodeShape.AllDeclared)
            .Where(m => m.IsVirtual && !m.IsAbstract && m.ReturnType == typeof(void) && m.GetParameters().Length == 0));
        var opened = new HashSet<MethodInfo>();
        var closed = new HashSet<MethodInfo>();
        foreach (MethodBase method in CodeShape.DeclaredMethods(CodeShape.RequireType(game, "DesktopScreen")))
        {
            List<CodeInstruction> il = CodeShape.ReadIL(method);
            if (il == null)
                continue;
            for (int i = 0; i < il.Count; i++)
            {
                if (!(il[i].operand is MethodInfo called) || !virtuals.Contains(called))
                    continue;
                for (int j = i - 1; j >= Math.Max(0, i - 8); j--)
                {
                    if (il[j].Calls(insert)) { opened.Add(called); break; }
                    if (il[j].Calls(remove)) { closed.Add(called); break; }
                }
            }
        }
        _opened = CodeShape.Single(opened, "window virtual called after inserting a window");
        _closed = CodeShape.Single(closed, "window virtual called after removing a window");
        if (_opened == _closed)
            throw new ShapeMismatchException("window opened and closed calls are the same method");
    }

    private static void ResolveDesktop()
    {
        Assembly game = HookHost.Game;
        Type desktop = CodeShape.RequireType(game, "DesktopScreen");
        Type texture = CodeShape.RequireType(game, "Texture");
        Type color = CodeShape.RequireType(game, "Color");
        Type vector2 = CodeShape.RequireType(game, "Vector2");

        _getDesktop = (MethodInfo)CodeShape.Single(CodeShape.DeclaredMethods(desktop).OfType<MethodInfo>()
            .Where(m => m.IsStatic && m.IsPublic && m.GetParameters().Length == 0 && m.ReturnType == desktop), "DesktopScreen instance getter");
        _openWindow = CodeShape.Single(desktop.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.IsPublic &&
            m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { _base, _maybeBase })), "public open-window method");
        _drawIcon = CodeShape.Single(desktop.GetMethods(CodeShape.AllDeclared).Where(m => !m.IsStatic && m.IsPublic &&
            m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { texture, typeof(string), color, vector2, typeof(Action) })), "public desktop icon method");
        _desktopUpdate = desktop.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
            ?? throw new ShapeMismatchException("DesktopScreen.Update not found");
        List<CodeInstruction> il = CodeShape.ReadIL(_desktopUpdate) ?? throw new ShapeMismatchException("DesktopScreen.Update has no body");
        IconBlockEnd(il);

        // The desktop font: the only TextDrawingOptions.Font-typed field read in the icon
        // overload that the public icon method forwards to, read from a static font set.
        Type fontType = CodeShape.RequireType(game, "TextDrawingOptions").GetField("Font")?.FieldType
            ?? throw new ShapeMismatchException("TextDrawingOptions.Font not found");
        MethodInfo iconImpl = CodeShape.Single(CodeShape.ReadIL(_drawIcon).Select(i => i.operand).OfType<MethodInfo>()
            .Where(m => m.DeclaringType == desktop), "icon overload called by the icon method");
        List<CodeInstruction> iconIL = CodeShape.ReadIL(iconImpl);
        CodeInstruction fontLoad = CodeShape.Single(iconIL.Where(i => i.opcode == OpCodes.Ldfld && i.operand is FieldInfo f && f.FieldType == fontType), "font read in icon method");
        var fontField = (FieldInfo)fontLoad.operand;
        var fonts = iconIL[iconIL.IndexOf(fontLoad) - 1].operand as MethodInfo;
        if (fonts == null || !fonts.IsStatic || fonts.ReturnType != fontField.DeclaringType || fonts.GetParameters().Length != 0)
            throw new ShapeMismatchException("desktop font isn't read from a static font set");
        _font = () => fontField.GetValue(fonts.Invoke(null, null));
        _fontSet = () => fonts.Invoke(null, null);

        // The icon overload draws at position + (-51, 7) + the texture's offset, and hit-tests the
        // texture's size: the only two Vector2 fields of Texture it reads.
        _textureVectors = iconIL.Where(i => i.opcode == OpCodes.Ldfld && i.operand is FieldInfo f && f.DeclaringType == texture && f.FieldType == vector2)
            .Select(i => (FieldInfo)i.operand).Distinct().ToArray();
        if (_textureVectors.Length != 2)
            throw new ShapeMismatchException($"expected 2 Vector2 texture fields read in the icon method, found {_textureVectors.Length}");
    }

    // The icon block is a using scope: the last icon call is the one followed by its leave.
    private static int IconBlockEnd(List<CodeInstruction> il)
    {
        List<int> calls = Enumerable.Range(0, il.Count).Where(i => il[i].Calls(_drawIcon)).ToList();
        return CodeShape.Single(calls.Where(i => i + 1 < il.Count && (il[i + 1].opcode == OpCodes.Leave || il[i + 1].opcode == OpCodes.Leave_S)),
            $"icon call ending the desktop's icon block (of {calls.Count} icon calls)");
    }

    private static void ResolveDrawing()
    {
        Assembly game = HookHost.Game;
        Type color = CodeShape.RequireType(game, "Color");
        Type vector2 = CodeShape.RequireType(game, "Vector2");
        Type bounds2 = CodeShape.RequireType(game, "Bounds2");
        Type fontType = CodeShape.RequireType(game, "TextDrawingOptions").GetField("Font").FieldType;
        MethodInfo fromHex = color.GetMethod("FromHex", new[] { typeof(int) }) ?? throw new ShapeMismatchException("Color.FromHex not found");
        MethodInfo withSize = bounds2.GetMethod("WithSize", new[] { typeof(float), typeof(float), typeof(float), typeof(float) })
            ?? throw new ShapeMismatchException("Bounds2.WithSize not found");

        // The terminal clears its screen with Color.FromHex(...) followed by a static (Color) call
        // on the draw class.
        Type terminal = CodeShape.RequireType(game, "Terminal.MainWindow");
        MethodInfo terminalUpdate = CodeShape.Single(terminal.GetMethods(CodeShape.AllDeclared).Where(m => m.GetBaseDefinition() == _update), "terminal update override");
        List<CodeInstruction> il = CodeShape.ReadIL(terminalUpdate);
        MethodInfo clear = CodeShape.Single(Enumerable.Range(0, il.Count - 1)
            .Where(i => il[i].Calls(fromHex) && il[i + 1].operand is MethodInfo m && m.IsStatic && m.ReturnType == typeof(void)
                && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { color }))
            .Select(i => (MethodInfo)il[i + 1].operand), "clear call in terminal update");
        Type draw = clear.DeclaringType;
        MethodInfo fill = DrawMethod(draw, color, bounds2);

        // The terminal draws each character with a (char, Vector2, font, Color, bool) call, using
        // one variant of the desktop font family (family.variants[N]), centred in its cell by glyph width.
        MethodInfo drawChar = CodeShape.Single(il.Select(i => i.operand).OfType<MethodInfo>().Distinct().Where(m => m.DeclaringType == draw && m.IsStatic &&
            m.GetParameters().Length == 5 && m.GetParameters().Select(p => p.ParameterType).Where((t, n) => n != 2)
                .SequenceEqual(new[] { typeof(char), vector2, color, typeof(bool) })), "character draw call in terminal update");
        Type gridFont = drawChar.GetParameters()[2].ParameterType;
        Type glyphInfo = CodeShape.RequireType(game, "FontGlyphInfo");
        MethodInfo glyph = CodeShape.Single(il.Select(i => i.operand).OfType<MethodInfo>().Distinct().Where(m => !m.IsStatic && m.ReturnType == glyphInfo &&
            m.DeclaringType.IsAssignableFrom(gridFont) && m.GetParameters().Length == 1), "glyph lookup in terminal update");
        Type glyphKey = glyph.GetParameters()[0].ParameterType;
        MemberInfo glyphWidth = (MemberInfo)glyphInfo.GetField("Width") ?? glyphInfo.GetProperty("Width") ?? throw new ShapeMismatchException("FontGlyphInfo.Width not found");
        MemberInfo glyphHeight = (MemberInfo)glyphInfo.GetField("Height") ?? glyphInfo.GetProperty("Height") ?? throw new ShapeMismatchException("FontGlyphInfo.Height not found");
        int variantAt = CodeShape.Single(Enumerable.Range(0, il.Count - 2).Where(i => il[i].opcode == OpCodes.Ldfld && il[i].operand is FieldInfo f &&
            f.DeclaringType == fontType && f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
            f.FieldType.GetGenericArguments()[1] == gridFont && Constant(il[i + 1]) != null &&
            il[i + 2].operand is MethodInfo m && m.Name == "get_Item"), "terminal font variant read from the font family");
        var variants = (FieldInfo)il[variantAt].operand;
        object variant = Enum.ToObject(variants.FieldType.GetGenericArguments()[0], Constant(il[variantAt + 1]).Value);
        // TextFont.Bold is the terminal's variant. TextFont.Regular is variant 0, the family's first
        // and thinnest face (system_regular; the terminal's is system_bold). Mono and Cga are the
        // font set's two fonts cut from a 16x16 grid image (font_cpmono, 9 wide; font_cga, 8 wide):
        // the only ones whose glyphs have a negative height.
        object regular = Enum.ToObject(variants.FieldType.GetGenericArguments()[0], 0);
        _gridFont = f =>
        {
            if (f == TextFont.Mono || f == TextFont.Cga)
            {
                object set = _fontSet();
                int width = f == TextFont.Mono ? 9 : 8;
                return CodeShape.Single(set.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(field => field.FieldType == fontType).Select(field => field.GetValue(set)).Where(family => family != null)
                    .Select(family => ((System.Collections.IDictionary)variants.GetValue(family))[regular]).Where(font => font != null).Distinct()
                    .Where(font => _glyphHeight(font, 'M') < 0 && _glyphWidth(font, 'M') == width), $"{width}-pixel grid font in the font set");
            }
            var desktopFamily = (System.Collections.IDictionary)variants.GetValue(_font());
            object key = f == TextFont.Regular ? regular : variant;
            return desktopFamily.Contains(key) ? desktopFamily[key] : desktopFamily[variant];
        };

        ParameterExpression rgb = Expression.Parameter(typeof(int), "rgb");
        ParameterExpression x = Expression.Parameter(typeof(float), "x"), y = Expression.Parameter(typeof(float), "y");
        ParameterExpression w = Expression.Parameter(typeof(float), "w"), h = Expression.Parameter(typeof(float), "h");
        ParameterExpression c = Expression.Parameter(typeof(char), "c"), font = Expression.Parameter(typeof(object), "font");
        Expression colorOf = Expression.Call(fromHex, rgb);
        _clear = Expression.Lambda<Action<int>>(Expression.Call(clear, colorOf), rgb).Compile();
        _fill = Expression.Lambda<Action<float, float, float, float, int>>(
            Expression.Call(fill, colorOf, Expression.Call(withSize, x, y, w, h)), x, y, w, h, rgb).Compile();
        _drawChar = Expression.Lambda<Action<char, float, float, object, int>>(
            Expression.Call(drawChar, c, Expression.New(_vector2Ctor, x, y), Expression.Convert(font, gridFont), colorOf, Expression.Constant(false)), c, x, y, font, rgb).Compile();
        _glyphWidth = Expression.Lambda<Func<object, char, float>>(Expression.Convert(
            Expression.MakeMemberAccess(Expression.Call(Expression.Convert(font, glyph.DeclaringType), glyph, Expression.Convert(c, glyphKey)), glyphWidth), typeof(float)), font, c).Compile();
        _glyphHeight = Expression.Lambda<Func<object, char, float>>(Expression.Convert(
            Expression.MakeMemberAccess(Expression.Call(Expression.Convert(font, glyph.DeclaringType), glyph, Expression.Convert(c, glyphKey)), glyphHeight), typeof(float)), font, c).Compile();
    }

    private static int? Constant(CodeInstruction i)
    {
        OpCode op = i.opcode;
        if (op == OpCodes.Ldc_I4_M1) return -1;
        if (op == OpCodes.Ldc_I4_0) return 0;
        if (op == OpCodes.Ldc_I4_1) return 1;
        if (op == OpCodes.Ldc_I4_2) return 2;
        if (op == OpCodes.Ldc_I4_3) return 3;
        if (op == OpCodes.Ldc_I4_4) return 4;
        if (op == OpCodes.Ldc_I4_5) return 5;
        if (op == OpCodes.Ldc_I4_6) return 6;
        if (op == OpCodes.Ldc_I4_7) return 7;
        if (op == OpCodes.Ldc_I4_8) return 8;
        if (op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S) return Convert.ToInt32(i.operand);
        return null;
    }

    private static MethodInfo DrawMethod(Type draw, params Type[] parameters) =>
        CodeShape.Single(draw.GetMethods(CodeShape.AllDeclared).Where(m => m.IsStatic && m.ReturnType == typeof(void) &&
            m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters)),
            $"static void ({string.Join(", ", parameters.Select(p => p.Name))}) on the draw class");

    private static void ResolveInput()
    {
        Assembly game = HookHost.Game;
        MethodInfo typed = CodeShape.Single(game.GetTypes().SelectMany(t => t.GetMethods(CodeShape.AllDeclared))
            .Where(m => m.IsStatic && m.IsPublic && m.ReturnType == typeof(List<char>) && m.GetParameters().Length == 0), "typed-characters getter");
        _typedChars = (Func<List<char>>)Delegate.CreateDelegate(typeof(Func<List<char>>), typed);
        Type input = typed.DeclaringType;
        Type key = CodeShape.RequireType(game, "Key");

        // The modifier state (shift 1, ctrl 2, alt 4), and the "pressed or repeated this frame"
        // check that takes modifiers and a key: the only one reading the key set directly.
        MethodInfo modifiers = CodeShape.Single(input.GetMethods(CodeShape.AllDeclared).Where(m => m.IsStatic && m.IsPublic &&
            m.GetParameters().Length == 0 && m.ReturnType.IsEnum && m.ReturnType != key), "modifier state getter");
        Type modifierType = modifiers.ReturnType;
        MethodInfo contains = typeof(HashSet<>).MakeGenericType(key).GetMethod("Contains");
        MethodInfo pressed = (MethodInfo)CodeShape.SingleMethod(input.GetMethods(CodeShape.AllDeclared).Where(m => m.IsStatic && m.IsPublic &&
            m.ReturnType == typeof(bool) && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { modifierType, key })),
            il => il.Any(i => i.Calls(contains)), "pressed-or-repeated key check");

        ParameterExpression mods = Expression.Parameter(typeof(int), "mods"), k = Expression.Parameter(typeof(int), "key");
        _modifiers = Expression.Lambda<Func<int>>(Expression.Convert(Expression.Call(modifiers), typeof(int))).Compile();
        _pressed = Expression.Lambda<Func<int, int, bool>>(Expression.Call(pressed,
            Expression.Convert(mods, modifierType), Expression.Convert(k, key)), mods, k).Compile();

        var names = new (ProgramKey, string)[]
        {
            (ProgramKey.Enter, "RETURN"), (ProgramKey.Enter, "KP_ENTER"), (ProgramKey.Backspace, "BACKSPACE"), (ProgramKey.Delete, "DELETE"),
            (ProgramKey.Escape, "ESCAPE"), (ProgramKey.Tab, "TAB"), (ProgramKey.Up, "UP"), (ProgramKey.Down, "DOWN"),
            (ProgramKey.Left, "LEFT"), (ProgramKey.Right, "RIGHT"), (ProgramKey.Home, "HOME"), (ProgramKey.End, "END"),
            (ProgramKey.PageUp, "PAGEUP"), (ProgramKey.PageDown, "PAGEDOWN"),
        };
        _keys = names.Concat(Enumerable.Range(1, 12).Select(n => (ProgramKey.F1 + n - 1, "F" + n)))
            .Select(p => (p.Item1, Enum.IsDefined(key, p.Item2) ? Convert.ToInt32(Enum.Parse(key, p.Item2)) : throw new ShapeMismatchException($"Key.{p.Item2} not found")))
            .ToArray();
    }

    private static void ResolveSaveFolder()
    {
        // GameLogic's save folder method builds the path under My Documents and creates it.
        Type gameLogic = CodeShape.RequireType(HookHost.Game, "GameLogic");
        FieldInfo instance = CodeShape.SingleField(gameLogic, f => f.IsStatic && f.FieldType == gameLogic, "GameLogic instance field");
        MethodInfo getFolderPath = typeof(Environment).GetMethod(nameof(Environment.GetFolderPath), new[] { typeof(Environment.SpecialFolder) });
        MethodInfo createDirectory = typeof(Directory).GetMethod(nameof(Directory.CreateDirectory), new[] { typeof(string) });
        var method = (MethodInfo)CodeShape.SingleMethod(CodeShape.DeclaredMethods(gameLogic).Where(m => !m.IsStatic && m is MethodInfo mi &&
            mi.ReturnType == typeof(string) && m.GetParameters().Length == 0),
            il => il.Any(i => i.Calls(getFolderPath)) && il.Any(i => i.Calls(createDirectory)), "save folder method");
        string folder = null;
        _saveDirectory = () =>
        {
            if (folder != null)
                return folder;

            // Reading GameLogic's statics before the game starts would run its static constructor early.
            if (!_desktopShown)
                throw new InvalidOperationException("the game isn't running yet");
            object game = instance.GetValue(null) ?? throw new InvalidOperationException("the game isn't running yet");
            try
            {
                return folder = (string)method.Invoke(game, null);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        };
    }

    // ---- Registration ----

    private sealed class ProgramHost : IProgramHost
    {
        public void Add(string modId, IModLog log, string label, ModTexture icon, IDesktopProgram program)
        {
            Attach();
            _module ??= AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("LastCallBBS.Programs"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("Programs");
            var registration = new Registration(modId, log, label, icon, program, Registrations.Count);
            registration.WindowType = EmitWindow(registration);
            Registrations.Add(registration);
            Log.Info($"Program \"{label}\" added by {modId}.");
        }

        public string DataDirectory(string modId)
        {
            Resolve();
            return Path.Combine(_saveDirectory(), "mods", modId);
        }
    }

    private static Type EmitWindow(Registration registration)
    {
        string name = new string(registration.Label.Where(char.IsLetterOrDigit).ToArray());
        TypeBuilder tb = _module.DefineType($"LastCallBBS.Programs.{(name.Length > 0 ? name : "Program")}{registration.Index}",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, _base);
        ILGenerator ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes).GetILGenerator();
        ctor.Emit(OpCodes.Ldarg_0);
        ctor.Emit(OpCodes.Call, _baseCtor);
        ctor.Emit(OpCodes.Ret);

        Override(tb, _title, nameof(ProgramWindowCalls.Title));
        Override(tb, _size, nameof(ProgramWindowCalls.Size));
        Override(tb, _palette, nameof(ProgramWindowCalls.Palette));
        Override(tb, _update, nameof(ProgramWindowCalls.Update));
        Override(tb, _opened, nameof(ProgramWindowCalls.Opened));
        Override(tb, _closed, nameof(ProgramWindowCalls.Closed));
        if (registration.Program is IProgramMenu)
            Override(tb, _menu, nameof(ProgramWindowCalls.Menu));
        return tb.CreateType();
    }

    private static void Override(TypeBuilder tb, MethodInfo target, string call)
    {
        MethodAttributes access = target.Attributes & MethodAttributes.MemberAccessMask;
        MethodBuilder m = tb.DefineMethod(target.Name, access | MethodAttributes.Virtual | MethodAttributes.HideBySig, target.ReturnType, Type.EmptyTypes);
        ILGenerator il = m.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(ProgramWindowCalls).GetMethod(call));
        if (target.ReturnType != typeof(void))
            il.Emit(target.ReturnType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, target.ReturnType);
        il.Emit(OpCodes.Ret);
        tb.DefineMethodOverride(m, target);
    }

    // ---- Desktop icons ----

    private static void IconPrefix(object __2) => _iconColor ??= __2;

    private static IEnumerable<CodeInstruction> DesktopTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> il = instructions.ToList();
        int at = IconBlockEnd(il) + 1;
        var load = new CodeInstruction(OpCodes.Ldarg_0);
        load.labels.AddRange(il[at].labels);
        il[at].labels.Clear();
        il.Insert(at, load);
        il.Insert(at + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DesktopPrograms), nameof(DrawIcons))));
        return il;
    }

    // Inside the desktop's input scope, so windows on top block clicks on the icons.
    public static void DrawIcons(object desktop)
    {
        _desktopShown = true;
        if (_iconsFailed || _iconColor == null)
            return;
        foreach (Registration registration in Registrations)
        {
            try
            {
                registration.Texture ??= IconTexture(registration);
                int slot = registration.Index;
                object position = _vector2Ctor.Invoke(new object[] { IconColumnX + IconColumnStep * (slot / IconRows.Length), IconRows[slot % IconRows.Length] });
                _drawIcon.Invoke(desktop, new[] { registration.Texture, registration.Label, _iconColor, position, registration.OpenAction });
            }
            catch (Exception e)
            {
                _iconsFailed = true;
                Log.Error($"Programs: desktop icons disabled: {Unwrap(e)}");
                return;
            }
        }
    }

    // Game icons sit in a 100x100 frame: centred, 9 px above the frame's bottom. A texture made
    // from a PNG has no offset, so it gets the same placement here.
    private static object IconTexture(Registration registration)
    {
        object texture = RuntimeTextures.Source.Create(registration.Icon.Path);
        int w = registration.Icon.Width, h = registration.Icon.Height;
        FieldInfo[] offset = _textureVectors.Where(f => !IsVector(f.GetValue(texture), w, h)).ToArray();
        if (offset.Length != 1 || !IsVector(offset[0].GetValue(texture), 0f, 0f))
            throw new ShapeMismatchException("can't tell the icon texture's offset from its size");
        offset[0].SetValue(texture, _vector2Ctor.Invoke(new object[] { (float)Math.Floor((100 - w) / 2f), 9f }));
        return texture;
    }

    // Not Equals: the game's Vector2.Equals(object) calls object.Equals(this, other), which recurses forever.
    private static bool IsVector(object vector, float x, float y)
    {
        Type type = vector.GetType();
        return (float)type.GetField("X").GetValue(vector) == x && (float)type.GetField("Y").GetValue(vector) == y;
    }

    private static void Open(Registration registration)
    {
        try
        {
            int columns = Math.Max(1, Math.Min(registration.Program.Columns, TextScreen.MaxColumns));
            int rows = Math.Max(1, Math.Min(registration.Program.Rows, registration.Program is IProgramMenu ? TextScreen.MaxRowsWithMenu : TextScreen.MaxRows));
            object window = Activator.CreateInstance(registration.WindowType);
            Sessions.Add(window, new Session(registration, new TextScreen(columns, rows)));
            _openWindow.Invoke(_getDesktop.Invoke(null, null), new[] { window, Activator.CreateInstance(_maybeBase) });
        }
        catch (Exception e)
        {
            registration.Log.Error($"{registration.Label} could not open: {Unwrap(e)}");
        }
    }

    // ---- Window members, called from the emitted subclasses ----

    internal static string Title(object self)
    {
        Session session = SessionOf(self);
        if (session.Failed)
            return session.Registration.Label;
        try
        {
            return session.Registration.Program.Title ?? session.Registration.Label;
        }
        catch (Exception e)
        {
            session.Fail("Title", e);
            return session.Registration.Label;
        }
    }

    internal static object Size(object self)
    {
        TextScreen screen = SessionOf(self).Window.Screen;
        return _vector2Ctor.Invoke(new object[] { (float)(screen.Columns * CellW), (float)(screen.Rows * CellH) });
    }

    internal static object Palette(object self) => _themePalette ??= _makePalette();

    // Read several times a frame by the desktop. Never empty, because an empty menu would give the
    // window the short frame it wasn't sized for.
    internal static object Menu(object self)
    {
        Session session = SessionOf(self);
        IReadOnlyList<ProgramMenuItem> items = null;
        if (!session.Failed)
        {
            try
            {
                items = ((IProgramMenu)session.Registration.Program).MenuItems;
            }
            catch (Exception e)
            {
                session.Fail("MenuItems", e);
            }
        }
        var menu = new List<object>();
        foreach (ProgramMenuItem item in items ?? Array.Empty<ProgramMenuItem>())
        {
            if (item == null)
                continue;
            if (item.IsSeparator)
            {
                menu.Add(_menuSeparatorCtor.Invoke(null));
                continue;
            }
            Action run = () => session.Call("menu item " + item.Label, item.Action);
            menu.Add(_menuItemCtor.Invoke(new[] { item.Label, Activator.CreateInstance(_noShortcut), item.Enabled && !session.Failed, run }));
        }
        if (menu.Count == 0)
            menu.Add(_menuSeparatorCtor.Invoke(null));
        Array result = Array.CreateInstance(_menuItemType, menu.Count);
        for (int i = 0; i < menu.Count; i++)
            result.SetValue(menu[i], i);
        return result;
    }

    internal static void Opened(object self)
    {
        Session session = SessionOf(self);
        session.Clock.Start();
        session.Call("Opened", () => session.Registration.Program.Opened(session.Window));
    }

    internal static void Closed(object self)
    {
        Session session = SessionOf(self);
        session.Call("Closed", () => session.Registration.Program.Closed(session.Window));
        session.Closed = true;
    }

    internal static void Update(object self)
    {
        Session session = SessionOf(self);
        if (session.Closed)
            return;
        float seconds = (float)session.Clock.Elapsed.TotalSeconds;
        session.Clock.Restart();
        session.Blink = (session.Blink + seconds) % 1f;

        if (!session.Failed)
        {
            foreach (KeyPress key in ReadKeys())
            {
                KeyPress k = key;
                if (!session.Call("KeyPressed", () => session.Registration.Program.KeyPressed(session.Window, k)))
                    break;
            }
            session.Call("Update", () => session.Registration.Program.Update(session.Window, seconds));
        }
        try
        {
            Draw(session.Window.Screen, session.Blink < 0.5f);
        }
        catch (Exception e)
        {
            if (!session.DrawFailed)
                session.Registration.Log.Error($"{session.Registration.Label}: drawing failed: {Unwrap(e)}");
            session.DrawFailed = true;
        }
    }

    private static Session SessionOf(object self) =>
        Sessions.TryGetValue(self, out Session session) ? session : throw new InvalidOperationException("program window without a session");

    // Typed characters first, then keys: within one frame their order is unknown.
    private static List<KeyPress> ReadKeys()
    {
        var keys = new List<KeyPress>();
        int mods = _modifiers();
        bool shift = (mods & 1) != 0, control = (mods & 2) != 0, alt = (mods & 4) != 0;
        foreach (char c in _typedChars())
        {
            if (c >= ' ' && c != '\u007f')
                keys.Add(new KeyPress(ProgramKey.Character, c, shift, control, alt));
        }
        ProgramKey last = (ProgramKey)(-1);
        foreach ((ProgramKey program, int game) in _keys)
        {
            if (program != last && _pressed(mods, game))
            {
                keys.Add(new KeyPress(program, '\0', shift, control, alt));
                last = program;
            }
        }
        return keys;
    }

    private static void Draw(TextScreen screen, bool cursorOn)
    {
        Face face = FaceFor(screen.Font);
        int background = screen.Background.Rgb;
        _clear(background);
        bool cursor = screen.CursorVisible && cursorOn;
        for (int row = 0; row < screen.Rows; row++)
        {
            float y = (screen.Rows - row - 1) * CellH;
            int runStart = 0, runColor = background;
            for (int col = 0; col <= screen.Columns; col++)
            {
                int color = col == screen.Columns ? background : Back(screen, col, row, cursor);
                if (color == runColor)
                    continue;
                if (runColor != background)
                    _fill(runStart * CellW, y, (col - runStart) * CellW, CellH, runColor);
                runStart = col;
                runColor = color;
            }
            for (int col = 0; col < screen.Columns; col++)
            {
                char c = screen.GetChar(col, row);
                if (c == ' ')
                    continue;
                (Face drawn, char glyph, float width) = Glyph(face, c);
                if (width > 0f)
                    _drawChar(glyph, col * CellW + (float)Math.Ceiling((CellW - width) / 2f), y + drawn.Raise, drawn.Font, Fore(screen, col, row, cursor));
            }
        }
    }

    // The desktop font's < and > are tall chevrons from the game's terminal look, not text.
    private const string DesktopFontReplaced = "<>";

    /// <summary>
    /// The face, glyph and width to draw a character with. The desktop font's faces lack some
    /// ASCII (= @ $ _ { } | ~) and accented letters, and Mono lacks everything past ASCII; such
    /// characters, and the desktop font's chevron &lt; and &gt;, come from Mono, then CGA, and anything
    /// none of them has shows as Mono's '?'.
    /// </summary>
    private static (Face Face, char Glyph, float Width) Glyph(Face face, char c)
    {
        char glyph = face.Map(c);
        float width = GlyphWidth(face, glyph);
        bool desktopFont = face.Kind == TextFont.Bold || face.Kind == TextFont.Regular;
        bool missing = width <= 0f || glyph == '?' && c != '?';
        if (!missing && !(desktopFont && DesktopFontReplaced.IndexOf(c) >= 0))
            return (face, glyph, width);
        foreach (TextFont fallback in new[] { TextFont.Mono, TextFont.Cga })
        {
            Face other = FaceFor(fallback);
            if (other.Kind != fallback || other == face)
                continue;
            char g = other.Map(c);
            float w = GlyphWidth(other, g);
            if (w > 0f && (g != '?' || c == '?'))
                return (other, g, w);
        }
        Face mono = FaceFor(TextFont.Mono);
        return mono.Kind == TextFont.Mono ? (mono, '?', GlyphWidth(mono, '?')) : (face, glyph, width);
    }

    private static float GlyphWidth(Face face, char glyph)
    {
        if (!GlyphWidths.TryGetValue((face.Kind, glyph), out float width))
            GlyphWidths[(face.Kind, glyph)] = width = _glyphWidth(face.Font, glyph);
        return width;
    }

    private static Face FaceFor(TextFont kind)
    {
        if (Faces.TryGetValue(kind, out Face face))
            return face;
        try
        {
            object font = _gridFont(kind);
            float height = _glyphHeight(font, 'M');
            // A grid font's position is the top of its cell, drawn downward: centre the cell in the
            // row. The desktop font's position is near the baseline, 3 px above the row's bottom.
            int raise = height < 0 ? (int)Math.Ceiling((CellH - height) / 2f) : 3;
            face = new Face(kind, font, raise);
        }
        catch (Exception e) when (kind != TextFont.Bold)
        {
            Log.Warn($"Programs: font {kind} unavailable, using Bold: {Unwrap(e).Message}");
            face = FaceFor(TextFont.Bold);
        }
        Faces[kind] = face;
        return face;
    }

    private sealed class Face
    {
        private readonly Dictionary<char, char> _map = new();

        public Face(TextFont kind, object font, int raise)
        {
            Kind = kind;
            Font = font;
            Raise = raise;
        }

        public TextFont Kind { get; }
        public object Font { get; }
        public int Raise { get; }

        /// <summary>The glyph to draw for a character: code page 437 for Cga, ASCII for Mono.</summary>
        public char Map(char c)
        {
            if (c < 127 || Kind == TextFont.Bold || Kind == TextFont.Regular)
                return c;
            if (_map.TryGetValue(c, out char mapped))
                return mapped;
            if (Kind == TextFont.Cga)
            {
                mapped = (char)CodePage437.GetBytes(new[] { c })[0];
            }
            else
            {
                string folded = c.ToString().Normalize(System.Text.NormalizationForm.FormD);
                mapped = folded.Length > 0 && folded[0] < 127 ? folded[0] : '?';
            }
            return _map[c] = mapped;
        }
    }

    // The cursor swaps its cell's colours.
    private static int Back(TextScreen screen, int col, int row, bool cursor) =>
        (cursor && col == screen.CursorColumn && row == screen.CursorRow ? screen.GetForeground(col, row) : screen.GetBackground(col, row)).Rgb;

    private static int Fore(TextScreen screen, int col, int row, bool cursor) =>
        (cursor && col == screen.CursorColumn && row == screen.CursorRow ? screen.GetBackground(col, row) : screen.GetForeground(col, row)).Rgb;

    private static Exception Unwrap(Exception e) => e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;

    private sealed class Registration
    {
        public Registration(string modId, IModLog log, string label, ModTexture icon, IDesktopProgram program, int index)
        {
            ModId = modId;
            Log = log;
            Label = label;
            Icon = icon;
            Program = program;
            Index = index;
            OpenAction = () => Open(this);
        }

        public string ModId { get; }
        public IModLog Log { get; }
        public string Label { get; }
        public ModTexture Icon { get; }
        public IDesktopProgram Program { get; }
        public int Index { get; }
        public Action OpenAction { get; }
        public Type WindowType;
        public object Texture;
    }

    private sealed class Session
    {
        public Session(Registration registration, TextScreen screen)
        {
            Registration = registration;
            Window = new ProgramWindow(screen, new ModPrograms(Host, registration.ModId, registration.Log, true));
        }

        public Registration Registration { get; }
        public ProgramWindow Window { get; }
        public Stopwatch Clock { get; } = new();
        public float Blink;
        public bool Failed, DrawFailed, Closed;

        /// <summary>Runs one program callback. On an exception the program stops and its screen shows why.</summary>
        public bool Call(string what, Action action)
        {
            if (Failed)
                return false;
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                Fail(what, e);
                return false;
            }
        }

        public void Fail(string what, Exception e)
        {
            Failed = true;
            Registration.Log.Error($"{Registration.Label} stopped: {what} threw {e}");
            TextScreen screen = Window.Screen;
            screen.Foreground = TextColor.FromRgb(0xFFB0A0);
            screen.Background = TextColor.FromRgb(0x200808);
            screen.CursorVisible = false;
            screen.Clear();
            screen.Write(1, 1, $"{Registration.Label} stopped: {e.GetType().Name}");
            screen.Write(1, 2, e.Message);
            screen.Write(1, 4, "Details are in LastCallBBS.Launcher.log. Close and reopen the window to restart.");
        }
    }
}

/// <summary>Called by the emitted window classes, which live in another assembly.</summary>
public static class ProgramWindowCalls
{
    public static object Title(object self) => DesktopPrograms.Title(self);
    public static object Size(object self) => DesktopPrograms.Size(self);
    public static object Palette(object self) => DesktopPrograms.Palette(self);
    public static object Menu(object self) => DesktopPrograms.Menu(self);
    public static void Update(object self) => DesktopPrograms.Update(self);
    public static void Opened(object self) => DesktopPrograms.Opened(self);
    public static void Closed(object self) => DesktopPrograms.Closed(self);
}
