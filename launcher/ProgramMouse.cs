using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LastCallBBS.ModAPI;

namespace LastCallBBS.Launcher;

/// <summary>
/// Mouse input for program windows, from the game's static input class. Before each window
/// updates, the desktop moves the mouse into the window's content coordinates (origin bottom-left)
/// and hides it from windows that aren't on top; game windows click with the same calls.
/// </summary>
internal static class ProgramMouse
{
    private static Func<(float X, float Y)> _position;
    private static Func<int, bool> _held;
    private static Func<int> _wheel;
    private static (MouseButton Button, int Game)[] _buttons;
    private static string _failure;
    private static bool _resolved;

    /// <summary>Finds the game code. Returns one message if it couldn't be found.</summary>
    public static List<string> Verify()
    {
        try
        {
            Resolve();
            Log.Info("Program mouse: OK");
            return new List<string>();
        }
        catch (ShapeMismatchException e)
        {
            Log.Error($"Program mouse: FAILED - {e.Message}");
            return new List<string> { $"program mouse: {e.Message}" };
        }
    }

    /// <summary>Whether mouse input works on this build. The first failure is logged.</summary>
    public static bool Available
    {
        get
        {
            if (_resolved)
                return true;
            if (_failure != null)
                return false;
            try
            {
                Resolve();
                return true;
            }
            catch (ShapeMismatchException e)
            {
                Log.Warn($"Program windows get no mouse input: {e.Message}");
                return false;
            }
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
            Assembly game = HookHost.Game;
            MethodInfo typed = CodeShape.Single(game.GetTypes().SelectMany(t => t.GetMethods(CodeShape.AllDeclared))
                .Where(m => m.IsStatic && m.IsPublic && m.ReturnType == typeof(List<char>) && m.GetParameters().Length == 0), "typed-characters getter");
            Type input = typed.DeclaringType;
            Type vector2 = CodeShape.RequireType(game, "Vector2");
            MethodBase[] statics = input.GetMethods(CodeShape.AllDeclared).Where(m => m.IsStatic && m.IsPublic).ToArray<MethodBase>();

            // The position windows use: (-1000, -1000) while the mouse belongs to something else.
            var position = (MethodInfo)CodeShape.SingleMethod(statics.Where(m => ((MethodInfo)m).ReturnType == vector2 && m.GetParameters().Length == 0),
                il => il.Any(i => i.opcode == OpCodes.Ldc_R4 && i.operand is float f && f == -1000f), "guarded mouse position getter");

            // Held check: the only (button) method that reads the held set once.
            Type button = CodeShape.Single(game.GetTypes().Where(t => t.IsEnum && new[] { "Left", "Middle", "Right", "Back" }.All(n => Enum.IsDefined(t, n))), "mouse button enum");
            MethodInfo contains = typeof(HashSet<>).MakeGenericType(button).GetMethod("Contains");
            var held = (MethodInfo)CodeShape.SingleMethod(statics.Where(m => ((MethodInfo)m).ReturnType == typeof(bool) &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { button })),
                il => il.Count(i => i.Calls(contains)) == 1, "mouse button held check");

            var wheel = (MethodInfo)CodeShape.Single(statics.Where(m => ((MethodInfo)m).ReturnType == typeof(int) && m.GetParameters().Length == 0), "mouse wheel getter");

            ParameterExpression v = Expression.Variable(vector2, "v");
            _position = Expression.Lambda<Func<(float, float)>>(Expression.Block(new[] { v },
                Expression.Assign(v, Expression.Call(position)),
                Expression.New(typeof((float, float)).GetConstructor(new[] { typeof(float), typeof(float) }),
                    Expression.Field(v, "X"), Expression.Field(v, "Y")))).Compile();
            ParameterExpression b = Expression.Parameter(typeof(int), "button");
            _held = Expression.Lambda<Func<int, bool>>(Expression.Call(held, Expression.Convert(b, button)), b).Compile();
            _wheel = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), wheel);
            _buttons = new[]
            {
                (MouseButton.Left, Convert.ToInt32(Enum.Parse(button, "Left"))),
                (MouseButton.Right, Convert.ToInt32(Enum.Parse(button, "Right"))),
                (MouseButton.Middle, Convert.ToInt32(Enum.Parse(button, "Middle"))),
            };
            _resolved = true;
        }
        catch (ShapeMismatchException e)
        {
            _failure = e.Message;
            throw;
        }
    }

    /// <summary>
    /// Updates the window's mouse cell and returns this frame's clicks and wheel. <paramref name="armed"/>
    /// keeps, per button, whether the window has seen it up while the mouse was over the grid: a click is
    /// an armed button going down. The desktop hides the mouse from windows while a menu is open and
    /// the menu acts on the press, so the press that picks a menu item reaches the window already held
    /// and isn't a click.
    /// </summary>
    public static (List<MouseClick> Clicks, int Wheel) Read(ProgramWindow window, bool[] armed, int cellWidth, int cellHeight)
    {
        var clicks = new List<MouseClick>();
        if (!Available)
            return (clicks, 0);
        TextScreen screen = window.Screen;
        (float x, float y) = _position();
        int column = (int)Math.Floor(x / cellWidth);
        int row = screen.Rows - 1 - (int)Math.Floor(y / cellHeight);
        bool inside = x >= 0 && y >= 0 && column < screen.Columns && row >= 0;
        window.MouseColumn = inside ? column : -1;
        window.MouseRow = inside ? row : -1;
        for (int i = 0; i < _buttons.Length; i++)
        {
            bool held = _held(_buttons[i].Game);
            if (!inside)
                armed[i] = false;
            else if (!held)
                armed[i] = true;
            else if (armed[i])
            {
                clicks.Add(new MouseClick(column, row, _buttons[i].Button));
                armed[i] = false;
            }
        }
        return (clicks, inside ? _wheel() : 0);
    }
}
