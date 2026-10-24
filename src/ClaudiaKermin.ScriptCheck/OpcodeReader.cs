// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using kOS.Safe.Compilation;

namespace ClaudiaKermin.ScriptCheck
{
    /// <summary>
    /// Reads compiled opcodes against the <see cref="ForbiddenList"/>. A symbolic stack follows what each
    /// opcode pushes, so a suffix knows its receiver (CONFIG, ADDONS, the TIMEWARP, another vessel) and a call
    /// knows whether its arguments are constants. It reads branches straight through, which can leave stale
    /// values on the stack but never hides a forbidden name. Values computed at run time can slip past it;
    /// the runtime gates catch those (ADR 0003).
    /// </summary>
    internal sealed class OpcodeReader
    {
        private static readonly PropertyInfo PushArgument =
            typeof(OpcodePush).GetProperty("Argument", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>What the stack holds: a constant, a variable, a suffix of a receiver, or a result.</summary>
        private sealed class Value
        {
            public bool Marker;
            public bool IsConstant;
            /// <summary>The constant's text, when it is a string.</summary>
            public string Text;
            /// <summary>The variable pushed, lower case, without '$'.</summary>
            public string Variable;
            /// <summary>The suffix that produced this value, lower case.</summary>
            public string Suffix;
            /// <summary>Another vessel, or something reached from one (its parts, modules, control).</summary>
            public bool OtherVessel;
        }

        private readonly List<string> _findings = new List<string>();
        private readonly HashSet<string> _seen = new HashSet<string>();
        private readonly HashSet<string> _otherVesselVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _timewarpVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _reporting;

        /// <summary>
        /// Each finding, e.g. "line 3: COPYPATH (touch files)", in order. The code is read twice: the first pass
        /// learns which variables hold another vessel or the TIMEWARP, wherever they are assigned.
        /// </summary>
        public static List<string> Read(IEnumerable<List<Opcode>> sections)
        {
            var reader = new OpcodeReader();
            var list = new List<List<Opcode>>(sections);
            foreach (var section in list) reader.Walk(section);
            reader._reporting = true;
            foreach (var section in list) reader.Walk(section);
            return reader._findings;
        }

        private void Walk(List<Opcode> code)
        {
            var stack = new Stack<Value>();
            foreach (var op in code) Step(op, stack);
        }

        private void Step(Opcode op, Stack<Value> stack)
        {
            int line = op.SourceLine;

            var push = op as OpcodePush;
            if (push != null)
            {
                stack.Push(Pushed(PushArgument.GetValue(push, null), line));
                return;
            }
            if (op is OpcodePushRelocateLater || op is OpcodePushDelegate)
            {
                stack.Push(new Value());
                return;
            }

            var get = op as OpcodeGetMember; // and OpcodeGetMethod
            if (get != null)
            {
                var receiver = Pop(stack);
                var id = Lower(get.Identifier);
                Suffix(receiver, id, line);
                stack.Push(new Value
                {
                    Suffix = receiver.Variable != null && _timewarpVariables.Contains(receiver.Variable) && id == "mode" ? "mode" : id,
                    OtherVessel = receiver.OtherVessel,
                });
                return;
            }

            var set = op as OpcodeSetMember;
            if (set != null)
            {
                var value = Pop(stack);
                var receiver = Pop(stack);
                Consumed(value, line);
                SetSuffix(receiver, Lower(set.Identifier), value, line);
                return;
            }

            if (op is OpcodeStore || op is OpcodeStoreLocal || op is OpcodeStoreGlobal || op is OpcodeStoreExist)
            {
                var value = Pop(stack);
                Stored(Lower(((OpcodeIdentifierBase)op).Identifier).TrimStart('$'), value, line);
                return;
            }

            var call = op as OpcodeCall;
            if (call != null)
            {
                var args = new List<Value>();
                while (stack.Count > 0 && !stack.Peek().Marker) args.Insert(0, stack.Pop());
                if (stack.Count > 0) stack.Pop();
                var destination = call.Destination as string;
                if (destination == null && (call.DestinationLabel ?? "").StartsWith("@LR", StringComparison.Ordinal))
                {
                    // RUN, RUNPATH and RUNONCEPATH call kOS's program loader, which reads a file from a volume.
                    foreach (var arg in args) Consumed(arg, line);
                    Found(line, "RUN", ForbiddenList.TouchFiles);
                    stack.Push(new Value());
                }
                else if (destination == null || destination == "<indirect>")
                {
                    var callee = Pop(stack);
                    foreach (var arg in args) Consumed(arg, line);
                    stack.Push(new Value { OtherVessel = callee.OtherVessel });
                }
                else stack.Push(Called(Name(destination), args, line));
                return;
            }

            if (op is OpcodeGetIndex)
            {
                Pop(stack);
                var collection = Pop(stack);
                stack.Push(new Value { OtherVessel = collection.OtherVessel });
                return;
            }
            if (op is OpcodeSetIndex)
            {
                Consumed(Pop(stack), line);
                Pop(stack);
                var collection = Pop(stack);
                if (collection.OtherVessel) Found(line, "SET [index]", ForbiddenList.OtherVessels);
                return;
            }

            if (op is BinaryOpcode || op is OpcodeLogicAnd || op is OpcodeLogicOr)
            {
                Consumed(Pop(stack), line);
                Consumed(Pop(stack), line);
                stack.Push(new Value());
                return;
            }
            if (op is OpcodeLogicNot || op is OpcodeLogicToBool || op is OpcodeMathNegate || op is OpcodeExists)
            {
                Consumed(Pop(stack), line);
                stack.Push(new Value());
                return;
            }
            if (op is OpcodeEval)
            {
                stack.Push(Pop(stack));
                return;
            }
            if (op is OpcodeDup)
            {
                var top = Pop(stack);
                stack.Push(top);
                stack.Push(top);
                return;
            }
            if (op is OpcodeSwap)
            {
                var a = Pop(stack);
                var b = Pop(stack);
                stack.Push(a);
                stack.Push(b);
                return;
            }
            if (op is OpcodeTestArgBottom || op is OpcodeTestCancelled)
            {
                stack.Push(new Value());
                return;
            }
            if (op is OpcodePop || op is OpcodeReturn || op is OpcodeBranchIfFalse || op is OpcodeBranchIfTrue
                || op is OpcodeWait || op is OpcodeAddTrigger || op is OpcodeRemoveTrigger || op is OpcodeUnset || op is OpcodeJumpStack)
            {
                Consumed(Pop(stack), line);
            }
            // Scopes, labels, NOPs and jumps leave the stack alone.
        }

        private Value Pushed(object argument, int line)
        {
            if (argument != null && argument.GetType().Name == "KOSArgMarkerType") return new Value { Marker = true };
            var raw = argument as string;
            if (raw != null && raw.StartsWith("$", StringComparison.Ordinal))
            {
                var name = Lower(raw.Substring(1));
                if (name.EndsWith("*", StringComparison.Ordinal))
                {
                    // A function pointer (or lock): pointing at a forbidden built-in is using it.
                    Function(name.TrimEnd('*'), name.TrimEnd('*').ToUpperInvariant() + "@", line);
                    return new Value();
                }
                if (name == "archive") Found(line, "ARCHIVE", ForbiddenList.TouchFiles);
                return new Value
                {
                    Variable = name,
                    Suffix = _timewarpVariables.Contains(name) ? "timewarp" : null,
                    OtherVessel = name == "target" || _otherVesselVariables.Contains(name),
                };
            }
            // Any other argument is a constant: a raw compiler string, a StringValue, a number, a boolean.
            return new Value
            {
                IsConstant = true,
                Text = argument == null ? null : Convert.ToString(argument, CultureInfo.InvariantCulture),
            };
        }

        private void Suffix(Value receiver, string id, int line)
        {
            string item;
            if (ForbiddenList.Suffixes.TryGetValue(id, out item)) Found(line, ":" + id.ToUpperInvariant(), item);
            if (receiver.Variable == "addons" && !ForbiddenList.AllowedAddonSuffixes.Contains(id))
                Found(line, "ADDONS:" + id.ToUpperInvariant(), ForbiddenList.OtherAddons);
            if (receiver.OtherVessel && ForbiddenList.OtherVesselMutators.Contains(id))
                Found(line, ":" + id.ToUpperInvariant() + " on another vessel", ForbiddenList.OtherVessels);
        }

        private void SetSuffix(Value receiver, string id, Value value, int line)
        {
            var what = "SET :" + id.ToUpperInvariant();
            if (receiver.Variable == "config") Found(line, "SET CONFIG:" + id.ToUpperInvariant(), ForbiddenList.ChangeConfig);
            else if (receiver.Variable == "addons") Found(line, "SET ADDONS:" + id.ToUpperInvariant(), ForbiddenList.OtherAddons);
            if (id == "activevessel") Found(line, what, ForbiddenList.SwitchVessels);
            if (id == "bootfilename") Found(line, what, ForbiddenList.SetBootFiles);
            if (id == "mode" && !IsText(value, "rails") && (receiver.Suffix == "timewarp" || IsText(value, "physics")))
                Found(line, "SET TIMEWARP:MODE", ForbiddenList.PhysicsWarp);
            if (receiver.OtherVessel) Found(line, what + " on another vessel", ForbiddenList.OtherVessels);
        }

        private void Stored(string id, Value value, int line)
        {
            if (id == "warpmode" && !IsText(value, "rails")) Found(line, "SET WARPMODE", ForbiddenList.PhysicsWarp);
            if (value.Variable == "config") Found(line, "CONFIG held in a variable", ForbiddenList.ChangeConfig);
            if (value.Variable == "addons") Found(line, "ADDONS held in a variable", ForbiddenList.OtherAddons);
            var name = id.TrimEnd('*');
            if (value.OtherVessel) _otherVesselVariables.Add(name);
            if (value.Suffix == "timewarp") _timewarpVariables.Add(name);
        }

        /// <summary>A value used as an argument or operand: CONFIG and ADDONS may only be read through a suffix.</summary>
        private void Consumed(Value value, int line)
        {
            if (value.Variable == "config") Found(line, "CONFIG passed on", ForbiddenList.ChangeConfig);
            if (value.Variable == "addons") Found(line, "ADDONS passed on", ForbiddenList.OtherAddons);
        }

        private Value Called(string name, List<Value> args, int line)
        {
            foreach (var arg in args) Consumed(arg, line);
            if (name == "makebuiltindelegate")
            {
                if (args.Count == 1 && args[0].IsConstant && args[0].Text != null)
                    Function(Lower(args[0].Text), args[0].Text.ToUpperInvariant() + "@", line);
                else Found(line, "MAKEBUILTINDELEGATE", ForbiddenList.ComputedDelegate);
                return new Value();
            }
            Function(name, name.ToUpperInvariant(), line);
            if ((name == "buildlist" || name == "printlist") && args.Count > 0 && args[0].IsConstant
                && args[0].Text != null && ForbiddenList.FileListKinds.Contains(args[0].Text))
                Found(line, "LIST " + args[0].Text.ToUpperInvariant(), ForbiddenList.TouchFiles);
            var targets = name == "buildlist" && args.Count > 0 && IsText(args[0], "targets");
            return new Value { OtherVessel = name == "vessel" || targets };
        }

        private void Function(string name, string what, int line)
        {
            string item;
            if (ForbiddenList.Functions.TryGetValue(name, out item)) Found(line, what, item);
        }

        private void Found(int line, string what, string item)
        {
            if (!_reporting) return;
            var finding = "line " + line.ToString(CultureInfo.InvariantCulture) + ": " + what + " (" + item + ")";
            if (_seen.Add(finding)) _findings.Add(finding);
        }

        private static Value Pop(Stack<Value> stack)
        {
            return stack.Count > 0 ? stack.Pop() : new Value();
        }

        private static bool IsText(Value value, string text)
        {
            return value.IsConstant && string.Equals(value.Text, text, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A call target as written: "copypath()" (built-in), "vessel" (resolved at run time) or "$f*" (user function).</summary>
        private static string Name(string destination)
        {
            var name = Lower(destination).TrimStart('$');
            if (name.EndsWith("()", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 2);
            return name.TrimEnd('*');
        }

        private static string Lower(string s)
        {
            return (s ?? "").ToLowerInvariant();
        }
    }
}
