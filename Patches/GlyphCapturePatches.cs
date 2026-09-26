using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;

namespace LanguageStudyStardewValleyMod.Patches
{
    /// <summary>
    /// Records where each character of a drawn string actually landed, from inside the game's two
    /// text renderers' own glyph loops.
    ///
    /// The previous approach re-derived the layout after the fact -- re-wrapping SpriteText's
    /// input, measuring prefixes, assuming a line height -- and dialogue drifted wherever the
    /// guess disagreed with the renderer. SpriteText.drawString (1.6.15 IL) strips "\n" entirely,
    /// breaks lines on '^', wraps at <c>x + width - 4</c> by <c>Game1.asianSpacingRegex</c> units
    /// (keeping small kana and closing punctuation with the character before them), skips glyphs
    /// missing from the font and only draws up to the typewriter position. Every one of those was
    /// a source of error; none of them matter when the positions are read rather than predicted.
    ///
    /// Transpilers insert a call at two points in each loop: where a character is drawn (its
    /// pen position then is the cell's left edge) and at the loop increment (the pen position
    /// there is its right edge). Characters that are never drawn -- line breaks, '^', missing
    /// glyphs, text past the typewriter -- never produce a cell.
    ///
    /// If a transpiler can't find the shape it expects (a game or MonoGame update), it leaves the
    /// method untouched, logs a warning, and the renderer stays on the old re-derived layout.
    /// </summary>
    public static class GlyphCapturePatches
    {
        /// <summary>Whether SpriteText.drawString was instrumented.</summary>
        public static bool SpriteTextActive { get; private set; }

        /// <summary>Which of the four SpriteBatch.DrawString overloads the game calls were instrumented.</summary>
        public static bool StringActive { get; private set; }
        public static bool StringScaledActive { get; private set; }
        public static bool BuilderActive { get; private set; }
        public static bool BuilderScaledActive { get; private set; }

        /// <summary>Where the glyphs of the string being drawn right now go; null to discard them.</summary>
        private static List<GlyphCell>? current;

        /// <summary>The height of one line of the string being drawn, in its renderer's local units.</summary>
        private static float currentLineHeight;

        /// <summary>The length of the string being drawn, to recognise its last character.</summary>
        private static int currentLength;

        /// <summary>The character whose left edge has been seen, awaiting its right edge.</summary>
        private static int pendingIndex = -1;
        private static float pendingX;
        private static float pendingY;

        private static readonly Stack<List<GlyphCell>> pool = new();

        #region Capture state
        /// <summary>Starts capturing glyphs for a string about to be drawn, returning the list they'll land in.</summary>
        public static List<GlyphCell> Begin(int length, float lineHeight)
        {
            var list = pool.Count > 0 ? pool.Pop() : new List<GlyphCell>();
            current = list;
            currentLength = length;
            currentLineHeight = lineHeight;
            pendingIndex = -1;
            return list;
        }

        /// <summary>Discards glyphs until the next <see cref="Begin"/> -- for text that isn't being recorded.</summary>
        public static void Ignore()
        {
            current = null;
            pendingIndex = -1;
        }

        /// <summary>Hands a string's glyph list back for reuse once the frame it belonged to has been hit-tested.</summary>
        public static void Recycle(List<GlyphCell>? list)
        {
            if (list is null)
                return;

            if (ReferenceEquals(list, current))
                current = null;

            list.Clear();
            if (pool.Count < 512)
                pool.Push(list);
        }

        /// <summary>The line height SpriteText is about to draw with, per the branch its glyph loop will take.</summary>
        public static float SpriteTextLineHeight()
        {
            float zoom = SpriteText.FontPixelZoom;
            bool spriteSheet = LocalizedContentManager.CurrentLanguageLatin || SpriteText.FontFile is null;
            return spriteSheet ? 18 * zoom : (SpriteText.FontFile!.Common.LineHeight + 2) * zoom;
        }
        #endregion

        #region Callbacks the transpiled loops call
        /// <summary>SpriteText is about to draw character <paramref name="index"/> with its pen at <paramref name="pen"/>.</summary>
        public static void SpriteTextGlyphDrawn(int index, Vector2 pen)
        {
            if (current is null)
                return;

            // some locales draw a character several times (shadow passes) -- same pen each time
            pendingIndex = index;
            pendingX = pen.X;
            pendingY = pen.Y;
        }

        /// <summary>SpriteText has finished with character <paramref name="index"/>; the pen now stands past it.</summary>
        public static void SpriteTextCharDone(int index, Vector2 pen)
        {
            if (current is null || pendingIndex != index)
                return;

            pendingIndex = -1;
            if (Math.Abs(pen.Y - pendingY) >= 0.5f)
                return;

            float right = pen.X;

            // the sprite-sheet (Latin) branch doesn't advance past the string's last character, so
            // its cell would be a sliver; give it a glyph's nominal width
            float nominal = 8 * SpriteText.FontPixelZoom;
            if (index == currentLength - 1 && right - pendingX < nominal / 2)
                right = pendingX + nominal;

            if (right <= pendingX)
                return;

            current.Add(new GlyphCell(index, pendingX, pendingY, right, pendingY + currentLineHeight));
        }

        /// <summary>DrawString is starting character <paramref name="index"/> with its pen at <paramref name="offset"/> (string-local units).</summary>
        public static void FontCharStart(int index, Vector2 offset)
        {
            if (current is null)
                return;

            pendingIndex = index;
            pendingX = offset.X;
            pendingY = offset.Y;
        }

        /// <summary>DrawString (the scaled overloads) has finished character <paramref name="index"/>; <paramref name="transform"/> is its local-to-screen matrix.</summary>
        public static void FontCharDone(int index, Vector2 offset, Matrix transform)
        {
            if (!TryTakeFontCell(index, offset, out Vector2 topLeft, out Vector2 bottomRight))
                return;

            AddTransformed(index, Vector2.Transform(topLeft, transform), Vector2.Transform(bottomRight, transform));
        }

        /// <summary>DrawString (the unscaled overloads) has finished character <paramref name="index"/>, drawn relative to <paramref name="position"/>.</summary>
        public static void FontCharDoneUnscaled(int index, Vector2 offset, Vector2 position)
        {
            if (!TryTakeFontCell(index, offset, out Vector2 topLeft, out Vector2 bottomRight))
                return;

            AddTransformed(index, position + topLeft, position + bottomRight);
        }

        private static bool TryTakeFontCell(int index, Vector2 offset, out Vector2 topLeft, out Vector2 bottomRight)
        {
            topLeft = bottomRight = default;

            if (current is null || pendingIndex != index)
                return false;

            pendingIndex = -1;

            // a '\n' moved the pen to the next line and a '\r' didn't move it: neither is a glyph
            if (Math.Abs(offset.Y - pendingY) >= 0.5f || offset.X <= pendingX)
                return false;

            topLeft = new Vector2(pendingX, pendingY);
            bottomRight = new Vector2(offset.X, pendingY + currentLineHeight);
            return true;
        }

        private static void AddTransformed(int index, Vector2 a, Vector2 b)
        {
            // a flipped or rotated draw can swap the corners
            current!.Add(new GlyphCell(index, Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
        }
        #endregion

        #region Transpilers
        /// <summary>Instruments SpriteText.drawString's glyph loop.</summary>
        public static IEnumerable<CodeInstruction> Transpile_SpriteTextDrawString(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();

            try
            {
                var loop = FindLoop(code, textArg: 1, extraLengthOperand: true)
                           ?? throw new InvalidOperationException("the glyph loop's condition (i < Math.Min(s.Length, characterPosition))");

                // the pen: the Vector2 whose X is reset to the x argument at every line break
                CodeInstruction pen = FindVectorReset(code, loop, value => value.opcode == OpCodes.Ldarg_2)
                                      ?? throw new InvalidOperationException("the pen position (pen.X = x at a line break)");

                var draws = Enumerable.Range(loop.BodyStart, loop.ConditionStart - loop.BodyStart)
                    .Where(i => code[i].opcode == OpCodes.Callvirt
                                && code[i].operand is MethodInfo { Name: nameof(SpriteBatch.Draw) } method
                                && method.DeclaringType == typeof(SpriteBatch))
                    .ToList();
                if (draws.Count == 0)
                    throw new InvalidOperationException("any SpriteBatch.Draw call inside the glyph loop");

                // insert back to front so earlier indices stay valid
                code.InsertRange(loop.Increment, WithLabelsFrom(code[loop.Increment],
                    LoadLocal(loop.Index), LoadLocal(pen), Call(nameof(SpriteTextCharDone))));

                foreach (int at in draws.OrderByDescending(i => i))
                    code.InsertRange(at, new[] { LoadLocal(loop.Index), LoadLocal(pen), Call(nameof(SpriteTextGlyphDrawn)) });

                SpriteTextActive = true;
                return code;
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Couldn't instrument SpriteText.drawString -- couldn't find {ex.Message}. Dialogue word hover falls back to re-wrapping the text, which can misplace words.", LogLevel.Warn);
                return instructions;
            }
        }

        /// <summary>Instruments one of MonoGame's SpriteBatch.DrawString glyph loops.</summary>
        public static IEnumerable<CodeInstruction> Transpile_DrawString(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = instructions.ToList();
            var parameters = original.GetParameters();
            bool builder = parameters[1].ParameterType == typeof(StringBuilder);
            bool scaled = parameters.Length > 4;

            try
            {
                var loop = FindLoop(code, textArg: 2, extraLengthOperand: false)
                           ?? throw new InvalidOperationException("the glyph loop's condition (i < text.Length)");

                // the pen: the Vector2 whose X is zeroed at every '\n'
                CodeInstruction offset = FindVectorReset(code, loop, value => value.opcode == OpCodes.Ldc_R4 && value.operand is 0f)
                                         ?? throw new InvalidOperationException("the pen offset (offset.X = 0 at a newline)");

                // the scaled overloads map the pen through a matrix; the unscaled ones just add the position
                CodeInstruction[] toScreen;
                string done;
                int transform = Enumerable.Range(loop.BodyStart, loop.ConditionStart - loop.BodyStart)
                    .FirstOrDefault(i => code[i].opcode == OpCodes.Call
                                         && code[i].operand is MethodInfo { Name: nameof(Vector2.Transform) } method
                                         && method.DeclaringType == typeof(Vector2)
                                         && method.GetParameters().Length == 3, -1);
                if (transform >= 0)
                {
                    toScreen = new[] { LoadLocal(code[transform - 2]) };
                    done = nameof(FontCharDone);
                }
                else if (!scaled)
                {
                    toScreen = new[] { new CodeInstruction(OpCodes.Ldarg_3) };
                    done = nameof(FontCharDoneUnscaled);
                }
                else
                    throw new InvalidOperationException("the local-to-screen matrix (Vector2.Transform in the glyph loop)");

                var atIncrement = new List<CodeInstruction> { LoadLocal(loop.Index), LoadLocal(offset) };
                atIncrement.AddRange(toScreen);
                atIncrement.Add(Call(done));
                code.InsertRange(loop.Increment, WithLabelsFrom(code[loop.Increment], atIncrement.ToArray()));

                code.InsertRange(loop.BodyStart, WithLabelsFrom(code[loop.BodyStart],
                    LoadLocal(loop.Index), LoadLocal(offset), Call(nameof(FontCharStart))));

                SetDrawStringActive(builder, scaled);
                return code;
            }
            catch (Exception ex)
            {
                ModEntry.Log($"Couldn't instrument SpriteBatch.DrawString({(builder ? "StringBuilder" : "string")}, scaled: {scaled}) -- couldn't find {ex.Message}. That text falls back to measured layout.", LogLevel.Warn);
                return instructions;
            }
        }

        private static void SetDrawStringActive(bool builder, bool scaled)
        {
            if (builder && scaled) BuilderScaledActive = true;
            else if (builder) BuilderActive = true;
            else if (scaled) StringScaledActive = true;
            else StringActive = true;
        }

        /// <param name="Index">The instruction that loads the loop's character index.</param>
        /// <param name="BodyStart">The first instruction of the loop body.</param>
        /// <param name="Increment">The first instruction of <c>i++</c>, where every path through the body ends up.</param>
        /// <param name="ConditionStart">The first instruction of the loop condition.</param>
        private sealed record Loop(CodeInstruction Index, int BodyStart, int Increment, int ConditionStart);

        /// <summary>
        /// Finds a <c>for (i = 0; i &lt; text.Length [, min with another arg]; i++)</c> loop: the
        /// condition <c>ldloc i; ldarg text; callvirt get_Length; [ldarg; call Math.Min;] blt body</c>,
        /// the body it branches back to, and the <c>ldloc i; ldc.i4.1; add; stloc i</c> just before it.
        /// </summary>
        private static Loop? FindLoop(List<CodeInstruction> code, int textArg, bool extraLengthOperand)
        {
            for (int k = code.Count - 4; k >= 4; k--)
            {
                if (!code[k].IsLdloc() || !IsLdarg(code[k + 1], textArg))
                    continue;

                if (code[k + 2].operand is not MethodInfo { Name: "get_Length" })
                    continue;

                int branch = k + 3;
                if (extraLengthOperand)
                {
                    if (branch + 1 >= code.Count || !code[branch].IsLdarg() || code[branch + 1].operand is not MethodInfo { Name: nameof(Math.Min) })
                        continue;
                    branch += 2;
                }

                if ((code[branch].opcode != OpCodes.Blt && code[branch].opcode != OpCodes.Blt_S) || code[branch].operand is not Label body)
                    continue;

                int bodyStart = code.FindIndex(instruction => instruction.labels.Contains(body));
                int increment = k - 4;
                if (bodyStart < 0 || increment < bodyStart
                    || !SameLocal(code[increment], code[k]) || code[increment + 1].opcode != OpCodes.Ldc_I4_1
                    || code[increment + 2].opcode != OpCodes.Add || !code[increment + 3].IsStloc() || !SameLocal(code[increment + 3], code[k]))
                    continue;

                return new Loop(code[k], bodyStart, increment, k);
            }

            return null;
        }

        /// <summary>Finds <c>ldloca v; &lt;value&gt;; [conv.r4;] stfld Vector2.X</c> in a loop body, returning the <c>ldloca</c>.</summary>
        private static CodeInstruction? FindVectorReset(List<CodeInstruction> code, Loop loop, Func<CodeInstruction, bool> value)
        {
            for (int i = loop.BodyStart; i + 2 < loop.ConditionStart; i++)
            {
                if (code[i].opcode != OpCodes.Ldloca && code[i].opcode != OpCodes.Ldloca_S)
                    continue;

                if (code[i].operand is not LocalVariableInfo { LocalType: var type } || type != typeof(Vector2) || !value(code[i + 1]))
                    continue;

                int store = code[i + 2].opcode == OpCodes.Conv_R4 ? i + 3 : i + 2;
                if (store < code.Count && code[store].opcode == OpCodes.Stfld && code[store].operand is FieldInfo { Name: nameof(Vector2.X) })
                    return code[i];
            }

            return null;
        }

        private static bool IsLdarg(CodeInstruction instruction, int index)
        {
            return index switch
            {
                1 => instruction.opcode == OpCodes.Ldarg_1,
                2 => instruction.opcode == OpCodes.Ldarg_2,
                _ => false
            };
        }

        /// <summary>The local an ldloc/ldloca/stloc refers to, as an index.</summary>
        private static int? LocalIndex(CodeInstruction instruction)
        {
            var op = instruction.opcode;
            if (op == OpCodes.Ldloc_0 || op == OpCodes.Stloc_0) return 0;
            if (op == OpCodes.Ldloc_1 || op == OpCodes.Stloc_1) return 1;
            if (op == OpCodes.Ldloc_2 || op == OpCodes.Stloc_2) return 2;
            if (op == OpCodes.Ldloc_3 || op == OpCodes.Stloc_3) return 3;

            return instruction.operand switch
            {
                LocalVariableInfo local => local.LocalIndex,
                int index => index,
                byte index => index,
                _ => null
            };
        }

        private static bool SameLocal(CodeInstruction a, CodeInstruction b) => LocalIndex(a) is { } index && index == LocalIndex(b);

        /// <summary>An instruction that loads (by value) the local another instruction loads, stores or takes the address of.</summary>
        private static CodeInstruction LoadLocal(CodeInstruction source)
        {
            var op = source.opcode;
            if (op == OpCodes.Ldloc_0 || op == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
            if (op == OpCodes.Ldloc_1 || op == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
            if (op == OpCodes.Ldloc_2 || op == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
            if (op == OpCodes.Ldloc_3 || op == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);

            return source.operand is LocalVariableInfo
                ? new CodeInstruction(OpCodes.Ldloc, source.operand)
                : throw new InvalidOperationException($"a loadable local (got {source})");
        }

        private static CodeInstruction Call(string name)
        {
            return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GlyphCapturePatches), name));
        }

        /// <summary>
        /// Moves a branch target's labels (and exception blocks) onto the first inserted instruction,
        /// so every jump that used to land on <paramref name="target"/> now runs the insertion first.
        /// </summary>
        private static CodeInstruction[] WithLabelsFrom(CodeInstruction target, params CodeInstruction[] inserted)
        {
            target.MoveLabelsTo(inserted[0]);
            target.MoveBlocksTo(inserted[0]);
            return inserted;
        }
        #endregion
    }
}
